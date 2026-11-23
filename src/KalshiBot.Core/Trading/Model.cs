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
/// A probability from the research pipeline. ValidationStatus is always "unvalidated" in this phase
/// (enforced by a schema CHECK); the type keeps the field so the boundary is explicit at every use.
/// </summary>
public sealed record ProbabilityEstimate(
    long ResearchResultId, string PacketId, long PacketSnapshotId, string PacketRulesSha256, string PacketDataOrigin,
    decimal ProbabilityYes, string Confidence, string SourceKind, string ValidationStatus, DateTimeOffset ImportedAt,
    DateTimeOffset AsOf)
{
    public bool IsValidated => ValidationStatus == "validated";
    public string Label => IsValidated ? "validated" : "EXPERIMENTAL (unvalidated)";
}

public sealed record PositionView(string Ticker, string Side, long Quantity, decimal CostBasis);

public sealed record StrategyInput(MarketSnapshot Snapshot, string RulesSha256, ProbabilityEstimate? Estimate,
    IReadOnlyList<PositionView> Positions, decimal? FeeMultiplier = null);

public sealed record OrderIntent(string Ticker, string Side, string Action, int Quantity, decimal LimitPrice);

public sealed record StrategyDecision(IReadOnlyList<OrderIntent> Orders, string Rationale);

/// <summary>
/// Deterministic strategy contract: Decide must be a pure function of its input and the strategy's own
/// parameters. No clock, no I/O, no randomness. The engine hashes (Id, Parameters, input) into a decision id,
/// which makes reruns idempotent.
/// </summary>
public interface IStrategy
{
    /// <summary>Stable name including version, e.g. "edge-threshold/1". Change the version when logic changes.</summary>
    string Id { get; }

    /// <summary>Canonical parameter text included in the decision hash.</summary>
    string Parameters { get; }

    StrategyDecision Decide(StrategyInput input);
}

public sealed record AccountState(long AccountId, string Name, decimal StartingCash, decimal Cash,
    IReadOnlyList<PositionView> Positions, decimal TodayPremiumSpent, decimal RealizedPnl, bool Halted)
{
    public long Held(string ticker, string side) =>
        Positions.Where(p => p.Ticker == ticker && p.Side == side).Sum(p => p.Quantity);

    public decimal OpenCostBasis => Positions.Sum(p => p.CostBasis);
}
