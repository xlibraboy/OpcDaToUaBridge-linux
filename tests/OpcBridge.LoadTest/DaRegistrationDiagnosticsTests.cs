using OpcBridge.Da;
using Xunit;
using BinaryFacts = OpcBridge.Da.DaRegistrationDiagnostics.DaServerBinaryFacts;
using Finding = OpcBridge.Da.DaRegistrationDiagnostics.DaDiagnosticFinding;
using Report = OpcBridge.Da.DaRegistrationDiagnostics.DaRegistrationReport;
using Snapshot = OpcBridge.Da.DaRegistrationDiagnostics.DaRegistrationSnapshot;
using ViewFinding = OpcBridge.Da.DaRegistrationDiagnostics.DaRegistryViewFinding;

namespace OpcBridge.LoadTest;

/// <summary>
/// The registration analysis is pure over a captured snapshot, so every defect class from the
/// PRW11709 session (dead build-machine path, wrong bitness view, incomplete registration) is
/// pinned here with synthetic data — the registry capture itself is Windows-only and exercised
/// on the host, not in this suite.
/// </summary>
public sealed class DaRegistrationDiagnosticsTests
{
    private const string PmdProgId = "PMD.DDT_OPCDataServer.1";
    private const string PmdClsid = "{A2152446-BCD9-408C-9C1C-CC5A11FFECC9}";
    private const string OtherClsid = "{11111111-1111-1111-1111-111111111111}";
    private const string InstalledPmdPath =
        @"C:\Program Files (x86)\Honeywell\PMD\PMD Data Access\PMD_OPCDataServer.dll";
    private const string BuildMachinePath =
        @"F:\SetUpWork\PMD\R800\PMD Data Access\PMD_OPCDataServer\ReleaseUMinDependency\PMD_OPCDataServer.dll";

    [Fact]
    public void Analyze_ProgIdNotRegisteredInAnyView_Fails()
    {
        Snapshot snapshot = LocalSnapshot(false, new[]
        {
            MakeView("HKLM 32-bit", null),
            MakeView("HKLM 64-bit", null),
            MakeView("HKCU 32-bit", null),
            MakeView("HKCU 64-bit", null)
        });

        Report report = DaRegistrationDiagnostics.Analyze(snapshot);

        Assert.Equal(DaRegistrationDiagnostics.SeverityFail, report.Verdict);
        Finding finding = Assert.Single(report.Findings.Where(f => f.Id == "progid.views"));
        Assert.Equal(DaRegistrationDiagnostics.SeverityFail, finding.Severity);
        Assert.Contains("not registered", report.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Analyze_RegisteredOnlyIn64BitView_WhenBridgeIs32Bit_Fails()
    {
        Snapshot snapshot = LocalSnapshot(
            false,
            new[]
            {
                MakeView("HKLM 32-bit", null),
                MakeView("HKLM 64-bit", PmdClsid, inproc: InstalledPmdPath),
                MakeView("HKCU 32-bit", null),
                MakeView("HKCU 64-bit", null)
            },
            new[] { MakeBinary(InstalledPmdPath, @"C:\", rootExists: true, fileExists: true) });

        Report report = DaRegistrationDiagnostics.Analyze(snapshot);

        Assert.Equal(DaRegistrationDiagnostics.SeverityFail, report.Verdict);
        Finding finding = Assert.Single(report.Findings.Where(f => f.Id == "progid.views"));
        Assert.Contains("64-bit", finding.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void Analyze_RegisteredOnlyIn64BitView_WhenBridgeIs64Bit_Ok()
    {
        Snapshot snapshot = LocalSnapshot(
            true,
            new[]
            {
                MakeView("HKLM 32-bit", null),
                MakeView("HKLM 64-bit", PmdClsid, inproc: InstalledPmdPath),
                MakeView("HKCU 32-bit", null),
                MakeView("HKCU 64-bit", null)
            },
            new[] { MakeBinary(InstalledPmdPath, @"C:\", rootExists: true, fileExists: true) });

        Report report = DaRegistrationDiagnostics.Analyze(snapshot);

        Assert.Equal(DaRegistrationDiagnostics.SeverityOk, report.Verdict);
    }

    [Fact]
    public void Analyze_DeadBuildMachinePath_FailsWithDriveDetail()
    {
        Snapshot snapshot = LocalSnapshot(
            false,
            new[]
            {
                MakeView("HKLM 32-bit", PmdClsid, inproc: BuildMachinePath),
                MakeView("HKLM 64-bit", null),
                MakeView("HKCU 32-bit", null),
                MakeView("HKCU 64-bit", null)
            },
            new[] { MakeBinary(BuildMachinePath, @"F:\", rootExists: false, fileExists: false) });

        Report report = DaRegistrationDiagnostics.Analyze(snapshot);

        Assert.Equal(DaRegistrationDiagnostics.SeverityFail, report.Verdict);
        Finding finding = Assert.Single(report.Findings.Where(f => f.Id == "clsid.dll"));
        Assert.Equal(DaRegistrationDiagnostics.SeverityFail, finding.Severity);
        Assert.Contains(@"F:\", finding.Detail, StringComparison.Ordinal);
        Assert.Contains("not available", finding.Detail, StringComparison.Ordinal);
        Assert.Contains("pmd-opc-da-field-notes", finding.Remediation!, StringComparison.Ordinal);
        Assert.StartsWith("1 problem", report.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Analyze_MissingDllFile_FailsWithReregisterRemediation()
    {
        Snapshot snapshot = LocalSnapshot(
            false,
            new[]
            {
                MakeView("HKLM 32-bit", PmdClsid, inproc: InstalledPmdPath),
                MakeView("HKLM 64-bit", null),
                MakeView("HKCU 32-bit", null),
                MakeView("HKCU 64-bit", null)
            },
            new[] { MakeBinary(InstalledPmdPath, @"C:\", rootExists: true, fileExists: false) });

        Report report = DaRegistrationDiagnostics.Analyze(snapshot);

        Assert.Equal(DaRegistrationDiagnostics.SeverityFail, report.Verdict);
        Finding finding = Assert.Single(report.Findings.Where(f => f.Id == "clsid.dll"));
        Assert.Contains("not found", finding.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("regsvr32", finding.Remediation!, StringComparison.Ordinal);
    }

    [Fact]
    public void Analyze_NoServerEntry_Fails()
    {
        Snapshot snapshot = LocalSnapshot(false, new[]
        {
            MakeView("HKLM 32-bit", PmdClsid, inproc: null, local: null),
            MakeView("HKLM 64-bit", null),
            MakeView("HKCU 32-bit", null),
            MakeView("HKCU 64-bit", null)
        });

        Report report = DaRegistrationDiagnostics.Analyze(snapshot);

        Assert.Equal(DaRegistrationDiagnostics.SeverityFail, report.Verdict);
        Finding finding = Assert.Single(report.Findings.Where(f => f.Id == "clsid.class"));
        Assert.Contains("InprocServer32", finding.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void Analyze_MissingDiscoveryKeys_Warns()
    {
        Snapshot snapshot = LocalSnapshot(
            false,
            new[]
            {
                MakeView("HKLM 32-bit", PmdClsid, inproc: InstalledPmdPath, category: false),
                MakeView("HKLM 64-bit", null),
                MakeView("HKCU 32-bit", null),
                MakeView("HKCU 64-bit", null)
            },
            new[] { MakeBinary(InstalledPmdPath, @"C:\", rootExists: true, fileExists: true) });

        Report report = DaRegistrationDiagnostics.Analyze(snapshot);

        Assert.Equal(DaRegistrationDiagnostics.SeverityWarn, report.Verdict);
        Finding finding = Assert.Single(report.Findings.Where(f => f.Id == "clsid.discovery"));
        Assert.Equal(DaRegistrationDiagnostics.SeverityWarn, finding.Severity);
        Assert.Contains("warning", report.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Analyze_ViewsMismatch_Warns()
    {
        Snapshot snapshot = LocalSnapshot(
            false,
            new[]
            {
                MakeView("HKLM 32-bit", PmdClsid, inproc: InstalledPmdPath),
                MakeView("HKLM 64-bit", null),
                MakeView("HKCU 32-bit", OtherClsid, inproc: InstalledPmdPath),
                MakeView("HKCU 64-bit", null)
            },
            new[] { MakeBinary(InstalledPmdPath, @"C:\", rootExists: true, fileExists: true) });

        Report report = DaRegistrationDiagnostics.Analyze(snapshot);

        Finding finding = Assert.Single(report.Findings.Where(f => f.Id == "progid.views.mismatch"));
        Assert.Equal(DaRegistrationDiagnostics.SeverityWarn, finding.Severity);
    }

    [Fact]
    public void Analyze_RemoteHostWithoutAppId_WarnsAboutSurrogate()
    {
        Snapshot snapshot = MakeSnapshot(
            false,
            "10.20.30.40",
            new[]
            {
                MakeView("HKLM 32-bit", PmdClsid, inproc: InstalledPmdPath),
                MakeView("HKLM 64-bit", null),
                MakeView("HKCU 32-bit", null),
                MakeView("HKCU 64-bit", null)
            },
            new[] { MakeBinary(InstalledPmdPath, @"C:\", rootExists: true, fileExists: true) });

        Report report = DaRegistrationDiagnostics.Analyze(snapshot);

        Assert.Equal(DaRegistrationDiagnostics.SeverityWarn, report.Verdict);
        Assert.Single(report.Findings.Where(f => f.Id == "host.remote" && f.Severity == DaRegistrationDiagnostics.SeverityInfo));
        Assert.Single(report.Findings.Where(f => f.Id == "clsid.appid" && f.Severity == DaRegistrationDiagnostics.SeverityWarn));
    }

    [Fact]
    public void Analyze_HealthyPmdRegistration_Ok()
    {
        Snapshot snapshot = LocalSnapshot(
            false,
            new[]
            {
                MakeView("HKLM 32-bit", PmdClsid, inproc: InstalledPmdPath, appId: "{22222222-2222-2222-2222-222222222222}"),
                MakeView("HKLM 64-bit", null),
                MakeView("HKCU 32-bit", null),
                MakeView("HKCU 64-bit", null)
            },
            new[] { MakeBinary(InstalledPmdPath, @"C:\", rootExists: true, fileExists: true) });

        Report report = DaRegistrationDiagnostics.Analyze(snapshot);

        Assert.Equal(DaRegistrationDiagnostics.SeverityOk, report.Verdict);
        Assert.Contains("Registration OK", report.Summary, StringComparison.Ordinal);
        Finding vendor = Assert.Single(report.Findings.Where(f => f.Id == "vendor.known-path"));
        Assert.Equal(DaRegistrationDiagnostics.SeverityOk, vendor.Severity);
        Assert.DoesNotContain(report.Findings, f => f.Severity == DaRegistrationDiagnostics.SeverityFail);
    }

    private static Snapshot LocalSnapshot(bool bridgeIs64Bit, ViewFinding[] views, BinaryFacts[]? binaries = null) =>
        MakeSnapshot(bridgeIs64Bit, "localhost", views, binaries ?? Array.Empty<BinaryFacts>());

    private static Snapshot MakeSnapshot(bool bridgeIs64Bit, string host, ViewFinding[] views, BinaryFacts[] binaries) =>
        new(
            PmdProgId,
            host,
            bridgeIs64Bit,
            views,
            binaries.ToDictionary(binary => binary.Path, StringComparer.OrdinalIgnoreCase));

    private static ViewFinding MakeView(
        string name,
        string? clsid,
        bool clsidKeyPresent = true,
        string? inproc = null,
        string? local = null,
        string? appId = null,
        bool category = true,
        bool reverseProgId = true) =>
        new(name, @"HKLM\SOFTWARE\Classes", clsid, clsidKeyPresent, null, inproc, "Apartment", local, appId, category, reverseProgId);

    private static BinaryFacts MakeBinary(string path, string root, bool rootExists, bool fileExists) =>
        new(path, root, rootExists, fileExists, "Honeywell", "PMD Data Access", "8.0.0.0");
}
