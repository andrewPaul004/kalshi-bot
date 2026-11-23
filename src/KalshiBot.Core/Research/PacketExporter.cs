using System.Text.Json.Nodes;
using KalshiBot.Core.Common;
using KalshiBot.Core.Recording;
using KalshiBot.Core.Storage;

namespace KalshiBot.Core.Research;

public sealed record ExportedPacket(string PacketId, string Ticker, string DataOrigin, string Json, bool AlreadyExisted);

/// <summary>
/// Builds a self-contained research packet from recorded data only (no network). The packet id is the SHA-256 of
/// the canonical packet body (everything except packet_id itself), so the same snapshot always yields
/// the same id and bytes. Quotes are excluded on purpose to keep the estimate independent of market price.
/// </summary>
public sealed class PacketExporter(Db db, TimeProvider time)
{
    public ExportedPacket Export(string ticker, long? snapshotId = null)
    {
        var store = new MarketStore(db);
        var s = (snapshotId is { } id ? store.ById(id) : store.Latest(ticker))
                ?? throw new KalshiBotException($"No recorded snapshot for {ticker}. Run 'kb record' first.");
        if (s.Ticker != ticker) throw new KalshiBotException($"Snapshot {s.Id} is for {s.Ticker}, not {ticker}.");
        var rules = store.Rules(s.RulesId)!;
        var ev = store.LatestEvent(s.EventTicker, s.ObservedAt);
        var series = ev is null ? null : store.LatestSeries(ev.SeriesTicker, s.ObservedAt);

        var sources = new JsonArray { Source("market", store.Raw(s.RawMarketId)!) };
        if (s.RawOrderbookId is { } ob) sources.Add(Source("orderbook", store.Raw(ob)!));
        if (ev is not null) sources.Add(Source("event", store.Raw(ev.RawId)!));
        if (series is not null) sources.Add(Source("series", store.Raw(series.RawId)!));

        var body = new JsonObject
        {
            ["schema"] = ResearchContract.PacketSchema,
            ["data_origin"] = s.DataOrigin,
            ["synthetic_warning"] = s.IsSynthetic
                ? "SYNTHETIC FIXTURE DATA. This market does not exist on Kalshi. Any estimate is a pipeline test only."
                : null,
            ["market"] = new JsonObject
            {
                ["ticker"] = s.Ticker,
                ["event_ticker"] = s.EventTicker,
                ["market_type"] = s.MarketType,
                ["title"] = s.Title,
                ["yes_sub_title"] = s.YesSubTitle,
                ["status"] = s.Status,
                ["close_time"] = s.CloseTime is { } c ? Ts.ToText(c) : null,
                ["expected_expiration_time"] = s.ExpectedExpirationTime is { } x ? Ts.ToText(x) : null,
                ["observed_at"] = Ts.ToText(s.ObservedAt),
                ["snapshot_id"] = s.Id,
            },
            ["rules"] = new JsonObject
            {
                ["rules_sha256"] = rules.Sha256,
                ["rules_primary"] = rules.Primary,
                ["rules_secondary"] = rules.Secondary,
                ["early_close_condition"] = rules.EarlyCloseCondition,
                ["first_seen_at"] = Ts.ToText(rules.FirstSeenAt),
            },
            ["event"] = ev is null ? null : new JsonObject
            {
                ["event_ticker"] = ev.EventTicker,
                ["series_ticker"] = ev.SeriesTicker,
                ["title"] = ev.Title,
                ["settlement_sources"] = ParseOrNull(ev.SettlementSourcesJson),
            },
            ["series"] = series is null ? null : new JsonObject
            {
                ["series_ticker"] = series.SeriesTicker,
                ["title"] = series.Title,
                ["category"] = series.Category,
                ["settlement_sources"] = ParseOrNull(series.SettlementSourcesJson),
                ["contract_terms_url"] = series.ContractTermsUrl,
            },
            ["sources"] = sources,
            ["task"] = ResearchContract.Task,
            ["result_contract"] = new JsonObject
            {
                ["schema"] = ResearchContract.ResultSchema,
                ["json_schema"] = JsonNode.Parse(ResearchContract.ResultJsonSchema),
            },
        };

        var packetId = CanonicalJson.HashOf(body);
        var full = body.DeepClone().AsObject();
        full["packet_id"] = packetId;
        var json = CanonicalJson.Serialize(full, indented: true);

        var inserted = db.Exec("""
            INSERT OR IGNORE INTO research_packets (packet_id, ticker, snapshot_id, rules_id, data_origin, created_at, packet_json)
            VALUES ($id, $t, $s, $r, $o, $c, $j)
            """,
            ("$id", packetId), ("$t", s.Ticker), ("$s", s.Id), ("$r", rules.Id), ("$o", s.DataOrigin),
            ("$c", Ts.ToText(time.GetUtcNow())), ("$j", json));
        var stored = db.Scalar<string>("SELECT packet_json FROM research_packets WHERE packet_id = $id", ("$id", packetId))!;
        return new ExportedPacket(packetId, s.Ticker, s.DataOrigin, stored, inserted == 0);
    }

    private static JsonObject Source(string kind, RawRef raw) => new()
    {
        ["kind"] = kind,
        ["raw_response_id"] = raw.Id,
        ["url"] = raw.Url,
        ["http_status"] = raw.HttpStatus,
        ["received_at"] = Ts.ToText(raw.ReceivedAt),
        ["server_date"] = raw.ServerDate is { } d ? Ts.ToText(d) : null,
        ["body_sha256"] = raw.BodySha256,
    };

    private static JsonNode? ParseOrNull(string? json) => json is null ? null : JsonNode.Parse(json);

    /// <summary>Text for a manual round trip: paste into Claude (claude.ai or Claude Code) under your subscription.</summary>
    public static string ManualPrompt(string packetJson) =>
        "You are assisting with EXPERIMENTAL paper-trading research. Follow the packet's \"task\" exactly and reply " +
        "with only one JSON object matching packet.result_contract.json_schema.\n\n```json\n" + packetJson + "\n```\n";
}
