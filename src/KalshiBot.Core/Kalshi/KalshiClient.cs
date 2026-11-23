using System.Net;
using KalshiBot.Core.Common;

namespace KalshiBot.Core.Kalshi;

public sealed record KalshiEndpoint(string Name, string BaseUrl, string DataOrigin)
{
    // Hosts verified against Kalshi's official SDK (kalshi-python-sync / kalshi-typescript 3.36.0) host settings.
    public static readonly KalshiEndpoint Production =
        new("production", "https://external-api.kalshi.com/trade-api/v2", Common.DataOrigin.LiveProduction);

    public static readonly KalshiEndpoint Demo =
        new("demo", "https://external-api.demo.kalshi.co/trade-api/v2", Common.DataOrigin.LiveDemo);

    /// <summary>Synthetic fixtures served by <see cref="FixtureHandler"/>; base URL is a non-routable marker.</summary>
    public static readonly KalshiEndpoint Fixture =
        new("fixture", "https://fixture.invalid/trade-api/v2", Common.DataOrigin.Synthetic);
}

public sealed record RawHttpResponse(
    string Method, string Url, int Status, DateTimeOffset RequestedAt, DateTimeOffset ReceivedAt,
    DateTimeOffset? ServerDate, string Body)
{
    public bool IsSuccess => Status is >= 200 and < 300;
}

public sealed record KalshiClientOptions
{
    /// <summary>Minimum spacing between requests. Conservative; Kalshi's published tier limits were not verifiable from this build environment.</summary>
    public TimeSpan MinInterval { get; init; } = TimeSpan.FromMilliseconds(250);
    public int MaxRetries { get; init; } = 3;
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Read-only client for Kalshi's public market-data endpoints. These endpoints need no API key, so this
/// phase holds no Kalshi credentials at all. The client cannot send anything but GET.
/// </summary>
public sealed class KalshiPublicClient(HttpClient http, KalshiEndpoint endpoint, TimeProvider time, KalshiClientOptions? options = null)
{
    private readonly KalshiClientOptions _options = options ?? new KalshiClientOptions();
    private DateTimeOffset _lastRequest = DateTimeOffset.MinValue;

    public KalshiEndpoint Endpoint => endpoint;

    public Task<RawHttpResponse> GetMarketAsync(string ticker, CancellationToken ct) =>
        GetAsync($"/markets/{Esc(ticker)}", ct);

    public Task<RawHttpResponse> GetOrderbookAsync(string ticker, CancellationToken ct) =>
        GetAsync($"/markets/{Esc(ticker)}/orderbook", ct);

    public Task<RawHttpResponse> GetEventAsync(string eventTicker, CancellationToken ct) =>
        GetAsync($"/events/{Esc(eventTicker)}", ct);

    public Task<RawHttpResponse> GetSeriesAsync(string seriesTicker, CancellationToken ct) =>
        GetAsync($"/series/{Esc(seriesTicker)}", ct);

    public Task<RawHttpResponse> ListOpenMarketsAsync(string seriesTicker, string? cursor, CancellationToken ct) =>
        GetAsync($"/markets?series_ticker={Esc(seriesTicker)}&status=open&limit=200" +
                 (string.IsNullOrEmpty(cursor) ? "" : $"&cursor={Esc(cursor)}"), ct);

    /// <summary>
    /// Settled markets of a series. Kalshi keeps recent settled markets on <c>/markets</c> and older ones on
    /// <c>/historical/markets</c> (see <c>/historical/cutoff</c>); a full history needs both.
    /// </summary>
    public Task<RawHttpResponse> ListSettledMarketsAsync(string seriesTicker, string? cursor, bool historical, CancellationToken ct) =>
        GetAsync((historical ? "/historical/markets" : "/markets") + $"?series_ticker={Esc(seriesTicker)}" +
                 (historical ? "" : "&status=settled") + "&limit=1000" +
                 (string.IsNullOrEmpty(cursor) ? "" : $"&cursor={Esc(cursor)}"), ct);

    /// <summary>Boundary between the live and the /historical partitions (<c>market_settled_ts</c>).</summary>
    public Task<RawHttpResponse> GetHistoricalCutoffAsync(CancellationToken ct) => GetAsync("/historical/cutoff", ct);

    /// <summary>
    /// Candlesticks for one market. The series path serves recent markets (and, verified for Sep 2026, some older ones);
    /// the historical path serves markets before the cutoff. They answer with different field names for the same data.
    /// </summary>
    public Task<RawHttpResponse> GetCandlesticksAsync(string seriesTicker, string marketTicker, DateTimeOffset start, DateTimeOffset end,
        int periodMinutes, bool historical, CancellationToken ct) =>
        GetAsync((historical ? $"/historical/markets/{Esc(marketTicker)}/candlesticks" : $"/series/{Esc(seriesTicker)}/markets/{Esc(marketTicker)}/candlesticks") +
                 $"?start_ts={start.ToUnixTimeSeconds()}&end_ts={end.ToUnixTimeSeconds()}&period_interval={periodMinutes}", ct);

    private static string Esc(string s) => Uri.EscapeDataString(s);

    private async Task<RawHttpResponse> GetAsync(string relative, CancellationToken ct)
    {
        var url = endpoint.BaseUrl + relative;
        for (var attempt = 0; ; attempt++)
        {
            await PaceAsync(ct);
            var requestedAt = time.GetUtcNow();
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Accept.ParseAdd("application/json");
            using var resp = await http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            var receivedAt = time.GetUtcNow();
            var status = (int)resp.StatusCode;

            var retryable = resp.StatusCode == HttpStatusCode.TooManyRequests || status >= 500;
            if (retryable && attempt < _options.MaxRetries)
            {
                var wait = resp.Headers.RetryAfter?.Delta
                           ?? TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt));
                await Task.Delay(wait > _options.MaxBackoff ? _options.MaxBackoff : wait, time, ct);
                continue;
            }

            return new RawHttpResponse("GET", url, status, requestedAt, receivedAt, resp.Headers.Date, body);
        }
    }

    private async Task PaceAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var due = _lastRequest + _options.MinInterval;
        if (due > now) await Task.Delay(due - now, time, ct);
        _lastRequest = time.GetUtcNow();
    }
}

/// <summary>
/// Serves synthetic Kalshi-shaped JSON from a directory so the pipeline can run offline.
/// Path mapping: /trade-api/v2/markets/X -> markets/X.json, /markets/X/orderbook -> orderbooks/X.json,
/// /events/X -> events/X.json, /series/X -> series/X.json, /markets?series_ticker=S -> lists/S.json. Missing file -> 404.
/// </summary>
public sealed class FixtureHandler(string root) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.Method != HttpMethod.Get) throw new InvalidOperationException("Fixture handler is read-only.");
        var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
        var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query);
        const string prefix = "/trade-api/v2/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal)) return Task.FromResult(NotFound());
        var parts = path[prefix.Length..].Split('/');
        string? file = parts switch
        {
            ["markets"] when query["series_ticker"] is { } st => System.IO.Path.Combine(root, "lists", st + ".json"),
            ["markets", var t] => System.IO.Path.Combine(root, "markets", t + ".json"),
            ["markets", var t, "orderbook"] => System.IO.Path.Combine(root, "orderbooks", t + ".json"),
            ["events", var e] => System.IO.Path.Combine(root, "events", e + ".json"),
            ["series", var s] => System.IO.Path.Combine(root, "series", s + ".json"),
            _ => null,
        };
        if (file is null || parts.Any(p => p.Contains("..")) || (query["series_ticker"]?.Contains("..") ?? false) || !File.Exists(file))
            return Task.FromResult(NotFound());
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(File.ReadAllText(file), System.Text.Encoding.UTF8, "application/json"),
        });
    }

    private static HttpResponseMessage NotFound() =>
        new(HttpStatusCode.NotFound) { Content = new StringContent("{\"error\":\"not found (fixture)\"}") };
}
