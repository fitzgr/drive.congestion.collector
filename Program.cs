using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient();
builder.Services.AddSingleton<TrafficStore>();
builder.Services.AddSingleton<TrafficPointStore>();
builder.Services.AddSingleton<TrafficCollectorServiceTrigger>();
builder.Services.AddSingleton<TrafficCollectorService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TrafficCollectorService>());

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/status", (TrafficStore store, IConfiguration config) =>
    Results.Ok(store.GetStatus(config)));
app.MapGet("/api/points", (TrafficPointStore points) => Results.Ok(points.Get()));
app.MapPut("/api/points", async (List<TrafficPoint> value, TrafficPointStore points, TrafficCollectorService service, CancellationToken ct) =>
{
    var error = TrafficPointStore.Validate(value);
    if (error is not null) return Results.BadRequest(new { error });
    await points.SaveAsync(value, ct);
    service.InvalidateBindings();
    return Results.Ok(points.Get());
});
app.MapGet("/api/samples", async (TrafficStore store, CancellationToken ct) =>
    Results.Ok(await store.ReadSamplesAsync(ct)));
app.MapPost("/api/validate-points", async (TrafficCollectorService service, CancellationToken ct) => Results.Ok(await service.ValidatePointsAsync(ct)));
app.MapPost("/api/collect-now", (TrafficCollectorServiceTrigger trigger) =>
{
    trigger.Trigger();
    return Results.Accepted();
});

app.Run();

public sealed record TrafficPoint(string Id, string Label, string Direction, string Point, double? Heading = null);
public sealed record PointValidation(string Id, string Label, string Direction, string RequestedPoint, double? ProjectedLatitude, double? ProjectedLongitude, string? RoadName, string? RoadNumbers, int? Frc, double? SpeedLimitKph, string Status, string? Error);

public sealed class TrafficPointStore
{
    private readonly string _path = Path.Combine(AppContext.BaseDirectory, "data", "points.json");
    private readonly IConfiguration _config;
    private readonly object _gate = new();
    private List<TrafficPoint> _points;

    public TrafficPointStore(IConfiguration config)
    {
        _config = config;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        _points = Load();
    }

    private List<TrafficPoint> Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<List<TrafficPoint>>(File.ReadAllText(_path)) ?? new();
        }
        catch { }
        return _points.Get();
    }

    public List<TrafficPoint> Get() { lock (_gate) return _points.ToList(); }

    public async Task SaveAsync(List<TrafficPoint> points, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(points, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(_path, json, ct);
        lock (_gate) _points = points.ToList();
    }

    public static string? Validate(List<TrafficPoint> points)
    {
        if (points.Count == 0) return "At least one checkpoint is required.";
        if (points.Any(p => string.IsNullOrWhiteSpace(p.Id) || string.IsNullOrWhiteSpace(p.Label))) return "Every point needs an id and label.";
        if (points.GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1)) return "Point ids must be unique.";
        foreach (var p in points)
        {
            if (p.Direction is not ("WESTBOUND" or "EASTBOUND")) return $"{p.Id}: direction must be WESTBOUND or EASTBOUND.";
            var parts = p.Point.Split(',');
            if (parts.Length != 2 ||
                !double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var lat) ||
                !double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var lon) ||
                lat is < -90 or > 90 || lon is < -180 or > 180)
                return $"{p.Id}: point must be latitude,longitude.";
        }
        return null;
    }
}

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
    string? RequestedPoint,
    double? MatchedLatitude,
    double? MatchedLongitude,
    string? SegmentGeometry,
    bool? LooksLikeHighway,
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
    private readonly TrafficPointStore _points;
    private readonly Dictionary<string, BoundTrafficPoint> _boundPoints = new();
    private readonly SemaphoreSlim _bindGate = new(1, 1);

    public TrafficCollectorService(
        IHttpClientFactory httpClientFactory,
        TrafficStore store,
        IConfiguration config,
        TrafficCollectorServiceTrigger trigger,
        TrafficPointStore points)
    {
        _httpClientFactory = httpClientFactory;
        _store = store;
        _config = config;
        _trigger = trigger;
        _points = points;
    }

    public void InvalidateBindings()
    {
        lock (_boundPoints) _boundPoints.Clear();
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
        var points = _points.Get();

        if (string.IsNullOrWhiteSpace(apiKey) || points.Count == 0)
            return;

        // Bind each configured checkpoint to the intended Hwy 401 carriageway once per
        // process. Recurring Flow requests then use the TomTom-projected 401 coordinate
        // instead of asking Flow to choose whichever road happens to be nearest the raw point.
        await EnsurePointsBoundTo401Async(apiKey, points, ct);

        foreach (var point in points)
        {
            if (ct.IsCancellationRequested) break;
            if (_boundPoints.TryGetValue(point.Id, out var bound))
                await CollectPointAsync(apiKey, point, bound, ct);
            else
                await StoreUnboundPointAsync(point, ct);
        }
    }

    private sealed record BoundTrafficPoint(string Point, double Latitude, double Longitude, string? RoadName, string? RoadNumbers, int Frc, double SpeedLimitKph);

    private async Task EnsurePointsBoundTo401Async(string apiKey, List<TrafficPoint> points, CancellationToken ct)
    {
        if (points.All(p => _boundPoints.ContainsKey(p.Id))) return;
        await _bindGate.WaitAsync(ct);
        try
        {
            foreach (var point in points)
            {
                if (_boundPoints.ContainsKey(point.Id)) continue;
                var bound = await BindPointTo401Async(apiKey, point, ct);
                if (bound is not null) _boundPoints[point.Id] = bound;
            }
        }
        finally { _bindGate.Release(); }
    }

    private async Task<BoundTrafficPoint?> BindPointTo401Async(string apiKey, TrafficPoint p, CancellationToken ct)
    {
        try
        {
            var parts = p.Point.Split(',');
            var lat = double.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
            var lon = double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
            var heading = p.Heading ?? (p.Direction == "WESTBOUND" ? 270d : 90d);
            var rad = heading * Math.PI / 180d;
            const double metres = 120d;
            var dLat = (metres * Math.Cos(rad)) / 111320d;
            var dLon = (metres * Math.Sin(rad)) / (111320d * Math.Cos(lat * Math.PI / 180d));
            var lat1 = lat - dLat / 2; var lon1 = lon - dLon / 2;
            var lat2 = lat + dLat / 2; var lon2 = lon + dLon / 2;
            var pts = $"{lon1.ToString(System.Globalization.CultureInfo.InvariantCulture)},{lat1.ToString(System.Globalization.CultureInfo.InvariantCulture)};{lon2.ToString(System.Globalization.CultureInfo.InvariantCulture)},{lat2.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            var headings = $"{heading.ToString(System.Globalization.CultureInfo.InvariantCulture)};{heading.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            var fields = "{projectedPoints{geometry{coordinates},properties{snapResult}},route{properties{frc,address{roadName,roadNumbers},speedLimits{value,unit,type}}}}";
            var url = $"https://api.tomtom.com/snapToRoads/1?key={Uri.EscapeDataString(apiKey)}&points={Uri.EscapeDataString(pts)}&headings={Uri.EscapeDataString(headings)}&fields={Uri.EscapeDataString(fields)}&vehicleType=PassengerCar&measurementSystem=metric&offroadMargin=100";
            using var response = await _httpClientFactory.CreateClient().GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;

            double? plat = null, plon = null, limit = null;
            string? road = null, nums = null;
            int? frc = null;
            if (root.TryGetProperty("projectedPoints", out var pp) && pp.ValueKind == JsonValueKind.Array && pp.GetArrayLength() > 0)
            {
                var middle = pp[pp.GetArrayLength() / 2];
                var coords = middle.GetProperty("geometry").GetProperty("coordinates");
                if (coords.GetArrayLength() >= 2) { plon = coords[0].GetDouble(); plat = coords[1].GetDouble(); }
            }
            if (root.TryGetProperty("route", out var route) && route.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in route.EnumerateArray())
                {
                    if (!el.TryGetProperty("properties", out var pr)) continue;
                    if (frc is null && pr.TryGetProperty("frc", out var f) && f.TryGetInt32(out var fi)) frc = fi;
                    if (pr.TryGetProperty("address", out var ad))
                    {
                        if (road is null && ad.TryGetProperty("roadName", out var rn)) road = rn.GetString();
                        if (nums is null && ad.TryGetProperty("roadNumbers", out var rns)) nums = rns.ValueKind == JsonValueKind.Array ? string.Join(",", rns.EnumerateArray().Select(x => x.GetString())) : rns.ToString();
                    }
                    if (limit is null && pr.TryGetProperty("speedLimits", out var sl) && sl.ValueKind == JsonValueKind.Array && sl.GetArrayLength() > 0 && sl[0].TryGetProperty("value", out var sv) && sv.TryGetDouble(out var sd)) limit = sd;
                }
            }

            // FRC 0 plus a highway-speed limit is the same validation rule exposed by
            // /api/validate-points. Do not collect a nearby ramp/local-road result.
            if (plat is null || plon is null || frc != 0 || limit is null || limit < 90) return null;
            var boundPoint = $"{plat.Value.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)},{plon.Value.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)}";
            return new BoundTrafficPoint(boundPoint, plat.Value, plon.Value, road, nums, frc.Value, limit.Value);
        }
        catch { return null; }
    }

    private Task StoreUnboundPointAsync(TrafficPoint point, CancellationToken ct) =>
        _store.AppendAsync(new TrafficSample(
            DateTimeOffset.Now, "tomtom-flow-segment",
            point.Id, point.Label, point.Direction,
            null, null, null, null, null, null, null,
            point.Point, null, null, null, false,
            "Checkpoint not bound to a validated Hwy 401 FRC0/highway-speed segment; Flow request skipped."), ct);

    private string? LoadTomTomApiKey()
    {
        var envKey = Environment.GetEnvironmentVariable("TOMTOM_API_KEY");
        if (!string.IsNullOrWhiteSpace(envKey)) return envKey.Trim();

        var keyFile = Path.Combine(AppContext.BaseDirectory, "secrets", "tomtom.key");
        if (!File.Exists(keyFile))
            keyFile = Path.Combine(Directory.GetCurrentDirectory(), "secrets", "tomtom.key");

        return File.Exists(keyFile) ? File.ReadAllText(keyFile).Trim() : null;
    }

    public async Task<List<PointValidation>> ValidatePointsAsync(CancellationToken ct)
    {
        var key = LoadTomTomApiKey();
        var points = _points.Get();
        var results = new List<PointValidation>();
        if (string.IsNullOrWhiteSpace(key)) return results;

        foreach (var p in points)
        {
            try
            {
                var parts = p.Point.Split(',');
                var lat = double.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
                var lon = double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
                // Snap-to-Roads requires at least two related GPS points. Create a short trace
                // along the configured heading, centred on the checkpoint.
                var heading = p.Heading ?? (p.Direction == "WESTBOUND" ? 270d : 90d);
                var rad = heading * Math.PI / 180d;
                const double metres = 120d;
                var dLat = (metres * Math.Cos(rad)) / 111320d;
                var dLon = (metres * Math.Sin(rad)) / (111320d * Math.Cos(lat * Math.PI / 180d));
                var lat1 = lat - dLat / 2; var lon1 = lon - dLon / 2;
                var lat2 = lat + dLat / 2; var lon2 = lon + dLon / 2;
                var pts = $"{lon1.ToString(System.Globalization.CultureInfo.InvariantCulture)},{lat1.ToString(System.Globalization.CultureInfo.InvariantCulture)};{lon2.ToString(System.Globalization.CultureInfo.InvariantCulture)},{lat2.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
                var headings = $"{heading.ToString(System.Globalization.CultureInfo.InvariantCulture)};{heading.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
                var fields = "{projectedPoints{geometry{coordinates},properties{snapResult}},route{properties{frc,address{roadName,roadNumbers},speedLimits{value,unit,type}}}}";
                var url = $"https://api.tomtom.com/snapToRoads/1?key={Uri.EscapeDataString(key)}&points={Uri.EscapeDataString(pts)}&headings={Uri.EscapeDataString(headings)}&fields={Uri.EscapeDataString(fields)}&vehicleType=PassengerCar&measurementSystem=metric&offroadMargin=100";
                using var response = await _httpClientFactory.CreateClient().GetAsync(url, ct);
                response.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                var root = doc.RootElement;
                double? plat=null, plon=null; string? road=null, nums=null; int? frc=null; double? limit=null;
                if (root.TryGetProperty("projectedPoints", out var pp) && pp.ValueKind==JsonValueKind.Array && pp.GetArrayLength()>0) {
                    var coords=pp[0].GetProperty("geometry").GetProperty("coordinates");
                    if(coords.GetArrayLength()>=2){ plon=coords[0].GetDouble(); plat=coords[1].GetDouble(); }
                }
                if (root.TryGetProperty("route", out var route) && route.ValueKind==JsonValueKind.Array && route.GetArrayLength()>0) {
                    foreach(var el in route.EnumerateArray()) {
                        if(!el.TryGetProperty("properties",out var pr)) continue;
                        if(frc is null && pr.TryGetProperty("frc",out var f) && f.TryGetInt32(out var fi)) frc=fi;
                        if(pr.TryGetProperty("address",out var ad)) {
                            if(road is null && ad.TryGetProperty("roadName",out var rn)) road=rn.GetString();
                            if(nums is null && ad.TryGetProperty("roadNumbers",out var rns)) nums=rns.ValueKind==JsonValueKind.Array?string.Join(",",rns.EnumerateArray().Select(x=>x.GetString())):rns.ToString();
                        }
                        if(limit is null && pr.TryGetProperty("speedLimits",out var sl) && sl.ValueKind==JsonValueKind.Array && sl.GetArrayLength()>0 && sl[0].TryGetProperty("value",out var sv) && sv.TryGetDouble(out var sd)) limit=sd;
                    }
                }
                var highway = frc==0 && limit>=90;
                results.Add(new PointValidation(p.Id,p.Label,p.Direction,p.Point,plat,plon,road,nums,frc,limit,highway?"VALIDATED_401":"REVIEW",null));
            }
            catch(Exception ex) { results.Add(new PointValidation(p.Id,p.Label,p.Direction,p.Point,null,null,null,null,null,null,"ERROR",ex.Message)); }
        }
        return results;
    }

    private async Task CollectPointAsync(string apiKey, TrafficPoint point, BoundTrafficPoint bound, CancellationToken ct)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            var url = $"https://api.tomtom.com/traffic/services/4/flowSegmentData/absolute/12/json?point={Uri.EscapeDataString(bound.Point)}&unit=KMPH&key={Uri.EscapeDataString(apiKey)}";
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
                slowdown,
                point.Point,
                FirstCoordinate(fsd)?.Lat,
                FirstCoordinate(fsd)?.Lon,
                CoordinateSummary(fsd),
                free.HasValue ? free.Value >= 85 : null,
                null);

            await _store.AppendAsync(sample, ct);
        }
        catch (Exception ex)
        {
            await _store.AppendAsync(new TrafficSample(
                DateTimeOffset.Now, "tomtom-flow-segment",
                point.Id, point.Label, point.Direction,
                null, null, null, null, null, null, null,
                point.Point, null, null, null, null, ex.Message), ct);
        }
    }

    private sealed record Coordinate(double Lat, double Lon);

    private static Coordinate? FirstCoordinate(JsonElement fsd)
    {
        if (!fsd.TryGetProperty("coordinates", out var coordinates) ||
            coordinates.ValueKind != JsonValueKind.Object ||
            !coordinates.TryGetProperty("coordinate", out var array) ||
            array.ValueKind != JsonValueKind.Array ||
            array.GetArrayLength() == 0) return null;

        var first = array[0];
        if (!first.TryGetProperty("latitude", out var lat) || !lat.TryGetDouble(out var latitude) ||
            !first.TryGetProperty("longitude", out var lon) || !lon.TryGetDouble(out var longitude)) return null;
        return new Coordinate(latitude, longitude);
    }

    private static string? CoordinateSummary(JsonElement fsd)
    {
        if (!fsd.TryGetProperty("coordinates", out var coordinates) ||
            !coordinates.TryGetProperty("coordinate", out var array) ||
            array.ValueKind != JsonValueKind.Array) return null;

        var pts = new List<string>();
        foreach (var p in array.EnumerateArray())
        {
            if (p.TryGetProperty("latitude", out var lat) && lat.TryGetDouble(out var latitude) &&
                p.TryGetProperty("longitude", out var lon) && lon.TryGetDouble(out var longitude))
                pts.Add($"{latitude:F6},{longitude:F6}");
        }
        return string.Join(";", pts);
    }

    private static double? TryDouble(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.TryGetDouble(out var d) ? d : null;
}
