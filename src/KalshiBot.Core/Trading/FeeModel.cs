using KalshiBot.Core.Common;

namespace KalshiBot.Core.Trading;

/// <param name="VerifiedOn">Date the operator checked it against Kalshi's published schedule; null = unverified.</param>
/// <param name="SyntheticOnly">May only price synthetic fixture data.</param>
public sealed record FeeSchedule(string Id, string Source, DateOnly? VerifiedOn, DateTimeOffset EffectiveFrom,
    decimal TakerCoefficient, IReadOnlySet<string> FeeTypes, bool SyntheticOnly);

/// <summary>
/// Unknown fees block new risk (DATA-01, spec 5.4). The only built-in schedule is a labeled synthetic assumption for
/// fixture replay. A real schedule is added here only after the operator checks
/// https://kalshi.com/docs/kalshi-fee-schedule.pdf (unreachable from the build environment), with its verification
/// date and effective date.
/// </summary>
public static class FeeSchedules
{
    public static readonly FeeSchedule SyntheticFixture = new(
        "synthetic-fixture-quadratic-0.07",
        "SYNTHETIC assumption for fixture replay only: ceil_cent(0.07 * max(1, multiplier) * C * P * (1 - P)). Not a verified Kalshi schedule.",
        VerifiedOn: null, EffectiveFrom: DateTimeOffset.MinValue, TakerCoefficient: 0.07m,
        FeeTypes: new HashSet<string>(StringComparer.Ordinal) { "quadratic" }, SyntheticOnly: true);

    /// <summary>
    /// Kalshi general taker fee, from the Kalshi Fee Schedule PDF "Last updated and effective: July 7, 2026"
    /// (sha256 c326a69f596a11e8f8be2620402d39a8d4823920c21cc97c93a114d862699601), reviewed 2026-11-23.
    /// fees = round up(M x 0.07 x C x P x (1-P)), M = per-series multiplier (default 1). The PDF text says the
    /// fee rounds so fee + position cost lands on a centicent, but its own General Trading Fees Table matches
    /// rounding up to a whole cent; we use the table's (higher) cent rounding. Maker fees (0.0175, default M = 0)
    /// apply only to resting orders, which paper never places, so quadratic_with_maker_fees prices takers the same.
    /// </summary>
    public static readonly FeeSchedule KalshiGeneral20260707 = new(
        "kalshi-general-2026-07-07",
        "Kalshi Fee Schedule PDF, last updated and effective July 7, 2026 (sha256 c326a69f...9601); general taker formula, cent rounding per the published table",
        VerifiedOn: new DateOnly(2026, 11, 23), EffectiveFrom: new DateTimeOffset(2026, 7, 7, 4, 0, 0, TimeSpan.Zero),
        TakerCoefficient: 0.07m,
        FeeTypes: new HashSet<string>(StringComparer.Ordinal) { "quadratic", "quadratic_with_maker_fees" }, SyntheticOnly: false);

    /// <summary>
    /// Verified real schedules. Re-check https://kalshi.com/fee-schedule for scheduled changes; add a new entry with
    /// its own effective date rather than editing this one.
    /// </summary>
    public static IReadOnlyList<FeeSchedule> Verified { get; } = [KalshiGeneral20260707];

    public static FeeContext? Resolve(string dataOrigin, string? feeType, decimal? multiplier, DateTimeOffset at, out string? reason)
    {
        reason = null;
        if (string.IsNullOrEmpty(feeType)) { reason = "market fee type unknown (no series/event fee data recorded)"; return null; }
        var candidates = DataOrigin.IsSynthetic(dataOrigin)
            ? [SyntheticFixture]
            : Verified.Where(s => !s.SyntheticOnly && s.VerifiedOn is not null).ToList();
        var schedule = candidates.Where(s => s.EffectiveFrom <= at && s.FeeTypes.Contains(feeType))
            .OrderByDescending(s => s.EffectiveFrom).FirstOrDefault();
        if (schedule is null)
        {
            reason = DataOrigin.IsSynthetic(dataOrigin)
                ? $"fee type '{feeType}' not covered by the synthetic fixture schedule"
                : $"no verified fee schedule for fee type '{feeType}'; verify Kalshi's schedule before pricing live data";
            return null;
        }
        return new FeeContext(schedule.Id, schedule.TakerCoefficient, multiplier, feeType);
    }
}

public static class FeeModel
{
    public static decimal TakerFee(long contracts, decimal price, FeeContext fees)
    {
        if (contracts <= 0) return 0m;
        if (price is <= 0m or >= 1m) throw new ArgumentOutOfRangeException(nameof(price), price, "Price must be in (0, 1).");
        // The schedule's M can be 0 (fee-free series) or above 1; the API's single fee_multiplier is not documented as
        // the taker M, so paper uses max(1, M): never below the standard fee.
        var multiplier = Math.Max(1m, fees.Multiplier ?? 1m);
        return Dec.CeilCents(fees.TakerCoefficient * multiplier * contracts * price * (1m - price));
    }
}
