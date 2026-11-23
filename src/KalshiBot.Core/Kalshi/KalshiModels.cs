using System.Text.Json;
using System.Text.Json.Nodes;
using KalshiBot.Core.Common;

namespace KalshiBot.Core.Kalshi;

public sealed record PriceLevel(decimal Price, decimal Size);

/// <summary>
/// Kalshi returns bids only: YES bids and NO bids. A NO bid at p is a YES ask at 1 - p (and vice versa).
/// Levels here are sorted best-first (highest bid first) regardless of payload order.
/// </summary>
public sealed record OrderBook(IReadOnlyList<PriceLevel> YesBids, IReadOnlyList<PriceLevel> NoBids)
{
    public static readonly OrderBook Empty = new([], []);

    /// <summary>Asks to buy YES, cheapest first, derived from NO bids.</summary>
    public IReadOnlyList<PriceLevel> YesAsks => NoBids.Select(l => l with { Price = 1m - l.Price }).OrderBy(l => l.Price).ToList();

    /// <summary>Asks to buy NO, cheapest first, derived from YES bids.</summary>
    public IReadOnlyList<PriceLevel> NoAsks => YesBids.Select(l => l with { Price = 1m - l.Price }).OrderBy(l => l.Price).ToList();

    public IReadOnlyList<PriceLevel> AsksFor(string side) => side == "yes" ? YesAsks : NoAsks;
    public IReadOnlyList<PriceLevel> BidsFor(string side) => side == "yes" ? YesBids : NoBids;

    public string ToJson() => new JsonObject
    {
        ["yes_bids"] = Levels(YesBids),
        ["no_bids"] = Levels(NoBids),
    }.ToJsonString();

    public static OrderBook FromJson(string json)
    {
        var o = JsonNode.Parse(json)!.AsObject();
        return new OrderBook(ReadLevels(o["yes_bids"]!.AsArray()), ReadLevels(o["no_bids"]!.AsArray()));
    }

    private static JsonArray Levels(IEnumerable<PriceLevel> levels) =>
        new(levels.Select(l => (JsonNode)new JsonArray(Dec.ToText(l.Price), Dec.ToText(l.Size))).ToArray());

    internal static List<PriceLevel> ReadLevels(JsonArray arr) =>
        arr.Select(n =>
        {
            var pair = n!.AsArray();
            if (pair.Count != 2) throw new FormatException("Order book level must be [price, size].");
            var price = Dec.Parse(pair[0]!.GetValue<string>());
            var size = Dec.Parse(pair[1]!.GetValue<string>());
            if (price <= 0m || price >= 1m || size < 0m) throw new FormatException($"Order book level out of range: {price}, {size}.");
            return new PriceLevel(price, size);
        }).OrderByDescending(l => l.Price).ToList();
}

public sealed record MarketData(
    string Ticker, string EventTicker, string MarketType, string? Title, string? YesSubTitle, string Status,
    DateTimeOffset? OpenTime, DateTimeOffset? CloseTime, DateTimeOffset? ExpectedExpirationTime,
    decimal? YesBid, decimal? YesAsk, decimal? NoBid, decimal? NoAsk, decimal? LastPrice,
    decimal? Volume, decimal? OpenInterest, string? Result,
    string RulesPrimary, string RulesSecondary, string? EarlyCloseCondition, string? PriceRangesJson)
{
    public string RulesSha256 => Hash.Sha256Hex(RulesPrimary + "\u001f" + RulesSecondary + "\u001f" + (EarlyCloseCondition ?? ""));
}

public sealed record EventData(string EventTicker, string SeriesTicker, string? Title, bool? MutuallyExclusive,
    string? SettlementSourcesJson, string? FeeTypeOverride, decimal? FeeMultiplierOverride);

public sealed record SeriesData(string SeriesTicker, string? Title, string? Category, string? FeeType,
    decimal? FeeMultiplier, string? SettlementSourcesJson, string? ContractTermsUrl);

/// <summary>
/// Parses the fields this system relies on. Required fields missing or mistyped -> FormatException
/// (the raw body is still stored). Unknown fields are ignored here because the raw body is the record.
/// Field names verified against Kalshi's official OpenAPI-generated SDK models, v3.36.0.
/// </summary>
public static class KalshiParse
{
    public static MarketData Market(string body)
    {
        var m = Obj(JsonNode.Parse(body), "market");
        return new MarketData(
            Ticker: Req(m, "ticker"),
            EventTicker: Req(m, "event_ticker"),
            MarketType: Opt(m, "market_type") ?? "binary",
            Title: Opt(m, "title"),
            YesSubTitle: Opt(m, "yes_sub_title"),
            Status: Req(m, "status"),
            OpenTime: Ts.ParseExternal(Opt(m, "open_time")),
            CloseTime: Ts.ParseExternal(Opt(m, "close_time")),
            ExpectedExpirationTime: Ts.ParseExternal(Opt(m, "expected_expiration_time")),
            YesBid: Dec.ParseOrNull(Opt(m, "yes_bid_dollars")),
            YesAsk: Dec.ParseOrNull(Opt(m, "yes_ask_dollars")),
            NoBid: Dec.ParseOrNull(Opt(m, "no_bid_dollars")),
            NoAsk: Dec.ParseOrNull(Opt(m, "no_ask_dollars")),
            LastPrice: Dec.ParseOrNull(Opt(m, "last_price_dollars")),
            Volume: Dec.ParseOrNull(Opt(m, "volume_fp")),
            OpenInterest: Dec.ParseOrNull(Opt(m, "open_interest_fp")),
            Result: Opt(m, "result"),
            RulesPrimary: ReqAllowEmpty(m, "rules_primary"),
            RulesSecondary: ReqAllowEmpty(m, "rules_secondary"),
            EarlyCloseCondition: Opt(m, "early_close_condition"),
            PriceRangesJson: m["price_ranges"] is JsonArray pr ? pr.ToJsonString() : null);
    }

    public static OrderBook Orderbook(string body)
    {
        var ob = Obj(JsonNode.Parse(body), "orderbook_fp");
        var yes = ob["yes_dollars"] as JsonArray;
        var no = ob["no_dollars"] as JsonArray;
        // Kalshi may send null for an empty side.
        return new OrderBook(yes is null ? [] : OrderBook.ReadLevels(yes), no is null ? [] : OrderBook.ReadLevels(no));
    }

    public static EventData Event(string body)
    {
        var root = JsonNode.Parse(body)?.AsObject() ?? throw new FormatException("Empty event body.");
        var e = root["event"] as JsonObject ?? throw new FormatException("Missing 'event'.");
        return new EventData(
            Req(e, "event_ticker"), Req(e, "series_ticker"), Opt(e, "title"),
            e["mutually_exclusive"] is JsonValue mv && mv.TryGetValue<bool>(out var b) ? b : null,
            e["settlement_sources"] is JsonArray ss ? ss.ToJsonString() : null,
            Opt(e, "fee_type_override"),
            Num(e, "fee_multiplier_override"));
    }

    public static SeriesData Series(string body)
    {
        var s = Obj(JsonNode.Parse(body), "series");
        return new SeriesData(
            Req(s, "ticker"), Opt(s, "title"), Opt(s, "category"), Opt(s, "fee_type"), Num(s, "fee_multiplier"),
            s["settlement_sources"] is JsonArray ss ? ss.ToJsonString() : null, Opt(s, "contract_terms_url"));
    }

    public static (List<string> Tickers, string? Cursor) MarketList(string body)
    {
        var root = JsonNode.Parse(body)?.AsObject() ?? throw new FormatException("Empty body.");
        var markets = root["markets"] as JsonArray ?? throw new FormatException("Missing 'markets'.");
        var tickers = markets.Select(m => Req(m!.AsObject(), "ticker")).ToList();
        var cursor = root["cursor"]?.GetValue<string>();
        return (tickers, string.IsNullOrEmpty(cursor) ? null : cursor);
    }

    private static JsonObject Obj(JsonNode? root, string key) =>
        root?[key] as JsonObject ?? throw new FormatException($"Missing object '{key}'.");

    private static string Req(JsonObject o, string key)
    {
        var v = Opt(o, key);
        return string.IsNullOrEmpty(v) ? throw new FormatException($"Missing required field '{key}'.") : v;
    }

    private static string ReqAllowEmpty(JsonObject o, string key) =>
        o[key] is JsonValue v && v.GetValueKind() == JsonValueKind.String
            ? v.GetValue<string>()
            : throw new FormatException($"Missing required string field '{key}'.");

    private static string? Opt(JsonObject o, string key) =>
        o[key] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    private static decimal? Num(JsonObject o, string key) =>
        o[key] is JsonValue v && v.GetValueKind() == JsonValueKind.Number ? v.GetValue<decimal>() : null;
}
