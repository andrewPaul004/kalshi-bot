using System.Text.Json.Nodes;
using KalshiBot.Core.Common;
using KalshiBot.Core.Recording;

namespace KalshiBot.Core.Trading;

/// <summary>
/// PAPER fixture limits from spec 6.2 (bankroll $500, $10 per event, $75 portfolio, $15 daily loss trigger,
/// 3 entries/day) plus operational guards. Not live limits: live values are unset by design and there is no live preset.
/// </summary>
public sealed record RiskLimits
{
    public decimal StartingBankroll { get; init; } = 500m;
    public int MaxContractsPerOrder { get; init; } = 10;
    public decimal MaxOrderCapitalAtRisk { get; init; } = 10m;
    public decimal MaxMarketCapitalAtRisk { get; init; } = 10m;
    public decimal MaxEventCapitalAtRisk { get; init; } = 10m;
    public decimal MaxPortfolioCapitalAtRisk { get; init; } = 75m;
    public decimal DailyLossTrigger { get; init; } = 15m;
    public int MaxEntriesPerDay { get; init; } = 3;
    public int MaxOpenOrders { get; init; } = 5;
    public decimal MinCashBuffer { get; init; } = 0m;
    public TimeSpan MaxSnapshotAge { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan MaxForecastAge { get; init; } = TimeSpan.FromHours(24);
    public TimeSpan MinTimeToClose { get; init; } = TimeSpan.FromMinutes(30);
    public TimeSpan MaxClockSkew { get; init; } = TimeSpan.FromMinutes(2);
    public decimal MinPrice { get; init; } = 0.02m;
    public decimal MaxPrice { get; init; } = 0.98m;

    public static RiskLimits PaperFixture => new();
}

/// <param name="RulesChanged">The snapshot's rules version differs from the market's previous snapshot.</param>
/// <param name="ValuationProblem">Non-null when marked equity is uncertain (missing or stale marks): no new risk.</param>
/// <param name="SignalAlreadyEntered">An entry already exists for this signal key (spec 7.2, AC-20).</param>
public sealed record RiskContext(
    ExecutionMode Mode, MarketSnapshot Snapshot, Forecast? Forecast, FeeContext? Fees, AccountState Account,
    DateTimeOffset Now, bool RulesChanged = false, string? ValuationProblem = null, bool SignalAlreadyEntered = false);

public sealed record RiskVerdict(bool Approved, IReadOnlyList<string> Reasons)
{
    public override string ToString() => Approved ? "approved" : "rejected: " + string.Join("; ", Reasons);
}

/// <summary>
/// Pure, fail-closed pre-trade checks. Every check runs and every failure is reported in a fixed order, so a verdict
/// is reproducible from its inputs (AC-10, AC-14). The caller evaluates and reserves inside one IMMEDIATE transaction,
/// so concurrent candidates cannot spend the same capacity (AC-06).
/// </summary>
public sealed class RiskEngine(RiskLimits limits)
{
    public RiskLimits Limits => limits;

    public RiskVerdict Evaluate(OrderIntent o, RiskContext c)
    {
        var r = new List<string>();
        var s = c.Snapshot;
        var a = c.Account;

        // Authority and mode boundaries.
        if (c.Mode != ExecutionMode.Paper) r.Add("live execution is not authorized in this phase");
        if (a.Halted) r.Add("safety halt engaged (operator reset required: 'kb resume')");
        if (a.DataOrigin != s.DataOrigin) r.Add($"account holds {a.DataOrigin} data; snapshot is {s.DataOrigin}");
        if (c.ValuationProblem is { } vp) r.Add($"valuation uncertain: {vp}");

        // Required inputs (DATA-01, AC-10).
        if (c.Forecast is not { } f) r.Add("no forecast from a deterministic model");
        else
        {
            if (f.Synthetic != s.IsSynthetic) r.Add("forecast and snapshot differ in data origin");
            if (c.Now - f.InputsAsOf > limits.MaxForecastAge) r.Add($"forecast inputs older than {limits.MaxForecastAge}");
        }
        if (c.Fees is null) r.Add("fees unknown");
        if (c.RulesChanged) r.Add("market rules changed since the previous snapshot; review with 'kb rules'");
        if (c.SignalAlreadyEntered) r.Add("signal already entered; no re-entry rule is approved");

        // Order shape and price grid.
        if (o.Ticker != s.Ticker) r.Add("order ticker does not match snapshot");
        if (!Side.IsValid(o.Side)) r.Add($"invalid side '{o.Side}'");
        if (!OrderAction.IsValid(o.Action)) r.Add($"invalid action '{o.Action}'");
        if (o.Quantity <= 0) r.Add("quantity must be positive");
        if (o.Quantity > limits.MaxContractsPerOrder) r.Add($"quantity {o.Quantity} exceeds max {limits.MaxContractsPerOrder} per order");
        if (o.LimitPrice < limits.MinPrice || o.LimitPrice > limits.MaxPrice)
            r.Add($"limit {Dec.Show(o.LimitPrice)} outside [{Dec.Show(limits.MinPrice)}, {Dec.Show(limits.MaxPrice)}]");
        else if (!IsOnTick(o.LimitPrice, s.PriceRangesJson)) r.Add($"limit {Dec.ToText(o.LimitPrice)} is not on this market's price grid");

        // Market state and data freshness.
        if (s.MarketType != "binary") r.Add($"market_type '{s.MarketType}' unsupported");
        if (s.Status != "active") r.Add($"market status '{s.Status}' is not 'active'");
        if (s.ObservedAt - c.Now > limits.MaxClockSkew) r.Add("snapshot timestamp is in the future (clock problem)");
        if (c.Now - s.ObservedAt > limits.MaxSnapshotAge) r.Add($"snapshot older than {limits.MaxSnapshotAge}");
        if (s.CloseTime is not { } close) r.Add("snapshot has no close_time");
        else if (close - c.Now < limits.MinTimeToClose) r.Add($"less than {limits.MinTimeToClose} to close");

        // Capital at risk, reservations included (spec 6.1).
        if (o.Action == OrderAction.Buy && o.Quantity > 0 && o.LimitPrice is > 0m and < 1m && c.Fees is { } fees)
        {
            var worst = WorstCaseBuyCost(o, fees);
            var market = a.CapitalAtRiskByTicker.GetValueOrDefault(o.Ticker);
            var evt = a.CapitalAtRiskByEvent.GetValueOrDefault(s.EventTicker);
            if (worst > limits.MaxOrderCapitalAtRisk) r.Add($"order capital at risk {Dec.Show(worst)} exceeds {Dec.Show(limits.MaxOrderCapitalAtRisk)}");
            if (market + worst > limits.MaxMarketCapitalAtRisk) r.Add($"market capital at risk would be {Dec.Show(market + worst)} > {Dec.Show(limits.MaxMarketCapitalAtRisk)}");
            if (evt + worst > limits.MaxEventCapitalAtRisk) r.Add($"event capital at risk would be {Dec.Show(evt + worst)} > {Dec.Show(limits.MaxEventCapitalAtRisk)}");
            if (a.CapitalAtRisk + worst > limits.MaxPortfolioCapitalAtRisk) r.Add($"portfolio capital at risk would be {Dec.Show(a.CapitalAtRisk + worst)} > {Dec.Show(limits.MaxPortfolioCapitalAtRisk)}");
            if (a.AvailableCash - worst < limits.MinCashBuffer) r.Add($"insufficient available paper cash ({Dec.Show(a.AvailableCash)} after reservations; need {Dec.Show(worst)})");
            if (a.EntriesToday >= limits.MaxEntriesPerDay) r.Add($"daily entry limit {limits.MaxEntriesPerDay} reached (America/Chicago day)");
            if (a.OpenOrders >= limits.MaxOpenOrders) r.Add($"open order limit {limits.MaxOpenOrders} reached");
        }
        else if (o.Action == OrderAction.Sell && o.Quantity > a.Held(o.Ticker, o.Side))
        {
            r.Add($"cannot sell {o.Quantity} {o.Side}; holding {a.Held(o.Ticker, o.Side)} (no short selling)");
        }

        return new RiskVerdict(r.Count == 0, r);
    }

    /// <summary>
    /// True upper bound on what a buy can cost: every contract at the limit, plus a separately rounded fee per contract
    /// at the fee-maximizing price at or below the limit. The broker rounds per price level, which never exceeds
    /// rounding per contract, so the actual cost cannot exceed this. Reservations hold exactly this amount.
    /// </summary>
    public static decimal WorstCaseBuyCost(OrderIntent o, FeeContext fees)
    {
        var feePeak = Math.Min(o.LimitPrice, 0.5m);
        return o.Quantity * o.LimitPrice + o.Quantity * FeeModel.TakerFee(1, feePeak, fees);
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
