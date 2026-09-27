using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient();
builder.Services.AddSingleton<TrafficStore>();
builder.Services.AddSingleton<TrafficCollectorServiceTrigger>();
builder.Services.AddHostedService<TrafficCollectorService>();

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/status", (TrafficStore store, IConfiguration config) =>
    Results.Ok(store.GetStatus(config)));
app.MapGet("/api/samples", async (TrafficStore store, CancellationToken ct) =>
    Results.Ok(await store.ReadSamplesAsync(ct)));
app.MapPost("/api/collect-now", (TrafficCollectorServiceTrigger trigger) =>
{
    trigger.Trigger();
    return Results.Accepted();
});

app.Run();

public sealed record TrafficPoint(string Id, string Label, string Direction, string Point);

public sealed record TrafficSample(
    DateTimeOffset Timestamp,
    string Source,
    string PointId,
    string Label,
    string Direction,
    double? CurrentSpeedKph,
    double? FreeFlowSpeedKph,
    double? CurrentTravelTimeSeconds,
    double? FreeFlowTravelTimeSeconds,
    double? Confidence,
    bool? RoadClosure,
    double? SlowdownPercent,
    string? Error);

public sealed class TrafficStore
{
    private readonly string _dataDir = Path.Combine(AppContext.BaseDirectory, "data");
    private readonly string _path;
    private readonly object _gate = new();
    private readonly Dictionary<string, TrafficSample> _lastByPoint = new();

    public TrafficStore()
    {
        Directory.CreateDirectory(_dataDir);
        _path = Path.Combine(_dataDir, "traffic.jsonl");
    }

    public object GetStatus(IConfiguration config)
    {
        lock (_gate)
        {
            return new
            {
                running = true,
                schedule = new
                {
                    startHour = config.GetValue<int>("Collector:StartHour"),
                    endHour = config.GetValue<int>("Collector:EndHour"),
                    intervalMinutes = config.GetValue<int>("Collector:IntervalMinutes"),
                    timeZone = config["Collector:TimeZone"]
                },
                points = _lastByPoint.Values.OrderBy(x => x.Direction).ThenBy(x => x.PointId).ToArray(),
                dataFile = _path
            };
        }
    }

    public async Task AppendAsync(TrafficSample sample, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(sample);
        await File.AppendAllTextAsync(_path, json + Environment.NewLine, ct);
        lock (_gate) _lastByPoint[sample.PointId] = sample;
    }

    public async Task<List<TrafficSample>> ReadSamplesAsync(CancellationToken ct)
    {
        var result = new List<TrafficSample>();
        if (!File.Exists(_path)) return result;
        foreach (var line in await File.ReadAllLinesAsync(_path, ct))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var sample = JsonSerializer.Deserialize<TrafficSample>(line);
                if (sample is not null) result.Add(sample);
            }
            catch { /* tolerate old/incomplete lines */ }
        }
        return result;
    }
}

public sealed class TrafficCollectorServiceTrigger
{
    private readonly SemaphoreSlim _signal = new(0);
    public void Trigger() => _signal.Release();
    public Task WaitAsync(CancellationToken ct) => _signal.WaitAsync(ct);
}

public sealed class TrafficCollectorService : BackgroundService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TrafficStore _store;
    private readonly IConfiguration _config;
    private readonly TrafficCollectorServiceTrigger _trigger;

    public TrafficCollectorService(
        IHttpClientFactory httpClientFactory,
        TrafficStore store,
        IConfiguration config,
        TrafficCollectorServiceTrigger trigger)
    {
        _httpClientFactory = httpClientFactory;
        _store = store;
        _config = config;
        _trigger = trigger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalMinutes = Math.Max(1, _config.GetValue<int?>("Collector:IntervalMinutes") ?? 20);

        while (!stoppingToken.IsCancellationRequested)
        {
            if (IsWithinCollectionWindow())
                await CollectAllAsync(stoppingToken);

            using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var delayTask = Task.Delay(TimeSpan.FromMinutes(intervalMinutes), delayCts.Token);
            var triggerTask = _trigger.WaitAsync(stoppingToken);
            var completed = await Task.WhenAny(delayTask, triggerTask);
            if (completed == triggerTask)
            {
                delayCts.Cancel();
                await CollectAllAsync(stoppingToken, manual: true);
            }
        }
    }

    private bool IsWithinCollectionWindow()
    {
        var tzId = _config["Collector:TimeZone"] ?? "Eastern Standard Time";
        var tz = TimeZoneInfo.FindSystemTimeZoneById(tzId);
        var local = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tz);
        var start = _config.GetValue<int?>("Collector:StartHour") ?? 5;
        var end = _config.GetValue<int?>("Collector:EndHour") ?? 22;
        return local.Hour >= start && local.Hour < end;
    }

    private async Task CollectAllAsync(CancellationToken ct, bool manual = false)
    {
        var apiKey = LoadTomTomApiKey();
        var points = _config.GetSection("Collector:Points").Get<List<TrafficPoint>>() ?? new();

        if (string.IsNullOrWhiteSpace(apiKey) || points.Count == 0)
            return;

        foreach (var point in points)
        {
            if (ct.IsCancellationRequested) break;
            await CollectPointAsync(apiKey, point, ct);
        }
    }

    private string? LoadTomTomApiKey()\n    {\n        var envKey = Environment.GetEnvironmentVariable("TOMTOM_API_KEY");\n        if (!string.IsNullOrWhiteSpace(envKey)) return envKey.Trim();\n\n        var keyFile = Path.Combine(AppContext.BaseDirectory, "secrets", "tomtom.key");\n        if (!File.Exists(keyFile))\n            keyFile = Path.Combine(Directory.GetCurrentDirectory(), "secrets", "tomtom.key");\n\n        return File.Exists(keyFile) ? File.ReadAllText(keyFile).Trim() : null;\n    }\n\n    private async Task CollectPointAsync(string apiKey, TrafficPoint point, CancellationToken ct)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            var url = $"https://api.tomtom.com/traffic/services/4/flowSegmentData/absolute/12/json?point={Uri.EscapeDataString(point.Point)}&unit=KMPH&key={Uri.EscapeDataString(apiKey)}";
            using var response = await client.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var fsd = doc.RootElement.GetProperty("flowSegmentData");

            var current = TryDouble(fsd, "currentSpeed");
            var free = TryDouble(fsd, "freeFlowSpeed");
            double? slowdown = current.HasValue && free > 0
                ? Math.Round((1.0 - current.Value / free.Value) * 100.0, 1)
                : null;

            var sample = new TrafficSample(
                DateTimeOffset.Now, "tomtom-flow-segment",
                point.Id, point.Label, point.Direction,
                current, free,
                TryDouble(fsd, "currentTravelTime"),
                TryDouble(fsd, "freeFlowTravelTime"),
                TryDouble(fsd, "confidence"),
                fsd.TryGetProperty("roadClosure", out var rc) && rc.ValueKind is JsonValueKind.True or JsonValueKind.False ? rc.GetBoolean() : null,
                slowdown, null);

            await _store.AppendAsync(sample, ct);
        }
        catch (Exception ex)
        {
            await _store.AppendAsync(new TrafficSample(
                DateTimeOffset.Now, "tomtom-flow-segment",
                point.Id, point.Label, point.Direction,
                null, null, null, null, null, null, null, ex.Message), ct);
        }
    }

    private static double? TryDouble(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.TryGetDouble(out var d) ? d : null;
}
