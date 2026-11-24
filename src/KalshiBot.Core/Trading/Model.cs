using KalshiBot.Core.Recording;

namespace KalshiBot.Core.Trading;

/// <summary>Live exists only so the risk engine can refuse it explicitly. Nothing in this phase executes live.</summary>
public enum ExecutionMode { Paper, Live }

public static class Side
{
    public const string Yes = "yes";
    public const string No = "no";
    public static bool IsValid(string s) => s is Yes or No;
}

public static class OrderAction
{
    public const string Buy = "buy";
    public const string Sell = "sell";
    public static bool IsValid(string s) => s is Buy or Sell;
}

/// <summary>
/// A probability from a deterministic, versioned forecast model (spec 3.1 "Strategy evaluator"). Never from research:
/// Claude's experimental probabilities live in the research tables and have no path into this type (AC-05).
/// </summary>
/// <param name="Uncertainty">Policy buffer subtracted from the probability before computing edge (spec 5.3/5.4).</param>
/// <param name="EvidenceRevision">Stable identity of the model inputs; part of the signal key so a new scan of the
/// same evidence cannot authorize another entry (spec 7.2, AC-20).</param>
public sealed record Forecast(string ModelId, decimal ProbabilityYes, decimal Uncertainty, string Method,
    DateTimeOffset InputsAsOf, string EvidenceRevision, bool Synthetic)
{
    public decimal ConservativeFor(string side) =>
        (side == Side.Yes ? ProbabilityYes : 1m - ProbabilityYes) - Uncertainty;
}

public interface IForecastModel
{
    string Id { get; }

    /// <summary>
    /// Deterministic: same snapshot and same evidence view, same forecast. Null means abstain. The view is fixed at decision
    /// time (<see cref="IEvidenceView.AsOf"/>); a model reads evidence only through it.
    /// </summary>
    Forecast? Forecast(MarketSnapshot snapshot, IEvidenceView evidence);
}

/// <summary>A fee schedule that applies to this market at this time. Absent = fees unknown = no new risk (DATA-01).</summary>
public sealed record FeeContext(string ScheduleId, decimal TakerCoefficient, decimal? Multiplier, string FeeType);

public sealed record PositionView(string Ticker, string Side, long Quantity, decimal CostBasis);

public sealed record StrategyInput(MarketSnapshot Snapshot, string? SeriesTicker, Forecast? Forecast, FeeContext? Fees,
    IReadOnlyList<PositionView> Positions);

public sealed record OrderIntent(string Ticker, string Side, string Action, int Quantity, decimal LimitPrice);

/// <param name="SignalKey">Stable key over strategy version, market, side and evidence window (spec 7.2).</param>
public sealed record Candidate(OrderIntent Intent, string SignalKey);

public sealed record StrategyDecision(IReadOnlyList<Candidate> Candidates, string Rationale);

/// <summary>
/// Deterministic strategy contract: Decide is a pure function of its input and the strategy's own configuration.
/// No clock, no I/O, no randomness, no research input.
/// </summary>
public interface IStrategy
{
    /// <summary>Name and version, e.g. "edge-threshold/2". A logic change is a new version.</summary>
    string Id { get; }

    /// <summary>Hash of the full configuration (parameters, allowlist). Part of every decision id.</summary>
    string ConfigHash { get; }

    StrategyDecision Decide(StrategyInput input);
}

/// <summary>Everything the risk engine needs about the account, recomputed from the ledger and reservations.</summary>
public sealed record AccountState(
    long AccountId, string Name, string DataOrigin, decimal StartingCash, decimal Cash,
    IReadOnlyList<PositionView> Positions,
    decimal HeldReservations,
    IReadOnlyDictionary<string, decimal> CapitalAtRiskByTicker,
    IReadOnlyDictionary<string, decimal> CapitalAtRiskByEvent,
    int EntriesToday, int OpenOrders, decimal RealizedPnl, bool Halted)
{
    public long Held(string ticker, string side) =>
        Positions.Where(p => p.Ticker == ticker && p.Side == side).Sum(p => p.Quantity);

    public decimal OpenCostBasis => Positions.Sum(p => p.CostBasis);

    /// <summary>Unsettled capital at risk: open cost basis plus every held reservation (spec 6.1).</summary>
    public decimal CapitalAtRisk => OpenCostBasis + HeldReservations;

    public decimal AvailableCash => Cash - HeldReservations;
}
