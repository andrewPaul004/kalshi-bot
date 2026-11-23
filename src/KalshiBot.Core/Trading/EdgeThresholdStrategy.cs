using System.Globalization;
using KalshiBot.Core.Common;

namespace KalshiBot.Core.Trading;

/// <summary>
/// The single Phase 2 strategy. It is a plumbing reference, not a claimed edge: it compares an externally
/// supplied probability with the best ask after fees and buys one side only when the gap clears a threshold.
/// Without a probability estimate it never trades.
/// </summary>
public sealed class EdgeThresholdStrategy(decimal minEdge = 0.05m, int contracts = 1, long maxHeldPerSide = 5) : IStrategy
{
    public string Id => "edge-threshold/1";

    public string Parameters => string.Create(CultureInfo.InvariantCulture,
        $"min_edge={Dec.Normalize(minEdge)};contracts={contracts};max_held_per_side={maxHeldPerSide}");

    public StrategyDecision Decide(StrategyInput input)
    {
        var s = input.Snapshot;
        if (input.Estimate is not { } est) return None("no probability estimate; strategy does not trade without one");
        if (s.MarketType != "binary") return None($"market_type '{s.MarketType}' unsupported");
        if (s.Status != "active") return None($"market status '{s.Status}' is not active");
        if (s.Book is not { } book) return None("no order book in snapshot");

        var candidates = new List<(string Side, decimal Edge, decimal Ask)>();
        foreach (var side in new[] { Side.Yes, Side.No })
        {
            var asks = book.AsksFor(side);
            if (asks.Count == 0) continue;
            var ask = asks[0].Price;
            var p = side == Side.Yes ? est.ProbabilityYes : 1m - est.ProbabilityYes;
            var feePerContract = FeeModel.TakerFee(1, ask, input.FeeMultiplier);
            candidates.Add((side, p - ask - feePerContract, ask));
        }

        var detail = string.Join("; ", candidates.Select(c =>
            string.Create(CultureInfo.InvariantCulture, $"{c.Side}: ask={c.Ask} edge_after_fee={c.Edge:0.####}")));
        var best = candidates.OrderByDescending(c => c.Edge).ThenBy(c => c.Side, StringComparer.Ordinal).FirstOrDefault();
        if (best.Side is null || best.Edge < minEdge)
            return None($"no side clears min_edge {Dec.ToText(minEdge)} ({detail}); estimate {est.Label}");

        var held = input.Positions.Where(p => p.Side == best.Side).Sum(p => p.Quantity);
        if (held + contracts > maxHeldPerSide)
            return None($"already holding {held} {best.Side}; cap {maxHeldPerSide}");

        return new StrategyDecision(
            [new OrderIntent(s.Ticker, best.Side, OrderAction.Buy, contracts, best.Ask)],
            $"buy {contracts} {best.Side} @ {Dec.ToText(best.Ask)} ({detail}); estimate p_yes={Dec.ToText(est.ProbabilityYes)} {est.Label}");
    }

    private static StrategyDecision None(string why) => new([], why);
}
