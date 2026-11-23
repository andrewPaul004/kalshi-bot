using KalshiBot.Core.Recording;
using KalshiBot.Core.Storage;

namespace KalshiBot.Core.Research;

public sealed record ScoredRecord(long RecordId, string ResearchId, string MarketTicker, string EventTicker, string SourceKind,
    string DataOrigin, decimal Probability, int Outcome, decimal Brier, decimal? MarketMid, decimal? MarketBrier);

public sealed record ScoreSummary(int Records, int WithProbability, int Unresolved, IReadOnlyList<ScoredRecord> Scored)
{
    public int IndependentEvents => Scored.Select(s => s.EventTicker).Distinct().Count();
    public decimal? MeanBrier => Scored.Count == 0 ? null : Scored.Average(s => s.Brier);
    public decimal? MeanMarketBrier => Scored.Any(s => s.MarketBrier is not null)
        ? Scored.Where(s => s.MarketBrier is not null).Average(s => s.MarketBrier!.Value) : null;
}

/// <summary>
/// Scores experimental research probabilities against settled outcomes (spec 8.3). Brier score = (p - outcome)^2.
/// The baseline is the market's own YES mid-price on the snapshot the packet was built from (same time, before any
/// research). Read-only and report-only: nothing here feeds a trading decision.
/// </summary>
public static class ResearchScoring
{
    public static ScoreSummary Score(Db db)
    {
        var store = new MarketStore(db);
        var records = db.Query("""
            SELECT rr.id, rr.research_id, rr.market_ticker, rr.source_kind, ep.probability_yes, p.snapshot_id, p.event_ticker, p.data_origin
            FROM research_records rr JOIN research_packets p ON p.packet_id = rr.packet_id
            LEFT JOIN experimental_probabilities ep ON ep.research_record_id = rr.id ORDER BY rr.id
            """, r => new
            {
                Id = r.L("id"), ResearchId = r.S("research_id"), Ticker = r.S("market_ticker"), Source = r.S("source_kind"),
                P = r.DN("probability_yes"), SnapshotId = r.L("snapshot_id"), Event = r.S("event_ticker"), Origin = r.S("data_origin"),
            });

        var scored = new List<ScoredRecord>();
        var unresolved = 0;
        foreach (var rec in records.Where(r => r.P is not null))
        {
            var latest = store.Latest(rec.Ticker);
            if (latest is not { Status: "finalized", Result: "yes" or "no" }) { unresolved++; continue; }
            var outcome = latest.Result == "yes" ? 1 : 0;
            var at = store.ById(rec.SnapshotId);
            decimal? mid = at is { YesBid: { } bid, YesAsk: { } ask } && bid > 0m && ask > 0m && ask >= bid ? (bid + ask) / 2m : null;
            scored.Add(new ScoredRecord(rec.Id, rec.ResearchId, rec.Ticker, rec.Event, rec.Source, rec.Origin, rec.P!.Value, outcome,
                Sq(rec.P.Value - outcome), mid, mid is { } m ? Sq(m - outcome) : null));
        }
        return new ScoreSummary(records.Count, records.Count(r => r.P is not null), unresolved, scored);
    }

    private static decimal Sq(decimal d) => d * d;
}
