using KalshiBot.Core.Common;

namespace KalshiBot.Core.Trading;

/// <summary>
/// Paper fee model. UNVERIFIED in this build: kalshi.com (fee schedule PDF) was unreachable from the build
/// environment. Implements the commonly published general taker formula
/// fee = ceil_to_cent(0.07 * C * P * (1 - P)) and applies a series fee_multiplier only when it would
/// increase the fee (multiplier semantics unverified), so paper results err toward higher costs.
/// </summary>
public static class FeeModel
{
    public const decimal TakerCoefficient = 0.07m;
    public const string Description = "UNVERIFIED paper taker fee: ceil_cent(0.07 * max(1, fee_multiplier) * C * P * (1-P))";

    public static decimal TakerFee(long contracts, decimal price, decimal? feeMultiplier = null)
    {
        if (contracts <= 0) return 0m;
        if (price is <= 0m or >= 1m) throw new ArgumentOutOfRangeException(nameof(price), price, "Price must be in (0, 1).");
        var multiplier = Math.Max(1m, feeMultiplier ?? 1m);
        return Dec.CeilCents(TakerCoefficient * multiplier * contracts * price * (1m - price));
    }
}
