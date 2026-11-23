using KalshiBot.Core.Kalshi;

namespace KalshiBot.Core.Trading;

public sealed record SimulatedFill(int Quantity, decimal Price, decimal Fee);

/// <summary>
/// Paper fill model (spec 8.4). A marketable IOC limit takes displayed liquidity at or better than its limit from the
/// first snapshot observed at or after its simulated arrival (selected by <see cref="PaperEngine"/>), never from the
/// decision snapshot. Liquidity already consumed by earlier paper fills on that snapshot is subtracted, whole contracts
/// only, and any remainder is cancelled. No resting orders, no queue model, no price improvement. Every fill is SIMULATED.
/// </summary>
public static class PaperBroker
{
    /// <param name="consumed">Contracts already filled by paper orders against this snapshot, keyed by price level.</param>
    public static IReadOnlyList<SimulatedFill> Simulate(OrderIntent o, OrderBook book, FeeContext fees,
        IReadOnlyDictionary<decimal, long>? consumed = null)
    {
        var fills = new List<SimulatedFill>();
        var remaining = o.Quantity;
        var levels = o.Action == OrderAction.Buy ? book.AsksFor(o.Side) : book.BidsFor(o.Side);
        foreach (var level in levels)
        {
            if (remaining == 0) break;
            var acceptable = o.Action == OrderAction.Buy ? level.Price <= o.LimitPrice : level.Price >= o.LimitPrice;
            if (!acceptable) break;
            var displayed = (long)Math.Floor(level.Size) - (consumed?.GetValueOrDefault(level.Price) ?? 0);
            var take = (int)Math.Min(remaining, Math.Max(0, displayed));
            if (take <= 0) continue;
            fills.Add(new SimulatedFill(take, level.Price, FeeModel.TakerFee(take, level.Price, fees)));
            remaining -= take;
        }
        return fills;
    }
}
