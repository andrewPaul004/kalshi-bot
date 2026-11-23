using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using KalshiBot.Core.Common;
using KalshiBot.Core.Kalshi;
using KalshiBot.Core.Recording;
using KalshiBot.Core.Storage;

namespace KalshiBot.Core.Truth;

/// <summary>
/// The truth database is separate from any recording database (spec 8.1). The database itself binds one origin; each row
/// also names its own source so the two archives stay distinguishable inside it.
/// </summary>
public static class TruthOrigin
{
    public const string Database = "archive:truth";
    public const string Kalshi = "archive:kalshi";
    public const string Iem = "archive:iem";
}

public sealed record TruthCollectSummary(long RunId, string Status, int Pages, int MarketsSeen, int EventsWithValue, int NewVintages,
    int Unchanged, int Conflicts, int Inconsistent, int NoValue, IReadOnlyList<string> Errors);

public sealed record CliCollectSummary(long RunId, string Status, int Years, int Days, int NewVintages, int Unchanged, IReadOnlyList<string> Errors);

/// <summary>
/// Collects what KXHIGHNY-style daily markets settled on, from Kalshi's public settled-market lists (live partition, then
/// <c>/historical</c>). Pages are stored verbatim first. Per event it appends a row only when the value, regime or status
/// differs from the latest stored vintage, so a later revision is kept beside the original (spec 8.2).
/// </summary>
public sealed class KalshiTruthCollector(Db db, KalshiPublicClient client, TimeProvider time)
{
    public const int MaxPagesPerPartition = 200;

    public async Task<TruthCollectSummary> CollectAsync(string series, CancellationToken ct = default)
    {
        series = TruthStore.ValidateSeries(series);
        DatabaseOrigin.BindOrThrow(db, TruthOrigin.Database, time);
        TruthStore.RecoverAbandonedRuns(db, time);
        var runId = TruthStore.StartRun(db, time, "kalshi", TruthOrigin.Kalshi, series);

        var byTicker = new Dictionary<string, (SettledMarket Market, long RawId)>(StringComparer.Ordinal);
        var errors = new List<string>();
        var pages = 0;
        try
        {
            foreach (var historical in new[] { false, true })
            {
                string? cursor = null;
                for (var page = 0; ; page++)
                {
                    if (page >= MaxPagesPerPartition) { errors.Add($"{(historical ? "historical" : "live")}: page limit {MaxPagesPerPartition} reached"); break; }
                    var raw = await client.ListSettledMarketsAsync(series, cursor, historical, ct);
                    var rawId = TruthStore.StoreRaw(db, runId, raw);
                    pages++;
                    if (!raw.IsSuccess) { errors.Add($"{(historical ? "historical" : "live")}: HTTP {raw.Status}"); break; }
                    List<SettledMarket> markets;
                    string? next;
                    try { (markets, next) = TruthParse.SettledMarkets(raw.Body); }
                    catch (Exception e) when (e is FormatException or JsonException or InvalidOperationException)
                    {
                        errors.Add($"{(historical ? "historical" : "live")} page {page + 1}: {e.Message}");
                        break;
                    }
                    foreach (var m in markets) byTicker.TryAdd(m.Ticker, (m, rawId)); // live partition first; a market is never counted twice
                    if (next is null) break;
                    cursor = next;
                }
            }
        }
        catch (Exception e) when (e is HttpRequestException || (e is TaskCanceledException && !ct.IsCancellationRequested))
        {
            errors.Add(e is TaskCanceledException ? "request timed out" : $"{e.GetType().Name}: {e.Message}");
        }
        catch (Exception e)
        {
            errors.Add($"aborted: {e.GetType().Name}: {e.Message}");
            TruthStore.FinishRun(db, time, runId, "failed", errors);
            throw;
        }

        int newVintages = 0, unchanged = 0, conflicts = 0, inconsistent = 0, noValue = 0, withValue = 0;
        db.InTransaction(() =>
        {
            foreach (var g in byTicker.Values.GroupBy(x => x.Market.EventTicker, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var t = TruthParse.Aggregate(series, g.Key, g.ToList());
                if (t is null) { noValue++; continue; }
                withValue++;
                if (t.Status == TruthStatus.Conflict) conflicts++;
                if (t.Status == TruthStatus.Inconsistent) inconsistent++;
                if (TruthStore.AppendSettled(db, runId, TruthOrigin.Kalshi, t, time.GetUtcNow())) newVintages++; else unchanged++;
            }
        });

        var status = errors.Count == 0 ? "completed" : withValue > 0 ? "partial" : "failed";
        TruthStore.FinishRun(db, time, runId, status, errors);
        return new TruthCollectSummary(runId, status, pages, byTicker.Count, withValue, newVintages, unchanged, conflicts, inconsistent, noValue, errors);
    }
}

/// <summary>
/// Collects NWS climate-report daily highs from the IEM archive (<c>/json/cli.py</c>), one request per year. This is a
/// cross-check on Kalshi's settled value and the truth proxy for years before Kalshi's data starts, never a replacement
/// for it inside the Kalshi era.
/// </summary>
public sealed class IemCliCollector(Db db, HttpClient http, TimeProvider time, string baseUrl = "https://mesonet.agron.iastate.edu",
    TimeSpan? minInterval = null)
{
    public const string UserAgent = "kalshi-bot/0.4 (personal research; read-only)";
    private readonly TimeSpan _minInterval = minInterval ?? TimeSpan.FromMilliseconds(500);

    public async Task<CliCollectSummary> CollectAsync(string station, int fromYear, int toYear, CancellationToken ct = default)
    {
        station = TruthStore.ValidateStation(station);
        if (fromYear < 1990 || toYear < fromYear || toYear - fromYear > 40) throw new KalshiBotException($"Unreasonable year range {fromYear}-{toYear}.");
        DatabaseOrigin.BindOrThrow(db, TruthOrigin.Database, time);
        TruthStore.RecoverAbandonedRuns(db, time);
        var runId = TruthStore.StartRun(db, time, "iem-cli", TruthOrigin.Iem, $"{station} {fromYear}-{toYear}");

        var errors = new List<string>();
        int years = 0, days = 0, newVintages = 0, unchanged = 0;
        try
        {
            for (var year = fromYear; year <= toYear; year++)
            {
                if (year > fromYear && _minInterval > TimeSpan.Zero) await Task.Delay(_minInterval, time, ct);
                var url = $"{baseUrl}/json/cli.py?station={Uri.EscapeDataString(station)}&year={year}";
                var requested = time.GetUtcNow();
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.UserAgent.ParseAdd(UserAgent);
                req.Headers.Accept.ParseAdd("application/json");
                using var resp = await http.SendAsync(req, ct);
                var body = await resp.Content.ReadAsStringAsync(ct);
                var raw = new RawHttpResponse("GET", url, (int)resp.StatusCode, requested, time.GetUtcNow(), resp.Headers.Date, body);
                var rawId = TruthStore.StoreRaw(db, runId, raw);
                if (!raw.IsSuccess) { errors.Add($"{year}: HTTP {raw.Status}"); continue; }
                List<CliHigh> highs;
                try { highs = TruthParse.CliHighs(body); }
                catch (Exception e) when (e is FormatException or JsonException or InvalidOperationException) { errors.Add($"{year}: {e.Message}"); continue; }

                years++;
                db.InTransaction(() =>
                {
                    foreach (var h in highs.Where(h => string.Equals(h.Station, station, StringComparison.Ordinal)).OrderBy(h => h.ClimateDate))
                    {
                        days++;
                        if (TruthStore.AppendCli(db, runId, rawId, TruthOrigin.Iem, h, raw.ReceivedAt)) newVintages++; else unchanged++;
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
            TruthStore.FinishRun(db, time, runId, "failed", errors);
            throw;
        }

        var status = errors.Count == 0 ? "completed" : years > 0 ? "partial" : "failed";
        TruthStore.FinishRun(db, time, runId, status, errors);
        return new CliCollectSummary(runId, status, years, days, newVintages, unchanged, errors);
    }
}

public sealed record SettledRow(string EventTicker, DateOnly ClimateDate, int? Value, string Regime, string Status, int MarketsChecked, int MarketsInconsistent);

public sealed record CliRow(DateOnly ClimateDate, int High, string? Product);

/// <summary>One scoring-ready day. Source is "kalshi" (authoritative) or "nws-cli" (proxy, only before Kalshi's data begins).</summary>
public sealed record DailyTruth(DateOnly ClimateDate, int Value, string Source, string Regime);

public sealed record TruthDifference(DateOnly ClimateDate, string EventTicker, int Kalshi, int Cli, string Regime);

public sealed record RegimeAgreement(string Regime, int Compared, int Agree);

public sealed record TruthReport(
    string Station, DateOnly? KalshiFrom, DateOnly? KalshiTo, int KalshiOk, int KalshiConflict, int KalshiInconsistent, int CliDays,
    int Compared, int Agree, IReadOnlyList<RegimeAgreement> ByRegime, IReadOnlyList<TruthDifference> Differences,
    IReadOnlyList<DateOnly> MissingKalshiDays, int ResolvedKalshi, int ResolvedCliProxy)
{
    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"truth report ({Station})");
        sb.AppendLine($"  Kalshi settled event-days: {KalshiOk + KalshiConflict + KalshiInconsistent}" +
                      (KalshiFrom is { } f && KalshiTo is { } t ? $", {f:yyyy-MM-dd} to {t:yyyy-MM-dd}" : "") +
                      $"  (ok {KalshiOk}, conflict {KalshiConflict}, inconsistent {KalshiInconsistent})");
        sb.AppendLine($"  NWS climate-report highs: {CliDays} day(s)");
        sb.AppendLine($"  Kalshi vs NWS high on {Compared} day(s): {Agree} agree" + (Compared > 0 ? $" ({100.0 * Agree / Compared:0.0}%)" : ""));
        foreach (var r in ByRegime) sb.AppendLine($"    {r.Regime,-28} {r.Agree}/{r.Compared}");
        foreach (var d in Differences.Take(20)) sb.AppendLine($"    differs {d.ClimateDate:yyyy-MM-dd} {d.EventTicker}: Kalshi {d.Kalshi}, NWS {d.Cli} ({d.Regime})");
        if (Differences.Count > 20) sb.AppendLine($"    ... {Differences.Count - 20} more");
        sb.AppendLine($"  days inside the Kalshi range with no settled value (excluded from scoring): {MissingKalshiDays.Count}" +
                      (MissingKalshiDays.Count > 0 ? "  e.g. " + string.Join(", ", MissingKalshiDays.Take(6).Select(d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))) : ""));
        sb.AppendLine($"  scoring truth series: {ResolvedKalshi + ResolvedCliProxy} day(s) = {ResolvedKalshi} Kalshi + {ResolvedCliProxy} NWS-high proxy (before Kalshi's data only)");
        sb.AppendLine("  Kalshi's value is authoritative. A day where the two differ is reported here and is never overwritten.");
        return sb.ToString();
    }
}

/// <summary>Writes and reads the append-only truth tables.</summary>
public static class TruthStore
{
    private static readonly Regex SeriesPattern = new("^[A-Z0-9_]{2,24}$", RegexOptions.CultureInvariant);
    private static readonly Regex StationPattern = new("^[A-Z0-9]{3,5}$", RegexOptions.CultureInvariant);

    public static string ValidateSeries(string series) =>
        SeriesPattern.IsMatch(series) ? series : throw new KalshiBotException($"Invalid series ticker '{series}'.");

    public static string ValidateStation(string station) =>
        StationPattern.IsMatch(station) ? station : throw new KalshiBotException($"Invalid station '{station}'.");

    public static int RecoverAbandonedRuns(Db db, TimeProvider time) =>
        db.Exec("UPDATE truth_runs SET status = 'abandoned', finished_at = $t, detail = COALESCE(detail, 'process ended before run finished') WHERE status = 'running'",
            ("$t", Ts.ToText(time.GetUtcNow())));

    public static long StartRun(Db db, TimeProvider time, string source, string origin, string scope) =>
        db.Insert("INSERT INTO truth_runs (started_at, status, source, data_origin, scope) VALUES ($s, 'running', $src, $o, $sc)",
            ("$s", Ts.ToText(time.GetUtcNow())), ("$src", source), ("$o", origin), ("$sc", scope));

    public static void FinishRun(Db db, TimeProvider time, long runId, string status, IReadOnlyList<string> errors) =>
        db.Exec("UPDATE truth_runs SET status = $st, finished_at = $f, detail = $d WHERE id = $id",
            ("$st", status), ("$f", Ts.ToText(time.GetUtcNow())), ("$d", errors.Count == 0 ? null : string.Join("; ", errors)), ("$id", runId));

    public static long StoreRaw(Db db, long runId, RawHttpResponse r) =>
        db.Insert("""
            INSERT INTO truth_raw (run_id, url, http_status, requested_at, received_at, server_date, body_sha256, body)
            VALUES ($r, $u, $s, $rq, $rc, $sd, $h, $b)
            """,
            ("$r", runId), ("$u", r.Url), ("$s", r.Status), ("$rq", Ts.ToText(r.RequestedAt)), ("$rc", Ts.ToText(r.ReceivedAt)),
            ("$sd", r.ServerDate is null ? null : Ts.ToText(r.ServerDate.Value)), ("$h", Hash.Sha256Hex(r.Body)), ("$b", r.Body));

    /// <summary>Appends a vintage unless the latest stored one for the event already says the same thing. Returns whether it appended.</summary>
    public static bool AppendSettled(Db db, long runId, string origin, EventTruth t, DateTimeOffset receivedAt)
    {
        var value = t.Value is { } v ? v.ToString(CultureInfo.InvariantCulture) : null;
        var latest = db.Query("SELECT expiration_value, regime, status FROM settled_values WHERE event_ticker = $e ORDER BY id DESC LIMIT 1",
            r => (Value: r.Str("expiration_value"), Regime: r.S("regime"), Status: r.S("status")), ("$e", t.EventTicker));
        if (latest.Count == 1 && latest[0].Value == value && latest[0].Regime == t.Regime && latest[0].Status == t.StatusText) return false;
        db.Exec("""
            INSERT INTO settled_values (run_id, raw_id, data_origin, series_ticker, event_ticker, climate_date, expiration_value, regime,
                markets_seen, markets_checked, markets_inconsistent, status, received_at)
            VALUES ($r, $raw, $o, $s, $e, $d, $v, $g, $ms, $mc, $mi, $st, $at)
            """,
            ("$r", runId), ("$raw", t.RawId), ("$o", origin), ("$s", t.SeriesTicker), ("$e", t.EventTicker),
            ("$d", t.ClimateDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), ("$v", value), ("$g", t.Regime),
            ("$ms", t.MarketsSeen), ("$mc", t.MarketsChecked), ("$mi", t.MarketsInconsistent), ("$st", t.StatusText), ("$at", Ts.ToText(receivedAt)));
        return true;
    }

    public static bool AppendCli(Db db, long runId, long rawId, string origin, CliHigh h, DateTimeOffset receivedAt)
    {
        var date = h.ClimateDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var high = h.High.ToString(CultureInfo.InvariantCulture);
        var latest = db.Query("SELECT high, product FROM cli_highs WHERE station = $s AND climate_date = $d ORDER BY id DESC LIMIT 1",
            r => (High: r.S("high"), Product: r.Str("product")), ("$s", h.Station), ("$d", date));
        if (latest.Count == 1 && latest[0].High == high && latest[0].Product == h.Product) return false;
        db.Exec("""
            INSERT INTO cli_highs (run_id, raw_id, data_origin, station, climate_date, high, product, received_at)
            VALUES ($r, $raw, $o, $s, $d, $h, $p, $at)
            """,
            ("$r", runId), ("$raw", rawId), ("$o", origin), ("$s", h.Station), ("$d", date), ("$h", high), ("$p", h.Product), ("$at", Ts.ToText(receivedAt)));
        return true;
    }

    /// <summary>The latest vintage of every event.</summary>
    public static List<SettledRow> CurrentSettled(Db db) =>
        db.Query("""
            SELECT s.event_ticker, s.climate_date, s.expiration_value, s.regime, s.status, s.markets_checked, s.markets_inconsistent
            FROM settled_values s JOIN (SELECT event_ticker, MAX(id) AS id FROM settled_values GROUP BY event_ticker) m ON m.id = s.id
            ORDER BY s.climate_date, s.event_ticker
            """,
            r => new SettledRow(r.S("event_ticker"), DateOnly.ParseExact(r.S("climate_date"), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                r.Str("expiration_value") is { } v ? int.Parse(v, CultureInfo.InvariantCulture) : null,
                r.S("regime"), r.S("status"), (int)r.L("markets_checked"), (int)r.L("markets_inconsistent")));

    /// <summary>The latest vintage of every day for a station.</summary>
    public static List<CliRow> CurrentCli(Db db, string station) =>
        db.Query("""
            SELECT c.climate_date, c.high, c.product FROM cli_highs c
            JOIN (SELECT climate_date, MAX(id) AS id FROM cli_highs WHERE station = $s GROUP BY climate_date) m ON m.id = c.id
            ORDER BY c.climate_date
            """,
            r => new CliRow(DateOnly.ParseExact(r.S("climate_date"), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                int.Parse(r.S("high"), CultureInfo.InvariantCulture), r.Str("product")), ("$s", station));

    /// <summary>
    /// The series a model is scored against. Inside the Kalshi range only Kalshi's own value counts: a day it never stated,
    /// or a flagged day (conflict or inconsistent), is excluded rather than filled from another source. Before Kalshi's
    /// first day the NWS climate-report high stands in; it matched Kalshi on 1,860 of 1,861 shared days when checked.
    /// </summary>
    public static List<DailyTruth> Resolve(Db db, string station)
    {
        var kalshi = KalshiByDate(CurrentSettled(db));
        var cli = CurrentCli(db, station);
        var result = new List<DailyTruth>();
        DateOnly? first = kalshi.Count == 0 ? null : kalshi.Keys.Min();
        foreach (var (date, rows) in kalshi.OrderBy(k => k.Key))
        {
            var ok = rows.Where(r => r.Status == "ok" && r.Value is not null).ToList();
            if (ok.Count == rows.Count && ok.Select(r => r.Value).Distinct().Count() == 1)
                result.Add(new DailyTruth(date, ok[0].Value!.Value, "kalshi", ok[0].Regime));
        }
        foreach (var c in cli)
            if (first is null || c.ClimateDate < first) result.Add(new DailyTruth(c.ClimateDate, c.High, "nws-cli", "nws-cli-proxy"));
        return result.OrderBy(d => d.ClimateDate).ToList();
    }

    public static TruthReport Report(Db db, string station)
    {
        var settled = CurrentSettled(db);
        var kalshi = KalshiByDate(settled);
        var cli = CurrentCli(db, station);
        var cliByDate = cli.ToDictionary(c => c.ClimateDate);

        int compared = 0, agree = 0;
        var byRegime = new Dictionary<string, (int Compared, int Agree)>(StringComparer.Ordinal);
        var diffs = new List<TruthDifference>();
        foreach (var s in settled.Where(s => s.Status == "ok" && s.Value is not null))
        {
            if (!cliByDate.TryGetValue(s.ClimateDate, out var c)) continue;
            compared++;
            var same = c.High == s.Value;
            if (same) agree++; else diffs.Add(new TruthDifference(s.ClimateDate, s.EventTicker, s.Value!.Value, c.High, s.Regime));
            byRegime.TryGetValue(s.Regime, out var cur);
            byRegime[s.Regime] = (cur.Compared + 1, cur.Agree + (same ? 1 : 0));
        }

        var missing = new List<DateOnly>();
        DateOnly? from = kalshi.Count == 0 ? null : kalshi.Keys.Min(), to = kalshi.Count == 0 ? null : kalshi.Keys.Max();
        if (from is { } f && to is { } t)
            for (var d = f; d <= t; d = d.AddDays(1))
                if (!kalshi.ContainsKey(d)) missing.Add(d);

        var resolved = Resolve(db, station);
        return new TruthReport(station, from, to,
            settled.Count(s => s.Status == "ok"), settled.Count(s => s.Status == "conflict"), settled.Count(s => s.Status == "inconsistent"),
            cli.Count, compared, agree,
            byRegime.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => new RegimeAgreement(k.Key, k.Value.Compared, k.Value.Agree)).ToList(),
            diffs, missing, resolved.Count(r => r.Source == "kalshi"), resolved.Count(r => r.Source == "nws-cli"));
    }

    private static Dictionary<DateOnly, List<SettledRow>> KalshiByDate(IEnumerable<SettledRow> rows) =>
        rows.GroupBy(r => r.ClimateDate).ToDictionary(g => g.Key, g => g.ToList());
}
