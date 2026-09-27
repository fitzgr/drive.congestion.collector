using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient();
builder.Services.AddSingleton<TrafficStore>();
builder.Services.AddHostedService<TrafficCollectorService>();

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/status", (TrafficStore store) => Results.Ok(store.GetStatus()));
app.MapGet("/api/samples", async (TrafficStore store, CancellationToken ct) =>
    Results.Ok(await store.ReadSamplesAsync(ct)));
app.MapPost("/api/collect-now", async (TrafficCollectorServiceTrigger trigger, CancellationToken ct) =>
{
    await trigger.RunNowAsync(ct);
    return Results.Accepted();
});

app.Run();

public sealed record TrafficSample(
    DateTimeOffset Timestamp,
    string Source,
    double? CurrentSpeedKph,
    double? FreeFlowSpeedKph,
    double? CurrentTravelTimeSeconds,
    double? FreeFlowTravelTimeSeconds,
    double? Confidence,
    string? RoadClosure,
    string? Error);

public sealed class TrafficStore
{
    private readonly string _dataDir = Path.Combine(AppContext.BaseDirectory, "data");
    private readonly string _path;
    private readonly object _gate = new();
    private TrafficSample? _last;

    public TrafficStore()
    {
        Directory.CreateDirectory(_dataDir);
        _path = Path.Combine(_dataDir, "traffic.jsonl");
    }

    public void SetLast(TrafficSample sample)
    {
        lock (_gate) _last = sample;
    }

    public object GetStatus()
    {
        lock (_gate)
        {
            return new
            {
                running = true,
                lastSample = _last,
                dataFile = _path
            };
        }
    }

    public async Task AppendAsync(TrafficSample sample, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(sample);
        await File.AppendAllTextAsync(_path, json + Environment.NewLine, ct);
        SetLast(sample);
    }

    public async Task<List<TrafficSample>> ReadSamplesAsync(CancellationToken ct)
    {
        var result = new List<TrafficSample>();
        if (!File.Exists(_path)) return result;

        foreach (var line in await File.ReadAllLinesAsync(_path, ct))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var sample = JsonSerializer.Deserialize<TrafficSample>(line);
            if (sample is not null) result.Add(sample);
        }

        return result;
    }
}

public sealed class TrafficCollectorServiceTrigger
{
    private readonly SemaphoreSlim _signal = new(0);

    public void Trigger() => _signal.Release();

    public Task WaitAsync(CancellationToken ct) => _signal.WaitAsync(ct);

    public Task RunNowAsync(CancellationToken ct)
    {
        Trigger();
        return Task.CompletedTask;
    }
}

public sealed class TrafficCollectorService : BackgroundService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TrafficStore _store;
    private readonly IConfiguration _config;
    private readonly TrafficCollectorServiceTrigger _trigger = new();

    public TrafficCollectorService(
        IHttpClientFactory httpClientFactory,
        TrafficStore store,
        IConfiguration config)
    {
        _httpClientFactory = httpClientFactory;
        _store = store;
        _config = config;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalMinutes = _config.GetValue<int?>("Collector:IntervalMinutes") ?? 5;

        while (!stoppingToken.IsCancellationRequested)
        {
            await CollectOnceAsync(stoppingToken);

            using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var delayTask = Task.Delay(TimeSpan.FromMinutes(intervalMinutes), delayCts.Token);
            var triggerTask = _trigger.WaitAsync(stoppingToken);
            var completed = await Task.WhenAny(delayTask, triggerTask);
            if (completed == triggerTask)
                delayCts.Cancel();
        }
    }

    private async Task CollectOnceAsync(CancellationToken ct)
    {
        var apiKey = Environment.GetEnvironmentVariable("TOMTOM_API_KEY");
        var point = _config["Collector:Point"];

        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(point))
        {
            await _store.AppendAsync(new TrafficSample(
                DateTimeOffset.Now,
                "tomtom-flow-segment",
                null, null, null, null, null, null,
                "Missing TOMTOM_API_KEY or Collector:Point configuration."), ct);
            return;
        }

        try
        {
            var client = _httpClientFactory.CreateClient();
            var url = $"https://api.tomtom.com/traffic/services/4/flowSegmentData/absolute/10/json?point={Uri.EscapeDataString(point)}&unit=KMPH&key={Uri.EscapeDataString(apiKey)}";
            using var response = await client.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var fsd = doc.RootElement.GetProperty("flowSegmentData");

            var sample = new TrafficSample(
                DateTimeOffset.Now,
                "tomtom-flow-segment",
                TryDouble(fsd, "currentSpeed"),
                TryDouble(fsd, "freeFlowSpeed"),
                TryDouble(fsd, "currentTravelTime"),
                TryDouble(fsd, "freeFlowTravelTime"),
                TryDouble(fsd, "confidence"),
                fsd.TryGetProperty("roadClosure", out var rc) ? rc.ToString() : null,
                null);

            await _store.AppendAsync(sample, ct);
        }
        catch (Exception ex)
        {
            await _store.AppendAsync(new TrafficSample(
                DateTimeOffset.Now,
                "tomtom-flow-segment",
                null, null, null, null, null, null,
                ex.Message), ct);
        }
    }

    private static double? TryDouble(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.TryGetDouble(out var d) ? d : null;
}
