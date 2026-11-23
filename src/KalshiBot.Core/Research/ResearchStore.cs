using KalshiBot.Core.Common;
using KalshiBot.Core.Storage;
using KalshiBot.Core.Trading;

namespace KalshiBot.Core.Research;

public sealed record StoredPacket(string PacketId, string Ticker, long SnapshotId, long RulesId, string RulesSha256,
    string DataOrigin, DateTimeOffset CreatedAt, string Json);

public sealed record StoredResult(long Id, string PacketId, string Ticker, decimal? ProbabilityYes, string? AbstainReason,
    string Confidence, string SourceKind, string ValidationStatus, DateTimeOffset ImportedAt, string DataOrigin);

public sealed class ResearchStore(Db db)
{
    public StoredPacket? Packet(string packetId) =>
        db.QuerySingle("""
            SELECT p.*, r.rules_sha256 FROM research_packets p JOIN market_rules r ON r.id = p.rules_id
            WHERE p.packet_id = $p
            """,
            r => new StoredPacket(r.S("packet_id"), r.S("ticker"), r.L("snapshot_id"), r.L("rules_id"), r.S("rules_sha256"),
                r.S("data_origin"), Ts.Parse(r.S("created_at")), r.S("packet_json")),
            ("$p", packetId));

    public List<StoredResult> Results(int limit = 20) =>
        db.Query("""
            SELECT rr.*, p.data_origin FROM research_results rr JOIN research_packets p ON p.packet_id = rr.packet_id
            ORDER BY rr.imported_at DESC, rr.id DESC LIMIT $n
            """, MapResult, ("$n", limit));

    /// <summary>
    /// The most recent result for a ticker. If the most recent one abstained, there is no estimate:
    /// an older probability is never silently resurrected.
    /// </summary>
    public ProbabilityEstimate? LatestEstimate(string ticker) =>
        db.Query("""
            SELECT rr.*, p.snapshot_id, p.data_origin FROM research_results rr
            JOIN research_packets p ON p.packet_id = rr.packet_id
            WHERE rr.ticker = $t ORDER BY rr.imported_at DESC, rr.id DESC LIMIT 1
            """,
            r => r.DN("probability_yes") is not { } p ? null : new ProbabilityEstimate(
                r.L("id"), r.S("packet_id"), r.L("snapshot_id"), r.S("rules_sha256"), r.S("data_origin"), p,
                r.S("confidence"), r.S("source_kind"), r.S("validation_status"), Ts.Parse(r.S("imported_at"))),
            ("$t", ticker)).FirstOrDefault();

    private static StoredResult MapResult(Microsoft.Data.Sqlite.SqliteDataReader r) => new(
        r.L("id"), r.S("packet_id"), r.S("ticker"), r.DN("probability_yes"), r.Str("abstain_reason"), r.S("confidence"),
        r.S("source_kind"), r.S("validation_status"), Ts.Parse(r.S("imported_at")), r.S("data_origin"));
}
