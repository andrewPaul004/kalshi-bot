using System.Reflection;
using System.Text.Json.Nodes;
using KalshiBot.Core.Common;
using KalshiBot.Core.Kalshi;
using KalshiBot.Core.Storage;

namespace KalshiBot.Core.Recording;

/// <summary>
/// Each database holds one data origin (fixture, demo, or production) so datasets and P&amp;L never blend (spec 8.1).
/// The first capture binds it; later captures of another origin are refused.
/// </summary>
public static class DatabaseOrigin
{
    public static string? Get(Db db) => db.Scalar<string>("SELECT value FROM controls WHERE key = 'db_data_origin'");

    public static void BindOrThrow(Db db, string origin, TimeProvider time)
    {
        var bound = Get(db);
        if (bound is null)
            db.Exec("INSERT INTO controls (key, value, updated_at) VALUES ('db_data_origin', $o, $t)",
                ("$o", origin), ("$t", Ts.ToText(time.GetUtcNow())));
        else if (bound != origin)
            throw new KalshiBotException(
                $"{db.Path} holds {bound} data; recording {origin} data into it would blend datasets. Use a separate --db.");
    }
}

public sealed record CaptureSummary(long RunId, string Status, string DataOrigin, int Snapshots, int RulesChanges, IReadOnlyList<string> Errors);

/// <summary>
/// Phase 1 recorder. Observe-only: it issues public GETs and writes to SQLite. Each HTTP body is stored
/// verbatim before parsing, so a parser bug loses no evidence. Derived rows for a ticker are written in one
/// transaction, so a crash never leaves a snapshot without its rules or raw references.
/// </summary>
public sealed class Recorder(Db db, KalshiPublicClient client, TimeProvider time)
{
    public static string AppVersion =>
        typeof(Recorder).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    /// <summary>Marks runs left 'running' by a crashed process as 'abandoned'. Returns how many.</summary>
    public int RecoverAbandonedRuns() =>
        db.Exec("UPDATE capture_runs SET status = 'abandoned', finished_at = $t, detail = COALESCE(detail, 'process ended before run finished') WHERE status = 'running'",
            ("$t", Ts.ToText(time.GetUtcNow())));

    /// <summary>Upper bound on markets discovered per watched series per capture (a safety valve, not a target).</summary>
    public const int MaxMarketsPerSeries = 200;

    public async Task<CaptureSummary> CaptureAsync(IEnumerable<string> tickers, CancellationToken ct = default) =>
        await CaptureAsync(tickers, [], ct);

    /// <summary>Records the given markets plus every open market in each watched series (spec 3.1 recorder).</summary>
    public async Task<CaptureSummary> CaptureAsync(IEnumerable<string> tickers, IEnumerable<string> series, CancellationToken ct = default)
    {
        RecoverAbandonedRuns();
        var origin = client.Endpoint.DataOrigin;
        DatabaseOrigin.BindOrThrow(db, origin, time);
        var runId = db.Insert(
            "INSERT INTO capture_runs (started_at, status, data_origin, base_url, app_version) VALUES ($s, 'running', $o, $b, $v)",
            ("$s", Ts.ToText(time.GetUtcNow())), ("$o", origin), ("$b", client.Endpoint.BaseUrl), ("$v", AppVersion));

        var errors = new List<string>();
        var discovered = new List<string>();
        foreach (var seriesTicker in series.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            try
            {
                string? cursor = null;
                var found = 0;
                do
                {
                    var raw = await client.ListOpenMarketsAsync(seriesTicker, cursor, ct);
                    StoreRaw(runId, raw);
                    if (!raw.IsSuccess) { errors.Add($"series {seriesTicker}: market list HTTP {raw.Status}"); break; }
                    var (page, next) = KalshiParse.MarketList(raw.Body);
                    discovered.AddRange(page);
                    found += page.Count;
                    cursor = next;
                } while (cursor is not null && found < MaxMarketsPerSeries);
                if (found == 0) errors.Add($"series {seriesTicker}: no open markets");
            }
            catch (Exception e) when (e is FormatException or System.Text.Json.JsonException or HttpRequestException
                                        || (e is TaskCanceledException && !ct.IsCancellationRequested))
            {
                errors.Add($"series {seriesTicker}: {e.GetType().Name}: {e.Message}");
            }
        }
        tickers = tickers.Concat(discovered);
        var snapshots = 0;
        var rulesChanges = 0;
        var seenEvents = new HashSet<string>(StringComparer.Ordinal);
        var seenSeries = new HashSet<string>(StringComparer.Ordinal);
        var list = tickers.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

        foreach (var ticker in list)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var marketRaw = await client.GetMarketAsync(ticker, ct);
                var marketRawId = StoreRaw(runId, marketRaw);
                if (!marketRaw.IsSuccess) { errors.Add($"{ticker}: market HTTP {marketRaw.Status}"); continue; }
                var market = KalshiParse.Market(marketRaw.Body);
                if (!string.Equals(market.Ticker, ticker, StringComparison.Ordinal))
                {
                    errors.Add($"{ticker}: response ticker '{market.Ticker}' does not match request");
                    continue;
                }

                var bookRaw = await client.GetOrderbookAsync(ticker, ct);
                long? bookRawId = StoreRaw(runId, bookRaw);
                OrderBook? book = null;
                if (bookRaw.IsSuccess)
                {
                    try { book = KalshiParse.Orderbook(bookRaw.Body); }
                    catch (Exception e) when (e is FormatException or System.Text.Json.JsonException or InvalidOperationException)
                    { errors.Add($"{ticker}: orderbook parse failed: {e.Message}"); }
                }
                else errors.Add($"{ticker}: orderbook HTTP {bookRaw.Status}");

                if (seenEvents.Add(market.EventTicker))
                    await CaptureEventAndSeriesAsync(runId, origin, market.EventTicker, seenSeries, errors, ct);

                var changed = db.InTransaction(() =>
                {
                    var (rulesId, isNew) = UpsertRules(market, marketRawId, marketRaw.ReceivedAt);
                    InsertSnapshot(runId, origin, market, book, marketRawId, book is null ? null : bookRawId, rulesId, marketRaw.ReceivedAt);
                    return isNew;
                });
                snapshots++;
                if (changed) rulesChanges++;
            }
            catch (Exception e) when (e is FormatException or System.Text.Json.JsonException or HttpRequestException or InvalidOperationException
                                        || (e is TaskCanceledException && !ct.IsCancellationRequested)) // HttpClient timeout
            {
                errors.Add($"{ticker}: {(e is TaskCanceledException ? "request timed out" : e.GetType().Name + ": " + e.Message)}");
            }
            catch (Exception e)
            {
                // Anything else (including cancellation) ends the run; record why instead of leaving it 'running'.
                errors.Add($"{ticker}: aborted: {e.GetType().Name}: {e.Message}");
                Finish(runId, "failed", errors);
                throw;
            }
        }

        var status = errors.Count == 0 ? "completed" : snapshots > 0 ? "partial" : "failed";
        if (list.Count == 0) status = "completed";
        Finish(runId, status, errors);
        return new CaptureSummary(runId, status, origin, snapshots, rulesChanges, errors);
    }

    private void Finish(long runId, string status, List<string> errors) =>
        db.Exec("UPDATE capture_runs SET status = $st, finished_at = $f, detail = $d WHERE id = $id",
            ("$st", status), ("$f", Ts.ToText(time.GetUtcNow())),
            ("$d", errors.Count == 0 ? null : new JsonArray(errors.Select(e => (JsonNode)e).ToArray()).ToJsonString()),
            ("$id", runId));

    private async Task CaptureEventAndSeriesAsync(long runId, string origin, string eventTicker, HashSet<string> seenSeries,
        List<string> errors, CancellationToken ct)
    {
        var raw = await client.GetEventAsync(eventTicker, ct);
        var rawId = StoreRaw(runId, raw);
        if (!raw.IsSuccess) { errors.Add($"event {eventTicker}: HTTP {raw.Status}"); return; }
        var ev = KalshiParse.Event(raw.Body);
        db.Exec("""
            INSERT INTO event_snapshots (capture_run_id, raw_id, data_origin, observed_at, event_ticker, series_ticker, title,
                mutually_exclusive, settlement_sources_json, fee_type_override, fee_multiplier_override)
            VALUES ($r, $raw, $o, $t, $e, $s, $ti, $me, $ss, $fto, $fmo)
            """,
            ("$r", runId), ("$raw", rawId), ("$o", origin), ("$t", Ts.ToText(raw.ReceivedAt)), ("$e", ev.EventTicker),
            ("$s", ev.SeriesTicker), ("$ti", ev.Title), ("$me", ev.MutuallyExclusive is null ? null : ev.MutuallyExclusive.Value ? 1 : 0),
            ("$ss", ev.SettlementSourcesJson), ("$fto", ev.FeeTypeOverride),
            ("$fmo", ev.FeeMultiplierOverride is null ? null : Dec.ToText(ev.FeeMultiplierOverride.Value)));

        if (!seenSeries.Add(ev.SeriesTicker)) return;
        var sraw = await client.GetSeriesAsync(ev.SeriesTicker, ct);
        var srawId = StoreRaw(runId, sraw);
        if (!sraw.IsSuccess) { errors.Add($"series {ev.SeriesTicker}: HTTP {sraw.Status}"); return; }
        var s = KalshiParse.Series(sraw.Body);
        db.Exec("""
            INSERT INTO series_snapshots (capture_run_id, raw_id, data_origin, observed_at, series_ticker, title, category,
                fee_type, fee_multiplier, settlement_sources_json, contract_terms_url)
            VALUES ($r, $raw, $o, $t, $s, $ti, $c, $ft, $fm, $ss, $ctu)
            """,
            ("$r", runId), ("$raw", srawId), ("$o", origin), ("$t", Ts.ToText(sraw.ReceivedAt)), ("$s", s.SeriesTicker),
            ("$ti", s.Title), ("$c", s.Category), ("$ft", s.FeeType),
            ("$fm", s.FeeMultiplier is null ? null : Dec.ToText(s.FeeMultiplier.Value)),
            ("$ss", s.SettlementSourcesJson), ("$ctu", s.ContractTermsUrl));
    }

    private long StoreRaw(long runId, RawHttpResponse r) =>
        db.Insert("""
            INSERT INTO raw_responses (capture_run_id, method, url, http_status, requested_at, received_at, server_date, body_sha256, body)
            VALUES ($r, $m, $u, $s, $rq, $rc, $sd, $h, $b)
            """,
            ("$r", runId), ("$m", r.Method), ("$u", r.Url), ("$s", r.Status), ("$rq", Ts.ToText(r.RequestedAt)),
            ("$rc", Ts.ToText(r.ReceivedAt)), ("$sd", r.ServerDate is null ? null : Ts.ToText(r.ServerDate.Value)),
            ("$h", Hash.Sha256Hex(r.Body)), ("$b", r.Body));

    private (long Id, bool IsNew) UpsertRules(MarketData m, long rawId, DateTimeOffset seenAt)
    {
        var sha = m.RulesSha256;
        var existing = db.Scalar<long?>("SELECT id FROM market_rules WHERE ticker = $t AND rules_sha256 = $h", ("$t", m.Ticker), ("$h", sha));
        if (existing is { } id)
        {
            db.Exec("UPDATE market_rules SET last_seen_at = $s, last_raw_id = $r WHERE id = $id AND last_seen_at <= $s",
                ("$s", Ts.ToText(seenAt)), ("$r", rawId), ("$id", id));
            return (id, false);
        }
        var newId = db.Insert("""
            INSERT INTO market_rules (ticker, rules_sha256, rules_primary, rules_secondary, early_close_condition,
                first_seen_at, last_seen_at, first_raw_id, last_raw_id)
            VALUES ($t, $h, $p, $s, $e, $at, $at, $r, $r)
            """,
            ("$t", m.Ticker), ("$h", sha), ("$p", m.RulesPrimary), ("$s", m.RulesSecondary), ("$e", m.EarlyCloseCondition),
            ("$at", Ts.ToText(seenAt)), ("$r", rawId));
        return (newId, true);
    }

    private void InsertSnapshot(long runId, string origin, MarketData m, OrderBook? book, long rawMarketId, long? rawBookId,
        long rulesId, DateTimeOffset observedAt) =>
        db.Exec("""
            INSERT INTO market_snapshots (capture_run_id, raw_market_id, raw_orderbook_id, rules_id, data_origin, observed_at,
                ticker, event_ticker, market_type, title, yes_sub_title, status, open_time, close_time, expected_expiration_time,
                yes_bid, yes_ask, no_bid, no_ask, last_price, volume, open_interest, result, price_ranges_json, orderbook_json,
                strike_type, floor_strike, cap_strike)
            VALUES ($run, $rm, $rb, $rules, $o, $obs, $t, $e, $mt, $ti, $yst, $st, $ot, $ct, $eet,
                $yb, $ya, $nb, $na, $lp, $v, $oi, $res, $pr, $ob, $stt, $fs, $cs)
            """,
            ("$run", runId), ("$rm", rawMarketId), ("$rb", rawBookId), ("$rules", rulesId), ("$o", origin),
            ("$obs", Ts.ToText(observedAt)), ("$t", m.Ticker), ("$e", m.EventTicker), ("$mt", m.MarketType), ("$ti", m.Title),
            ("$yst", m.YesSubTitle), ("$st", m.Status), ("$ot", T(m.OpenTime)), ("$ct", T(m.CloseTime)),
            ("$eet", T(m.ExpectedExpirationTime)), ("$yb", D(m.YesBid)), ("$ya", D(m.YesAsk)), ("$nb", D(m.NoBid)),
            ("$na", D(m.NoAsk)), ("$lp", D(m.LastPrice)), ("$v", D(m.Volume)), ("$oi", D(m.OpenInterest)),
            ("$res", m.Result), ("$pr", m.PriceRangesJson), ("$ob", book?.ToJson()),
            ("$stt", m.StrikeType), ("$fs", D(m.FloorStrike)), ("$cs", D(m.CapStrike)));

    private static string? T(DateTimeOffset? t) => t is null ? null : Ts.ToText(t.Value);
    private static string? D(decimal? d) => d is null ? null : Dec.ToText(d.Value);
}
