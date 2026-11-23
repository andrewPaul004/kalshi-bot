using KalshiBot.Core.Common;
using KalshiBot.Core.Kalshi;
using KalshiBot.Core.Recording;
using KalshiBot.Core.Trading;
using Xunit;

namespace KalshiBot.Tests;

public class RiskEngineTests
{
    private static readonly FixedClock Clock = FixedClock.Default();
    private static readonly RiskEngine Risk = new(RiskLimits.PaperFixture);
    private static readonly FeeContext Fees = new("test", 0.07m, null, "quadratic");
    private const string Grid = """[{"start":"0.0100","end":"0.9900","step":"0.0100"}]""";

    private static MarketSnapshot Snap(string status = "active", DateTimeOffset? observed = null, DateTimeOffset? close = null,
        string origin = DataOrigin.Synthetic, string type = "binary", string grid = Grid) =>
        new(1, 1, origin, observed ?? Clock.Now, Fx.T50, "EV", type, "t", "y", status, close ?? Clock.Now.AddDays(30), null,
            0.40m, 0.45m, 0.55m, 0.60m, 0.42m, 1m, 1m, "", grid, null, 1, 1, null);

    private static Forecast Fc(bool synthetic = true, DateTimeOffset? asOf = null) =>
        new("m/1", 0.6m, 0.05m, "test", asOf ?? Clock.Now, "rev", synthetic);

    private static AccountState Account(decimal cash = 500m, bool halted = false, decimal held = 0m, int entries = 0, int open = 0,
        decimal market = 0m, decimal evt = 0m, string origin = DataOrigin.Synthetic, decimal openCost = 0m) =>
        new(1, "paper", origin, 500m, cash,
            openCost == 0m ? [] : [new PositionView("OTHER", Side.Yes, 1, openCost)],
            held,
            new Dictionary<string, decimal> { [Fx.T50] = market },
            new Dictionary<string, decimal> { ["EV"] = evt },
            entries, open, 0m, halted);

    private static RiskVerdict Eval(OrderIntent o, MarketSnapshot? s = null, AccountState? a = null, Forecast? f = null,
        FeeContext? fees = null, ExecutionMode mode = ExecutionMode.Paper, bool rulesChanged = false, string? valuation = null,
        bool entered = false, bool noForecast = false, bool noFees = false) =>
        Risk.Evaluate(o, new RiskContext(mode, s ?? Snap(), noForecast ? null : f ?? Fc(), noFees ? null : fees ?? Fees,
            a ?? Account(), Clock.Now, rulesChanged, valuation, entered));

    private static OrderIntent Buy(int qty = 1, decimal px = 0.45m, string side = Side.Yes) => new(Fx.T50, side, OrderAction.Buy, qty, px);

    [Fact]
    public void Clean_paper_order_is_approved() => Assert.True(Eval(Buy()).Approved);

    [Fact]
    public void Live_mode_is_always_refused() =>
        Assert.Contains("live execution is not authorized in this phase", Eval(Buy(), mode: ExecutionMode.Live).Reasons);

    [Fact]
    public void Safety_halt_blocks_new_risk() =>
        Assert.Contains(Eval(Buy(), a: Account(halted: true)).Reasons, r => r.StartsWith("safety halt engaged"));

    [Fact] // AC-10: each invalid required input gives a specific deterministic rejection.
    public void Invalid_required_inputs_are_rejected_with_specific_reasons()
    {
        Assert.Contains("no forecast from a deterministic model", Eval(Buy(), noForecast: true).Reasons);
        Assert.Contains("fees unknown", Eval(Buy(), noFees: true).Reasons);
        Assert.Contains(Eval(Buy(), rulesChanged: true).Reasons, r => r.StartsWith("market rules changed"));
        Assert.Contains("valuation uncertain: mark for X is stale", Eval(Buy(), valuation: "mark for X is stale").Reasons);
        Assert.Contains(Eval(Buy(), Snap(observed: Clock.Now.AddMinutes(-11))).Reasons, r => r.StartsWith("snapshot older than"));
        Assert.Contains("snapshot timestamp is in the future (clock problem)", Eval(Buy(), Snap(observed: Clock.Now.AddMinutes(5))).Reasons);
        Assert.Contains(Eval(Buy(), f: Fc(asOf: Clock.Now.AddDays(-2))).Reasons, r => r.StartsWith("forecast inputs older than"));
        Assert.Contains("forecast and snapshot differ in data origin", Eval(Buy(), f: Fc(synthetic: false)).Reasons);
        Assert.Contains(Eval(Buy(), a: Account(origin: DataOrigin.LiveProduction)).Reasons, r => r.StartsWith("account holds live:production"));
        Assert.Contains("signal already entered; no re-entry rule is approved", Eval(Buy(), entered: true).Reasons);
    }

    [Theory]
    [InlineData("closed")]
    [InlineData("determined")]
    [InlineData("finalized")]
    [InlineData("initialized")]
    public void Only_active_markets_trade(string status) =>
        Assert.Contains($"market status '{status}' is not 'active'", Eval(Buy(), Snap(status)).Reasons);

    [Fact]
    public void Near_close_and_scalar_markets_are_refused()
    {
        Assert.Contains(Eval(Buy(), Snap(close: Clock.Now.AddMinutes(10))).Reasons, r => r.StartsWith("less than"));
        Assert.Contains(Eval(Buy(), Snap(type: "scalar")).Reasons, r => r.Contains("unsupported"));
    }

    [Fact] // Spec 6.2 paper fixture: $10 per event/market/order, $75 portfolio, 3 entries/day.
    public void Capital_at_risk_limits_include_reservations()
    {
        var tight = new RiskEngine(RiskLimits.PaperFixture with { MaxOrderCapitalAtRisk = 1m });
        Assert.Contains(tight.Evaluate(Buy(qty: 3), new RiskContext(ExecutionMode.Paper, Snap(), Fc(), Fees, Account(), Clock.Now)).Reasons,
            r => r.StartsWith("order capital at risk"));
        Assert.Contains(Eval(Buy(), a: Account(market: 9.6m, evt: 9.6m)).Reasons, r => r.StartsWith("market capital at risk would be"));
        Assert.Contains(Eval(Buy(), a: Account(evt: 9.6m)).Reasons, r => r.StartsWith("event capital at risk would be"));
        Assert.Contains(Eval(Buy(), a: Account(held: 40m, openCost: 34.8m)).Reasons, r => r.StartsWith("portfolio capital at risk would be"));
        Assert.Contains(Eval(Buy(), a: Account(cash: 1m, held: 0.6m)).Reasons, r => r.StartsWith("insufficient available paper cash"));
        Assert.Contains(Eval(Buy(), a: Account(entries: 3)).Reasons, r => r.StartsWith("daily entry limit 3"));
        Assert.Contains(Eval(Buy(), a: Account(open: 5)).Reasons, r => r.StartsWith("open order limit"));
    }

    [Fact]
    public void Verdicts_are_deterministic_and_report_every_failure()
    {
        var o = new OrderIntent("OTHER", "maybe", "hold", 0, 1.5m);
        var a = Eval(o, Snap("closed"), Account(halted: true), mode: ExecutionMode.Live, noForecast: true, noFees: true);
        var b = Eval(o, Snap("closed"), Account(halted: true), mode: ExecutionMode.Live, noForecast: true, noFees: true);
        Assert.Equal(a.Reasons, b.Reasons);
        Assert.True(a.Reasons.Count >= 9);
    }

    [Fact] // AC-12: YES/NO near grid boundaries.
    public void Side_complement_grid_and_maximum_loss_are_economically_correct()
    {
        // NO bid 0.99 => YES ask 0.01; YES bid 0.02 => NO ask 0.98. Complement prices are exact decimals.
        var book = new OrderBook([new PriceLevel(0.02m, 5m)], [new PriceLevel(0.99m, 5m)]);
        Assert.Equal(0.01m, book.YesAsks[0].Price);
        Assert.Equal(0.98m, book.NoAsks[0].Price);

        // Buying NO at 0.98: worst-case loss is premium plus rounded-up fee, never the complement.
        var buyNo = new OrderIntent(Fx.T50, Side.No, OrderAction.Buy, 3, 0.98m);
        Assert.Equal(3 * 0.98m + 3 * FeeModel.TakerFee(1, 0.5m, Fees), RiskEngine.WorstCaseBuyCost(buyNo, Fees));
        var fill = Assert.Single(PaperBroker.Simulate(buyNo, book, Fees));
        Assert.Equal((3, 0.98m, 0.01m), (fill.Quantity, fill.Price, fill.Fee)); // 0.07*3*0.98*0.02=0.004116 -> 0.01

        // Sub-cent grids are respected; off-grid limits are refused.
        const string fine = """[{"start":"0.0000","end":"0.1000","step":"0.0010"},{"start":"0.1000","end":"0.9000","step":"0.0100"}]""";
        Assert.True(RiskEngine.IsOnTick(0.053m, fine));
        Assert.False(RiskEngine.IsOnTick(0.455m, fine));
        Assert.Contains(Eval(Buy(px: 0.455m)).Reasons, r => r.Contains("not on this market's price grid"));
        Assert.Contains(Eval(Buy(px: 0.99m)).Reasons, r => r.Contains("outside"));
    }

    [Fact]
    public void Worst_case_cost_bounds_per_level_fee_rounding()
    {
        // Eight one-contract asks 0.173..0.180 on a 0.001 grid: per-level rounding costs more than one rounded fee.
        var levels = Enumerable.Range(0, 8).Select(i => new PriceLevel(1m - (0.173m + i * 0.001m), 1m)).ToList();
        var o = new OrderIntent(Fx.T50, Side.Yes, OrderAction.Buy, 8, 0.180m);
        var fills = PaperBroker.Simulate(o, new OrderBook([], levels), Fees);
        Assert.Equal(1.572m, fills.Sum(f => f.Quantity * f.Price + f.Fee));
        Assert.True(RiskEngine.WorstCaseBuyCost(o, Fees) >= 1.572m);
    }

    [Fact]
    public void Selling_more_than_held_is_refused()
    {
        var sell = new OrderIntent(Fx.T50, Side.Yes, OrderAction.Sell, 3, 0.40m);
        var holding = new AccountState(1, "paper", DataOrigin.Synthetic, 500m, 500m, [new PositionView(Fx.T50, Side.Yes, 2, 0.9m)],
            0m, new Dictionary<string, decimal>(), new Dictionary<string, decimal>(), 0, 0, 0m, false);
        Assert.Contains(Eval(sell, a: holding).Reasons, r => r.StartsWith("cannot sell 3"));
    }
}

public class StrategyTests
{
    private static readonly FixedClock Clock = FixedClock.Default();
    private static readonly FeeContext Fees = new("test", 0.07m, null, "quadratic");

    private static MarketSnapshot Snapshot()
    {
        using var t = new TempDb();
        Fx.Record(t.Db, Clock, Fx.Open, Fx.T50);
        return new MarketStore(t.Db).Latest(Fx.T50)!;
    }

    private static Forecast Fc(decimal p, decimal u = 0.05m) => new("m/1", p, u, "test", Clock.Now, "rev-1", true);

    [Fact]
    public void Abstains_without_allowlisted_series_model_or_fees()
    {
        var s = Snapshot();
        var strategy = Fx.Strategy();
        Assert.Contains("allowlist", strategy.Decide(new StrategyInput(s, "OTHER", Fc(0.9m), Fees, [])).Rationale);
        Assert.Contains("no validated forecast model", strategy.Decide(new StrategyInput(s, "KXSYNTHWX", null, Fees, [])).Rationale);
        Assert.Contains("fees unknown", strategy.Decide(new StrategyInput(s, "KXSYNTHWX", Fc(0.9m), null, [])).Rationale);
    }

    [Fact]
    public void Uses_conservative_probability_after_fees_and_is_deterministic()
    {
        var s = Snapshot();
        var strategy = Fx.Strategy(minEdge: 0.05m);
        // YES: 0.60-0.05 - 0.45 - 0.02 = 0.08
        var yes = strategy.Decide(new StrategyInput(s, "KXSYNTHWX", Fc(0.60m), Fees, []));
        Assert.Equal(new OrderIntent(Fx.T50, Side.Yes, OrderAction.Buy, 1, 0.45m), Assert.Single(yes.Candidates).Intent);
        var again = strategy.Decide(new StrategyInput(s, "KXSYNTHWX", Fc(0.60m), Fees, []));
        Assert.Equal(yes.Rationale, again.Rationale);
        Assert.Equal(yes.Candidates, again.Candidates);
        // NO: (1-0.25)-0.05 - 0.60 - 0.02 = 0.08
        Assert.Equal(Side.No, Assert.Single(strategy.Decide(new StrategyInput(s, "KXSYNTHWX", Fc(0.25m), Fees, [])).Candidates).Intent.Side);
        // A wide uncertainty buffer removes the edge.
        Assert.Empty(strategy.Decide(new StrategyInput(s, "KXSYNTHWX", Fc(0.60m, 0.10m), Fees, [])).Candidates);
    }

    [Fact]
    public void Signal_key_is_stable_for_the_same_evidence_and_changes_with_new_evidence()
    {
        var s = Snapshot();
        var strategy = Fx.Strategy();
        var k1 = strategy.Decide(new StrategyInput(s, "KXSYNTHWX", Fc(0.6m), Fees, [])).Candidates[0].SignalKey;
        var k2 = strategy.Decide(new StrategyInput(s with { Id = 99, ObservedAt = Clock.Now.AddHours(1) }, "KXSYNTHWX", Fc(0.6m), Fees, [])).Candidates[0].SignalKey;
        var k3 = strategy.Decide(new StrategyInput(s, "KXSYNTHWX", Fc(0.6m) with { EvidenceRevision = "rev-2" }, Fees, [])).Candidates[0].SignalKey;
        Assert.Equal(k1, k2);
        Assert.NotEqual(k1, k3);
        Assert.Equal(Fx.Strategy(minEdge: 0.050m).ConfigHash, Fx.Strategy(minEdge: 0.05m).ConfigHash);
    }

    [Fact]
    public void Fixture_model_refuses_live_data()
    {
        var s = Snapshot();
        Assert.NotNull(Fx.Model().Forecast(s));
        Assert.Null(Fx.Model().Forecast(s with { DataOrigin = DataOrigin.LiveProduction }));
    }
}
