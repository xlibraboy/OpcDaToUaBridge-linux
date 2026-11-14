#if ANDROID
using Android.Content;
using Android.Net;
using Android.Net.Wifi;
using Android.OS;
using Microsoft.Maui.ApplicationModel;
using OpcBridge.Mobile.Core;

namespace OpcBridge.Mobile;

/// <summary>
/// Android's Wi-Fi surface for the plant access point. The current association is polled so a
/// dropped link closes the gate within a few seconds, and Android 10+ joins the bound BSSID
/// through an app-scoped <see cref="WifiNetworkSpecifier"/> request: no internet capability, the
/// process is bound to that network while it is active, and the system asks the operator once.
/// </summary>
public sealed class AndroidAccessPointNetwork : IAccessPointNetwork, IDisposable
{
    private const int KeyManagementNone = 0;
    private const int KeyManagementWpaPsk = 1;

    private readonly Context context_;
    private readonly WifiManager wifi_;
    private readonly ConnectivityManager connectivity_;
    private readonly System.Threading.Timer poll_;
    private readonly object sync_ = new();
    private AccessPointConnection current_ = AccessPointConnection.Unknown;
    private ConnectivityManager.NetworkCallback? requestCallback_;
    private volatile Network? requestedNetwork_;

    public AndroidAccessPointNetwork()
    {
        context_ = Android.App.Application.Context;
        wifi_ = (WifiManager)context_.GetSystemService(Context.WifiService)!;
        connectivity_ = (ConnectivityManager)context_.GetSystemService(Context.ConnectivityService)!;
        poll_ = new System.Threading.Timer(
            _ => Refresh(),
            null,
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(3));
        Refresh();
    }

    public event Action? Changed;

    public AccessPointConnection Current
    {
        get
        {
            lock (sync_)
            {
                return current_;
            }
        }
    }

    public void Refresh()
    {
        AccessPointConnection next = ReadCurrent();
        bool changed;
        lock (sync_)
        {
            changed = !Equals(current_, next);
            current_ = next;
        }

        if (!next.Connected && requestCallback_ is null && requestedNetwork_ is not null)
        {
            // A manually joined Wi-Fi network has no system callback; once it is gone the
            // process must stop being pinned to a network that no longer exists.
            ReleaseRequest();
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    private void BindToCurrentWifi(SavedAccessPoint accessPoint)
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.Q)
        {
            return;
        }

        try
        {
            foreach (Network network in connectivity_.GetAllNetworks() ?? Array.Empty<Network>())
            {
                NetworkCapabilities? capabilities = connectivity_.GetNetworkCapabilities(network);
                if (capabilities is null
                    || !capabilities.HasTransport(TransportType.Wifi)
                    || capabilities.TransportInfo is not WifiInfo info
                    || !AccessPointCodec.SameBssid(info.BSSID, accessPoint.Bssid))
                {
                    continue;
                }

                requestedNetwork_ = network;
                connectivity_.BindProcessToNetwork(network);
                return;
            }
        }
        catch (Exception)
        {
        }
    }

    public async Task<MobileResult<IReadOnlyList<NearbyAccessPoint>>> ScanAsync(CancellationToken cancellationToken)
    {
        if (!await EnsurePermissionsAsync())
        {
            Refresh();
            return MobileResult<IReadOnlyList<NearbyAccessPoint>>.Fail(
                "allow nearby Wi-Fi and location access to list networks");
        }

        Refresh();
        if (!wifi_.IsWifiEnabled)
        {
            return MobileResult<IReadOnlyList<NearbyAccessPoint>>.Fail("turn Wi-Fi on first");
        }

        if (!LocationServicesEnabled())
        {
            return MobileResult<IReadOnlyList<NearbyAccessPoint>>.Fail(
                "turn on location services so Android can show network names");
        }

        try
        {
            // The scan is asynchronous and throttled; the cached list is still useful when the
            // platform declines a fresh one, so a failure here is not an empty result.
            wifi_.StartScan();
            await Task.Delay(1500, cancellationToken);
            IList<ScanResult>? results = wifi_.ScanResults;
            if (results is null)
            {
                return MobileResult<IReadOnlyList<NearbyAccessPoint>>.Success(Array.Empty<NearbyAccessPoint>());
            }

            List<NearbyAccessPoint> accessPoints = results
                .Where(result => !string.IsNullOrWhiteSpace(result.Ssid) && !string.IsNullOrWhiteSpace(result.Bssid))
                .GroupBy(result => AccessPointCodec.NormalizeBssid(result.Bssid))
                .Select(group => group.OrderByDescending(result => result.Level).First())
                .Select(result => new NearbyAccessPoint(
                    CleanSsid(result.Ssid),
                    AccessPointCodec.NormalizeBssid(result.Bssid),
                    result.Level,
                    Classify(result.Capabilities)))
                .OrderByDescending(result => result.SignalLevel)
                .ToList();
            return MobileResult<IReadOnlyList<NearbyAccessPoint>>.Success(accessPoints);
        }
        catch (System.OperationCanceledException)
        {
            throw;
        }
        catch (Java.Lang.SecurityException)
        {
            return MobileResult<IReadOnlyList<NearbyAccessPoint>>.Fail(
                "allow nearby Wi-Fi and location access to list networks");
        }
        catch (Exception exception)
        {
            return MobileResult<IReadOnlyList<NearbyAccessPoint>>.Fail("scan failed · " + exception.Message);
        }
    }

    public async Task<MobileResult<AccessPointConnection>> ConnectAsync(
        SavedAccessPoint accessPoint,
        string? password,
        CancellationToken cancellationToken)
    {
        if (!await EnsurePermissionsAsync())
        {
            Refresh();
            return MobileResult<AccessPointConnection>.Fail(
                "allow nearby Wi-Fi and location access to connect");
        }

        if (!wifi_.IsWifiEnabled)
        {
            return MobileResult<AccessPointConnection>.Fail("turn Wi-Fi on first");
        }

        if (!LocationServicesEnabled())
        {
            return MobileResult<AccessPointConnection>.Fail(
                "turn on location services so Android can verify the plant access point");
        }

        Refresh();
        AccessPointConnection current = Current;
        if (current.Connected
            && current.IdentityVisible
            && AccessPointCodec.SameBssid(current.Bssid, accessPoint.Bssid))
        {
            BindToCurrentWifi(accessPoint);
            return MobileResult<AccessPointConnection>.Success(current);
        }

        if (accessPoint.Security == AccessPointSecurity.Wpa2Enterprise)
        {
            return MobileResult<AccessPointConnection>.Fail(
                "join this enterprise network in Android Wi-Fi settings, then bind it");
        }

        if (accessPoint.Security == AccessPointSecurity.Wep)
        {
            return MobileResult<AccessPointConnection>.Fail("WEP networks are not supported");
        }

        if (accessPoint.Security != AccessPointSecurity.Open && string.IsNullOrEmpty(password))
        {
            return MobileResult<AccessPointConnection>.Fail("enter the Wi-Fi password");
        }

        return Build.VERSION.SdkInt >= BuildVersionCodes.Q
            ? await ConnectWithSpecifierAsync(accessPoint, password, cancellationToken)
            : await ConnectLegacyAsync(accessPoint, password, cancellationToken);
    }

    public Task DisconnectAsync()
    {
        ReleaseRequest();
        Refresh();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        poll_.Dispose();
        ReleaseRequest();
    }

    private AccessPointConnection ReadCurrent()
    {
        try
        {
            if (!wifi_.IsWifiEnabled)
            {
                return new AccessPointConnection(false, false, false, false, string.Empty, string.Empty);
            }

            if (!HasPermissions())
            {
                return new AccessPointConnection(true, true, false, false, string.Empty, string.Empty);
            }

            WifiInfo? info = null;
            Network? requested = requestedNetwork_;
            if (requested is not null && Build.VERSION.SdkInt >= BuildVersionCodes.Q)
            {
                // A local-only specifier network can run beside the phone's normal Wi-Fi;
                // the connection info of the requested network is the one the app is using.
                info = connectivity_.GetNetworkCapabilities(requested)?.TransportInfo as WifiInfo;
            }

            bool hasRequestedNetwork = info is not null;
            info ??= wifi_.ConnectionInfo;
            if (info is null || (!hasRequestedNetwork && info.NetworkId == -1))
            {
                return new AccessPointConnection(true, false, false, false, string.Empty, string.Empty);
            }

            string bssid = AccessPointCodec.NormalizeBssid(info.BSSID);
            bool identityVisible = bssid.Length > 0 && bssid != "02:00:00:00:00:00";
            return new AccessPointConnection(
                true,
                false,
                true,
                identityVisible,
                CleanSsid(info.SSID),
                identityVisible ? bssid : string.Empty);
        }
        catch (Java.Lang.SecurityException)
        {
            return new AccessPointConnection(true, true, false, false, string.Empty, string.Empty);
        }
        catch (Exception)
        {
            return new AccessPointConnection(true, false, false, false, string.Empty, string.Empty);
        }
    }

    private bool LocationServicesEnabled()
    {
        try
        {
            if (Build.VERSION.SdkInt < BuildVersionCodes.P)
            {
                return true;
            }

            Android.Locations.LocationManager? manager =
                (Android.Locations.LocationManager?)context_.GetSystemService(Context.LocationService);
            return manager?.IsLocationEnabled == true;
        }
        catch (Exception)
        {
            return true;
        }
    }

    private bool HasPermissions()
    {
        try
        {
            if (context_.CheckSelfPermission(Android.Manifest.Permission.AccessFineLocation)
                != Android.Content.PM.Permission.Granted)
            {
                return false;
            }

            return Build.VERSION.SdkInt < BuildVersionCodes.Tiramisu
                || context_.CheckSelfPermission(Android.Manifest.Permission.NearbyWifiDevices)
                    == Android.Content.PM.Permission.Granted;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task<bool> EnsurePermissionsAsync()
    {
        if (HasPermissions())
        {
            return true;
        }

        try
        {
            PermissionStatus fine = await Permissions.RequestAsync<Permissions.LocationWhenInUse>();
            PermissionStatus nearby = Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu
                ? await Permissions.RequestAsync<Permissions.NearbyWifiDevices>()
                : PermissionStatus.Granted;
            return fine == PermissionStatus.Granted && nearby == PermissionStatus.Granted;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task<MobileResult<AccessPointConnection>> ConnectWithSpecifierAsync(
        SavedAccessPoint accessPoint,
        string? password,
        CancellationToken cancellationToken)
    {
        ReleaseRequest();
        TaskCompletionSource<MobileResult<AccessPointConnection>> completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        WifiNetworkSpecifier.Builder specifierBuilder = new();
        specifierBuilder.SetSsid(accessPoint.Ssid);
        specifierBuilder.SetBssid(MacAddress.FromString(accessPoint.Bssid));
        switch (accessPoint.Security)
        {
            case AccessPointSecurity.Wpa3:
                specifierBuilder.SetWpa3Passphrase(password!);
                break;
            case AccessPointSecurity.Wpa2:
                specifierBuilder.SetWpa2Passphrase(password!);
                break;
        }

        WifiNetworkSpecifier specifier = specifierBuilder.Build()!;
        NetworkRequest.Builder requestBuilder = new();
        requestBuilder.AddTransportType(TransportType.Wifi);
        requestBuilder.RemoveCapability(NetCapability.Internet);
        requestBuilder.SetNetworkSpecifier(specifier);
        NetworkRequest request = requestBuilder.Build()!;

        SpecifierCallback callback = new(this, accessPoint, completion);
        requestCallback_ = callback;
        try
        {
            connectivity_.RequestNetwork(request, callback);
        }
        catch (Exception exception)
        {
            requestCallback_ = null;
            return MobileResult<AccessPointConnection>.Fail(
                "could not start the Wi-Fi request · " + exception.Message);
        }

        using CancellationTokenRegistration registration = cancellationToken.Register(() =>
        {
            ReleaseRequest();
            completion.TrySetCanceled(cancellationToken);
        });

        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30));
            if (!completion.Task.IsCompleted)
            {
                ReleaseRequest();
                completion.TrySetResult(MobileResult<AccessPointConnection>.Fail(
                    "the phone did not join " + accessPoint.Ssid + " in time"));
            }
        });

        try
        {
            return await completion.Task;
        }
        finally
        {
            if (!completion.Task.IsCompletedSuccessfully || !completion.Task.Result.Ok)
            {
                ReleaseRequest();
            }
        }
    }

    private async Task<MobileResult<AccessPointConnection>> ConnectLegacyAsync(
        SavedAccessPoint accessPoint,
        string? password,
        CancellationToken cancellationToken)
    {
        if (accessPoint.Security == AccessPointSecurity.Wpa3)
        {
            return MobileResult<AccessPointConnection>.Fail("WPA3 needs Android 10 or newer");
        }

        try
        {
            WifiConfiguration configuration = new()
            {
                Ssid = "\"" + accessPoint.Ssid + "\"",
                Bssid = accessPoint.Bssid,
                Priority = 1
            };
            if (accessPoint.Security == AccessPointSecurity.Open)
            {
                configuration.AllowedKeyManagement!.Set(KeyManagementNone);
            }
            else
            {
                configuration.AllowedKeyManagement!.Set(KeyManagementWpaPsk);
                configuration.PreSharedKey = "\"" + password + "\"";
            }

            int networkId = wifi_.AddNetwork(configuration);
            if (networkId < 0)
            {
                foreach (WifiConfiguration existing in wifi_.ConfiguredNetworks ?? new List<WifiConfiguration>())
                {
                    if (string.Equals(existing.Ssid, configuration.Ssid, StringComparison.Ordinal)
                        && string.Equals(existing.Bssid, configuration.Bssid, StringComparison.OrdinalIgnoreCase))
                    {
                        networkId = existing.NetworkId;
                        break;
                    }
                }
            }

            if (networkId < 0)
            {
                return MobileResult<AccessPointConnection>.Fail("the phone could not save the Wi-Fi network");
            }

            wifi_.Disconnect();
            wifi_.EnableNetwork(networkId, true);
            wifi_.Reconnect();

            for (int attempt = 0; attempt < 40; attempt++)
            {
                await Task.Delay(500, cancellationToken);
                Refresh();
                AccessPointConnection current = Current;
                if (current.Connected
                    && current.IdentityVisible
                    && AccessPointCodec.SameBssid(current.Bssid, accessPoint.Bssid))
                {
                    return MobileResult<AccessPointConnection>.Success(current);
                }
            }

            return MobileResult<AccessPointConnection>.Fail(
                "could not join " + accessPoint.Ssid + " — check the password and range");
        }
        catch (System.OperationCanceledException)
        {
            throw;
        }
        catch (Java.Lang.SecurityException)
        {
            return MobileResult<AccessPointConnection>.Fail(
                "allow nearby Wi-Fi and location access to connect");
        }
        catch (Exception exception)
        {
            return MobileResult<AccessPointConnection>.Fail("could not join " + accessPoint.Ssid + " · " + exception.Message);
        }
    }

    private void OnSpecifierAvailable(
        Network network,
        SavedAccessPoint accessPoint,
        TaskCompletionSource<MobileResult<AccessPointConnection>> completion)
    {
        requestedNetwork_ = network;
        try
        {
            // The specifier network is local-only; binding keeps every app socket on the plant
            // access point rather than letting it fall back to cellular or another Wi-Fi.
            connectivity_.BindProcessToNetwork(network);
        }
        catch (Exception)
        {
        }

        _ = Task.Run(async () =>
        {
            for (int attempt = 0; attempt < 24; attempt++)
            {
                Refresh();
                AccessPointConnection current = Current;
                if (current.Connected
                    && current.IdentityVisible
                    && AccessPointCodec.SameBssid(current.Bssid, accessPoint.Bssid))
                {
                    completion.TrySetResult(MobileResult<AccessPointConnection>.Success(current));
                    return;
                }

                await Task.Delay(250);
            }

            completion.TrySetResult(MobileResult<AccessPointConnection>.Fail(
                "joined " + accessPoint.Ssid + " but could not verify the bound BSSID — allow location access"));
        });
    }

    private void OnSpecifierLost()
    {
        requestedNetwork_ = null;
        ConnectivityManager.NetworkCallback? callback = requestCallback_;
        requestCallback_ = null;
        if (callback is not null)
        {
            try
            {
                connectivity_.UnregisterNetworkCallback(callback);
            }
            catch (Exception)
            {
            }
        }

        try
        {
            connectivity_.BindProcessToNetwork(null);
        }
        catch (Exception)
        {
        }

        Refresh();
    }

    private void ReleaseRequest()
    {
        requestedNetwork_ = null;
        ConnectivityManager.NetworkCallback? callback = requestCallback_;
        requestCallback_ = null;
        if (callback is not null)
        {
            try
            {
                connectivity_.UnregisterNetworkCallback(callback);
            }
            catch (Exception)
            {
            }
        }

        try
        {
            connectivity_.BindProcessToNetwork(null);
        }
        catch (Exception)
        {
        }
    }

    private static string CleanSsid(string? ssid) =>
        (ssid ?? string.Empty).Trim().Trim('"');

    private static AccessPointSecurity Classify(string? capabilities)
    {
        string value = capabilities ?? string.Empty;
        if (value.Contains("EAP", StringComparison.OrdinalIgnoreCase))
        {
            return AccessPointSecurity.Wpa2Enterprise;
        }

        if (value.Contains("WEP", StringComparison.OrdinalIgnoreCase))
        {
            return AccessPointSecurity.Wep;
        }

        if (value.Contains("SAE", StringComparison.OrdinalIgnoreCase)
            && !value.Contains("PSK", StringComparison.OrdinalIgnoreCase))
        {
            return AccessPointSecurity.Wpa3;
        }

        return value.Contains("WPA", StringComparison.OrdinalIgnoreCase)
            || value.Contains("PSK", StringComparison.OrdinalIgnoreCase)
            ? AccessPointSecurity.Wpa2
            : AccessPointSecurity.Open;
    }

    private sealed class SpecifierCallback : ConnectivityManager.NetworkCallback
    {
        private readonly AndroidAccessPointNetwork owner_;
        private readonly SavedAccessPoint accessPoint_;
        private readonly TaskCompletionSource<MobileResult<AccessPointConnection>> completion_;

        public SpecifierCallback(
            AndroidAccessPointNetwork owner,
            SavedAccessPoint accessPoint,
            TaskCompletionSource<MobileResult<AccessPointConnection>> completion)
        {
            owner_ = owner;
            accessPoint_ = accessPoint;
            completion_ = completion;
        }

        public override void OnAvailable(Network network) =>
            owner_.OnSpecifierAvailable(network, accessPoint_, completion_);

        public override void OnUnavailable() =>
            completion_.TrySetResult(MobileResult<AccessPointConnection>.Fail(
                "the phone could not join " + accessPoint_.Ssid + " — approval was denied or the AP is out of range"));

        public override void OnLost(Network network) => owner_.OnSpecifierLost();
    }
}
#endif
