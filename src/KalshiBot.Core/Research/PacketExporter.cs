using System.Text.Json.Nodes;
using KalshiBot.Core.Common;
using KalshiBot.Core.Recording;
using KalshiBot.Core.Storage;

namespace KalshiBot.Core.Research;

public sealed record ExportedPacket(string PacketId, string PacketHash, string MarketTicker, string DataOrigin, string Json, bool AlreadyExisted);

/// <summary>
/// Builds a bounded, self-contained research packet from recorded data only (spec 3.2). packetHash is the SHA-256 of
/// the canonical packet body; packetId is "pkt_{snapshotId}_{hash prefix}". Same snapshot, same bytes. Market prices
/// are excluded on purpose so research cannot anchor on them.
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
        var ev = store.EventFor(s);
        var series = ev is null ? null : store.SeriesFor(ev.SeriesTicker, s);

        var sources = new JsonArray { Source("market", store.Raw(s.RawMarketId)!) };
        if (s.RawOrderbookId is { } ob) sources.Add(Source("orderbook", store.Raw(ob)!));
        if (ev is not null) sources.Add(Source("event", store.Raw(ev.RawId)!));
        if (series is not null) sources.Add(Source("series", store.Raw(series.RawId)!));

        var body = new JsonObject
        {
            ["schemaVersion"] = ResearchContract.PacketSchema,
            ["promptVersion"] = ResearchContract.PromptVersion,
            ["dataOrigin"] = s.DataOrigin,
            ["syntheticWarning"] = s.IsSynthetic
                ? "SYNTHETIC FIXTURE DATA. This market does not exist on Kalshi. Any research is a pipeline test only."
                : null,
            ["market"] = new JsonObject
            {
                ["marketTicker"] = s.Ticker,
                ["eventTicker"] = s.EventTicker,
                ["seriesTicker"] = ev?.SeriesTicker,
                ["marketType"] = s.MarketType,
                ["title"] = s.Title,
                ["yesSubTitle"] = s.YesSubTitle,
                ["status"] = s.Status,
                ["closeTime"] = s.CloseTime is { } c ? Ts.ToText(c) : null,
                ["expectedExpirationTime"] = s.ExpectedExpirationTime is { } x ? Ts.ToText(x) : null,
                ["observedAtUtc"] = Ts.ToText(s.ObservedAt),
                ["snapshotId"] = s.Id,
            },
            ["rules"] = new JsonObject
            {
                ["rulesSha256"] = rules.Sha256,
                ["rulesPrimary"] = rules.Primary,
                ["rulesSecondary"] = rules.Secondary,
                ["earlyCloseCondition"] = rules.EarlyCloseCondition,
                ["firstSeenAtUtc"] = Ts.ToText(rules.FirstSeenAt),
            },
            ["event"] = ev is null ? null : new JsonObject
            {
                ["eventTicker"] = ev.EventTicker,
                ["title"] = ev.Title,
                ["settlementSources"] = ParseOrNull(ev.SettlementSourcesJson),
            },
            ["series"] = series is null ? null : new JsonObject
            {
                ["seriesTicker"] = series.SeriesTicker,
                ["title"] = series.Title,
                ["category"] = series.Category,
                ["settlementSources"] = ParseOrNull(series.SettlementSourcesJson),
                ["contractTermsUrl"] = series.ContractTermsUrl,
            },
            ["sources"] = sources,
            ["task"] = ResearchContract.Task,
            ["resultContract"] = new JsonObject
            {
                ["schemaVersion"] = ResearchContract.RecordSchema,
                ["jsonSchema"] = JsonNode.Parse(ResearchContract.SchemaJson),
            },
        };

        var packetHash = CanonicalJson.HashOf(body);
        var packetId = $"pkt_{s.Id}_{packetHash[..12]}";
        var full = body.DeepClone().AsObject();
        full["packetId"] = packetId;
        full["packetHash"] = packetHash;
        var json = CanonicalJson.Serialize(full, indented: true);

        var inserted = db.Exec("""
            INSERT OR IGNORE INTO research_packets (packet_id, packet_hash, market_ticker, event_ticker, series_ticker, snapshot_id,
                rules_id, rules_sha256, data_origin, prompt_version, created_at, packet_json)
            VALUES ($id, $h, $t, $e, $s, $snap, $r, $rh, $o, $pv, $c, $j)
            """,
            ("$id", packetId), ("$h", packetHash), ("$t", s.Ticker), ("$e", s.EventTicker), ("$s", ev?.SeriesTicker),
            ("$snap", s.Id), ("$r", rules.Id), ("$rh", rules.Sha256), ("$o", s.DataOrigin), ("$pv", ResearchContract.PromptVersion),
            ("$c", Ts.ToText(time.GetUtcNow())), ("$j", json));
        var stored = db.Scalar<string>("SELECT packet_json FROM research_packets WHERE packet_id = $id", ("$id", packetId))!;
        return new ExportedPacket(packetId, packetHash, s.Ticker, s.DataOrigin, stored, inserted == 0);
    }

    private static JsonObject Source(string kind, RawRef raw) => new()
    {
        ["kind"] = kind,
        ["rawResponseId"] = raw.Id,
        ["url"] = raw.Url,
        ["httpStatus"] = raw.HttpStatus,
        ["receivedAtUtc"] = Ts.ToText(raw.ReceivedAt),
        ["serverDateUtc"] = raw.ServerDate is { } d ? Ts.ToText(d) : null,
        ["bodySha256"] = raw.BodySha256,
    };

    private static JsonNode? ParseOrNull(string? json) => json is null ? null : JsonNode.Parse(json);

    /// <summary>Paste-ready text for a manual round trip in Claude under the user's subscription (spec 4.1).</summary>
    public static string ManualPrompt(string packetJson) =>
        "You are assisting with EXPERIMENTAL research for a paper-trading system. Follow the packet's \"task\" exactly and " +
        "reply with only one JSON object matching packet.resultContract.jsonSchema.\n\n```json\n" + packetJson + "\n```\n";
}
