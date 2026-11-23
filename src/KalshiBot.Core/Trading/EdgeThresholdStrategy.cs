using System.Globalization;
using KalshiBot.Core.Common;

namespace KalshiBot.Core.Trading;

/// <summary>
/// The single pilot strategy interface implementation (spec 5.3/5.4). It is a reference mechanism, not a claimed
/// edge: buy one side when conservative probability minus best ask minus fee clears a predeclared threshold.
/// It abstains without an allowlisted series, a model forecast, known fees, or an executable ask.
/// minEdge has no default on purpose: thresholds are strategy-specific and must be chosen explicitly.
/// </summary>
public sealed class EdgeThresholdStrategy : IStrategy
{
    private readonly decimal _minEdge;
    private readonly int _contracts;
    private readonly IReadOnlyList<string> _allowedSeries;

    public EdgeThresholdStrategy(decimal minEdge, int contracts, IEnumerable<string> allowedSeries)
    {
        if (minEdge <= 0m) throw new KalshiBotException("min edge must be positive.");
        if (contracts <= 0) throw new KalshiBotException("contracts must be positive.");
        _minEdge = Dec.Normalize(minEdge);
        _contracts = contracts;
        _allowedSeries = allowedSeries.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        ConfigHash = Hash.Sha256Hex(string.Create(CultureInfo.InvariantCulture,
            $"{Id};min_edge={Dec.ToText(_minEdge)};contracts={_contracts};series={string.Join(',', _allowedSeries)};tif=ioc;hold=to_settlement"));
    }

    public string Id => "edge-threshold/2";
    public string ConfigHash { get; }
    public IReadOnlyList<string> AllowedSeries => _allowedSeries;

    public StrategyDecision Decide(StrategyInput input)
    {
        var s = input.Snapshot;
        if (input.SeriesTicker is not { } series || !_allowedSeries.Contains(series))
            return None($"series '{input.SeriesTicker ?? "unknown"}' is not in this strategy's allowlist [{string.Join(", ", _allowedSeries)}]");
        if (input.Forecast is not { } f) return None("no validated forecast model for this market (observe/abstain)");
        if (input.Fees is not { } fees) return None("fees unknown for this market");
        if (s.MarketType != "binary") return None($"market_type '{s.MarketType}' unsupported");
        if (s.Status != "active") return None($"market status '{s.Status}' is not active");
        if (s.Book is not { } book) return None("no order book in snapshot");

        var options = new List<(string Side, decimal Edge, decimal Ask)>();
        foreach (var side in new[] { Side.Yes, Side.No })
        {
            var asks = book.AsksFor(side);
            if (asks.Count == 0) continue;
            var ask = asks[0].Price;
            options.Add((side, f.ConservativeFor(side) - ask - FeeModel.TakerFee(1, ask, fees), ask));
        }
        var detail = string.Join("; ", options.Select(o =>
            string.Create(CultureInfo.InvariantCulture, $"{o.Side}: ask={Dec.Show(o.Ask)} edge_after_fee={Dec.Show(o.Edge)}")));
        var best = options.OrderByDescending(o => o.Edge).ThenBy(o => o.Side, StringComparer.Ordinal).FirstOrDefault();
        if (best.Side is null || best.Edge < _minEdge)
            return None($"no side clears min_edge {Dec.Show(_minEdge)} ({detail}); model {f.ModelId}");

        var intent = new OrderIntent(s.Ticker, best.Side, OrderAction.Buy, _contracts, best.Ask);
        var signalKey = Hash.Sha256Hex($"{Id}|{ConfigHash}|{s.Ticker}|{best.Side}|{f.ModelId}|{f.EvidenceRevision}");
        return new StrategyDecision([new Candidate(intent, signalKey)],
            $"buy {_contracts} {best.Side} @ {Dec.Show(best.Ask)} IOC ({detail}); model {f.ModelId} p_yes={Dec.Show(f.ProbabilityYes)}±{Dec.Show(f.Uncertainty)}" +
            (f.Synthetic ? " SYNTHETIC" : ""));
    }

    private static StrategyDecision None(string why) => new([], why);
}
