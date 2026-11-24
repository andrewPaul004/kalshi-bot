using KalshiBot.Core.Common;
using KalshiBot.Core.Storage;
using KalshiBot.Core.Trading;

namespace KalshiBot.Core.Archive;

/// <summary>
/// Point-in-time evidence from the archive database: a MOS run is visible only when its cycle time plus the availability lag
/// is at or before <see cref="AsOf"/>. Rows are append-only and keyed by run, so a later run can never change what an earlier
/// view returns.
/// </summary>
public sealed class ArchiveEvidence(Db archive, DateTimeOffset asOf, TimeSpan? mosLag = null) : IEvidenceView
{
    private readonly TimeSpan _lag = mosLag ?? DecisionClock.AssumedMosLag;

    public DateTimeOffset AsOf => asOf;

    public MosMax? LatestMosMax(string station, DateOnly climateDate) =>
        archive.Query("""
            SELECT model, runtime, n_x FROM mos_rows
            WHERE station = $s AND model = 'GFS' AND ftime = $ft AND n_x IS NOT NULL AND runtime <= $latest
            ORDER BY runtime DESC LIMIT 1
            """,
            r => new MosMax(r.S("model"), Ts.Parse(r.S("runtime")), r.DN("n_x")!.Value),
            ("$s", station), ("$ft", Ts.ToText(DecisionClock.MosMaxForecastTime(climateDate))), ("$latest", Ts.ToText(asOf - _lag)))
        .FirstOrDefault();
}
