using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace OpcBridge.Da;

/// <summary>
/// Read-only registration diagnostics for an OPC DA server: walks the chain a local connect
/// takes — ProgID → CLSID in the registry views this process can see, CLSID → the
/// InprocServer32/LocalServer32 path, and the path → a file that exists on this machine.
/// It never writes registry values and never loads the server; activation is the isolated
/// probe's job (the 2026-09-30 PMD session showed a fault in an in-proc server takes the
/// host process down with it — see docs/pmd-opc-da-field-notes-2026-09-30.md).
/// <see cref="Capture"/> is Windows-only; <see cref="Analyze"/> is pure, so the reasoning
/// is testable everywhere the suite runs.
/// </summary>
public static class DaRegistrationDiagnostics
{
    public const string SeverityOk = "ok";
    public const string SeverityInfo = "info";
    public const string SeverityWarn = "warn";
    public const string SeverityFail = "fail";

    /// <summary>Everything one registry view says about the ProgID/CLSID pair; nulls mean "not present".</summary>
    public sealed record DaRegistryViewFinding(
        string ViewName,
        string ClassesRootPath,
        string? MappedClsid,
        bool ClsidKeyPresent,
        string? Description,
        string? InprocServer32,
        string? InprocThreadingModel,
        string? LocalServer32,
        string? AppId,
        bool CategoryPresent,
        bool ReverseProgIdPresent)
    {
        /// <summary>The binary a local connect would load: in-proc preferred, local-server fallback.</summary>
        public string? ServerPath =>
            string.IsNullOrWhiteSpace(InprocServer32) ? LocalServer32 : InprocServer32;

        public string ServerKeyName =>
            string.IsNullOrWhiteSpace(InprocServer32) ? "LocalServer32" : "InprocServer32";

        public bool HasServerEntry => !string.IsNullOrWhiteSpace(ServerPath);
    }

    /// <summary>File-system facts for one registered server path, gathered without loading the binary.</summary>
    public sealed record DaServerBinaryFacts(
        string Path,
        string PathRoot,
        bool RootExists,
        bool FileExists,
        string? Company,
        string? Product,
        string? FileVersion);

    public sealed record DaRegistrationSnapshot(
        string ProgId,
        string Host,
        bool BridgeIs64Bit,
        IReadOnlyList<DaRegistryViewFinding> Views,
        IReadOnlyDictionary<string, DaServerBinaryFacts> Binaries);

    public sealed record DaDiagnosticFinding(
        string Id,
        string Severity,
        string Title,
        string Detail,
        string? Remediation = null);

    public sealed record DaRegistrationReport(
        string ProgId,
        string Host,
        string Verdict,
        string Summary,
        IReadOnlyList<DaDiagnosticFinding> Findings);

    /// <summary>Capture and analyze in one call; the shape the endpoint and the connect-failure verdict use.</summary>
    [SupportedOSPlatform("windows")]
    public static DaRegistrationReport Diagnose(
        string progId,
        string? host,
        string? username = null,
        string? password = null,
        string? domain = null)
        => Analyze(Capture(progId, host, username, password, domain));

    /// <summary>
    /// Reads all four registry views (HKLM/HKCU × 32/64-bit) explicitly, so the report is
    /// correct from any process bitness. With credentials the read runs under impersonation,
    /// matching what the enumerator and a per-user registration expect.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static DaRegistrationSnapshot Capture(
        string progId,
        string? host,
        string? username = null,
        string? password = null,
        string? domain = null)
    {
        string normalizedProgId = progId.Trim();
        string normalizedHost = NormalizeHost(host);
        List<DaRegistryViewFinding> views = new();
        Dictionary<string, DaServerBinaryFacts> binaries = new(StringComparer.OrdinalIgnoreCase);

        WindowsImpersonation.Run(username, password, domain, Environment.MachineName, () =>
        {
            foreach ((RegistryHive hive, RegistryView view, string label) in RegistryViews())
            {
                try
                {
                    using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using RegistryKey? classes = baseKey.OpenSubKey(@"SOFTWARE\Classes");
                    if (classes is null)
                    {
                        continue;
                    }

                    string? mappedClsid;
                    using (RegistryKey? progIdKey = classes.OpenSubKey(normalizedProgId))
                    using (RegistryKey? clsidRef = progIdKey?.OpenSubKey("CLSID"))
                    {
                        mappedClsid = (clsidRef?.GetValue(null) as string)?.Trim();
                    }

                    string classesPath = HiveName(hive) + @"\SOFTWARE\Classes";
                    if (string.IsNullOrWhiteSpace(mappedClsid))
                    {
                        views.Add(new DaRegistryViewFinding(
                            label, classesPath, null, false, null, null, null, null, null, false, false));
                        continue;
                    }

                    bool clsidKeyPresent = false;
                    string? description = null;
                    string? inprocPath = null;
                    string? threadingModel = null;
                    string? localPath = null;
                    string? appId = null;
                    bool categoryPresent = false;
                    bool reverseProgIdPresent = false;

                    using (RegistryKey? clsidKey = classes.OpenSubKey(@"CLSID\" + mappedClsid))
                    {
                        if (clsidKey is not null)
                        {
                            clsidKeyPresent = true;
                            description = clsidKey.GetValue(null) as string;

                            using (RegistryKey? inproc = clsidKey.OpenSubKey("InprocServer32"))
                            {
                                if (inproc is not null)
                                {
                                    inprocPath = (inproc.GetValue(null) as string)?.Trim();
                                    threadingModel = inproc.GetValue("ThreadingModel") as string;
                                    AddBinaryCandidate(binaries, inprocPath);
                                }
                            }

                            using (RegistryKey? local = clsidKey.OpenSubKey("LocalServer32"))
                            {
                                localPath = (local?.GetValue(null) as string)?.Trim();
                                AddBinaryCandidate(binaries, localPath);
                            }

                            appId = clsidKey.GetValue("AppID") as string;
                            categoryPresent = clsidKey.OpenSubKey(
                                @"Implemented Categories\" + OpcServerEnumerator.OpcDaCategoryGuid) is not null;
                            reverseProgIdPresent = clsidKey.OpenSubKey("ProgID") is not null;
                        }
                    }

                    views.Add(new DaRegistryViewFinding(
                        label, classesPath, mappedClsid, clsidKeyPresent, description,
                        inprocPath, threadingModel, localPath, appId, categoryPresent, reverseProgIdPresent));
                }
                catch
                {
                    // A view that cannot be read must not sink the report; the analyzer
                    // treats a view it never saw the same as one without a mapping.
                }
            }
        });

        return new DaRegistrationSnapshot(
            normalizedProgId, normalizedHost, Environment.Is64BitProcess, views, binaries);
    }

    /// <summary>
    /// Pure analysis of a captured snapshot: findings for every hop, a verdict and a one-line
    /// summary. Fails the hops a connect would fail on; warns on the discovery-only gaps.
    /// </summary>
    public static DaRegistrationReport Analyze(DaRegistrationSnapshot snapshot)
    {
        List<DaDiagnosticFinding> findings = new();
        string bitness = snapshot.BridgeIs64Bit ? "64-bit" : "32-bit";
        string otherBitness = snapshot.BridgeIs64Bit ? "32-bit" : "64-bit";

        findings.Add(new DaDiagnosticFinding(
            "bridge.bitness",
            SeverityInfo,
            $"Bridge is a {bitness} process",
            $"COM resolves this ProgID through the {bitness} registry views; a registration that exists " +
            $"only in the {otherBitness} view cannot be loaded by this process."));

        List<DaRegistryViewFinding> mapped = snapshot.Views
            .Where(view => !string.IsNullOrWhiteSpace(view.MappedClsid))
            .ToList();
        List<DaRegistryViewFinding> effectiveMapped = mapped
            .Where(view => IsEffectiveView(view.ViewName, snapshot.BridgeIs64Bit))
            .ToList();

        // Hop 1: ProgID → CLSID (resolved locally — .NET's CLSIDFromProgID reads this machine).
        if (mapped.Count == 0)
        {
            findings.Add(new DaDiagnosticFinding(
                "progid.views",
                SeverityFail,
                "ProgID is not registered in any registry view",
                $"No CLSID mapping found for '{snapshot.ProgId}' in HKLM/HKCU, 32-bit or 64-bit.",
                "Install (or re-register) the OPC DA server on this machine, or correct the ProgID."));
            return BuildReport(snapshot, findings);
        }

        string found = string.Join(", ", mapped.Select(view => $"{view.ViewName} → {view.MappedClsid}"));

        if (effectiveMapped.Count == 0)
        {
            findings.Add(new DaDiagnosticFinding(
                "progid.views",
                SeverityFail,
                $"ProgID is registered only in the {otherBitness} view",
                $"{found}. This {bitness} bridge cannot see that view.",
                $"Register the server for the {bitness} view (regsvr32 with the matching DLL), " +
                "or install the matching-bitness build."));
            return BuildReport(snapshot, findings);
        }

        DaRegistryViewFinding target = effectiveMapped.FirstOrDefault(view => view.ClsidKeyPresent) ?? effectiveMapped[0];
        findings.Add(new DaDiagnosticFinding(
            "progid.views",
            SeverityOk,
            "ProgID resolves to a CLSID",
            $"{snapshot.ProgId} → {target.MappedClsid} ({found})."));

        int distinctClsids = mapped
            .Select(view => view.MappedClsid!.Trim('{', '}').ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .Count();
        if (distinctClsids > 1)
        {
            findings.Add(new DaDiagnosticFinding(
                "progid.views.mismatch",
                SeverityWarn,
                "Registry views disagree on the CLSID",
                $"Different views map '{snapshot.ProgId}' to different CLSIDs: {found}.",
                "Re-register the server so every view agrees; a stale view usually means a leftover install."));
        }

        // Hop 2: CLSID → a loadable server entry.
        if (!target.ClsidKeyPresent)
        {
            findings.Add(new DaDiagnosticFinding(
                "clsid.class",
                SeverityFail,
                $"CLSID {target.MappedClsid} is mapped but not registered",
                $"The mapping exists in {target.ViewName}, but there is no CLSID key under {target.ClassesRootPath}\\CLSID.",
                "Re-register the server (regsvr32 on the installed DLL) or repair the installation."));
            return BuildReport(snapshot, findings);
        }

        if (!target.HasServerEntry)
        {
            findings.Add(new DaDiagnosticFinding(
                "clsid.class",
                SeverityFail,
                "CLSID has neither InprocServer32 nor LocalServer32",
                $"CLSID {target.MappedClsid} ({target.ViewName}) is present but names no server binary.",
                "Re-register the server (regsvr32 on the installed DLL) or repair the installation."));
            return BuildReport(snapshot, findings);
        }

        findings.Add(new DaDiagnosticFinding(
            "clsid.class",
            SeverityOk,
            target.ServerKeyName == "InprocServer32" ? "In-proc server registered" : "Local server registered",
            $"{target.ServerKeyName} = '{target.ServerPath}' ({target.ViewName})." +
            (string.IsNullOrWhiteSpace(target.InprocThreadingModel) ? string.Empty : $" ThreadingModel: {target.InprocThreadingModel}.")));

        // Hop 3: the registered path → a file on this machine.
        string serverPath = target.ServerPath!;
        if (snapshot.Binaries.TryGetValue(serverPath, out DaServerBinaryFacts? facts))
        {
            if (!facts.RootExists)
            {
                findings.Add(new DaDiagnosticFinding(
                    "clsid.dll",
                    SeverityFail,
                    $"Server path root {facts.PathRoot} does not exist",
                    $"{target.ClassesRootPath}\\CLSID\\{target.MappedClsid}\\{target.ServerKeyName} names " +
                    $"'{facts.Path}' — {DescribeRoot(facts.PathRoot)}.",
                    BuildDllRemediation(snapshot.ProgId)));
            }
            else if (!facts.FileExists)
            {
                findings.Add(new DaDiagnosticFinding(
                    "clsid.dll",
                    SeverityFail,
                    "Server DLL file not found",
                    $"'{facts.Path}' does not exist on this machine ({target.ViewName} registration).",
                    BuildDllRemediation(snapshot.ProgId)));
            }
            else
            {
                string identity = string.Join(", ", new[] { facts.Company, facts.Product, facts.FileVersion }
                    .Where(part => !string.IsNullOrWhiteSpace(part)));
                findings.Add(new DaDiagnosticFinding(
                    "clsid.dll",
                    SeverityOk,
                    "Server binary exists",
                    $"'{facts.Path}'" + (identity.Length > 0 ? $" ({identity})" : string.Empty)));

                if (TryGetVendorHint(snapshot.ProgId, out DaKnownServerHint? hint))
                {
                    bool matches = facts.Path.Contains(hint.ExpectedPathFragment, StringComparison.OrdinalIgnoreCase);
                    findings.Add(new DaDiagnosticFinding(
                        "vendor.known-path",
                        matches ? SeverityOk : SeverityWarn,
                        matches
                            ? "Path matches the known installed location"
                            : $"Path differs from the known {hint.Vendor} install location",
                        matches
                            ? string.Empty
                            : $"Expected the DLL under '{hint.ExpectedPathFragment}'.",
                        matches ? null : hint.Reference));
                }
            }
        }

        // Discovery keys: what the Tag Browser's enumeration needs; a connect by ProgID does not.
        if (!target.CategoryPresent || !target.ReverseProgIdPresent)
        {
            string missing = !target.CategoryPresent && !target.ReverseProgIdPresent
                ? "the OPC DA category and the reverse ProgID"
                : (!target.CategoryPresent ? "the OPC DA category" : "the reverse ProgID");
            findings.Add(new DaDiagnosticFinding(
                "clsid.discovery",
                SeverityWarn,
                "Server will not appear in OPC DA browsing",
                $"CLSID {target.MappedClsid} is missing {missing}.",
                "Direct connect by ProgID still works; re-register the server to restore discovery."));
        }
        else
        {
            findings.Add(new DaDiagnosticFinding(
                "clsid.discovery",
                SeverityOk,
                "Server is discoverable",
                "OPC DA category and reverse ProgID are registered."));
        }

        if (!IsLocalHost(snapshot.Host))
        {
            findings.Add(new DaDiagnosticFinding(
                "host.remote",
                SeverityInfo,
                $"Remote host '{snapshot.Host}'",
                "The ProgID mapping is resolved on this machine, but the server DLL path and DCOM " +
                "settings that matter are on the remote host — check both machines.",
                "docs/opc-dcom-setup.md"));

            if (string.IsNullOrWhiteSpace(target.AppId))
            {
                findings.Add(new DaDiagnosticFinding(
                    "clsid.appid",
                    SeverityWarn,
                    "Registration has no AppID/DCOM surrogate",
                    "Remote activation of an in-proc server needs a DCOM surrogate (AppID) configured " +
                    "where the server runs.",
                    "docs/opc-dcom-setup.md"));
            }
        }
        else if (!string.IsNullOrWhiteSpace(target.AppId))
        {
            findings.Add(new DaDiagnosticFinding(
                "clsid.appid",
                SeverityInfo,
                "AppID registered",
                $"AppID = {target.AppId} ({target.ViewName})."));
        }

        return BuildReport(snapshot, findings);
    }

    private static DaRegistrationReport BuildReport(DaRegistrationSnapshot snapshot, List<DaDiagnosticFinding> findings)
    {
        int failures = findings.Count(finding => finding.Severity == SeverityFail);
        int warnings = findings.Count(finding => finding.Severity == SeverityWarn);
        string verdict = failures > 0 ? SeverityFail : warnings > 0 ? SeverityWarn : SeverityOk;
        DaDiagnosticFinding? first = findings.FirstOrDefault(finding => finding.Severity == SeverityFail)
            ?? findings.FirstOrDefault(finding => finding.Severity == SeverityWarn);

        string summary = verdict switch
        {
            SeverityFail => $"{failures} problem{(failures == 1 ? string.Empty : "s")}: {first!.Title}",
            SeverityWarn => $"{warnings} warning{(warnings == 1 ? string.Empty : "s")}: {first!.Title}",
            _ => $"Registration OK for {snapshot.ProgId}"
        };

        return new DaRegistrationReport(snapshot.ProgId, snapshot.Host, verdict, summary, findings);
    }

    private static string DescribeRoot(string root) =>
        root.Length >= 2 && char.IsAsciiLetter(root[0]) && root[1] == ':'
            ? $"drive {root} is not available on this machine"
            : $"'{root}' is not reachable";

    private static string BuildDllRemediation(string progId)
    {
        string known = TryGetVendorHint(progId, out DaKnownServerHint? hint)
            ? $" Known {hint.Vendor} defect — see {hint.Reference}."
            : string.Empty;
        return "Repoint the registration to the installed DLL (or re-run regsvr32 from its install " +
               "folder on this machine) and re-check." + known;
    }

    /// <summary>
    /// A vendor whose historical defect shapes the remediation text. The 2026-09-30 PRW11709
    /// session: the 32-bit registration named Honeywell's build-machine path (F:\SetUpWork\...),
    /// so "PMD server whose DLL is not the installed one" is a known signature, not a mystery.
    /// </summary>
    private sealed record DaKnownServerHint(
        string Vendor,
        string ProgIdPrefix,
        string ExpectedPathFragment,
        string Reference);

    private static bool TryGetVendorHint(string progId, [NotNullWhen(true)] out DaKnownServerHint? hint)
    {
        if (progId.StartsWith("PMD.", StringComparison.OrdinalIgnoreCase))
        {
            hint = new DaKnownServerHint(
                "Honeywell PMD",
                "PMD.",
                @"Honeywell\PMD\PMD Data Access\PMD_OPCDataServer.dll",
                "docs/pmd-opc-da-field-notes-2026-09-30.md");
            return true;
        }

        hint = null;
        return false;
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<(RegistryHive Hive, RegistryView View, string Label)> RegistryViews()
    {
        yield return (RegistryHive.LocalMachine, RegistryView.Registry32, "HKLM 32-bit");
        yield return (RegistryHive.LocalMachine, RegistryView.Registry64, "HKLM 64-bit");
        yield return (RegistryHive.CurrentUser, RegistryView.Registry32, "HKCU 32-bit");
        yield return (RegistryHive.CurrentUser, RegistryView.Registry64, "HKCU 64-bit");
    }

    private static string HiveName(RegistryHive hive) =>
        hive == RegistryHive.CurrentUser ? "HKCU" : "HKLM";

    private static bool IsEffectiveView(string viewName, bool bridgeIs64Bit) =>
        viewName.EndsWith(bridgeIs64Bit ? "64-bit" : "32-bit", StringComparison.Ordinal);

    private static string NormalizeHost(string? host)
    {
        string trimmed = host?.Trim() ?? string.Empty;
        if (trimmed.Length == 0
            || string.Equals(trimmed, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, ".", StringComparison.Ordinal)
            || string.Equals(trimmed, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
        {
            return "localhost";
        }

        return trimmed;
    }

    private static bool IsLocalHost(string host) =>
        string.Equals(NormalizeHost(host), "localhost", StringComparison.OrdinalIgnoreCase);

    private static void AddBinaryCandidate(Dictionary<string, DaServerBinaryFacts> binaries, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return;
        }

        string path = ExtractPath(raw);
        if (path.Length == 0 || binaries.ContainsKey(path))
        {
            return;
        }

        binaries[path] = ProbeBinary(path);
    }

    private static string ExtractPath(string raw)
    {
        string value = Environment.ExpandEnvironmentVariables(raw.Trim());
        if (value.StartsWith('"'))
        {
            int end = value.IndexOf('"', 1);
            if (end > 1)
            {
                value = value[1..end];
            }
        }
        else if (!File.Exists(value))
        {
            // LocalServer32 may carry arguments ("server.exe -Embedding"); retry just the exe.
            int space = value.IndexOf(' ');
            if (space > 0)
            {
                value = value[..space];
            }
        }

        return value.Trim();
    }

    private static DaServerBinaryFacts ProbeBinary(string path)
    {
        string root = Path.GetPathRoot(path) ?? string.Empty;
        bool rootExists = root.Length == 0 || Directory.Exists(root);
        bool fileExists = File.Exists(path);
        string? company = null;
        string? product = null;
        string? version = null;

        if (fileExists)
        {
            try
            {
                FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
                company = info.CompanyName;
                product = info.ProductName;
                version = info.FileVersion;
            }
            catch
            {
                // Version metadata is a bonus, never a reason to fail the check.
            }
        }

        return new DaServerBinaryFacts(path, root, rootExists, fileExists, company, product, version);
    }
}
