using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KalshiBot.Core.Common;
using KalshiBot.Core.Kalshi;
using KalshiBot.Core.Recording;
using KalshiBot.Core.Storage;
using KalshiBot.Core.Truth;

namespace KalshiBot.Core.Archive;

/// <summary>The archive database holds historical inputs and is kept apart from truth.db and from any recording database (spec 8.1).</summary>
public static class ArchiveOrigin
{
    public const string Database = "archive:history";
}

public sealed record MosOptions
{
    /// <summary>IEM answered a second request made seconds after the first with 429, so this is deliberately slow.</summary>
    public TimeSpan MinInterval { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(20);
    public int MaxRetries { get; init; } = 4;
}

public sealed record MosCollectSummary(long RunId, string Status, int Years, int Rows, int Inserted, int Unchanged, int Conflicts, IReadOnlyList<string> Errors);

public sealed record CandleTarget(string MarketTicker, string EventTicker, DateOnly ClimateDate);

public sealed record CandleCollectSummary(long RunId, string Status, int Targets, int Fetched, int AlreadyHave, int NotYetComplete, int Failed,
    int Candles, IReadOnlyList<string> Errors);

/// <summary>
/// Downloads GFS MOS runs for a station from IEM's bulk CSV endpoint, one request per year. Bodies are stored verbatim.
/// A run is immutable, so a stored row is never replaced: a re-download that disagrees with it is reported as a conflict.
/// </summary>
public sealed class IemMosCollector(Db db, HttpClient http, TimeProvider time, string baseUrl = "https://mesonet.agron.iastate.edu", MosOptions? options = null)
{
    public const string UserAgent = "kalshi-bot/0.4 (personal research; read-only)";
    private static readonly string[] Models = ["GFS"];
    private readonly MosOptions _options = options ?? new MosOptions();
    private DateTimeOffset _last = DateTimeOffset.MinValue;

    public async Task<MosCollectSummary> CollectAsync(string station, string model, int fromYear, int toYear, CancellationToken ct = default)
    {
        station = TruthStore.ValidateStation(station);
        if (!Models.Contains(model, StringComparer.Ordinal)) throw new KalshiBotException($"Unsupported MOS model '{model}'. Supported: {string.Join(", ", Models)}.");
        if (fromYear < 1990 || toYear < fromYear || toYear - fromYear > 40) throw new KalshiBotException($"Unreasonable year range {fromYear}-{toYear}.");
        DatabaseOrigin.BindOrThrow(db, ArchiveOrigin.Database, time);
        ArchiveStore.RecoverAbandonedRuns(db, time);
        var runId = ArchiveStore.StartRun(db, time, "iem-mos", TruthOrigin.Iem, $"{station} {model} {fromYear}-{toYear}");

        var errors = new List<string>();
        int years = 0, rows = 0, inserted = 0, unchanged = 0, conflicts = 0;
        try
        {
            for (var year = fromYear; year <= toYear; year++)
            {
                var start = new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero);
                if (start >= time.GetUtcNow()) break;
                var end = new DateTimeOffset(year + 1, 1, 1, 0, 0, 0, TimeSpan.Zero);
                var url = $"{baseUrl}/cgi-bin/request/mos.py?station={Uri.EscapeDataString(station)}&model={Uri.EscapeDataString(model)}" +
                          $"&sts={start:yyyy-MM-ddTHH:mm}Z&ets={end:yyyy-MM-ddTHH:mm}Z&format=csv";
                var raw = await GetAsync(url, ct);
                var rawId = ArchiveStore.StoreRaw(db, runId, raw);
                if (!raw.IsSuccess) { errors.Add($"{year}: HTTP {raw.Status}"); continue; }
                List<MosRow> parsed;
                try { parsed = MosCsv.Parse(raw.Body); }
                catch (FormatException e) { errors.Add($"{year}: {e.Message}"); continue; }

                years++;
                db.InTransaction(() =>
                {
                    foreach (var r in parsed.Where(r => r.Station == station && r.Model == model))
                    {
                        rows++;
                        switch (ArchiveStore.AppendMos(db, runId, rawId, TruthOrigin.Iem, r))
                        {
                            case AppendResult.Inserted: inserted++; break;
                            case AppendResult.Unchanged: unchanged++; break;
                            default:
                                conflicts++;
                                if (conflicts <= 10) errors.Add($"{year}: stored MOS row differs from download ({r.Runtime:yyyy-MM-dd HH}Z, {r.ForecastTime:yyyy-MM-dd HH}Z)");
                                break;
                        }
                    }
                });
            }
        }
        catch (Exception e) when (e is HttpRequestException || (e is TaskCanceledException && !ct.IsCancellationRequested))
        {
            errors.Add(e is TaskCanceledException ? "request timed out" : $"{e.GetType().Name}: {e.Message}");
        }
        catch (Exception e)
        {
            errors.Add($"aborted: {e.GetType().Name}: {e.Message}");
            ArchiveStore.FinishRun(db, time, runId, "failed", errors);
            throw;
        }

        var status = errors.Count == 0 ? "completed" : years > 0 ? "partial" : "failed";
        ArchiveStore.FinishRun(db, time, runId, status, errors);
        return new MosCollectSummary(runId, status, years, rows, inserted, unchanged, conflicts, errors);
    }

    private async Task<RawHttpResponse> GetAsync(string url, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var due = _last + _options.MinInterval;
            if (due > time.GetUtcNow()) await Task.Delay(due - time.GetUtcNow(), time, ct);
            _last = time.GetUtcNow();
            var requested = time.GetUtcNow();
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd(UserAgent);
            using var resp = await http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            var status = (int)resp.StatusCode;
            if ((resp.StatusCode == HttpStatusCode.TooManyRequests || status >= 500) && attempt < _options.MaxRetries)
            {
                await Task.Delay(resp.Headers.RetryAfter?.Delta ?? _options.RetryDelay * (attempt + 1), time, ct);
                continue;
            }
            return new RawHttpResponse("GET", url, status, requested, time.GetUtcNow(), resp.Headers.Date, body);
        }
    }
}

/// <summary>
/// Fetches hourly candles for each target market over its decision-day window, one request per market, resumable: a
/// window already recorded is skipped. Recent markets are read from the series endpoint and older ones from the
/// historical endpoint (chosen from Kalshi's cutoff), with the other tried once on a 404. Raw bodies are stored first.
/// </summary>
public sealed class KalshiCandleCollector(Db db, KalshiPublicClient client, TimeProvider time)
{
    public const int PeriodMinutes = 60;
    public const int MaxConsecutiveFailures = 20;
    private const int MaxErrorsListed = 25;
    /// <summary>A window is only fetched once its end is this far in the past, so candles are final.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromHours(2);

    public async Task<CandleCollectSummary> CollectAsync(IReadOnlyList<CandleTarget> targets, Action<string>? progress = null, CancellationToken ct = default)
    {
        DatabaseOrigin.BindOrThrow(db, ArchiveOrigin.Database, time);
        ArchiveStore.RecoverAbandonedRuns(db, time);
        var runId = ArchiveStore.StartRun(db, time, "kalshi-candles", TruthOrigin.Kalshi, $"{targets.Count} market(s)");
        var errors = new List<string>();
        int fetched = 0, already = 0, incomplete = 0, failed = 0, candles = 0, consecutive = 0;

        try
        {
            var cutoffRaw = await client.GetHistoricalCutoffAsync(ct);
            ArchiveStore.StoreRaw(db, runId, cutoffRaw);
            DateOnly cutoff;
            try
            {
                if (!cutoffRaw.IsSuccess) throw new FormatException($"HTTP {cutoffRaw.Status}");
                var ts = JsonNode.Parse(cutoffRaw.Body)?["market_settled_ts"]?.GetValue<string>() ?? throw new FormatException("no market_settled_ts");
                cutoff = DateOnly.FromDateTime(Ts.ParseExternal(ts)!.Value.UtcDateTime);
            }
            catch (Exception e) when (e is FormatException or JsonException or InvalidOperationException)
            {
                errors.Add($"historical cutoff unavailable: {e.Message}");
                ArchiveStore.FinishRun(db, time, runId, "failed", errors);
                return new CandleCollectSummary(runId, "failed", targets.Count, 0, 0, 0, 0, 0, errors);
            }

            var done = 0;
            foreach (var t in targets.OrderBy(t => t.ClimateDate).ThenBy(t => t.MarketTicker, StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                if (++done % 250 == 0) progress?.Invoke($"{done}/{targets.Count} markets ({fetched} fetched, {already} already stored, {failed} failed)");
                var (start, end) = DecisionClock.CandleWindow(t.ClimateDate);
                if (end > time.GetUtcNow() - Settle) { incomplete++; continue; }
                if (ArchiveStore.HasFetch(db, t.MarketTicker, start, end, PeriodMinutes)) { already++; continue; }

                var series = t.MarketTicker.Split('-')[0];
                var historicalFirst = t.ClimateDate.AddDays(1) < cutoff;
                var (raw, rawId, historical) = await FetchAsync(runId, series, t.MarketTicker, start, end, historicalFirst, ct);
                if (!raw.IsSuccess)
                {
                    failed++; consecutive++;
                    if (errors.Count < MaxErrorsListed) errors.Add($"{t.MarketTicker}: HTTP {raw.Status}");
                    if (consecutive >= MaxConsecutiveFailures) { errors.Add($"aborted after {MaxConsecutiveFailures} consecutive failures"); break; }
                    continue;
                }
                List<CandleRow> rows;
                try { rows = CandleParse.Page(raw.Body); }
                catch (Exception e) when (e is FormatException or JsonException or InvalidOperationException)
                {
                    failed++; consecutive++;
                    if (errors.Count < MaxErrorsListed) errors.Add($"{t.MarketTicker}: {e.Message}");
                    if (consecutive >= MaxConsecutiveFailures) { errors.Add($"aborted after {MaxConsecutiveFailures} consecutive failures"); break; }
                    continue;
                }
                consecutive = 0;
                ArchiveStore.AppendFetch(db, runId, rawId, TruthOrigin.Kalshi, t, start, end, PeriodMinutes, historical, rows, raw.ReceivedAt);
                fetched++;
                candles += rows.Count;
            }
        }
        catch (Exception e) when (e is HttpRequestException || (e is TaskCanceledException && !ct.IsCancellationRequested))
        {
            errors.Add(e is TaskCanceledException ? "request timed out" : $"{e.GetType().Name}: {e.Message}");
        }
        catch (Exception e)
        {
            errors.Add($"aborted: {e.GetType().Name}: {e.Message}");
            ArchiveStore.FinishRun(db, time, runId, "failed", errors);
            throw;
        }

        var status = errors.Count == 0 ? "completed" : fetched + already > 0 ? "partial" : "failed";
        ArchiveStore.FinishRun(db, time, runId, status, errors);
        return new CandleCollectSummary(runId, status, targets.Count, fetched, already, incomplete, failed, candles, errors);
    }

    private async Task<(RawHttpResponse Raw, long RawId, bool Historical)> FetchAsync(long runId, string series, string ticker, DateTimeOffset start,
        DateTimeOffset end, bool historicalFirst, CancellationToken ct)
    {
        var first = await client.GetCandlesticksAsync(series, ticker, start, end, PeriodMinutes, historicalFirst, ct);
        var firstId = ArchiveStore.StoreRaw(db, runId, first);
        if (first.Status != 404) return (first, firstId, historicalFirst);
        var second = await client.GetCandlesticksAsync(series, ticker, start, end, PeriodMinutes, !historicalFirst, ct);
        return (second, ArchiveStore.StoreRaw(db, runId, second), !historicalFirst);
    }
}

public enum AppendResult { Inserted, Unchanged, Conflict }

/// <summary>The latest candle at or before a time. A side is "two-sided" when both a real bid and a real ask are quoted.</summary>
public sealed record CandleQuote(DateTimeOffset EndPeriod, decimal? Bid, decimal? Ask)
{
    public bool TwoSided => Bid is > 0m && Ask is < 1m && Ask >= Bid;
    public decimal? Mid => TwoSided ? (Bid + Ask) / 2m : null;
}

public sealed record ArchiveReport(
    string Station, long MosRows, long MosRuns, long MosRuns12Z, DateTimeOffset? MosFirstRun, DateTimeOffset? MosLastRun,
    long CandleFetches, long CandleRows, long MarketsWithCandles, long EventDays, long EventDaysAnyQuote, long EventDaysAllQuoted,
    IReadOnlyList<(int Year, long Events, long AnyQuote)> ByYear)
{
    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"archive report ({Station})");
        sb.AppendLine($"  GFS MOS: {MosRows} row(s), {MosRuns} run(s) ({MosRuns12Z} at 12Z)" +
                      (MosFirstRun is { } a && MosLastRun is { } b ? $", {a:yyyy-MM-dd} to {b:yyyy-MM-dd}" : ""));
        sb.AppendLine($"  Kalshi candles: {CandleRows} hourly candle(s) from {CandleFetches} fetched window(s), {MarketsWithCandles} market(s) with data");
        sb.AppendLine($"  Event-days fetched: {EventDays}. Two-sided quote at the decision time: on at least one market {EventDaysAnyQuote}, on every fetched market {EventDaysAllQuoted}");
        foreach (var y in ByYear) sb.AppendLine($"    {y.Year}: {y.AnyQuote}/{y.Events} event-days with a two-sided quote");
        sb.AppendLine("  Candles are hourly quote closes, not executable depth: good for a probability baseline, never for fills.");
        sb.AppendLine($"  MOS availability is ASSUMED to be cycle time + {DecisionClock.AssumedMosLag.TotalHours:0} h (publication time is not in the archive).");
        return sb.ToString();
    }
}

public static class ArchiveStore
{
    public static int RecoverAbandonedRuns(Db db, TimeProvider time) =>
        db.Exec("UPDATE archive_runs SET status = 'abandoned', finished_at = $t, detail = COALESCE(detail, 'process ended before run finished') WHERE status = 'running'",
            ("$t", Ts.ToText(time.GetUtcNow())));

    public static long StartRun(Db db, TimeProvider time, string source, string origin, string scope) =>
        db.Insert("INSERT INTO archive_runs (started_at, status, source, data_origin, scope) VALUES ($s, 'running', $src, $o, $sc)",
            ("$s", Ts.ToText(time.GetUtcNow())), ("$src", source), ("$o", origin), ("$sc", scope));

    public static void FinishRun(Db db, TimeProvider time, long runId, string status, IReadOnlyList<string> errors) =>
        db.Exec("UPDATE archive_runs SET status = $st, finished_at = $f, detail = $d WHERE id = $id",
            ("$st", status), ("$f", Ts.ToText(time.GetUtcNow())), ("$d", errors.Count == 0 ? null : string.Join("; ", errors)), ("$id", runId));

    public static long StoreRaw(Db db, long runId, RawHttpResponse r) =>
        db.Insert("""
            INSERT INTO archive_raw (run_id, url, http_status, requested_at, received_at, server_date, body_sha256, body)
            VALUES ($r, $u, $s, $rq, $rc, $sd, $h, $b)
            """,
            ("$r", runId), ("$u", r.Url), ("$s", r.Status), ("$rq", Ts.ToText(r.RequestedAt)), ("$rc", Ts.ToText(r.ReceivedAt)),
            ("$sd", r.ServerDate is null ? null : Ts.ToText(r.ServerDate.Value)), ("$h", Hash.Sha256Hex(r.Body)), ("$b", r.Body));

    private static string? N(decimal? d) => d is null ? null : Dec.ToText(Dec.Normalize(d.Value));

    public static AppendResult AppendMos(Db db, long runId, long rawId, string origin, MosRow r)
    {
        var inserted = db.Exec("""
            INSERT INTO mos_rows (run_id, raw_id, data_origin, station, model, runtime, ftime, n_x, tmp)
            VALUES ($r, $raw, $o, $s, $m, $rt, $ft, $nx, $tmp) ON CONFLICT (station, model, runtime, ftime) DO NOTHING
            """,
            ("$r", runId), ("$raw", rawId), ("$o", origin), ("$s", r.Station), ("$m", r.Model), ("$rt", Ts.ToText(r.Runtime)),
            ("$ft", Ts.ToText(r.ForecastTime)), ("$nx", N(r.NX)), ("$tmp", N(r.Tmp)));
        if (inserted == 1) return AppendResult.Inserted;
        var stored = db.Query("SELECT n_x, tmp FROM mos_rows WHERE station = $s AND model = $m AND runtime = $rt AND ftime = $ft",
            x => (NX: x.Str("n_x"), Tmp: x.Str("tmp")), ("$s", r.Station), ("$m", r.Model), ("$rt", Ts.ToText(r.Runtime)), ("$ft", Ts.ToText(r.ForecastTime)));
        return stored.Count == 1 && stored[0].NX == N(r.NX) && stored[0].Tmp == N(r.Tmp) ? AppendResult.Unchanged : AppendResult.Conflict;
    }

    /// <summary>The MOS daily-max forecast for a climate date from one run, or null when the run or value is not stored.</summary>
    public static decimal? MosDailyMax(Db db, string station, string model, DateTimeOffset runtime, DateOnly climateDate) =>
        db.Query("SELECT n_x FROM mos_rows WHERE station = $s AND model = $m AND runtime = $rt AND ftime = $ft",
                x => x.DN("n_x"), ("$s", station), ("$m", model), ("$rt", Ts.ToText(runtime)), ("$ft", Ts.ToText(DecisionClock.MosMaxForecastTime(climateDate))))
            .FirstOrDefault();

    public static bool HasFetch(Db db, string ticker, DateTimeOffset start, DateTimeOffset end, int period) =>
        db.Scalar<long>("SELECT COUNT(*) FROM candle_fetches WHERE market_ticker = $t AND window_start = $s AND window_end = $e AND period_minutes = $p",
            ("$t", ticker), ("$s", Ts.ToText(start)), ("$e", Ts.ToText(end)), ("$p", period)) > 0;

    public static void AppendFetch(Db db, long runId, long rawId, string origin, CandleTarget t, DateTimeOffset start, DateTimeOffset end, int period,
        bool historical, IReadOnlyList<CandleRow> rows, DateTimeOffset receivedAt) =>
        db.InTransaction(() =>
        {
            var fetchId = db.Insert("""
                INSERT INTO candle_fetches (run_id, raw_id, data_origin, market_ticker, event_ticker, window_start, window_end, period_minutes, endpoint, candle_count, received_at)
                VALUES ($r, $raw, $o, $t, $e, $s, $en, $p, $ep, $n, $at)
                """,
                ("$r", runId), ("$raw", rawId), ("$o", origin), ("$t", t.MarketTicker), ("$e", t.EventTicker), ("$s", Ts.ToText(start)),
                ("$en", Ts.ToText(end)), ("$p", period), ("$ep", historical ? "historical" : "series"), ("$n", rows.Count), ("$at", Ts.ToText(receivedAt)));
            foreach (var c in rows)
                db.Exec("""
                    INSERT INTO candles (fetch_id, data_origin, market_ticker, end_period_ts, yes_bid_close, yes_ask_close, price_close, volume, open_interest)
                    VALUES ($f, $o, $t, $ts, $b, $a, $p, $v, $oi)
                    """,
                    ("$f", fetchId), ("$o", origin), ("$t", t.MarketTicker), ("$ts", Ts.ToText(c.EndPeriod)), ("$b", N(c.YesBidClose)),
                    ("$a", N(c.YesAskClose)), ("$p", N(c.PriceClose)), ("$v", N(c.Volume)), ("$oi", N(c.OpenInterest)));
        });

    /// <summary>The last candle ending at or before <paramref name="at"/> and no older than <paramref name="maxAge"/>, else null.</summary>
    public static CandleQuote? QuoteAtOrBefore(Db db, string ticker, DateTimeOffset at, TimeSpan maxAge) =>
        db.Query("""
            SELECT end_period_ts, yes_bid_close, yes_ask_close FROM candles
            WHERE market_ticker = $t AND end_period_ts <= $at AND end_period_ts >= $min ORDER BY end_period_ts DESC LIMIT 1
            """,
            r => new CandleQuote(Ts.Parse(r.S("end_period_ts")), r.DN("yes_bid_close"), r.DN("yes_ask_close")),
            ("$t", ticker), ("$at", Ts.ToText(at)), ("$min", Ts.ToText(at - maxAge))).FirstOrDefault();

    /// <summary>Every market of the settled, unflagged events in a truth database, with the event's climate date.</summary>
    public static List<CandleTarget> TargetsFromTruth(Db truth)
    {
        var okEvents = TruthStore.CurrentSettled(truth).Where(s => s.Status == "ok").ToDictionary(s => s.EventTicker, s => s.ClimateDate, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var targets = new List<CandleTarget>();
        foreach (var body in truth.Query("SELECT body FROM truth_raw WHERE http_status = 200 AND url LIKE '%markets%' ORDER BY id", r => r.S("body")))
        {
            var (markets, _) = TruthParse.SettledMarkets(body);
            foreach (var m in markets)
                if (okEvents.TryGetValue(m.EventTicker, out var date) && seen.Add(m.Ticker))
                    targets.Add(new CandleTarget(m.Ticker, m.EventTicker, date));
        }
        return targets;
    }

    public static ArchiveReport Report(Db db, string station)
    {
        var mosRows = db.Scalar<long>("SELECT COUNT(*) FROM mos_rows WHERE station = $s", ("$s", station));
        var runs = db.Query("SELECT DISTINCT runtime FROM mos_rows WHERE station = $s ORDER BY runtime", r => Ts.Parse(r.S("runtime")), ("$s", station));

        var fetches = db.Query("SELECT market_ticker, event_ticker FROM candle_fetches", r => (Ticker: r.S("market_ticker"), Event: r.S("event_ticker")));
        var byEvent = fetches.GroupBy(f => f.Event, StringComparer.Ordinal).ToList();
        long any = 0, all = 0;
        var years = new SortedDictionary<int, (long Events, long Any)>();
        foreach (var g in byEvent)
        {
            if (TruthParse.EventDate(g.Key) is not { } date) continue;
            var decision = DecisionClock.DecisionTimeUtc(date);
            var quotes = g.Select(f => QuoteAtOrBefore(db, f.Ticker, decision, TimeSpan.FromHours(3))).ToList();
            var anyQuote = quotes.Any(q => q is { TwoSided: true });
            if (anyQuote) any++;
            if (quotes.All(q => q is { TwoSided: true })) all++;
            years.TryGetValue(date.Year, out var y);
            years[date.Year] = (y.Events + 1, y.Any + (anyQuote ? 1 : 0));
        }

        return new ArchiveReport(station, mosRows, runs.Count, runs.Count(r => r.Hour == 12), runs.Count == 0 ? null : runs[0], runs.Count == 0 ? null : runs[^1],
            db.Scalar<long>("SELECT COUNT(*) FROM candle_fetches"), db.Scalar<long>("SELECT COUNT(*) FROM candles"),
            db.Scalar<long>("SELECT COUNT(DISTINCT market_ticker) FROM candles"), byEvent.Count, any, all,
            years.Select(k => (k.Key, k.Value.Events, k.Value.Any)).ToList());
    }
}
