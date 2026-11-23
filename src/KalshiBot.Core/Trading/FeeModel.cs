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

    /// <summary>Verified real schedules. Empty until verified; live-origin data therefore has unknown fees.</summary>
    public static IReadOnlyList<FeeSchedule> Verified { get; } = [];

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
        // Multiplier semantics are unverified; it may only raise the fee.
        var multiplier = Math.Max(1m, fees.Multiplier ?? 1m);
        return Dec.CeilCents(fees.TakerCoefficient * multiplier * contracts * price * (1m - price));
    }
}
