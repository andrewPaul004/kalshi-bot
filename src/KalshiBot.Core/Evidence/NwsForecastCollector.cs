using System.Globalization;
using System.Net;
using System.Text.Json;
using KalshiBot.Core.Common;
using KalshiBot.Core.Recording;
using KalshiBot.Core.Storage;

namespace KalshiBot.Core.Evidence;

/// <summary>A named forecast point. CLINYC (the KXHIGHNY settlement station) is Central Park.</summary>
public sealed record ForecastLocation(string Key, decimal Latitude, decimal Longitude, string Description)
{
    public static readonly ForecastLocation CentralPark = new("KNYC", 40.7789m, -73.9692m, "New York Central Park (CLINYC)");
    public static readonly IReadOnlyDictionary<string, ForecastLocation> Known = new Dictionary<string, ForecastLocation> { ["KNYC"] = CentralPark };
}

public sealed record ForecastPeriod(int Number, string Name, DateTimeOffset Start, DateTimeOffset End, bool IsDaytime,
    int Temperature, string TemperatureUnit, string? ShortForecast)
{
    /// <summary>The period's calendar date in the forecast's own local time (from the start time's offset).</summary>
    public string LocalDate => Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

public sealed record NwsForecast(string Grid, DateTimeOffset? GeneratedAt, DateTimeOffset? UpdateTime, IReadOnlyList<ForecastPeriod> Periods);

public sealed record ForecastCollectSummary(long RunId, string Status, int Periods, DateTimeOffset? UpdateTime, IReadOnlyList<string> Errors);

public sealed record DailyHighVintage(string LocalDate, int Temperature, string Unit, DateTimeOffset? UpdateTime, DateTimeOffset ReceivedAt, string DataOrigin);

/// <summary>
/// Parses api.weather.gov responses (GeoJSON). Field names follow the public NWS API documentation; they could not be
/// checked against live responses from the build environment, so raw bodies are always stored first.
/// </summary>
public static class NwsParse
{
    /// <summary>/points/{lat},{lon} -> the forecast URL and grid id. Only api.weather.gov HTTPS URLs are followed.</summary>
    public static (string ForecastUrl, string Grid) Points(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var p = doc.RootElement.GetProperty("properties");
        var url = p.GetProperty("forecast").GetString() ?? throw new FormatException("points response has no forecast URL");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != "https" || u.Host != "api.weather.gov")
            throw new FormatException($"refusing to follow non-NWS forecast URL '{url}'");
        var grid = $"{p.GetProperty("gridId").GetString()}/{p.GetProperty("gridX").GetInt32()},{p.GetProperty("gridY").GetInt32()}";
        return (url, grid);
    }

    public static NwsForecast Forecast(string body, string grid)
    {
        using var doc = JsonDocument.Parse(body);
        var p = doc.RootElement.GetProperty("properties");
        DateTimeOffset? Time(string k) => p.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String
            ? DateTimeOffset.Parse(v.GetString()!, CultureInfo.InvariantCulture) : null;
        var periods = p.GetProperty("periods").EnumerateArray().Select(e => new ForecastPeriod(
            e.GetProperty("number").GetInt32(),
            e.GetProperty("name").GetString() ?? "",
            DateTimeOffset.Parse(e.GetProperty("startTime").GetString()!, CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(e.GetProperty("endTime").GetString()!, CultureInfo.InvariantCulture),
            e.GetProperty("isDaytime").GetBoolean(),
            e.GetProperty("temperature").GetInt32(),
            e.GetProperty("temperatureUnit").GetString() ?? "",
            e.TryGetProperty("shortForecast", out var sf) ? sf.GetString() : null)).ToList();
        if (periods.Count == 0) throw new FormatException("forecast has no periods");
        return new NwsForecast(grid, Time("generatedAt"), Time("updateTime"), periods);
    }
}

/// <summary>
/// Evidence collector for NWS point forecasts (spec 3.1): GET /points, then the forecast URL it names. Read-only, no
/// credentials. Each run stores verbatim bodies, then one row per period with the NWS update time (publication) and
/// our retrieval time kept apart, so a later evaluation can use only forecasts available before a decision.
/// </summary>
public sealed class NwsForecastCollector(Db db, HttpClient http, TimeProvider time, string dataOrigin, string baseUrl = "https://api.weather.gov")
{
    public const string UserAgent = "kalshi-bot/0.4 (personal research; read-only)";

    public async Task<ForecastCollectSummary> CollectAsync(ForecastLocation location, CancellationToken ct = default)
    {
        var bound = DatabaseOrigin.Get(db);
        if (bound is not null && DataOrigin.IsSynthetic(bound) != DataOrigin.IsSynthetic(dataOrigin))
            throw new KalshiBotException($"{db.Path} holds {bound} data; collecting {dataOrigin} forecasts into it would blend datasets.");

        var runId = db.Insert("INSERT INTO forecast_runs (started_at, status, source, data_origin, location) VALUES ($t, 'running', 'nws:api.weather.gov', $o, $l)",
            ("$t", Ts.ToText(time.GetUtcNow())), ("$o", dataOrigin), ("$l", location.Key));
        var errors = new List<string>();
        var stored = 0;
        NwsForecast? forecast = null;
        try
        {
            var lat = location.Latitude.ToString("0.####", CultureInfo.InvariantCulture);
            var lon = location.Longitude.ToString("0.####", CultureInfo.InvariantCulture);
            var points = await GetAsync(runId, $"{baseUrl}/points/{lat},{lon}", ct);
            if (points.Status != 200) errors.Add($"points HTTP {points.Status}");
            else
            {
                var (url, grid) = NwsParse.Points(points.Body);
                if (baseUrl != "https://api.weather.gov") url = baseUrl + new Uri(url).PathAndQuery; // fixture/test base
                var fc = await GetAsync(runId, url, ct);
                if (fc.Status != 200) errors.Add($"forecast HTTP {fc.Status}");
                else
                {
                    forecast = NwsParse.Forecast(fc.Body, grid);
                    db.InTransaction(() =>
                    {
                        foreach (var p in forecast.Periods)
                            stored += db.Exec("""
                                INSERT INTO nws_forecast_periods (run_id, raw_id, data_origin, location, grid, generated_at, update_time, received_at,
                                    period_number, name, start_time, end_time, local_date, is_daytime, temperature, temperature_unit, short_forecast)
                                VALUES ($r, $raw, $o, $l, $g, $ga, $ut, $rc, $n, $nm, $st, $et, $ld, $d, $tp, $u, $sf)
                                """,
                                ("$r", runId), ("$raw", fc.RawId), ("$o", dataOrigin), ("$l", location.Key), ("$g", grid),
                                ("$ga", T(forecast.GeneratedAt)), ("$ut", T(forecast.UpdateTime)), ("$rc", Ts.ToText(fc.ReceivedAt)),
                                ("$n", p.Number), ("$nm", p.Name), ("$st", p.Start.ToString("O", CultureInfo.InvariantCulture)),
                                ("$et", p.End.ToString("O", CultureInfo.InvariantCulture)), ("$ld", p.LocalDate), ("$d", p.IsDaytime ? 1 : 0),
                                ("$tp", p.Temperature), ("$u", p.TemperatureUnit), ("$sf", p.ShortForecast));
                    });
                }
            }
        }
        catch (Exception e) when (e is FormatException or JsonException or KeyNotFoundException or InvalidOperationException
                                    or HttpRequestException || (e is TaskCanceledException && !ct.IsCancellationRequested))
        {
            errors.Add($"{e.GetType().Name}: {e.Message}");
        }
        var status = errors.Count == 0 ? "completed" : "failed";
        db.Exec("UPDATE forecast_runs SET status = $s, finished_at = $f, detail = $d WHERE id = $id",
            ("$s", status), ("$f", Ts.ToText(time.GetUtcNow())), ("$d", errors.Count == 0 ? null : string.Join("; ", errors)), ("$id", runId));
        return new ForecastCollectSummary(runId, status, stored, forecast?.UpdateTime, errors);
    }

    /// <summary>Daytime-high forecast vintages per local date, newest retrieval first.</summary>
    public static List<DailyHighVintage> DailyHighs(Db db, string location, int limit = 30) =>
        db.Query("""
            SELECT local_date, temperature, temperature_unit, update_time, received_at, data_origin FROM nws_forecast_periods
            WHERE location = $l AND is_daytime = 1 ORDER BY local_date DESC, received_at DESC LIMIT $n
            """, r => new DailyHighVintage(r.S("local_date"), (int)r.L("temperature"), r.S("temperature_unit"),
                Ts.ParseExternal(r.Str("update_time")), Ts.Parse(r.S("received_at")), r.S("data_origin")),
            ("$l", location), ("$n", limit));

    private async Task<(int Status, string Body, long RawId, DateTimeOffset ReceivedAt)> GetAsync(long runId, string url, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var requested = time.GetUtcNow();
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd(UserAgent); // api.weather.gov requires a User-Agent
            req.Headers.Accept.ParseAdd("application/geo+json");
            using var resp = await http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            var received = time.GetUtcNow();
            var status = (int)resp.StatusCode;
            if ((status >= 500 || resp.StatusCode == HttpStatusCode.TooManyRequests) && attempt < 2)
            {
                await Task.Delay(TimeSpan.FromSeconds(1 + attempt), time, ct);
                continue;
            }
            var rawId = db.Insert("""
                INSERT INTO forecast_raw (run_id, url, http_status, requested_at, received_at, server_date, body_sha256, body)
                VALUES ($r, $u, $s, $rq, $rc, $sd, $h, $b)
                """, ("$r", runId), ("$u", url), ("$s", status), ("$rq", Ts.ToText(requested)), ("$rc", Ts.ToText(received)),
                ("$sd", resp.Headers.Date is { } d ? Ts.ToText(d) : null), ("$h", Hash.Sha256Hex(body)), ("$b", body));
            return (status, body, rawId, received);
        }
    }

    private static string? T(DateTimeOffset? t) => t is null ? null : Ts.ToText(t.Value);
}

/// <summary>Serves fixtures/synthetic/nws/{points.json, forecast.json} for /points/* and /gridpoints/*/forecast.</summary>
public sealed class NwsFixtureHandler(string root) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.Method != HttpMethod.Get) throw new InvalidOperationException("Fixture handler is read-only.");
        var path = request.RequestUri!.AbsolutePath;
        var file = path.StartsWith("/points/", StringComparison.Ordinal) ? "points.json"
            : path.StartsWith("/gridpoints/", StringComparison.Ordinal) && path.EndsWith("/forecast", StringComparison.Ordinal) ? "forecast.json" : null;
        var full = file is null ? null : Path.Combine(root, file);
        return Task.FromResult(full is not null && File.Exists(full)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(File.ReadAllText(full), System.Text.Encoding.UTF8, "application/geo+json") }
            : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") });
    }
}
