using KalshiBot.Core.Common;
using KalshiBot.Core.Storage;

namespace KalshiBot.Core.Research;

public sealed record StoredPacket(string PacketId, string PacketHash, string MarketTicker, string EventTicker, string? SeriesTicker,
    long SnapshotId, long RulesId, string RulesSha256, string DataOrigin, string PromptVersion, DateTimeOffset CreatedAt, string Json);

public sealed record StoredRecord(long Id, string ResearchId, string PacketId, string MarketTicker, string Disposition,
    DateTimeOffset CreatedAtUtc, DateTimeOffset ValidUntilUtc, string SourceKind, decimal? ExperimentalProbability,
    string? ProbabilityMethod, string DataOrigin, DateTimeOffset ImportedAt);

public sealed record Attestation(long Id, DateTimeOffset AttestedAt, string Statement);

/// <summary>Research read side. Used by research and reporting code only; trading code never references it (AC-05).</summary>
public sealed class ResearchStore(Db db)
{
    public const string UsageCreditsStatement = "Usage credits / extra usage are disabled on the Claude account used for research.";

    public StoredPacket? Packet(string packetId) =>
        db.QuerySingle("SELECT * FROM research_packets WHERE packet_id = $p",
            r => new StoredPacket(r.S("packet_id"), r.S("packet_hash"), r.S("market_ticker"), r.S("event_ticker"), r.Str("series_ticker"),
                r.L("snapshot_id"), r.L("rules_id"), r.S("rules_sha256"), r.S("data_origin"), r.S("prompt_version"),
                Ts.Parse(r.S("created_at")), r.S("packet_json")),
            ("$p", packetId));

    public List<StoredRecord> Records(int limit = 20) =>
        db.Query("""
            SELECT rr.*, ep.probability_yes, ep.method, p.data_origin FROM research_records rr
            JOIN research_packets p ON p.packet_id = rr.packet_id
            LEFT JOIN experimental_probabilities ep ON ep.research_record_id = rr.id
            ORDER BY rr.imported_at DESC, rr.id DESC LIMIT $n
            """,
            r => new StoredRecord(r.L("id"), r.S("research_id"), r.S("packet_id"), r.S("market_ticker"), r.S("disposition"),
                Ts.Parse(r.S("created_at_utc")), Ts.Parse(r.S("valid_until_utc")), r.S("source_kind"), r.DN("probability_yes"),
                r.Str("method"), r.S("data_origin"), Ts.Parse(r.S("imported_at"))),
            ("$n", limit));

    /// <summary>Cache key per spec 4.3: an imported CLI record for this exact packet (hash) and prompt version.</summary>
    public bool HasCliRecordFor(string packetId, string promptVersion) =>
        db.Scalar<long>("""
            SELECT COUNT(*) FROM research_records WHERE packet_id = $p AND prompt_version = $v AND source_kind = 'claude-subscription-cli'
            """, ("$p", packetId), ("$v", promptVersion)) > 0;

    public Attestation? LatestAttestation() =>
        db.QuerySingle("SELECT * FROM billing_attestations ORDER BY attested_at DESC, id DESC LIMIT 1",
            r => new Attestation(r.L("id"), Ts.Parse(r.S("attested_at")), r.S("statement")));

    public long Attest(DateTimeOffset at) =>
        db.Insert("INSERT INTO billing_attestations (attested_at, statement) VALUES ($t, $s)",
            ("$t", Ts.ToText(at)), ("$s", UsageCreditsStatement));
}
