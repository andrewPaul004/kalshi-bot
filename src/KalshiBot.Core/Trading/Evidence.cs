using KalshiBot.Core.Common;
using KalshiBot.Core.Recording;

namespace KalshiBot.Core.Trading;

/// <summary>One GFS MOS run's daily-max temperature for a climate date.</summary>
public sealed record MosMax(string Model, DateTimeOffset Runtime, decimal MaxTempF);

/// <summary>
/// What a forecast model may know at decision time (docs/PHASE3_PLAN.md W4). A view is fixed at <see cref="AsOf"/> and must
/// never expose a vintage that was not available then, so a model that is a pure function of (snapshot, view) cannot see the
/// future, in a retrospective replay or live. The view is data, not a clock: models stay pure.
/// </summary>
public interface IEvidenceView
{
    /// <summary>The instant this view is valid for. Nothing published after it is visible.</summary>
    DateTimeOffset AsOf { get; }

    /// <summary>
    /// The newest GFS MOS run available at <see cref="AsOf"/> that carries a daily-max value for the climate date, or null.
    /// </summary>
    MosMax? LatestMosMax(string station, DateOnly climateDate);
}

/// <summary>No evidence at all: every query returns null, so evidence-dependent models abstain.</summary>
public sealed class EmptyEvidence(DateTimeOffset asOf) : IEvidenceView
{
    public DateTimeOffset AsOf => asOf;
    public MosMax? LatestMosMax(string station, DateOnly climateDate) => null;
}

/// <summary>The single path from a model to a forecast, so a look-ahead view cannot reach a model.</summary>
public static class EvidenceGuard
{
    /// <summary>
    /// Refuses a view dated after the snapshot it accompanies. Without this, a caller could hand a model next-day data and
    /// the forecast would look better than anything achievable live.
    /// </summary>
    public static Forecast? Run(IForecastModel model, MarketSnapshot snapshot, IEvidenceView evidence)
    {
        if (evidence.AsOf > snapshot.ObservedAt)
            throw new KalshiBotException(
                $"Evidence as of {Ts.ToText(evidence.AsOf)} is later than the snapshot at {Ts.ToText(snapshot.ObservedAt)}; refusing look-ahead.");
        return model.Forecast(snapshot, evidence);
    }
}
