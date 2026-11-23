using KalshiBot.Core.Common;
using KalshiBot.Core.Kalshi;
using KalshiBot.Core.Storage;
using Microsoft.Data.Sqlite;

namespace KalshiBot.Core.Recording;

public sealed record MarketSnapshot(
    long Id, long CaptureRunId, string DataOrigin, DateTimeOffset ObservedAt, string Ticker, string EventTicker,
    string MarketType, string? Title, string? YesSubTitle, string Status, DateTimeOffset? CloseTime,
    DateTimeOffset? ExpectedExpirationTime, decimal? YesBid, decimal? YesAsk, decimal? NoBid, decimal? NoAsk,
    decimal? LastPrice, decimal? Volume, decimal? OpenInterest, string? Result, string? PriceRangesJson,
    OrderBook? Book, long RulesId, long RawMarketId, long? RawOrderbookId)
{
    public bool IsSynthetic => Common.DataOrigin.IsSynthetic(DataOrigin);
}

public sealed record MarketRules(long Id, string Ticker, string Sha256, string Primary, string Secondary,
    string? EarlyCloseCondition, DateTimeOffset FirstSeenAt, DateTimeOffset LastSeenAt, long FirstRawId, long LastRawId);

public sealed record RawRef(long Id, string Url, int HttpStatus, DateTimeOffset ReceivedAt, DateTimeOffset? ServerDate, string BodySha256);

public sealed record EventSnapshot(long Id, long RawId, DateTimeOffset ObservedAt, string EventTicker, string SeriesTicker,
    string? Title, string? SettlementSourcesJson, string? FeeTypeOverride, decimal? FeeMultiplierOverride);

public sealed record SeriesSnapshot(long Id, long RawId, DateTimeOffset ObservedAt, string SeriesTicker, string? Title,
    string? Category, string? FeeType, decimal? FeeMultiplier, string? SettlementSourcesJson, string? ContractTermsUrl);

/// <summary>Read side of recorded market data.</summary>
public sealed class MarketStore(Db db)
{
    public MarketSnapshot? Latest(string ticker) =>
        db.QuerySingle("SELECT * FROM market_snapshots WHERE ticker = $t ORDER BY observed_at DESC, id DESC LIMIT 1", Map, ("$t", ticker));

    public MarketSnapshot? ById(long id) =>
        db.QuerySingle("SELECT * FROM market_snapshots WHERE id = $id", Map, ("$id", id));

    public List<MarketSnapshot> LatestPerTicker() =>
        db.Query("""
            SELECT s.* FROM market_snapshots s
            JOIN (SELECT ticker, MAX(id) AS id FROM market_snapshots GROUP BY ticker) m ON m.id = s.id
            ORDER BY s.ticker
            """, Map);

    public MarketRules? Rules(long id) =>
        db.QuerySingle("SELECT * FROM market_rules WHERE id = $id", MapRules, ("$id", id));

    public List<MarketRules> RulesHistory(string ticker) =>
        db.Query("SELECT * FROM market_rules WHERE ticker = $t ORDER BY first_seen_at, id", MapRules, ("$t", ticker));

    public RawRef? Raw(long id) =>
        db.QuerySingle("SELECT id, url, http_status, received_at, server_date, body_sha256 FROM raw_responses WHERE id = $id",
            r => new RawRef(r.L("id"), r.S("url"), (int)r.L("http_status"), Ts.Parse(r.S("received_at")),
                r.Str("server_date") is { } d ? Ts.Parse(d) : null, r.S("body_sha256")), ("$id", id));

    public EventSnapshot? LatestEvent(string eventTicker, DateTimeOffset asOf) =>
        db.QuerySingle("""
            SELECT * FROM event_snapshots WHERE event_ticker = $e AND observed_at <= $t
            ORDER BY observed_at DESC, id DESC LIMIT 1
            """,
            r => new EventSnapshot(r.L("id"), r.L("raw_id"), Ts.Parse(r.S("observed_at")), r.S("event_ticker"),
                r.S("series_ticker"), r.Str("title"), r.Str("settlement_sources_json"), r.Str("fee_type_override"),
                r.DN("fee_multiplier_override")),
            ("$e", eventTicker), ("$t", Ts.ToText(asOf)));

    public SeriesSnapshot? LatestSeries(string seriesTicker, DateTimeOffset asOf) =>
        db.QuerySingle("""
            SELECT * FROM series_snapshots WHERE series_ticker = $s AND observed_at <= $t
            ORDER BY observed_at DESC, id DESC LIMIT 1
            """,
            r => new SeriesSnapshot(r.L("id"), r.L("raw_id"), Ts.Parse(r.S("observed_at")), r.S("series_ticker"),
                r.Str("title"), r.Str("category"), r.Str("fee_type"), r.DN("fee_multiplier"),
                r.Str("settlement_sources_json"), r.Str("contract_terms_url")),
            ("$s", seriesTicker), ("$t", Ts.ToText(asOf)));

    private static MarketSnapshot Map(SqliteDataReader r) => new(
        r.L("id"), r.L("capture_run_id"), r.S("data_origin"), Ts.Parse(r.S("observed_at")), r.S("ticker"),
        r.S("event_ticker"), r.S("market_type"), r.Str("title"), r.Str("yes_sub_title"), r.S("status"),
        r.Str("close_time") is { } c ? Ts.Parse(c) : null,
        r.Str("expected_expiration_time") is { } x ? Ts.Parse(x) : null,
        r.DN("yes_bid"), r.DN("yes_ask"), r.DN("no_bid"), r.DN("no_ask"), r.DN("last_price"),
        r.DN("volume"), r.DN("open_interest"), r.Str("result"), r.Str("price_ranges_json"),
        r.Str("orderbook_json") is { } ob ? OrderBook.FromJson(ob) : null,
        r.L("rules_id"), r.L("raw_market_id"), r.LN("raw_orderbook_id"));

    private static MarketRules MapRules(SqliteDataReader r) => new(
        r.L("id"), r.S("ticker"), r.S("rules_sha256"), r.S("rules_primary"), r.S("rules_secondary"),
        r.Str("early_close_condition"), Ts.Parse(r.S("first_seen_at")), Ts.Parse(r.S("last_seen_at")),
        r.L("first_raw_id"), r.L("last_raw_id"));
}

public sealed class Watchlist(Db db, TimeProvider time)
{
    public bool Add(string ticker) =>
        db.Exec("INSERT OR IGNORE INTO watchlist (ticker, added_at) VALUES ($t, $a)",
            ("$t", Normalize(ticker)), ("$a", Ts.ToText(time.GetUtcNow()))) == 1;

    public bool Remove(string ticker) => db.Exec("DELETE FROM watchlist WHERE ticker = $t", ("$t", Normalize(ticker))) == 1;

    public List<string> List() => db.Query("SELECT ticker FROM watchlist ORDER BY ticker", r => r.S("ticker"));

    private static string Normalize(string ticker)
    {
        var t = ticker.Trim();
        if (t.Length is 0 or > 128 || t.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.')))
            throw new KalshiBotException($"Invalid ticker '{ticker}'.");
        return t;
    }
}
