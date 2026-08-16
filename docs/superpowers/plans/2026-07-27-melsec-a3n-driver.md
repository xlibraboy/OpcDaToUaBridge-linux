# Mitsubishi A3N PLC Driver Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add Mitsubishi A3NCPU as an inbound **Driver** source that speaks MELSEC A-compatible **1C Frame** over host RS-232 serial, maps D/M/X/Y devices, polls into the existing bridge pipeline, and write-throughs to the PLC.

**Architecture:** Approach A — extend `sources.json` / `DaSourceRuntimeSettings` with `SourceType=MelsecA3n` + serial fields; new `OpcBridge.Drivers.Melsec` assembly with pure 1C codec, transport seam, address parser, and `MelsecA3nClient : IDaClient`; `DaClientFactory` branches; `BridgeWorker` sessions/pollers/`WriteQueue` reused. UI product name is **Drivers**; code discriminator stays `SourceType`.

**Tech Stack:** .NET 8, `System.IO.Ports` (serial), ASP.NET minimal APIs, dashboard HTML/JS in `DashboardPage.cs`, xUnit under `tests/OpcBridge.LoadTest`.

**Spec:** `docs/superpowers/specs/2026-07-27-melsec-a3n-driver-design.md`

## Global Constraints

- Worktree: `/home/iwan/Development/Projects/OpcDaToUaBridge-linux/.worktrees/feature-mhi-plc-driver` on branch `feature/mhi-plc-driver`.
- Build gate: 0 Warning(s), 0 Error(s) via Docker SDK 8.0 when host SDK is unavailable:
  ```bash
  docker run --rm -v "$PWD":/workspace -w /workspace mcr.microsoft.com/dotnet/sdk:8.0 \
    dotnet test tests/OpcBridge.LoadTest/OpcBridge.LoadTest.csproj --filter FullyQualifiedName~<TestClass>
  ```
- Conventional commits: `feat(melsec): …`, `feat(dashboard): …`, `test(melsec): …`, `docs(melsec): …`.
- Do **not** rename `IDaClient`, mass-rename `DaItemId`, or rebrand `/api/da/*` path prefix in v1.
- Protocol: **MELSEC A-compatible 1C Frame / Dedicated Protocol Format 1** only. Not 3E/4E Ethernet MC.
- Transport v1: **Serial only**. `Transport=TcpTunnel` is rejected by API until a later plan.
- Devices v1: **D, M, X, Y** only. Hard-reject other device letters and AnN out-of-range addresses on map upsert.
- Address forms: `D100`, `M10`, `X20`, `Y0F`; bit-in-word `D100:8` or `D100.8`. X/Y octal; D/M decimal.
- Poll-only (no PLC subscriptions). Do not cast this client to `OpcDaClient`.
- Port exclusivity: two sources must not share the same `SerialPortName`.
- Existing OPC DA sources and outbound UA/MQTT/Influx behavior must keep working.
- YAGNI: no TCP tunnel impl, no tree browse, no TN/CN/B/W/R devices, no `ISourceClient` rename, no second BridgeWorker.
- Skip project-wide full-suite mid-task; run the named filter tests + `dotnet build` for the touched projects. Full solution build at Task 11.

---

## File map

| File | Responsibility |
|---|---|
| `src/OpcBridge.Core/SourceTypes.cs` | `OpcDa`, `MelsecA3n` constants |
| `src/OpcBridge.Drivers.Melsec/OpcBridge.Drivers.Melsec.csproj` | New net8.0 class library |
| `src/OpcBridge.Drivers.Melsec/Addressing/MelsecAddress.cs` | Parse/canonicalize/validate device ids |
| `src/OpcBridge.Drivers.Melsec/Protocol/Melsec1CFrameCodec.cs` | Pure 1C encode/decode + sum check |
| `src/OpcBridge.Drivers.Melsec/Protocol/Melsec1CCommands.cs` | Command builders/parsers for BR/BW/WR/WW |
| `src/OpcBridge.Drivers.Melsec/Transport/IMelsecTransport.cs` | Transact seam |
| `src/OpcBridge.Drivers.Melsec/Transport/SerialMelsecTransport.cs` | `SerialPort` implementation |
| `src/OpcBridge.Drivers.Melsec/MelsecA3nClientOptions.cs` | Options from runtime settings |
| `src/OpcBridge.Drivers.Melsec/MelsecA3nClient.cs` | `IDaClient` orchestration |
| `src/OpcBridge.App/DaRuntimeSettings.cs` | Extended record, DTO, migration, persist |
| `src/OpcBridge.App/DaServerConfigRequest.cs` | Polymorphic upsert request |
| `src/OpcBridge.App/DaClientFactory.cs` | Branch on `SourceType` |
| `src/OpcBridge.App/BridgeWorker.cs` | Connect skip rules per type |
| `src/OpcBridge.App/BridgeState.cs` | Status snapshot includes `sourceType` + endpoint summary |
| `src/OpcBridge.App/Program.cs` | Extended sources API; driver helper endpoints; mapping validation |
| `src/OpcBridge.App/DashboardPage.cs` | Drivers nav/view/wizard/form |
| `src/OpcBridge.App/HelpContent.cs` | Driver docs |
| `src/OpcBridge.App/OpcBridge.App.csproj` | ProjectReference to Drivers.Melsec |
| `OpcDaToUaBridge.sln` | Include new project |
| `context.md` | Architecture note |
| `tests/OpcBridge.LoadTest/*Melsec*.cs` | Unit/API tests |
| `tests/OpcBridge.LoadTest/OpcBridge.LoadTest.csproj` | Reference Drivers.Melsec |

---

### Task 1: Source model — `SourceType` + A3N serial fields + migration

**Files:**
- Create: `src/OpcBridge.Core/SourceTypes.cs`
- Modify: `src/OpcBridge.App/DaRuntimeSettings.cs` (`DaSourceRuntimeSettings`, `SourceConfigDto`, load/persist/normalize/`BuildInitialSources`/`ToOptions`)
- Modify: `src/OpcBridge.App/DaServerConfigRequest.cs`
- Modify: every `new DaSourceRuntimeSettings(` callsite in App + tests so the solution compiles
- Test: `tests/OpcBridge.LoadTest/MelsecSourceSettingsTests.cs`

**Interfaces:**
- Produces:
  - `SourceTypes.OpcDa = "OpcDa"`, `SourceTypes.MelsecA3n = "MelsecA3n"`
  - Extended record (keep name `DaSourceRuntimeSettings`):

```csharp
public sealed record DaSourceRuntimeSettings(
    string SourceId,
    string DisplayName,
    string SourceType,          // OpcDa | MelsecA3n
    string ProgId,
    string Host,
    string? RemoteUsername,
    string? RemotePassword,
    string? RemoteDomain,
    string Transport,           // Serial | TcpTunnel (v1 Serial only)
    string SerialPortName,      // e.g. /dev/ttyUSB0
    int BaudRate,
    int DataBits,
    string Parity,              // None | Odd | Even
    string StopBits,            // One | OnePointFive | Two
    string StationNo,           // ASCII 2-digit hex/decimal as stored string, default "00"
    string PcNo,                // default "FF"
    int TimeoutMs,
    int RetryCount,
    int MaxMappedTags,
    int UpdateRateMs);
```

  - Defaults when loading old JSON / empty fields:
    - `SourceType=OpcDa`, `Transport=Serial`, `SerialPortName=""`, `BaudRate=9600`, `DataBits=8`, `Parity=Odd`, `StopBits=One`, `StationNo=00`, `PcNo=FF`, `TimeoutMs=3000`, `RetryCount=2`, `MaxMappedTags=2000`
  - `SourceConfigMigration.FromDto(SourceConfigDto, defaultUpdateRate)` and `Normalize(...)` public/static for tests
  - `SourceConfigDto` gains matching properties (public for tests via InternalsVisibleTo already on App)
- Consumes: existing `sources.json` without `sourceType`

- [ ] **Step 1: Write failing tests**

```csharp
using OpcBridge.App;
using OpcBridge.Core;
using Xunit;

namespace OpcBridge.LoadTest;

public sealed class MelsecSourceSettingsTests
{
    [Fact]
    public void FromDto_MissingSourceType_DefaultsToOpcDa()
    {
        var source = SourceConfigMigration.FromDto(new SourceConfigDto
        {
            SourceId = "line1",
            ProgId = "Matrikon.OPC.Simulation.1",
            Host = "localhost",
            UpdateRateMs = 500
        }, defaultUpdateRate: 1000);

        Assert.Equal(SourceTypes.OpcDa, source.SourceType);
        Assert.Equal("Matrikon.OPC.Simulation.1", source.ProgId);
        Assert.Equal("", source.SerialPortName);
        Assert.Equal(2000, source.MaxMappedTags);
        Assert.Equal(9600, source.BaudRate);
        Assert.Equal("Odd", source.Parity);
    }

    [Fact]
    public void FromDto_MelsecA3n_MapsSerialFields()
    {
        var source = SourceConfigMigration.FromDto(new SourceConfigDto
        {
            SourceId = "a3n1",
            SourceType = "MelsecA3n",
            DisplayName = "Line A3N",
            SerialPortName = "/dev/ttyUSB0",
            BaudRate = 19200,
            DataBits = 8,
            Parity = "Odd",
            StopBits = "One",
            StationNo = "00",
            PcNo = "FF",
            TimeoutMs = 3000,
            RetryCount = 2,
            MaxMappedTags = 500,
            UpdateRateMs = 1000
        }, 1000);

        Assert.Equal(SourceTypes.MelsecA3n, source.SourceType);
        Assert.Equal("/dev/ttyUSB0", source.SerialPortName);
        Assert.Equal(19200, source.BaudRate);
        Assert.Equal("Serial", source.Transport);
        Assert.Equal(500, source.MaxMappedTags);
    }

    [Fact]
    public void Normalize_UnknownSourceType_BecomesOpcDa()
    {
        var source = SourceConfigMigration.Normalize(new DaSourceRuntimeSettings(
            "x", "X", "UnknownDriver", "", "localhost", null, null, null,
            "Serial", "", 9600, 8, "Odd", "One", "00", "FF", 3000, 2, 2000, 1000), 1000);
        Assert.Equal(SourceTypes.OpcDa, source.SourceType);
    }
}
```

- [ ] **Step 2: Run test — expect fail** (types missing)

```bash
docker run --rm -v "$PWD":/workspace -w /workspace mcr.microsoft.com/dotnet/sdk:8.0 \
  dotnet test tests/OpcBridge.LoadTest/OpcBridge.LoadTest.csproj --filter FullyQualifiedName~MelsecSourceSettingsTests
```

- [ ] **Step 3: Implement model**

1. Add `SourceTypes.cs` in Core with `OpcDa` and `MelsecA3n` only (do not add `OpcUa` on this branch unless already present).
2. Extend `DaSourceRuntimeSettings` + make `SourceConfigDto` public + add `SourceConfigMigration` (mirror UA-branch pattern but for Melsec fields).
3. `NormalizeSourceType`: empty → `OpcDa`; `MelsecA3n` case-insensitive → `MelsecA3n`; unknown → `OpcDa` (load resilience).
4. Serial defaults as in Interfaces; clamp `BaudRate` to >0 (else 9600); `DataBits` 7 or 8 (else 8); `TimeoutMs` default 3000 if ≤0; `RetryCount` default 2 if <0; `MaxMappedTags` default 2000 if ≤0.
5. Update `Persist`/`LoadFromDisk`/`BuildInitialSources`/`ToOptions` (DA `ToOptions` still only maps DA fields).
6. Fix **all** compile breaks: `Program.cs` upsert, import, discovery tests, DA link tests, etc. For DA-only constructors, pass `SourceTypes.OpcDa` and empty serial defaults.

Helper for tests/call sites (optional private static factory is fine):

```csharp
// Example DA source after change
new DaSourceRuntimeSettings(
    "s1", "S1", SourceTypes.OpcDa, "ProgId", "localhost",
    null, null, null,
    "Serial", "", 9600, 8, "Odd", "One", "00", "FF", 3000, 2, 2000, 1000);
```

- [ ] **Step 4: Run tests — expect pass**

- [ ] **Step 5: Commit**

```bash
git add src/OpcBridge.Core/SourceTypes.cs src/OpcBridge.App/DaRuntimeSettings.cs \
  src/OpcBridge.App/DaServerConfigRequest.cs tests/OpcBridge.LoadTest/MelsecSourceSettingsTests.cs
# plus every compile-fix callsite
git commit -m "feat(melsec): add SourceType and A3N serial fields to source model"
```

---

### Task 2: Address parser (D/M/X/Y + bit suffix)

**Files:**
- Create: `src/OpcBridge.Drivers.Melsec/OpcBridge.Drivers.Melsec.csproj`
- Create: `src/OpcBridge.Drivers.Melsec/Addressing/MelsecAddress.cs`
- Create: `src/OpcBridge.Drivers.Melsec/Addressing/MelsecDeviceKind.cs`
- Modify: `OpcDaToUaBridge.sln` (add project)
- Modify: `tests/OpcBridge.LoadTest/OpcBridge.LoadTest.csproj` (ProjectReference)
- Test: `tests/OpcBridge.LoadTest/MelsecAddressTests.cs`

**Interfaces:**
- Produces:

```csharp
namespace OpcBridge.Drivers.Melsec.Addressing;

public enum MelsecDeviceKind { D, M, X, Y }

public sealed record MelsecAddress(
    MelsecDeviceKind Device,
    int Number,          // numeric index in device radix (decimal for D/M, octal value for X/Y)
    int? BitIndex,       // 0-15 for D bit-in-word; null otherwise
    string Canonical);   // e.g. "D100", "D100:8", "X020", "M10"

public static class MelsecAddressParser
{
    // Returns true and address on success; error message on failure
    public static bool TryParse(string? input, out MelsecAddress address, out string error);

    // Canonicalize or throw/return false — used on map save
    public static string Canonicalize(string input);
}
```

AnN hard limits (reject with error string):
- D: 0–1023; bit 0–15 when present
- M: 0–2047; no bit-in-word on M in v1 (use M as bit device)
- X/Y: octal digits only; value 0–0x7FF
- Bit-in-word only on **D** (`D100:8` / `D100.8`)
- Case-insensitive device letter; canonicalize device upper-case; X/Y pad to at least 3 octal digits preferred (`X020`)

- [ ] **Step 1: Scaffold project + failing tests**

`OpcBridge.Drivers.Melsec.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RootNamespace>OpcBridge.Drivers.Melsec</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\OpcBridge.Core\OpcBridge.Core.csproj" />
    <ProjectReference Include="..\OpcBridge.Da\OpcBridge.Da.csproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="System.IO.Ports" Version="8.0.0" />
  </ItemGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="OpcBridge.LoadTest" />
  </ItemGroup>
</Project>
```

Add to solution under `src` folder. Reference from App and LoadTest.

Tests:

```csharp
using OpcBridge.Drivers.Melsec.Addressing;
using Xunit;

namespace OpcBridge.LoadTest;

public sealed class MelsecAddressTests
{
    [Theory]
    [InlineData("D100", "D100", MelsecDeviceKind.D, 100, null)]
    [InlineData("d100:8", "D100:8", MelsecDeviceKind.D, 100, 8)]
    [InlineData("D100.8", "D100:8", MelsecDeviceKind.D, 100, 8)]
    [InlineData("M10", "M10", MelsecDeviceKind.M, 10, null)]
    [InlineData("X20", "X020", MelsecDeviceKind.X, 16, null)] // 20 octal = 16
    [InlineData("Y0F", "Y00F", MelsecDeviceKind.Y, 15, null)]
    public void TryParse_Valid(string input, string canonical, MelsecDeviceKind kind, int number, int? bit)
    {
        Assert.True(MelsecAddressParser.TryParse(input, out var addr, out _));
        Assert.Equal(canonical, addr.Canonical);
        Assert.Equal(kind, addr.Device);
        Assert.Equal(number, addr.Number);
        Assert.Equal(bit, addr.BitIndex);
    }

    [Theory]
    [InlineData("")]
    [InlineData("W100")]
    [InlineData("D1024")]
    [InlineData("M2048")]
    [InlineData("X8")] // invalid octal digit
    [InlineData("D100:16")]
    [InlineData("M10:1")]
    public void TryParse_Invalid(string input)
    {
        Assert.False(MelsecAddressParser.TryParse(input, out _, out string error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }
}
```

- [ ] **Step 2: Run — expect fail**

- [ ] **Step 3: Implement parser**

- [ ] **Step 4: Run — expect pass**

- [ ] **Step 5: Commit**

```bash
git add src/OpcBridge.Drivers.Melsec OpcDaToUaBridge.sln \
  tests/OpcBridge.LoadTest/OpcBridge.LoadTest.csproj \
  tests/OpcBridge.LoadTest/MelsecAddressTests.cs \
  src/OpcBridge.App/OpcBridge.App.csproj
git commit -m "feat(melsec): add address parser and Drivers.Melsec project"
```

---

### Task 3: 1C Frame codec (sum check + BR/BW/WR/WW)

**Files:**
- Create: `src/OpcBridge.Drivers.Melsec/Protocol/Melsec1CFrameCodec.cs`
- Create: `src/OpcBridge.Drivers.Melsec/Protocol/Melsec1CCommands.cs`
- Create: `src/OpcBridge.Drivers.Melsec/Protocol/Melsec1CDeviceCodes.cs`
- Test: `tests/OpcBridge.LoadTest/Melsec1CFrameCodecTests.cs`

**Interfaces:**
- Produces pure functions (ASCII, Format 1):

```csharp
namespace OpcBridge.Drivers.Melsec.Protocol;

public static class Melsec1CFrameCodec
{
    public const byte Enq = 0x05;
    public const byte Ack = 0x06;
    public const byte Nak = 0x15;
    public const byte Stx = 0x02;
    public const byte Etx = 0x03;
    public const byte Cr  = 0x0D;

    // Sum of ASCII bytes of payload (characters after ENQ, before sum digits), low 8 bits as 2 uppercase hex chars
    public static string ComputeSumCheck(ReadOnlySpan<char> payloadWithoutEnqAndSumAndCr);

    // Build request: ENQ + body + sum + CR  (body includes station, pc, command, …)
    public static byte[] BuildRequest(string stationNo, string pcNo, string commandAndBody);

    // Parse response: detect NAK; for data responses parse STX…ETX sum; return payload chars or throw MelsecProtocolException
    public static string ParseDataResponse(ReadOnlySpan<byte> response);
    public static void EnsureAckOrThrow(ReadOnlySpan<byte> response);
}

public static class Melsec1CCommands
{
    // ACPU common commands (1C)
    // Bit batch read/write:  BR / BW
    // Word batch read/write: WR / WW
    public static string BuildBitReadBody(string deviceHead, int bitCount);
    public static string BuildBitWriteBody(string deviceHead, string bitData01);
    public static string BuildWordReadBody(string deviceHead, int wordCount);
    public static string BuildWordWriteBody(string deviceHead, IReadOnlyList<ushort> words);

    public static bool[] ParseBitReadData(string dataChars, int bitCount);
    public static ushort[] ParseWordReadData(string dataChars, int wordCount);
}

public static class Melsec1CDeviceCodes
{
    // Head device string as required by 1C body, e.g. "D0100", "M0010", "X0020"
    public static string FormatHead(MelsecAddress address);
}
```

**Locked framing rules for this plan (document in tests as golden strings):**

1. Request: `ENQ` + `Station(2)` + `PC(2)` + `Command(2)` + body + `SumCheck(2)` + `CR`
2. Sum check = lower 8 bits of sum of ASCII values of characters from first station digit through last body character (exclude ENQ, sum digits, CR), formatted as 2 uppercase hex digits.
3. Station default `"00"`, PC default `"FF"`.
4. Commands:
   - `BR` bit batch read — body: head device + bit count (4 hex digits)
   - `BW` bit batch write — body: head + bit count + `0`/`1` chars
   - `WR` word batch read — body: head + word count (4 hex digits)
   - `WW` word batch write — body: head + word count + data words as 4 hex digits each
5. Head device formatting (ACPU common style):
   - D: `D` + 4 decimal digits (`D0100`)
   - M: `M` + 4 decimal digits (`M0010`)
   - X/Y: `X`/`Y` + 3 octal digits zero-padded (`X020`)
6. Success data response typically `STX` + data + `ETX` + sum + `CR` (and/or leading ACK depending on unit — implement parser to accept:
   - optional leading `ACK`
   - then either pure `ACK` for write success, or `STX…ETX` data block with sum check
7. `NAK` → throw with clear message.

If a golden string from a trusted capture differs slightly (extra fields), adjust codec to match fixtures **and** update this plan’s golden strings in the same commit. Do not invent a second protocol dialect.

- [ ] **Step 1: Failing golden tests**

```csharp
using System.Text;
using OpcBridge.Drivers.Melsec.Protocol;
using Xunit;

namespace OpcBridge.LoadTest;

public sealed class Melsec1CFrameCodecTests
{
    [Fact]
    public void ComputeSumCheck_KnownVector()
    {
        // Sum of ASCII of "00FFBRD01000001" → low byte as hex (compute in test once implementation lands;
        // lock the expected string after first correct implementation using manual calculation).
        string payload = "00FFBRD01000001";
        int sum = 0;
        foreach (char c in payload) sum += c;
        string expected = (sum & 0xFF).ToString("X2");
        Assert.Equal(expected, Melsec1CFrameCodec.ComputeSumCheck(payload));
    }

    [Fact]
    public void BuildRequest_WordRead_D100_OneWord()
    {
        string body = Melsec1CCommands.BuildWordReadBody("D0100", 1);
        byte[] frame = Melsec1CFrameCodec.BuildRequest("00", "FF", "WR" + body.TrimStart()); 
        // Prefer: BuildRequest(station, pc, fullCommandBodyWithoutStationPc) that inserts station/pc itself.
        // Concrete preferred API:
        // byte[] frame = Melsec1CFrameCodec.BuildRequest("00", "FF", "WR", body);
        Assert.Equal(0x05, frame[0]);
        Assert.Equal(0x0D, frame[^1]);
        string ascii = Encoding.ASCII.GetString(frame);
        Assert.StartsWith("\u000500FFWR", ascii);
        Assert.Contains("D0100", ascii);
    }

    [Fact]
    public void ParseWordReadData_FourHexDigits()
    {
        ushort[] words = Melsec1CCommands.ParseWordReadData("00FF", 1);
        Assert.Equal((ushort)0x00FF, words[0]);
    }

    [Fact]
    public void ParseDataResponse_RejectsNak()
    {
        byte[] nak = new byte[] { 0x15, (byte)'0', (byte)'1', 0x0D };
        Assert.ThrowsAny<Exception>(() => Melsec1CFrameCodec.ParseDataResponse(nak));
    }
}
```

Refine `BuildRequest` signature during implementation to the cleaner form:

```csharp
public static byte[] BuildRequest(string stationNo, string pcNo, string command, string body);
// ENQ + station + pc + command + body + sum(station+pc+command+body) + CR
```

Update tests to match that single signature (do not leave dual APIs).

- [ ] **Step 2: Run — fail**

- [ ] **Step 3: Implement codec + commands + device head formatting**

- [ ] **Step 4: Run — pass**; fix golden expectations only with documented sum math

- [ ] **Step 5: Commit**

```bash
git commit -m "feat(melsec): implement 1C Frame codec and ACPU BR/BW/WR/WW builders"
```

---

### Task 4: Transport seam + mock + serial

**Files:**
- Create: `src/OpcBridge.Drivers.Melsec/Transport/IMelsecTransport.cs`
- Create: `src/OpcBridge.Drivers.Melsec/Transport/SerialMelsecTransport.cs`
- Create: `src/OpcBridge.Drivers.Melsec/Transport/ScriptedMelsecTransport.cs` (test helper; may live under tests if preferred — prefer production-internal + InternalsVisibleTo **or** test-local fake implementing the interface)
- Test: `tests/OpcBridge.LoadTest/MelsecTransportTests.cs` (scripted only)

**Interfaces:**

```csharp
namespace OpcBridge.Drivers.Melsec.Transport;

public interface IMelsecTransport : IAsyncDisposable
{
    bool IsOpen { get; }
    Task OpenAsync(CancellationToken cancellationToken);
    Task CloseAsync(CancellationToken cancellationToken);
    Task<byte[]> TransactAsync(ReadOnlyMemory<byte> request, TimeSpan timeout, CancellationToken cancellationToken);
}

public sealed class SerialMelsecTransport : IMelsecTransport
{
    public SerialMelsecTransport(string portName, int baudRate, int dataBits, Parity parity, StopBits stopBits) { … }
}
```

`TransactAsync` rules:
1. Write full request.
2. Read until CR or timeout (buffer).
3. Return raw bytes including control chars.
4. Thread-safe: one transaction at a time (`SemaphoreSlim(1,1)`).

Scripted fake (in tests):

```csharp
sealed class ScriptedMelsecTransport : IMelsecTransport
{
    public Queue<byte[]> Responses { get; } = new();
    public List<byte[]> Requests { get; } = new();
    public bool IsOpen { get; private set; }
    public Task OpenAsync(CancellationToken ct) { IsOpen = true; return Task.CompletedTask; }
    public Task CloseAsync(CancellationToken ct) { IsOpen = false; return Task.CompletedTask; }
    public Task<byte[]> TransactAsync(ReadOnlyMemory<byte> request, TimeSpan timeout, CancellationToken ct)
    {
        Requests.Add(request.ToArray());
        if (Responses.Count == 0) throw new TimeoutException("No scripted response");
        return Task.FromResult(Responses.Dequeue());
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
```

- [ ] **Step 1: Failing test** — scripted open + transact records request and returns response

- [ ] **Step 2: Run — fail**

- [ ] **Step 3: Implement interface + serial + keep scripted in tests**

- [ ] **Step 4: Run — pass**

- [ ] **Step 5: Commit**

```bash
git commit -m "feat(melsec): add serial transport seam and scripted test double"
```

---

### Task 5: `MelsecA3nClient` (`IDaClient`)

**Files:**
- Create: `src/OpcBridge.Drivers.Melsec/MelsecA3nClientOptions.cs`
- Create: `src/OpcBridge.Drivers.Melsec/MelsecA3nClient.cs`
- Test: `tests/OpcBridge.LoadTest/MelsecA3nClientTests.cs`

**Interfaces:**

```csharp
namespace OpcBridge.Drivers.Melsec;

public sealed class MelsecA3nClientOptions
{
    public string SourceId { get; init; } = "default";
    public string SerialPortName { get; init; } = "";
    public int BaudRate { get; init; } = 9600;
    public int DataBits { get; init; } = 8;
    public string Parity { get; init; } = "Odd";
    public string StopBits { get; init; } = "One";
    public string StationNo { get; init; } = "00";
    public string PcNo { get; init; } = "FF";
    public int TimeoutMs { get; init; } = 3000;
    public int RetryCount { get; init; } = 2;
}

public sealed class MelsecA3nClient : IDaClient
{
    // Production ctor builds SerialMelsecTransport from options
    public MelsecA3nClient(MelsecA3nClientOptions options, ILogger? logger = null);

    // Test ctor
    public MelsecA3nClient(MelsecA3nClientOptions options, IMelsecTransport transport, ILogger? logger = null);

    public Task ConnectAsync(CancellationToken cancellationToken);
    public Task<IReadOnlyList<BridgeValue>> ReadAsync(IReadOnlyList<TagMapping> mappings, CancellationToken cancellationToken);
    public Task<bool> WriteAsync(string daItemId, object? value, CancellationToken cancellationToken);
    public bool TryGetTagMetadata(string daItemId, out short? canonicalDataType, out int? accessRights);
    public ValueTask DisposeAsync();
}
```

Behavior:
- `ConnectAsync`: `OpenAsync` transport; optional probe = word read of `D0` (1 word) with retries; on failure close and throw.
- `ReadAsync`: parse each mapping `DaItemId`; skip invalid (log + Bad quality value); batch consecutive same-kind pure bits into BR; consecutive pure D words into WR; bit-in-word D as single-word WR then extract bit; map quality Good=0xC0.
- Batch caps: words ≤ 64 per WR; bits ≤ 256 per BR (split larger).
- `WriteAsync`: M/X/Y bit → BW 1 point; D word → WW 1 word; D bit-in-word → WR + modify + WW; coerce bool/int/short/string numeric.
- `TryGetTagMetadata`: Boolean for bit devices / bit-in-word; Int16 for D word; access ReadWrite.
- Retries: on timeout/protocol error, retry up to `RetryCount`.

- [ ] **Step 1: Failing tests with scripted transport**

```csharp
[Fact]
public async Task ReadAsync_Word_ReturnsBridgeValue()
{
    var transport = new ScriptedMelsecTransport();
    // Script ACK+STX data for one word 0x0012 — match whatever ParseDataResponse expects
    // e.g. build using codec helpers in reverse from known data "0012"
    transport.Responses.Enqueue(BuildStxDataResponse("0012"));

    var client = new MelsecA3nClient(new MelsecA3nClientOptions { SourceId = "a3n" }, transport);
    await client.ConnectAsync(CancellationToken.None); // may consume a probe response — enqueue probe first

    var values = await client.ReadAsync(new[]
    {
        new TagMapping { SourceId = "a3n", DaItemId = "D100", Enabled = true, Mode = TagMode.Source }
    }, CancellationToken.None);

    Assert.Single(values);
    Assert.True(values[0].IsGood);
    Assert.Equal("D100", values[0].DaItemId);
}

// Helper BuildStxDataResponse in test file using Melsec1CFrameCodec.ComputeSumCheck
```

Also test Write bit and invalid address → false / bad.

**Important:** If `ConnectAsync` probes, enqueue probe response before read response.

- [ ] **Step 2: Run — fail**

- [ ] **Step 3: Implement client**

- [ ] **Step 4: Run — pass**

- [ ] **Step 5: Commit**

```bash
git commit -m "feat(melsec): implement MelsecA3nClient IDaClient with poll read/write"
```

---

### Task 6: Factory + BridgeWorker connect rules

**Files:**
- Modify: `src/OpcBridge.App/DaClientFactory.cs`
- Modify: `src/OpcBridge.App/BridgeWorker.cs` (`ReconfigureSessionsAsync` skip rules)
- Modify: `src/OpcBridge.App/OpcBridge.App.csproj` (ensure Drivers.Melsec reference)
- Test: `tests/OpcBridge.LoadTest/MelsecFactoryTests.cs`

**Interfaces:**
- Produces:

```csharp
public sealed class DaClientFactory
{
    public IDaClient Create(DaRuntimeSettingsSnapshot settings, DaSourceRuntimeSettings source)
    {
        if (string.Equals(source.SourceType, SourceTypes.MelsecA3n, StringComparison.OrdinalIgnoreCase))
        {
            return new MelsecA3nClient(ToMelsecOptions(source));
        }

        return new OpcDaClient(source.ToOptions(settings.UseSubscriptions));
    }

    private static MelsecA3nClientOptions ToMelsecOptions(DaSourceRuntimeSettings source) => new()
    {
        SourceId = source.SourceId,
        SerialPortName = source.SerialPortName,
        BaudRate = source.BaudRate,
        DataBits = source.DataBits,
        Parity = source.Parity,
        StopBits = source.StopBits,
        StationNo = source.StationNo,
        PcNo = source.PcNo,
        TimeoutMs = source.TimeoutMs,
        RetryCount = source.RetryCount
    };
}
```

BridgeWorker `ReconfigureSessionsAsync` replace ProgId-only skip:

```csharp
if (string.Equals(source.SourceType, SourceTypes.MelsecA3n, StringComparison.OrdinalIgnoreCase))
{
    if (string.IsNullOrWhiteSpace(source.SerialPortName))
    {
        bridge_state_.SetSourceConnectionState(source.SourceId, "Disconnected");
        bridge_state_.SetSourceError(source.SourceId,
            new InvalidOperationException("Serial port is empty — enter a COM port (e.g. /dev/ttyUSB0)."));
        logger_.LogWarning("Source {SourceId} has no serial port, skipping connection", source.SourceId);
        continue;
    }
}
else if (string.IsNullOrWhiteSpace(source.ProgId))
{
    // existing DA empty ProgId path
    ...
}
```

Keep `if (client is OpcDaClient opcDa)` subscription hook as-is (A3N is poll-only).

- [ ] **Step 1: Failing factory test**

```csharp
[Fact]
public void Create_MelsecA3n_ReturnsMelsecA3nClient()
{
    var factory = new DaClientFactory();
    var source = SourceConfigMigration.FromDto(new SourceConfigDto
    {
        SourceId = "a3n",
        SourceType = SourceTypes.MelsecA3n,
        SerialPortName = "/dev/ttyUSB0",
        UpdateRateMs = 1000
    }, 1000);
    var settings = new DaRuntimeSettingsSnapshot(1000, true, new[] { source }, 1);
    IDaClient client = factory.Create(settings, source);
    Assert.IsType<MelsecA3nClient>(client);
}
```

- [ ] **Step 2: Run — fail**

- [ ] **Step 3: Implement factory + BridgeWorker rules**

- [ ] **Step 4: Run — pass** + `dotnet build OpcDaToUaBridge.sln`

- [ ] **Step 5: Commit**

```bash
git commit -m "feat(melsec): factory branch and BridgeWorker serial connect rules"
```

---

### Task 7: HTTP API — polymorphic sources + driver helpers + mapping validation

**Files:**
- Modify: `src/OpcBridge.App/DaServerConfigRequest.cs`
- Modify: `src/OpcBridge.App/Program.cs` (GET/POST sources, import/export, mappings add/update, new endpoints)
- Modify: `src/OpcBridge.App/BridgeState.cs` (`DaSourceStatusSnapshot` add `SourceType`, `EndpointSummary`)
- Test: `tests/OpcBridge.LoadTest/MelsecApiTests.cs`

**Interfaces / request shapes:**

```csharp
public sealed record DaServerConfigRequest(
    string SourceId,
    string? DisplayName,
    string? SourceType = null,       // default OpcDa
    string? ProgId = null,
    string? Host = null,
    string? RemoteUsername = null,
    string? RemotePassword = null,
    string? RemoteDomain = null,
    string? Transport = null,
    string? SerialPortName = null,
    int? BaudRate = null,
    int? DataBits = null,
    string? Parity = null,
    string? StopBits = null,
    string? StationNo = null,
    string? PcNo = null,
    int? TimeoutMs = null,
    int? RetryCount = null,
    int? MaxMappedTags = null,
    int UpdateRateMs = 0);

public sealed record MelsecTestConnectionRequest(
    string? SourceId = null,
    string? SerialPortName = null,
    int? BaudRate = null,
    int? DataBits = null,
    string? Parity = null,
    string? StopBits = null,
    string? StationNo = null,
    string? PcNo = null,
    int? TimeoutMs = null);

public sealed record MelsecParseAddressRequest(string Address);
```

**GET `/api/da/sources`** — include for each source:

```json
{
  "sourceId": "...",
  "displayName": "...",
  "sourceType": "MelsecA3n",
  "progId": "",
  "host": "",
  "updateRateMs": 1000,
  "transport": "Serial",
  "serialPortName": "/dev/ttyUSB0",
  "baudRate": 9600,
  "dataBits": 8,
  "parity": "Odd",
  "stopBits": "One",
  "stationNo": "00",
  "pcNo": "FF",
  "timeoutMs": 3000,
  "retryCount": 2,
  "maxMappedTags": 2000
}
```

**POST `/api/da/sources` validation:**
1. `SourceId` required.
2. Normalize `SourceType` via migration; if request type is neither OpcDa nor MelsecA3n → 400.
3. If `MelsecA3n`:
   - `SerialPortName` required non-empty
   - `Transport` must be empty or `Serial` (reject `TcpTunnel` with 400 message)
   - Parity/StopBits must be in allowed sets
   - Reject if another source already uses same `SerialPortName` (case-sensitive on Linux paths)
4. If `OpcDa`: keep existing ProgId/Host behavior (ProgId may be empty until user fills — same as today).

**POST `/api/drivers/melsec-a3n/test-connection`:**
- Resolve settings from body or existing `sourceId`
- Create short-lived `MelsecA3nClient`, `ConnectAsync` with timeout, dispose
- Return `{ ok: true }` or `{ ok: false, error: "..." }`
- Never leave port open on exception (`await using`)

**POST `/api/drivers/melsec-a3n/parse-address`:**
- Return `{ ok, canonical, error }`

**Mappings** (`/api/mappings/add`, `bulk-add`, `update`):
- After building candidate tags, if source is `MelsecA3n`:
  - `MelsecAddressParser.TryParse` each `DaItemId` — 400 on failure
  - Replace with `Canonical`
  - Enforce `MaxMappedTags` for that source count after add → 400 when exceeded

**Export/import:** include new fields; import uses `SourceConfigMigration.FromDto`.

**Status snapshot:**

```csharp
public sealed record DaSourceStatusSnapshot(
    string SourceId,
    string DisplayName,
    string Host,
    string ProgId,
    int UpdateRateMs,
    string ConnectionState,
    DateTime? LastReadUtc,
    string? LastError,
    int LastReadCount,
    long TotalReads,
    double? LastPollMs,
    string SourceType = "OpcDa",
    string EndpointSummary = ""); // e.g. "/dev/ttyUSB0@9600" or "host/ProgId"
```

Update `Configure`/`UpdateSources` to fill `SourceType` + summary (`Melsec` → `SerialPortName@BaudRate`, DA → `Host/ProgId`).

- [ ] **Step 1: API tests with `TestAppHandle` / existing web app pattern** (follow `DaLinkApiTests` / `BridgeAppApiTests` style)

Cover: POST Melsec source; GET returns fields; duplicate serial port 400; parse-address; mapping invalid address 400.

- [ ] **Step 2: Run — fail**

- [ ] **Step 3: Implement Program + request + status fields**

- [ ] **Step 4: Run — pass**

- [ ] **Step 5: Commit**

```bash
git commit -m "feat(melsec): sources API, test-connection, and address validation"
```

---

### Task 8: Dashboard — Drivers list + wizard + form

**Files:**
- Modify: `src/OpcBridge.App/DashboardPage.cs` (nav, view HTML, JS routes, render/save)
- Test: extend `tests/OpcBridge.LoadTest/DashboardPageTests.cs` if it asserts nav/routes

**UI contract (from spec):**

Nav under Sources group:

```html
<button class="tabbtn" data-tab="drivers" data-route="connectivity/drivers"
  onclick="navigate('connectivity/drivers')">Drivers</button>
```

Add `view-drivers` with:
- List of sources where `sourceType === 'MelsecA3n'` (and future drivers)
- Form fields with ids: `drvA3nSourceId`, `drvA3nName`, `drvA3nPort`, `drvA3nBaud`, `drvA3nDataBits`, `drvA3nParity`, `drvA3nStopBits`, `drvA3nStation`, `drvA3nPc`, `drvA3nTimeout`, `drvA3nRetry`, `drvA3nRate`, `drvA3nMaxTags`
- Buttons: Save, Reset, New, Remove, Test connection
- Wizard modal `wzDrv*` steps: type → identity → serial → defaults → review

JS:
- `ROUTE_TO_TAB['connectivity/drivers'] = 'drivers'`
- `loadSources` already used; `renderDrivers()` filters Melsec
- `saveDriverSource()` POST `/api/da/sources` with `sourceType: 'MelsecA3n'` and serial fields
- `testDriverConnection()` POST `/api/drivers/melsec-a3n/test-connection`
- Type badges on sources list/diagnostics when `sourceType` present (`DA` / `A3N`)
- Tag map: when selected source is Melsec, hide DA tree browse or show address-entry hint; free-form `DaItemId` still works via existing map form — add small hint text “Device address e.g. D100, M10, X20, D100:8”

Keep OPC DA page unchanged for DA sources.

- [ ] **Step 1: Add failing dashboard test** if suite already snapshots HTML for routes — assert `connectivity/drivers` and `drvA3nPort` exist in `DashboardPage.Html`

```csharp
[Fact]
public void Html_ContainsDriversRouteAndA3nControls()
{
    Assert.Contains("connectivity/drivers", DashboardPage.Html);
    Assert.Contains("drvA3nPort", DashboardPage.Html);
    Assert.Contains("sourceType", DashboardPage.Html); // JS save payload
}
```

- [ ] **Step 2: Run — fail**

- [ ] **Step 3: Implement nav/view/JS**

- [ ] **Step 4: Run dashboard tests — pass**

- [ ] **Step 5: Commit**

```bash
git commit -m "feat(dashboard): add Drivers page for Mitsubishi A3N"
```

---

### Task 9: Help + context architecture note

**Files:**
- Modify: `src/OpcBridge.App/HelpContent.cs`
- Modify: `context.md`
- Test: `HelpContentTests` if it greps sections — update expectations

Help section content (markdown):

```markdown
## PLC Drivers (Mitsubishi A3N)

The bridge can poll a Mitsubishi **A3NCPU** over **RS-232** using MELSEC **A-compatible 1C Frame**
(Dedicated Protocol / Format 1).

1. Open **Connectivity → Drivers** and add a Mitsubishi A3N driver.
2. Set the serial port (e.g. `/dev/ttyUSB0`), baud **9600**, **8 data bits**, **odd parity**, **1 stop bit** (match the PLC).
3. Map tags with device addresses: `D100`, `M10`, `X20`, `Y0F`, bit-in-word `D100:8`.
4. Writes on writeable tags go back to the PLC. Bit-in-word uses read-modify-write.

This is separate from **OPC DA** sources and from this process’s **OPC UA server** endpoint.
```

`context.md` bullet:

```markdown
- Inbound PLC drivers: `SourceType=MelsecA3n` → `MelsecA3nClient` (1C Frame serial) via `DaClientFactory`; UI under Connectivity → Drivers.
```

- [ ] **Step 1: Update help/tests**

- [ ] **Step 2: Run HelpContentTests**

- [ ] **Step 3: Commit**

```bash
git commit -m "docs(melsec): help and architecture notes for A3N driver"
```

---

### Task 10: Export/import + diagnostics polish

**Files:**
- Modify: `src/OpcBridge.App/Program.cs` export/import blocks (if not fully done in Task 7)
- Modify: `src/OpcBridge.App/DashboardPage.cs` diagnostics renderer to show `sourceType` + serial summary
- Test: import round-trip unit test in `MelsecApiTests` or settings tests

Ensure:
- Export JSON sources include Melsec fields (no secrets beyond existing DA password policy; serial has none)
- Import reconstructs `SourceType` and serial fields via migration
- Diagnostics cards show badge + port summary for A3N

- [ ] **Step 1: Test export/import fields present**

- [ ] **Step 2: Implement remaining gaps**

- [ ] **Step 3: Commit**

```bash
git commit -m "feat(melsec): export/import and diagnostics for A3N sources"
```

---

### Task 11: Solution verification

**Files:** none new

- [ ] **Step 1: Full build**

```bash
docker run --rm -v "$PWD":/workspace -w /workspace mcr.microsoft.com/dotnet/sdk:8.0 \
  dotnet build OpcDaToUaBridge.sln -c Release
```

Expected: 0 Warning(s), 0 Error(s) (or only pre-existing warnings already on main — do not add new ones).

- [ ] **Step 2: Melsec-focused tests**

```bash
docker run --rm -v "$PWD":/workspace -w /workspace mcr.microsoft.com/dotnet/sdk:8.0 \
  dotnet test tests/OpcBridge.LoadTest/OpcBridge.LoadTest.csproj \
  --filter "FullyQualifiedName~Melsec" -c Release
```

- [ ] **Step 3: Regression smoke** (DA discovery / dashboard / mapping tests that touch `DaSourceRuntimeSettings`)

```bash
docker run --rm -v "$PWD":/workspace -w /workspace mcr.microsoft.com/dotnet/sdk:8.0 \
  dotnet test tests/OpcBridge.LoadTest/OpcBridge.LoadTest.csproj \
  --filter "FullyQualifiedName~BridgeAppDiscoveryTests|FullyQualifiedName~DashboardPageTests|FullyQualifiedName~Melsec" -c Release
```

- [ ] **Step 4: Fix any failures**

- [ ] **Step 5: Final commit if fixes needed; otherwise done**

```bash
git status -sb
git log --oneline -15
```

---

## Spec coverage checklist

| Spec requirement | Task |
|---|---|
| SourceType + serial config + sources.json | 1, 7 |
| 1C Frame protocol codec | 3 |
| Serial transport + future seam | 4 |
| D/M/X/Y address + bit suffix | 2, 7 |
| MelsecA3nClient IDaClient read/write | 5 |
| Factory + BridgeWorker | 6 |
| Poll path reuse (no new worker) | 5–6 (existing pollers) |
| WriteQueue write-through | 5–6 (existing consumer) |
| Drivers UI list/wizard | 8 |
| test-connection + parse-address APIs | 7 |
| MaxMappedTags + port conflict | 7 |
| Export/import | 7, 10 |
| Help/docs | 9 |
| Unit tests without hardware | 2–5, 7 |
| Non-goals (no TCP tunnel, no tree browse, no 3E) | enforced by validation + omitted features |

## Placeholder / consistency self-review

- No TBD steps; 1C golden strings are algorithmically defined (sum of ASCII) with tests locking values.
- `BuildRequest(station, pc, command, body)` is the single codec entry used by client and tests.
- `SourceTypes.MelsecA3n` spelling consistent across model, factory, API, UI.
- Record field order from Task 1 is the only constructor shape; all later tasks use migration helpers or full argument lists matching that order.
