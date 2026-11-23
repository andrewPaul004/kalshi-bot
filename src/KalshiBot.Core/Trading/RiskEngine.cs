using System.Text.Json.Nodes;
using KalshiBot.Core.Common;
using KalshiBot.Core.Recording;

namespace KalshiBot.Core.Trading;

/// <summary>
/// PAPER defaults only. Live bankroll and limits have not been chosen; there is deliberately no live preset.
/// </summary>
public sealed record RiskLimits
{
    public int MaxContractsPerOrder { get; init; } = 10;
    public decimal MaxOrderCost { get; init; } = 10m;
    public long MaxPositionPerMarketSide { get; init; } = 20;
    public decimal MaxOpenCostBasis { get; init; } = 50m;
    public decimal MaxDailyPremium { get; init; } = 25m;
    public TimeSpan MaxSnapshotAge { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan MaxEstimateAge { get; init; } = TimeSpan.FromHours(24);
    public TimeSpan MinTimeToClose { get; init; } = TimeSpan.FromMinutes(30);
    public TimeSpan MaxClockSkew { get; init; } = TimeSpan.FromMinutes(2);
    public decimal MinPrice { get; init; } = 0.02m;
    public decimal MaxPrice { get; init; } = 0.98m;

    public static RiskLimits PaperDefaults => new();
}

/// <param name="SnapshotLiquidityUsed">An earlier paper order already filled against this snapshot (same side and action).</param>
/// <param name="PositionOrigins">Data origins of the paper orders that built the current position in this market.</param>
public sealed record RiskContext(ExecutionMode Mode, MarketSnapshot Snapshot, string SnapshotRulesSha256,
    ProbabilityEstimate? Estimate, AccountState Account, DateTimeOffset Now, decimal? FeeMultiplier = null,
    bool SnapshotLiquidityUsed = false, IReadOnlyCollection<string>? PositionOrigins = null);

public sealed record RiskVerdict(bool Approved, IReadOnlyList<string> Reasons)
{
    public override string ToString() => Approved ? "approved" : "rejected: " + string.Join("; ", Reasons);
}

/// <summary>
/// Pure, fail-closed pre-trade checks. Every check runs and every failure is reported, in a fixed order,
/// so a verdict is reproducible from its inputs.
/// </summary>
public sealed class RiskEngine(RiskLimits limits)
{
    public RiskLimits Limits => limits;

    public RiskVerdict Evaluate(OrderIntent o, RiskContext c)
    {
        var r = new List<string>();
        var s = c.Snapshot;

        // Authorization boundaries.
        if (c.Mode != ExecutionMode.Paper) r.Add("live execution is not authorized in this phase");
        if (c.Account.Halted) r.Add("kill switch engaged (run 'kb resume' to clear)");
        if (c.Estimate is { IsValidated: false } && c.Mode != ExecutionMode.Paper)
            r.Add("unvalidated research probability cannot drive non-paper execution");

        // Provenance.
        if (c.Estimate is { } e)
        {
            if (e.PacketRulesSha256 != c.SnapshotRulesSha256) r.Add("market rules changed since the research packet was built");
            if (e.PacketDataOrigin != s.DataOrigin) r.Add($"research estimate is from {e.PacketDataOrigin} data but the snapshot is {s.DataOrigin}");
            if (c.Now - e.AsOf > limits.MaxEstimateAge) r.Add($"research estimate evidence (as_of) older than {limits.MaxEstimateAge}");
        }

        // Order shape.
        if (o.Ticker != s.Ticker) r.Add("order ticker does not match snapshot");
        if (!Side.IsValid(o.Side)) r.Add($"invalid side '{o.Side}'");
        if (!OrderAction.IsValid(o.Action)) r.Add($"invalid action '{o.Action}'");
        if (o.Quantity <= 0) r.Add("quantity must be positive");
        if (o.Quantity > limits.MaxContractsPerOrder) r.Add($"quantity {o.Quantity} exceeds max {limits.MaxContractsPerOrder} per order");
        if (o.LimitPrice < limits.MinPrice || o.LimitPrice > limits.MaxPrice)
            r.Add($"limit {Dec.ToText(o.LimitPrice)} outside [{Dec.ToText(limits.MinPrice)}, {Dec.ToText(limits.MaxPrice)}]");
        else if (!IsOnTick(o.LimitPrice, s.PriceRangesJson)) r.Add($"limit {Dec.ToText(o.LimitPrice)} is not a valid tick for this market");

        // Market state and data freshness.
        if (s.MarketType != "binary") r.Add($"market_type '{s.MarketType}' unsupported");
        if (s.Status != "active") r.Add($"market status '{s.Status}' is not 'active'");
        if (s.ObservedAt - c.Now > limits.MaxClockSkew) r.Add("snapshot timestamp is in the future (clock problem)");
        if (c.Now - s.ObservedAt > limits.MaxSnapshotAge) r.Add($"snapshot older than {limits.MaxSnapshotAge}");
        if (s.CloseTime is not { } close) r.Add("snapshot has no close_time");
        else if (close - c.Now < limits.MinTimeToClose) r.Add($"less than {limits.MinTimeToClose} to close");

        if (c.SnapshotLiquidityUsed) r.Add("this snapshot's liquidity was already used by an earlier paper order; record a new snapshot");
        if (c.PositionOrigins?.FirstOrDefault(o => o != s.DataOrigin) is { } other)
            r.Add($"existing position in this market came from {other} data; snapshot is {s.DataOrigin}");

        // Exposure and cash.
        if (o.Quantity > 0 && o.LimitPrice is > 0m and < 1m)
        {
            var held = c.Account.Held(o.Ticker, o.Side);
            if (o.Action == OrderAction.Buy)
            {
                var worstCost = WorstCaseBuyCost(o, c.FeeMultiplier);
                if (worstCost > limits.MaxOrderCost) r.Add($"worst-case cost {Dec.ToText(worstCost)} exceeds max order cost {Dec.ToText(limits.MaxOrderCost)}");
                if (worstCost > c.Account.Cash) r.Add($"insufficient paper cash ({Dec.ToText(c.Account.Cash)} < {Dec.ToText(worstCost)})");
                if (held + o.Quantity > limits.MaxPositionPerMarketSide) r.Add($"position would exceed {limits.MaxPositionPerMarketSide} contracts");
                if (c.Account.OpenCostBasis + o.Quantity * o.LimitPrice > limits.MaxOpenCostBasis) r.Add($"open cost basis would exceed {Dec.ToText(limits.MaxOpenCostBasis)}");
                if (c.Account.TodayPremiumSpent + worstCost > limits.MaxDailyPremium) r.Add($"daily premium would exceed {Dec.ToText(limits.MaxDailyPremium)}");
            }
            else if (o.Action == OrderAction.Sell && o.Quantity > held)
            {
                r.Add($"cannot sell {o.Quantity} {o.Side}; holding {held} (no short selling)");
            }
        }

        return new RiskVerdict(r.Count == 0, r);
    }

    /// <summary>
    /// True upper bound on what a buy can cost: every contract at the limit, plus a separately rounded fee per contract
    /// at the fee-maximizing price at or below the limit. The broker rounds fees per price level, which is never more
    /// than rounding per contract, so actual cost cannot exceed this.
    /// </summary>
    public static decimal WorstCaseBuyCost(OrderIntent o, decimal? feeMultiplier)
    {
        var feePeak = Math.Min(o.LimitPrice, 0.5m);
        return o.Quantity * o.LimitPrice + o.Quantity * FeeModel.TakerFee(1, feePeak, feeMultiplier);
    }

    /// <summary>Tick check against Kalshi price_ranges [{start,end,step}]; without ranges, require whole cents.</summary>
    internal static bool IsOnTick(decimal price, string? priceRangesJson)
    {
        if (string.IsNullOrEmpty(priceRangesJson)) return price * 100m == Math.Floor(price * 100m);
        foreach (var n in JsonNode.Parse(priceRangesJson)!.AsArray())
        {
            var start = Dec.Parse(n!["start"]!.GetValue<string>());
            var end = Dec.Parse(n["end"]!.GetValue<string>());
            var step = Dec.Parse(n["step"]!.GetValue<string>());
            if (step > 0m && price >= start && price <= end && (price - start) % step == 0m) return true;
        }
        return false;
    }
}
