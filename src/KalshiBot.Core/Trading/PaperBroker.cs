using KalshiBot.Core.Kalshi;

namespace KalshiBot.Core.Trading;

public sealed record SimulatedFill(int Quantity, decimal Price, decimal Fee);

/// <summary>
/// Paper fill simulation against one recorded order-book snapshot. Immediate-or-cancel only: an order takes
/// displayed liquidity at or better than its limit, whole contracts only, and any remainder is cancelled.
/// No resting orders, no queue position, no market impact beyond the snapshot. Every fill is SIMULATED.
/// </summary>
public static class PaperBroker
{
    public static IReadOnlyList<SimulatedFill> Simulate(OrderIntent o, OrderBook book, decimal? feeMultiplier = null)
    {
        var fills = new List<SimulatedFill>();
        var remaining = o.Quantity;
        var levels = o.Action == OrderAction.Buy ? book.AsksFor(o.Side) : book.BidsFor(o.Side);
        foreach (var level in levels)
        {
            if (remaining == 0) break;
            var acceptable = o.Action == OrderAction.Buy ? level.Price <= o.LimitPrice : level.Price >= o.LimitPrice;
            if (!acceptable) break;
            var available = (int)Math.Min(remaining, Math.Floor(level.Size));
            if (available <= 0) continue;
            fills.Add(new SimulatedFill(available, level.Price, FeeModel.TakerFee(available, level.Price, feeMultiplier)));
            remaining -= available;
        }
        return fills;
    }
}
