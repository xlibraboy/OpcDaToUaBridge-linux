# HMI Trends via Bridge Influx Proxy Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add `GET /api/hmi/trends` (bridge proxy to Influx history) and an Avalonia faceplate sparkline that loads the last hour of points without ever holding Influx credentials.

**Architecture:** Stub-friendly `IInfluxTrendQuery` registered in `OpcBridge.App`; endpoint validates params and returns `HmiTrendResponse`. HMI calls only the bridge BaseUrl. Real Flux query replaces the unavailable stub after `feature/influxdb-access` merges (optional final task / rebase work). Writer path is out of scope on this branch.

**Tech Stack:** .NET 8, ASP.NET Core minimal API, Avalonia 11, xUnit (`OpcBridge.LoadTest`), Docker SDK builds.

**Spec:** `docs/superpowers/specs/2026-07-24-hmi-trends-proxy-design.md`

## Global Constraints

- Work only in worktree `/home/autoinst578/OpcDaToUaBridge/.worktrees/feature-hmi-trends` on branch `feature/hmi-trends`.
- Port stays **`http://0.0.0.0:8080`** — no second listener.
- HMI never references Influx SDK, Da, Ua, or COM; never stores Influx token.
- History storage is InfluxDB; access is **bridge proxy only**.
- Do **not** implement Influx writer, `InfluxEnabled`, or dashboard Influx Connection UI (owned by `feature/influxdb-access`).
- Soft Influx failure: **HTTP 200**, `points: []`, non-null `error` string.
- Bad params: **HTTP 400**.
- Default window: last **1 hour**; `maxPoints` default **500**, clamp **10..2000**.
- Zero-warning / zero-error Docker builds:
  ```bash
  docker run --rm -v "$PWD":/workspace -w /workspace mcr.microsoft.com/dotnet/sdk:8.0 \
    dotnet build OpcDaToUaBridge.sln -c Release
  ```
- Tests:
  ```bash
  docker run --rm -v "$PWD":/workspace -w /workspace mcr.microsoft.com/dotnet/sdk:8.0 \
    dotnet test tests/OpcBridge.LoadTest/OpcBridge.LoadTest.csproj -c Release
  ```
- Conventional commits: `feat(hmi):`, `test(hmi):`, `docs(hmi):`.

## File map

| Path | Responsibility |
|---|---|
| `src/OpcBridge.Client/HmiTrendPoint.cs` | Point DTO |
| `src/OpcBridge.Client/HmiTrendResponse.cs` | Series response DTO |
| `src/OpcBridge.App/Hmi/IInfluxTrendQuery.cs` | Query seam |
| `src/OpcBridge.App/Hmi/UnavailableInfluxTrendQuery.cs` | Stub when Influx not wired |
| `src/OpcBridge.App/Hmi/HmiTrendsEndpoint.cs` | Param parse + status mapping helper (optional; may live inline in Program) |
| `src/OpcBridge.App/Program.cs` | DI + `MapGet /api/hmi/trends` |
| `src/OpcBridge.Hmi/Services/BridgeApiClient.cs` | `GetTrendsAsync` |
| `src/OpcBridge.Hmi/ViewModels/MainViewModel.cs` | Load series on select + 30s timer |
| `src/OpcBridge.Hmi/Controls/SparklineControl.cs` | Lightweight polyline |
| `src/OpcBridge.Hmi/Views/MainWindow.axaml` | Chart + status under faceplate |
| `tests/OpcBridge.LoadTest/HmiTrendsApiTests.cs` | API TDD |
| `tests/OpcBridge.LoadTest/HmiTrendSeriesTests.cs` | Pure series/helper tests if needed |
| `context.md` | Document endpoint |

---

### Task 1: Client trend DTOs

**Files:**
- Create: `src/OpcBridge.Client/HmiTrendPoint.cs`
- Create: `src/OpcBridge.Client/HmiTrendResponse.cs`

**Interfaces:**
- Produces: `HmiTrendPoint`, `HmiTrendResponse` for App serializers and HMI client.

- [ ] **Step 1: Add `HmiTrendPoint`**

```csharp
namespace OpcBridge.Client;

public sealed class HmiTrendPoint
{
    public DateTime T { get; set; }
    public object? V { get; set; }
    public int? Q { get; set; }
    public bool? Good { get; set; }
}
```

- [ ] **Step 2: Add `HmiTrendResponse`**

```csharp
namespace OpcBridge.Client;

public sealed class HmiTrendResponse
{
    public string SourceId { get; set; } = string.Empty;
    public string DaItemId { get; set; } = string.Empty;
    public DateTime FromUtc { get; set; }
    public DateTime ToUtc { get; set; }
    public IReadOnlyList<HmiTrendPoint> Points { get; set; } = Array.Empty<HmiTrendPoint>();
    public bool Truncated { get; set; }
    public string? Error { get; set; }
}
```

- [ ] **Step 3: Build Client**

```bash
docker run --rm -v "$PWD":/workspace -w /workspace mcr.microsoft.com/dotnet/sdk:8.0 \
  dotnet build src/OpcBridge.Client/OpcBridge.Client.csproj -c Release
```
Expected: `0 Warning(s), 0 Error(s)`

- [ ] **Step 4: Commit**

```bash
git add src/OpcBridge.Client/HmiTrendPoint.cs src/OpcBridge.Client/HmiTrendResponse.cs
git commit -m "feat(hmi): add HmiTrendPoint and HmiTrendResponse DTOs"
```

---

### Task 2: `IInfluxTrendQuery` + unavailable stub

**Files:**
- Create: `src/OpcBridge.App/Hmi/IInfluxTrendQuery.cs`
- Create: `src/OpcBridge.App/Hmi/UnavailableInfluxTrendQuery.cs`
- Modify: `src/OpcBridge.App/Program.cs` (DI registration only)

**Interfaces:**
- Produces:
  ```csharp
  public interface IInfluxTrendQuery
  {
      Task<HmiTrendResponse> QueryAsync(
          string sourceId,
          string daItemId,
          DateTime fromUtc,
          DateTime toUtc,
          int maxPoints,
          CancellationToken ct);
  }
  ```
- Stub always returns empty points with `Error = "Influx not available"` and echoes ids/window; `Truncated = false`.

- [ ] **Step 1: Add interface**

`src/OpcBridge.App/Hmi/IInfluxTrendQuery.cs`:
```csharp
using OpcBridge.Client;

namespace OpcBridge.App.Hmi;

public interface IInfluxTrendQuery
{
    Task<HmiTrendResponse> QueryAsync(
        string sourceId,
        string daItemId,
        DateTime fromUtc,
        DateTime toUtc,
        int maxPoints,
        CancellationToken ct);
}
```

- [ ] **Step 2: Add stub**

`src/OpcBridge.App/Hmi/UnavailableInfluxTrendQuery.cs`:
```csharp
using OpcBridge.Client;

namespace OpcBridge.App.Hmi;

public sealed class UnavailableInfluxTrendQuery : IInfluxTrendQuery
{
    public Task<HmiTrendResponse> QueryAsync(
        string sourceId,
        string daItemId,
        DateTime fromUtc,
        DateTime toUtc,
        int maxPoints,
        CancellationToken ct)
    {
        return Task.FromResult(new HmiTrendResponse
        {
            SourceId = sourceId,
            DaItemId = daItemId,
            FromUtc = fromUtc,
            ToUtc = toUtc,
            Points = Array.Empty<HmiTrendPoint>(),
            Truncated = false,
            Error = "Influx not available"
        });
    }
}
```

- [ ] **Step 3: Register DI in `Program.cs`**

After other singleton registrations (near HMI services):
```csharp
builder.Services.AddSingleton<IInfluxTrendQuery, UnavailableInfluxTrendQuery>();
```

- [ ] **Step 4: Build App**

```bash
docker run --rm -v "$PWD":/workspace -w /workspace mcr.microsoft.com/dotnet/sdk:8.0 \
  dotnet build src/OpcBridge.App/OpcBridge.App.csproj -c Release
```
Expected: `0 Warning(s), 0 Error(s)`

- [ ] **Step 5: Commit**

```bash
git add src/OpcBridge.App/Hmi/IInfluxTrendQuery.cs src/OpcBridge.App/Hmi/UnavailableInfluxTrendQuery.cs src/OpcBridge.App/Program.cs
git commit -m "feat(hmi): add IInfluxTrendQuery with unavailable stub"
```

---

### Task 3: `GET /api/hmi/trends` + API tests (TDD)

**Files:**
- Create: `tests/OpcBridge.LoadTest/HmiTrendsApiTests.cs`
- Modify: `src/OpcBridge.App/Program.cs`
- Optional create: `src/OpcBridge.App/Hmi/HmiTrendsRequest.cs` (parse helpers as private statics in Program if small)

**Interfaces:**
- Consumes: `IInfluxTrendQuery.QueryAsync`
- Produces: `GET /api/hmi/trends?...` → `HmiTrendResponse` JSON

- [ ] **Step 1: Write failing tests**

Create `tests/OpcBridge.LoadTest/HmiTrendsApiTests.cs` (reuse same `WriteAppsettings` pattern as `HmiApiTests` — copy helper or share; keep local private helper to avoid drive-by refactors):

```csharp
using System.Net;
using System.Text.Json;
using Xunit;

namespace OpcBridge.LoadTest;

[Collection(nameof(DaLinkApiAppCollection))]
public sealed class HmiTrendsApiTests
{
    private static void WriteAppsettings(string dir)
    {
        var appsettings = new
        {
            Da = new { ProgId = "Matrikon.OPC.Simulation.1", Host = "localhost", UpdateRateMs = 1000, UseSubscriptions = true },
            Ua = new { ApplicationName = "OpcDaToUaBridge", EndpointUrl = "opc.tcp://0.0.0.0:4840/OpcBridge", AutoAcceptUntrustedCertificates = true, RequireAuthentication = false, Username = "", Password = "", AllowedIpAddresses = Array.Empty<string>() },
            Bridge = new { RateLimits = new { }, ExpectedTagCount = 100, Mappings = Array.Empty<object>() },
            Mqtt = new { Enabled = false, BrokerUrl = "tcp://localhost:1883", ClientId = "OpcDaToUaBridge", UserName = (string?)null, Password = (string?)null, Tls = false, IgnoreCertErrors = false, TopicPrefix = "bridge/tags", PayloadFields = "Value, Timestamp" }
        };
        File.WriteAllText(Path.Combine(dir, "appsettings.json"), JsonSerializer.Serialize(appsettings, new JsonSerializerOptions { WriteIndented = true }));
        string mapPath = Path.Combine(dir, "mappings.json");
        if (File.Exists(mapPath)) File.Delete(mapPath);
    }

    [Fact]
    public async Task HmiTrends_MissingSourceId_Returns400()
    {
        await using var handle = await TestAppHandle.StartAsync(WriteAppsettings);
        using HttpResponseMessage response = await handle.Client.GetAsync("/api/hmi/trends?daItemId=Random.Int1");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task HmiTrends_MissingDaItemId_Returns400()
    {
        await using var handle = await TestAppHandle.StartAsync(WriteAppsettings);
        using HttpResponseMessage response = await handle.Client.GetAsync("/api/hmi/trends?sourceId=default");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task HmiTrends_StubUnavailable_Returns200EmptyWithError()
    {
        await using var handle = await TestAppHandle.StartAsync(WriteAppsettings);
        using HttpResponseMessage response = await handle.Client.GetAsync(
            "/api/hmi/trends?sourceId=default&daItemId=Random.Int1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("default", doc.RootElement.GetProperty("sourceId").GetString());
        Assert.Equal("Random.Int1", doc.RootElement.GetProperty("daItemId").GetString());
        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("points").ValueKind);
        Assert.Equal(0, doc.RootElement.GetProperty("points").GetArrayLength());
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("error").GetString()));
    }
}
```

- [ ] **Step 2: Run tests — expect RED**

```bash
docker run --rm -v "$PWD":/workspace -w /workspace mcr.microsoft.com/dotnet/sdk:8.0 \
  dotnet test tests/OpcBridge.LoadTest/OpcBridge.LoadTest.csproj -c Release \
  --filter "FullyQualifiedName~HmiTrendsApiTests"
```
Expected: FAIL (404 NotFound for trends path)

- [ ] **Step 3: Map endpoint in `Program.cs`**

Ensure usings include `OpcBridge.App.Hmi` and `OpcBridge.Client`.

After `/api/hmi/write` mapping, add:

```csharp
app.MapGet("/api/hmi/trends", async (
    string? sourceId,
    string? daItemId,
    DateTime? from,
    DateTime? to,
    int? maxPoints,
    IInfluxTrendQuery trends,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(sourceId) || string.IsNullOrWhiteSpace(daItemId))
    {
        return Results.Json(
            new { error = "sourceId and daItemId are required" },
            statusCode: StatusCodes.Status400BadRequest);
    }

    DateTime toUtc = (to ?? DateTime.UtcNow).ToUniversalTime();
    DateTime fromUtc = (from ?? toUtc.AddHours(-1)).ToUniversalTime();
    if (fromUtc > toUtc)
    {
        return Results.Json(
            new { error = "from must be less than or equal to to" },
            statusCode: StatusCodes.Status400BadRequest);
    }

    int limit = maxPoints ?? 500;
    if (limit < 10) limit = 10;
    if (limit > 2000) limit = 2000;

    HmiTrendResponse response = await trends.QueryAsync(
        sourceId.Trim(),
        daItemId.Trim(),
        fromUtc,
        toUtc,
        limit,
        ct).ConfigureAwait(false);

    return Results.Json(response);
});
```

- [ ] **Step 4: Run tests — expect GREEN**

Same docker test filter as Step 2. Expected: all `HmiTrendsApiTests` PASS.

- [ ] **Step 5: Commit**

```bash
git add src/OpcBridge.App/Program.cs tests/OpcBridge.LoadTest/HmiTrendsApiTests.cs
git commit -m "feat(hmi): add GET /api/hmi/trends proxy endpoint"
```

---

### Task 4: Injectable fake for success-path API test

**Files:**
- Create: `tests/OpcBridge.LoadTest/FakeInfluxTrendQuery.cs` only if process host cannot swap DI — **preferred simpler approach:** unit-test the stub + keep host on `UnavailableInfluxTrendQuery`.

**Spec requires:** API stub unavailable covered in Task 3. Success path with known points: unit-test a small `RecordingInfluxTrendQuery` is optional.

- [ ] **Step 1: Add pure unit test for response shape (no host)**

`tests/OpcBridge.LoadTest/HmiTrendDtoTests.cs`:
```csharp
using System.Text.Json;
using OpcBridge.Client;
using Xunit;

namespace OpcBridge.LoadTest;

public sealed class HmiTrendDtoTests
{
    [Fact]
    public void HmiTrendResponse_RoundTripsJson()
    {
        HmiTrendResponse original = new()
        {
            SourceId = "default",
            DaItemId = "Random.Int1",
            FromUtc = new DateTime(2026, 7, 24, 0, 0, 0, DateTimeKind.Utc),
            ToUtc = new DateTime(2026, 7, 24, 1, 0, 0, DateTimeKind.Utc),
            Truncated = true,
            Error = null,
            Points = new[]
            {
                new HmiTrendPoint
                {
                    T = new DateTime(2026, 7, 24, 0, 30, 0, DateTimeKind.Utc),
                    V = 12.5,
                    Q = 192,
                    Good = true
                }
            }
        };

        string json = JsonSerializer.Serialize(original);
        HmiTrendResponse? back = JsonSerializer.Deserialize<HmiTrendResponse>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(back);
        Assert.Equal("default", back!.SourceId);
        Assert.Single(back.Points);
        Assert.Equal(12.5, Convert.ToDouble(back.Points[0].V));
        Assert.True(back.Truncated);
    }
}
```

Add project reference already exists Client via App; LoadTest already references Client from HMI merge tests — confirm `OpcBridge.LoadTest.csproj` has Client reference; add if missing:

```xml
<ProjectReference Include="..\..\src\OpcBridge.Client\OpcBridge.Client.csproj" />
```

- [ ] **Step 2: Run test**

```bash
docker run --rm -v "$PWD":/workspace -w /workspace mcr.microsoft.com/dotnet/sdk:8.0 \
  dotnet test tests/OpcBridge.LoadTest/OpcBridge.LoadTest.csproj -c Release \
  --filter "FullyQualifiedName~HmiTrendDtoTests"
```
Expected: PASS

- [ ] **Step 3: Commit**

```bash
git add tests/OpcBridge.LoadTest/HmiTrendDtoTests.cs tests/OpcBridge.LoadTest/OpcBridge.LoadTest.csproj
git commit -m "test(hmi): round-trip HmiTrendResponse JSON"
```

---

### Task 5: HMI `BridgeApiClient.GetTrendsAsync`

**Files:**
- Modify: `src/OpcBridge.Hmi/Services/BridgeApiClient.cs`

**Interfaces:**
- Produces:
  ```csharp
  Task<HmiTrendResponse> GetTrendsAsync(
      string sourceId,
      string daItemId,
      DateTime? fromUtc,
      DateTime? toUtc,
      int? maxPoints,
      CancellationToken ct);
  ```

- [ ] **Step 1: Add method**

```csharp
public async Task<HmiTrendResponse> GetTrendsAsync(
    string sourceId,
    string daItemId,
    DateTime? fromUtc,
    DateTime? toUtc,
    int? maxPoints,
    CancellationToken ct)
{
    var query = new List<string>
    {
        $"sourceId={Uri.EscapeDataString(sourceId)}",
        $"daItemId={Uri.EscapeDataString(daItemId)}"
    };
    if (fromUtc is not null)
    {
        query.Add($"from={Uri.EscapeDataString(fromUtc.Value.ToUniversalTime().ToString("o"))}");
    }
    if (toUtc is not null)
    {
        query.Add($"to={Uri.EscapeDataString(toUtc.Value.ToUniversalTime().ToString("o"))}");
    }
    if (maxPoints is not null)
    {
        query.Add($"maxPoints={maxPoints.Value}");
    }

    string path = "api/hmi/trends?" + string.Join("&", query);
    HmiTrendResponse? response = await client_.GetFromJsonAsync<HmiTrendResponse>(path, JsonOptions, ct)
        .ConfigureAwait(false);
    return response ?? new HmiTrendResponse
    {
        SourceId = sourceId,
        DaItemId = daItemId,
        Error = "Empty trends response"
    };
}
```

- [ ] **Step 2: Build Hmi**

```bash
docker run --rm -v "$PWD":/workspace -w /workspace mcr.microsoft.com/dotnet/sdk:8.0 \
  dotnet build src/OpcBridge.Hmi/OpcBridge.Hmi.csproj -c Release
```
Expected: 0w/0e

- [ ] **Step 3: Commit**

```bash
git add src/OpcBridge.Hmi/Services/BridgeApiClient.cs
git commit -m "feat(hmi): BridgeApiClient GetTrendsAsync"
```

---

### Task 6: MainViewModel trend series load + 30s timer

**Files:**
- Modify: `src/OpcBridge.Hmi/ViewModels/MainViewModel.cs`

**Interfaces:**
- Produces bindable:
  - `IReadOnlyList<double> TrendValues` (numeric Y samples for sparkline; empty if none)
  - `string TrendStatus` (e.g. empty, "No history", "Influx not available", errors)
  - `bool TrendLoading`
- On `OnSelectedTagChanged` / after connect if selection exists: fire `LoadTrendsAsync`
- While connected + selected: `DispatcherTimer` or `PeriodicTimer` every **30s** to refresh; dispose on disconnect/unselect

- [ ] **Step 1: Add fields/properties**

```csharp
[ObservableProperty] private string _trendStatus = string.Empty;
[ObservableProperty] private bool _trendLoading;
private IReadOnlyList<double> _trendValues = Array.Empty<double>();
public IReadOnlyList<double> TrendValues
{
    get => _trendValues;
    private set
    {
        if (SetProperty(ref _trendValues, value))
        {
            // SetProperty requires ObservableObject support for non-[ObservableProperty] — use manual OnPropertyChanged if needed
        }
    }
}
```

Prefer CommunityToolkit pattern:
```csharp
[ObservableProperty] private IReadOnlyList<double> _trendValues = Array.Empty<double>();
```

- [ ] **Step 2: Load method**

```csharp
private async Task LoadTrendsAsync(CancellationToken ct)
{
    TagItemViewModel? tag = SelectedTag;
    if (!IsConnected || tag is null)
    {
        TrendValues = Array.Empty<double>();
        TrendStatus = string.Empty;
        return;
    }

    TrendLoading = true;
    try
    {
        HmiTrendResponse response = await _api.GetTrendsAsync(
            tag.SourceId,
            tag.DaItemId,
            fromUtc: null,
            toUtc: null,
            maxPoints: 500,
            ct).ConfigureAwait(true);

        List<double> ys = new();
        foreach (HmiTrendPoint p in response.Points)
        {
            if (TryToDouble(p.V, out double y))
            {
                ys.Add(y);
            }
        }

        TrendValues = ys;
        if (!string.IsNullOrWhiteSpace(response.Error))
        {
            TrendStatus = response.Error!;
        }
        else if (ys.Count == 0)
        {
            TrendStatus = "No history";
        }
        else
        {
            TrendStatus = string.Empty;
        }
    }
    catch (OperationCanceledException)
    {
        // ignore
    }
    catch (Exception ex)
    {
        TrendValues = Array.Empty<double>();
        TrendStatus = ex.Message;
    }
    finally
    {
        TrendLoading = false;
    }
}

private static bool TryToDouble(object? value, out double y)
{
    y = 0;
    if (value is null) return false;
    if (value is double d) { y = d; return true; }
    if (value is float f) { y = f; return true; }
    if (value is int i) { y = i; return true; }
    if (value is long l) { y = l; return true; }
    if (value is JsonElement je)
    {
        if (je.ValueKind == JsonValueKind.Number && je.TryGetDouble(out d)) { y = d; return true; }
        return false;
    }
    return double.TryParse(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture),
        System.Globalization.NumberStyles.Float,
        System.Globalization.CultureInfo.InvariantCulture,
        out y);
}
```

Add `using System.Text.Json;` if needed.

- [ ] **Step 3: Hook selection + timer**

In `OnSelectedTagChanged`:
```csharp
partial void OnSelectedTagChanged(TagItemViewModel? value)
{
    WriteValue = value?.ValueText ?? string.Empty;
    WriteCommand.NotifyCanExecuteChanged();
    _ = LoadTrendsForSelectionAsync();
}

private async Task LoadTrendsForSelectionAsync()
{
    if (_connectCts is null || !IsConnected) return;
    await LoadTrendsAsync(_connectCts.Token).ConfigureAwait(true);
}
```

Start a 30s refresh loop when connected (e.g. after Connect succeeds, cancel with `_connectCts`):

```csharp
_ = TrendRefreshLoopAsync(_connectCts.Token);

private async Task TrendRefreshLoopAsync(CancellationToken ct)
{
    try
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(true))
        {
            if (SelectedTag is not null)
            {
                await LoadTrendsAsync(ct).ConfigureAwait(true);
            }
        }
    }
    catch (OperationCanceledException)
    {
    }
}
```

Call `LoadTrendsAsync` once after connect if `SelectedTag` already set.

On disconnect: clear `TrendValues` / `TrendStatus`.

- [ ] **Step 4: Build Hmi**

```bash
docker run --rm -v "$PWD":/workspace -w /workspace mcr.microsoft.com/dotnet/sdk:8.0 \
  dotnet build src/OpcBridge.Hmi/OpcBridge.Hmi.csproj -c Release
```
Expected: 0w/0e

- [ ] **Step 5: Commit**

```bash
git add src/OpcBridge.Hmi/ViewModels/MainViewModel.cs
git commit -m "feat(hmi): load faceplate trend series from bridge proxy"
```

---

### Task 7: Sparkline control + faceplate XAML

**Files:**
- Create: `src/OpcBridge.Hmi/Controls/SparklineControl.cs`
- Modify: `src/OpcBridge.Hmi/Views/MainWindow.axaml`

**Interfaces:**
- `SparklineControl` dependency property / styled property `Points` as `IEnumerable<double>`
- Draws min-max normalized polyline in `Render` or via `Geometry`

- [ ] **Step 1: Implement control**

```csharp
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using System.Collections.Generic;
using System.Linq;

namespace OpcBridge.Hmi.Controls;

public sealed class SparklineControl : Control
{
    public static readonly StyledProperty<IEnumerable<double>?> PointsProperty =
        AvaloniaProperty.Register<SparklineControl, IEnumerable<double>?>(nameof(Points));

    public IEnumerable<double>? Points
    {
        get => GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    static SparklineControl()
    {
        AffectsRender<SparklineControl>(PointsProperty);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        IEnumerable<double>? raw = Points;
        if (raw is null) return;
        double[] pts = raw as double[] ?? raw.ToArray();
        if (pts.Length < 2 || Bounds.Width <= 1 || Bounds.Height <= 1) return;

        double min = pts.Min();
        double max = pts.Max();
        double range = max - min;
        if (range <= 0) range = 1;

        StreamGeometry geometry = new();
        using (StreamGeometryContext ctx = geometry.Open())
        {
            for (int i = 0; i < pts.Length; i++)
            {
                double x = i * (Bounds.Width - 1) / (pts.Length - 1);
                double y = Bounds.Height - 1 - ((pts[i] - min) / range) * (Bounds.Height - 1);
                if (i == 0) ctx.BeginFigure(new Point(x, y), false);
                else ctx.LineTo(new Point(x, y));
            }
        }

        context.DrawGeometry(
            null,
            new Pen(Brushes.DeepSkyBlue, 1.5),
            geometry);
    }
}
```

Adjust Avalonia API if `StreamGeometryContext` / `BeginFigure` signatures differ for 11.2 — fix to compile (0w/0e).

- [ ] **Step 2: Wire XAML**

In `MainWindow.axaml` add xmlns:
```xml
xmlns:controls="using:OpcBridge.Hmi.Controls"
```

Inside faceplate `StackPanel`, after timestamp / before write panel:
```xml
          <TextBlock Text="History (1h)" FontWeight="SemiBold" Margin="0,8,0,0" />
          <controls:SparklineControl Height="72"
                                     Points="{Binding TrendValues}" />
          <TextBlock Text="{Binding TrendStatus}"
                     Opacity="0.85"
                     TextWrapping="Wrap" />
```

- [ ] **Step 3: Build Hmi**

```bash
docker run --rm -v "$PWD":/workspace -w /workspace mcr.microsoft.com/dotnet/sdk:8.0 \
  dotnet build src/OpcBridge.Hmi/OpcBridge.Hmi.csproj -c Release
```
Expected: 0w/0e

- [ ] **Step 4: Commit**

```bash
git add src/OpcBridge.Hmi/Controls/SparklineControl.cs src/OpcBridge.Hmi/Views/MainWindow.axaml
git commit -m "feat(hmi): faceplate sparkline for trend history"
```

---

### Task 8: Docs + full verification

**Files:**
- Modify: `context.md`

- [ ] **Step 1: Document API in `context.md`**

Under HTTP API HMI bullets, add:
- `GET /api/hmi/trends?sourceId=&daItemId=&from=&to=&maxPoints=` — history via bridge Influx proxy (HMI never holds Influx token). Soft-fails with empty points + `error` when Influx unavailable.

Note: writer path / `InfluxEnabled` documented when writer merges; this branch is read proxy + faceplate chart.

- [ ] **Step 2: Full build + tests**

```bash
docker run --rm -v "$PWD":/workspace -w /workspace mcr.microsoft.com/dotnet/sdk:8.0 \
  bash -lc 'dotnet build OpcDaToUaBridge.sln -c Release && dotnet test tests/OpcBridge.LoadTest/OpcBridge.LoadTest.csproj -c Release'
```
Expected: 0w/0e; all tests pass (baseline + new HmiTrends*).

- [ ] **Step 3: Commit**

```bash
git add context.md
git commit -m "docs(hmi): document GET /api/hmi/trends proxy in context.md"
```

- [ ] **Step 4: Manual checklist (not automated)**

After rebase onto writer + Influx on host:
1. Enable Influx write for a tag; wait.
2. HMI faceplate shows non-empty sparkline for last hour.
3. Stop Influx: live OK; chart shows unavailable/empty.
4. Confirm HMI has no Influx token fields/config.

---

### Task 9 (after writer merge — optional on this branch): real Flux query

**Do not start until** `feature/influxdb-access` is merged or this branch is rebased onto it.

**Files (expected after rebase):**
- Create/replace: `src/OpcBridge.Influx/InfluxTrendQuery.cs` implementing `IInfluxTrendQuery`
- Modify DI to register real implementation when Influx options enabled; keep unavailable fallback

**Flux must match writer schema:**
- measurement default `opc_tags`
- tags `source_id`, `da_item_id`
- fields `value`, `quality`, `is_good`
- range `from`..`to`
- thin to `maxPoints`

**Verification:** host smoke with real points; keep soft-error contract.

If writer not merged when Tasks 1–8 complete: ship stub; open follow-up PR after rebase. **Do not reimplement writer here.**

---

## Spec coverage checklist

| Spec requirement | Task |
|---|---|
| Client DTOs `HmiTrendPoint` / `HmiTrendResponse` | 1 |
| `IInfluxTrendQuery` + unavailable stub | 2 |
| `GET /api/hmi/trends` validation + 200 soft fail | 3 |
| JSON round-trip / shape | 4 |
| HMI REST client | 5 |
| Load on select + 30s refresh | 6 |
| Faceplate sparkline + status | 7 |
| `context.md` | 8 |
| Real Flux after writer | 9 (optional/later) |
| No writer / no HMI→Influx / port 8080 | Global |

## Self-review notes

- No TBD placeholders; DTO property names match design (`T`/`V`/`Q`/`Good` → camelCase `t`/`v`/`q`/`good` under default System.Text.Json).
- ASP.NET default JSON is camelCase for serialization of public properties.
- Stub satisfies “Influx not available” without blocking live HMI.
- Sparkline Avalonia draw API may need small compile fixes — task requires 0w/0e build.
- Parallel `feature/influxdb-access` remains untouched.
