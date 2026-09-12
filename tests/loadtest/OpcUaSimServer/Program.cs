using System.Diagnostics;
using System.Net;
using Opc.Ua;
using Opc.Ua.Configuration;
using Opc.Ua.Server;

namespace OpcUaSimServer;

/// <summary>
/// Load-test OPC UA server: hosts N Double variables ("Tag00001".."Tag{N:00000}")
/// under Objects/Tags (ns=2), all updated every SIM_UPDATE_MS milliseconds.
/// The first SIM_WRITEABLE nodes are writeable (AccessLevel Read|Write): a UA write
/// is accepted and the node is then frozen (no longer overwritten by UpdateAll) so
/// the written value persists and can be read back through a bridge.
/// SIM_BAD_TAGS="9,10" fault-injects Tag00009/Tag00010: they flip to BadOutOfService
/// and freeze (SIM_BAD_AFTER_MS ms after start; 0 = on the first update tick), so a
/// bridge sees a live good→bad quality transition for a connected tag.
/// SIM_EXTRA_TAGS="99999" adds Tag99999 to the address space at runtime
/// (SIM_EXTRA_AFTER_MS ms after start; 0 = first tick) — simulates a tag appearing at
/// the source later; a bridge retries its failed monitored item automatically.
/// SIM_STATUS_TAGS="Pump01.Run:4000,Valve01.Open:7000" adds read-only Boolean nodes
/// under Objects/Status (ns=2) that toggle themselves every :PeriodMs (the half-period),
/// so a bridge reads a genuinely changing on/off signal without anything writing to it.
/// Env: SIM_NODES (default 20000), SIM_UPDATE_MS (default 1000), SIM_PORT (default 4840),
///      SIM_WRITEABLE (default 10), SIM_BAD_TAGS (default none), SIM_BAD_AFTER_MS (default 0),
///      SIM_EXTRA_TAGS (default none), SIM_EXTRA_AFTER_MS (default 0),
///      SIM_STATUS_TAGS (default none),
///      SIM_CTRL_PORT (default 49331) — embedded HTTP control API + dashboard.
/// Endpoint: opc.tcp://0.0.0.0:{SIM_PORT}/opcuasim/  (SecurityMode None, anonymous).
/// Control:  http://0.0.0.0:{SIM_CTRL_PORT}/  (tag dashboard, per-tag rate changes).
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        int nodeCount = ParseEnv("SIM_NODES", 20000);
        int updateMs = ParseEnv("SIM_UPDATE_MS", 1000);
        int port = ParseEnv("SIM_PORT", 4840);
        int ctrlPort = ParseEnv("SIM_CTRL_PORT", 49331);
        int writeableCount = ParseEnv("SIM_WRITEABLE", 10);
        if (writeableCount > nodeCount)
        {
            writeableCount = nodeCount;
        }

        HashSet<int> badTags = ParseTagList("SIM_BAD_TAGS");
        int badAfterMs = ParseEnv("SIM_BAD_AFTER_MS", 0);
        HashSet<int> extraTags = ParseTagList("SIM_EXTRA_TAGS");
        int extraAfterMs = ParseEnv("SIM_EXTRA_AFTER_MS", 0);
        List<StatusTagSpec> statusTags = ParseStatusTags("SIM_STATUS_TAGS");

        try
        {
            return RunAsync(nodeCount, updateMs, port, ctrlPort, writeableCount, badTags, badAfterMs, extraTags, extraAfterMs, statusTags).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Exception? current = ex;
            int depth = 0;
            while (current is not null && depth < 10)
            {
                Console.WriteLine($"FATAL[{depth}] {current.GetType().Name}: {current.Message}");
                current = current.InnerException;
                depth++;
            }
            return 1;
        }
    }

    private static int ParseEnv(string name, int fallback)
    {
        return int.TryParse(Environment.GetEnvironmentVariable(name), out int value) && value > 0
            ? value
            : fallback;
    }

    /// <summary>Comma-separated 1-based tag numbers from an env var (SIM_BAD_TAGS / SIM_EXTRA_TAGS).</summary>
    private static HashSet<int> ParseTagList(string envName)
    {
        HashSet<int> result = new();
        string? raw = Environment.GetEnvironmentVariable(envName);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return result;
        }

        foreach (string part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(part, out int n) && n > 0)
            {
                result.Add(n);
            }
        }

        return result;
    }

    /// <summary>
    /// "Name:PeriodMs,Name:PeriodMs" from SIM_STATUS_TAGS. A missing or invalid period
    /// defaults to 5000 ms.
    /// </summary>
    private static List<StatusTagSpec> ParseStatusTags(string envName)
    {
        List<StatusTagSpec> result = new();
        string? raw = Environment.GetEnvironmentVariable(envName);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return result;
        }

        foreach (string part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] halves = part.Split(':', 2);
            string name = halves[0].Trim();
            if (name.Length == 0)
            {
                continue;
            }

            int periodMs = halves.Length > 1 && int.TryParse(halves[1].Trim(), out int parsed) && parsed > 0
                ? parsed
                : 5000;
            result.Add(new StatusTagSpec(name, periodMs));
        }

        return result;
    }

    private static async Task<int> RunAsync(
        int nodeCount,
        int updateMs,
        int port,
        int ctrlPort,
        int writeableCount,
        HashSet<int> badTags,
        int badAfterMs,
        HashSet<int> extraTags,
        int extraAfterMs,
        List<StatusTagSpec> statusTags)
    {
        string endpoint = $"opc.tcp://0.0.0.0:{port}/opcuasim/";
        string badInfo = badTags.Count == 0 ? "none" : string.Join(",", badTags.OrderBy(n => n)) + (badAfterMs > 0 ? $" after {badAfterMs}ms" : " at start");
        string extraInfo = extraTags.Count == 0 ? "none" : string.Join(",", extraTags.OrderBy(n => n)) + (extraAfterMs > 0 ? $" after {extraAfterMs}ms" : " at start");
        string statusInfo = statusTags.Count == 0
            ? "none"
            : string.Join(",", statusTags.Select(s => $"{s.Name}:{s.PeriodMs}ms"));
        Console.WriteLine($"Starting sim: {nodeCount} nodes ({writeableCount} writeable), bad tags: {badInfo}, extra tags: {extraInfo}, status tags: {statusInfo}, default {updateMs} ms update, {endpoint}");

        ApplicationConfiguration configuration = BuildConfiguration(endpoint);
        await configuration.ValidateAsync(ApplicationType.Server).ConfigureAwait(false);

        SimServer server = new(nodeCount, writeableCount, badTags, badAfterMs, extraTags, extraAfterMs, updateMs, endpoint, statusTags);
        ApplicationInstance application = new()
        {
            ApplicationName = "OpcUaSimServer",
            ApplicationType = ApplicationType.Server,
            ApplicationConfiguration = configuration
        };
        bool certificateOk = await application.CheckApplicationInstanceCertificatesAsync(false).ConfigureAwait(false);
        if (!certificateOk)
        {
            Console.WriteLine("FATAL: application certificate invalid");
            return 1;
        }
        await application.StartAsync(server).ConfigureAwait(false);

        Console.WriteLine("Sim server started");
        Console.WriteLine($"ENDPOINT {endpoint}");
        Console.WriteLine($"NODES {nodeCount}");

        // Embedded HTTP control API + dashboard. Ticks independently of the UA server.
        using CancellationTokenSource cts = new();
        Task controlTask = SimControlServer.RunAsync(server, ctrlPort, cts.Token);

        Stopwatch sw = Stopwatch.StartNew();
        long tick = 0;
        while (true)
        {
            // Tick at the smallest tag rate so fast tags update promptly; the per-tag
            // gating in UpdateAll decides which tags actually get a new value.
            int periodMs = server.GetMinRateMs();
            await Task.Delay(periodMs).ConfigureAwait(false);

            tick++;
            int updated = server.UpdateAll(tick, sw.ElapsedMilliseconds);
            if (tick % 20 == 0)
            {
                Console.WriteLine($"tick {tick}: {updated}/{nodeCount} nodes updated @ {periodMs}ms (global {server.GlobalRateMs}ms)");
            }
        }

        server.Dispose();
        return 0;
    }

    private static ApplicationConfiguration BuildConfiguration(string endpoint)
    {
        return new ApplicationConfiguration
        {
            ApplicationName = "OpcUaSimServer",
            ApplicationUri = $"urn:opcuasim:{Dns.GetHostName()}",
            ProductUri = "urn:opcuasim:loadtest",
            ApplicationType = ApplicationType.Server,
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificate = new CertificateIdentifier
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = "pki/own",
                    SubjectName = "OpcUaSimServer"
                },
                TrustedPeerCertificates = new CertificateTrustList
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = "pki/trusted"
                },
                TrustedIssuerCertificates = new CertificateTrustList
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = "pki/issuers"
                },
                RejectedCertificateStore = new CertificateTrustList
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = "pki/rejected"
                },
                AutoAcceptUntrustedCertificates = true,
                RejectSHA1SignedCertificates = true,
                MinimumCertificateKeySize = 2048
            },
            TransportConfigurations = new TransportConfigurationCollection(),
            TransportQuotas = new TransportQuotas
            {
                OperationTimeout = 15000,
                MaxStringLength = 1048576,
                MaxByteStringLength = 1048576,
                MaxArrayLength = 65535,
                MaxMessageSize = 4194304,
                MaxBufferSize = 65535,
                ChannelLifetime = 300000,
                SecurityTokenLifetime = 3600000
            },
            ServerConfiguration = new ServerConfiguration
            {
                BaseAddresses = new StringCollection { endpoint },
                SecurityPolicies = new ServerSecurityPolicyCollection
                {
                    new ServerSecurityPolicy
                    {
                        SecurityMode = MessageSecurityMode.None,
                        SecurityPolicyUri = SecurityPolicies.None
                    }
                },
                MinRequestThreadCount = 5,
                MaxRequestThreadCount = 100,
                MaxSessionCount = 50,
                MaxSubscriptionCount = 50,
                MaxMessageQueueSize = 1000,
                MaxNotificationQueueSize = 10000,
                MaxPublishRequestCount = 100
            },
            TraceConfiguration = new TraceConfiguration()
        };
    }

}
