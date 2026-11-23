using KalshiBot.Core.Recording;
using KalshiBot.Core.Trading;
using Xunit;

namespace KalshiBot.Tests;

public class RiskEngineTests
{
    private static readonly FixedClock Clock = FixedClock.Default();
    private static readonly RiskEngine Risk = new(RiskLimits.PaperDefaults);

    private static MarketSnapshot Snap(string status = "active", DateTimeOffset? observed = null, DateTimeOffset? close = null,
        string origin = "fixture:synthetic", string type = "binary") =>
        new(1, 1, origin, observed ?? Clock.Now, Fx.T50, "EV", type, "t", "y", status,
            close ?? Clock.Now.AddDays(30), null, 0.40m, 0.45m, 0.55m, 0.60m, 0.42m, 1m, 1m, "",
            """[{"start":"0.0100","end":"0.9900","step":"0.0100"}]""", null, 1, 1, null);

    private static AccountState Account(decimal cash = 100m, bool halted = false, long held = 0, decimal todaySpent = 0m, decimal openCost = 0m) =>
        new(1, "paper", 100m, cash,
            held == 0 && openCost == 0m ? [] : [new PositionView(Fx.T50, Side.Yes, held, openCost)],
            todaySpent, 0m, halted);

    private static ProbabilityEstimate Est(string rules = "R", string origin = "fixture:synthetic", DateTimeOffset? asOf = null) =>
        new(1, "pk", 1, rules, origin, 0.6m, "low", "synthetic-fixture", "unvalidated", Clock.Now, asOf ?? Clock.Now);

    private static RiskVerdict Eval(OrderIntent o, MarketSnapshot? s = null, AccountState? a = null,
        ProbabilityEstimate? e = null, ExecutionMode mode = ExecutionMode.Paper) =>
        Risk.Evaluate(o, new RiskContext(mode, s ?? Snap(), "R", e, a ?? Account(), Clock.Now));

    private static OrderIntent Buy(int qty = 1, decimal px = 0.45m, string side = Side.Yes) => new(Fx.T50, side, OrderAction.Buy, qty, px);

    [Fact]
    public void Clean_paper_order_is_approved() => Assert.True(Eval(Buy(), e: Est()).Approved);

    [Fact]
    public void Live_mode_is_always_refused_even_for_a_clean_order()
    {
        var v = Eval(Buy(), e: Est(), mode: ExecutionMode.Live);
        Assert.False(v.Approved);
        Assert.Contains("live execution is not authorized in this phase", v.Reasons);
        Assert.Contains("unvalidated research probability cannot drive non-paper execution", v.Reasons);
    }

    [Fact]
    public void Kill_switch_blocks_everything() =>
        Assert.Contains(Eval(Buy(), a: Account(halted: true)).Reasons, r => r.StartsWith("kill switch engaged"));

    [Fact]
    public void Estimate_provenance_is_checked()
    {
        Assert.Contains("market rules changed since the research packet was built", Eval(Buy(), e: Est(rules: "OTHER")).Reasons);
        Assert.Contains(Eval(Buy(), e: Est(origin: "live:production")).Reasons, r => r.StartsWith("research estimate is from live:production data"));
        // Live demo vs live production also differs (review finding 3).
        var prodSnap = Snap(origin: "live:production");
        Assert.Contains(Eval(Buy(), prodSnap, e: Est(origin: "live:demo")).Reasons, r => r.StartsWith("research estimate is from live:demo data"));
        Assert.True(Eval(Buy(), prodSnap, e: Est(origin: "live:production")).Approved);
        // Age is measured from the evidence date (as_of), even when the import is fresh (review finding 4).
        Assert.Contains(Eval(Buy(), e: Est(asOf: Clock.Now.AddDays(-90))).Reasons, r => r.StartsWith("research estimate evidence (as_of) older than"));
    }

    [Theory]
    [InlineData("closed")]
    [InlineData("determined")]
    [InlineData("finalized")]
    [InlineData("initialized")]
    public void Only_active_markets_trade(string status) =>
        Assert.Contains($"market status '{status}' is not 'active'", Eval(Buy(), Snap(status)).Reasons);

    [Fact]
    public void Stale_future_and_near_close_snapshots_are_refused()
    {
        Assert.Contains(Eval(Buy(), Snap(observed: Clock.Now.AddMinutes(-11))).Reasons, r => r.StartsWith("snapshot older than"));
        Assert.Contains("snapshot timestamp is in the future (clock problem)", Eval(Buy(), Snap(observed: Clock.Now.AddMinutes(5))).Reasons);
        Assert.Contains(Eval(Buy(), Snap(close: Clock.Now.AddMinutes(10))).Reasons, r => r.StartsWith("less than"));
        Assert.Contains(Eval(Buy(), Snap(type: "scalar")).Reasons, r => r.Contains("unsupported"));
    }

    [Fact]
    public void Order_shape_limits()
    {
        Assert.Contains("quantity must be positive", Eval(Buy(qty: 0)).Reasons);
        Assert.Contains(Eval(Buy(qty: 11)).Reasons, r => r.Contains("exceeds max 10 per order"));
        Assert.Contains(Eval(Buy(px: 0.99m)).Reasons, r => r.Contains("outside"));
        Assert.Contains(Eval(Buy(px: 0.455m)).Reasons, r => r.Contains("not a valid tick"));
        Assert.Contains("invalid side 'maybe'", Eval(Buy(side: "maybe")).Reasons);
    }

    [Fact]
    public void Exposure_and_cash_limits()
    {
        var tight = new RiskEngine(RiskLimits.PaperDefaults with { MaxOrderCost = 1m });
        Assert.Contains(tight.Evaluate(Buy(qty: 3), new RiskContext(ExecutionMode.Paper, Snap(), "R", null, Account(), Clock.Now)).Reasons,
            r => r.Contains("exceeds max order cost"));
        Assert.Contains(Eval(Buy(), a: Account(cash: 0.10m)).Reasons, r => r.StartsWith("insufficient paper cash"));
        Assert.Contains(Eval(Buy(qty: 2), a: Account(held: 19, openCost: 5m)).Reasons, r => r.StartsWith("position would exceed"));
        Assert.Contains(Eval(Buy(qty: 5), a: Account(held: 1, openCost: 48m)).Reasons, r => r.StartsWith("open cost basis would exceed"));
        Assert.Contains(Eval(Buy(), a: Account(todaySpent: 24.9m)).Reasons, r => r.StartsWith("daily premium would exceed"));
    }

    [Fact]
    public void Worst_case_cost_bounds_per_level_fee_rounding()
    {
        // Review finding 6: eight one-contract asks 0.173..0.180 at a 0.001 tick. Per-level rounding costs more than
        // one rounded fee on the total; the bound must still cover it.
        var levels = Enumerable.Range(0, 8).Select(i => new KalshiBot.Core.Kalshi.PriceLevel(1m - (0.173m + i * 0.001m), 1m)).ToList();
        var book = new KalshiBot.Core.Kalshi.OrderBook([], levels);
        var o = new OrderIntent(Fx.T50, Side.Yes, OrderAction.Buy, 8, 0.180m);
        var fills = PaperBroker.Simulate(o, book);
        var actual = fills.Sum(f => f.Quantity * f.Price + f.Fee);
        Assert.Equal(8, fills.Sum(f => f.Quantity));
        Assert.Equal(1.572m, actual);
        Assert.True(RiskEngine.WorstCaseBuyCost(o, null) >= actual);

        // With exactly the old single-fee estimate (1.53) in cash, the order is now refused instead of going negative.
        const string fine = """[{"start":"0.0000","end":"1.0000","step":"0.0010"}]""";
        var snap = Snap() with { PriceRangesJson = fine };
        var v = Risk.Evaluate(o, new RiskContext(ExecutionMode.Paper, snap, "R", null, Account(cash: 1.53m), Clock.Now));
        Assert.Contains(v.Reasons, r => r.StartsWith("insufficient paper cash"));
    }

    [Fact]
    public void Snapshot_liquidity_reuse_and_origin_mixing_are_refused()
    {
        var used = Risk.Evaluate(Buy(), new RiskContext(ExecutionMode.Paper, Snap(), "R", null, Account(), Clock.Now, SnapshotLiquidityUsed: true));
        Assert.Contains(used.Reasons, r => r.StartsWith("this snapshot's liquidity was already used"));
        var mixed = Risk.Evaluate(Buy(), new RiskContext(ExecutionMode.Paper, Snap(origin: "live:production"), "R", null, Account(), Clock.Now,
            PositionOrigins: ["fixture:synthetic"]));
        Assert.Contains(mixed.Reasons, r => r.StartsWith("existing position in this market came from fixture:synthetic"));
    }

    [Fact]
    public void Selling_more_than_held_is_refused()
    {
        var sell = new OrderIntent(Fx.T50, Side.Yes, OrderAction.Sell, 3, 0.40m);
        Assert.Contains(Eval(sell, a: Account(held: 2, openCost: 0.9m)).Reasons, r => r.StartsWith("cannot sell 3"));
        Assert.True(Eval(sell with { Quantity = 2 }, a: Account(held: 2, openCost: 0.9m)).Approved);
    }

    [Fact]
    public void Verdicts_are_deterministic_and_report_every_failure()
    {
        var o = new OrderIntent("OTHER", "maybe", "hold", 0, 1.5m);
        var a = Eval(o, Snap("closed"), Account(halted: true), Est(rules: "X"), ExecutionMode.Live);
        var b = Eval(o, Snap("closed"), Account(halted: true), Est(rules: "X"), ExecutionMode.Live);
        Assert.Equal(a.Reasons, b.Reasons);
        Assert.True(a.Reasons.Count >= 8);
    }

    [Fact]
    public void Tick_check_uses_price_ranges_or_whole_cents()
    {
        const string ranges = """[{"start":"0.0000","end":"0.1000","step":"0.0010"},{"start":"0.1000","end":"0.9000","step":"0.0100"}]""";
        Assert.True(RiskEngine.IsOnTick(0.053m, ranges));
        Assert.False(RiskEngine.IsOnTick(0.455m, ranges));
        Assert.True(RiskEngine.IsOnTick(0.45m, null));
        Assert.False(RiskEngine.IsOnTick(0.455m, null));
    }
}

public class StrategyTests
{
    private static readonly FixedClock Clock = FixedClock.Default();

    [Fact]
    public void Never_trades_without_an_estimate()
    {
        using var t = new TempDb();
        Fx.Record(t.Db, Clock, Fx.Open, Fx.T50);
        var s = new MarketStore(t.Db).Latest(Fx.T50)!;
        var d = new EdgeThresholdStrategy().Decide(new StrategyInput(s, "R", null, []));
        Assert.Empty(d.Orders);
        Assert.Contains("no probability estimate", d.Rationale);
    }

    [Fact]
    public void Buys_the_side_whose_edge_after_fees_clears_the_threshold_and_is_deterministic()
    {
        using var t = new TempDb();
        Fx.Record(t.Db, Clock, Fx.Open, Fx.T50);
        var s = new MarketStore(t.Db).Latest(Fx.T50)!;
        ProbabilityEstimate Est(decimal p) => new(1, "pk", s.Id, "R", s.DataOrigin, p, "low", "manual", "unvalidated", Clock.Now, Clock.Now);
        var strategy = new EdgeThresholdStrategy(minEdge: 0.05m);

        // p=0.60: yes ask 0.45 + fee 0.02 -> edge 0.13.
        var yes = strategy.Decide(new StrategyInput(s, "R", Est(0.60m), []));
        Assert.Equal(new OrderIntent(Fx.T50, Side.Yes, OrderAction.Buy, 1, 0.45m), Assert.Single(yes.Orders));
        var again = strategy.Decide(new StrategyInput(s, "R", Est(0.60m), []));
        Assert.Equal(yes.Rationale, again.Rationale);
        Assert.Equal(yes.Orders, again.Orders);

        // p=0.30: no ask 0.60 + fee 0.02 -> edge 0.70-0.62 = 0.08.
        Assert.Equal(Side.No, Assert.Single(strategy.Decide(new StrategyInput(s, "R", Est(0.30m), [])).Orders).Side);

        // p=0.48: neither side clears 0.05.
        Assert.Empty(strategy.Decide(new StrategyInput(s, "R", Est(0.48m), [])).Orders);

        // Position cap.
        Assert.Empty(strategy.Decide(new StrategyInput(s, "R", Est(0.60m), [new PositionView(Fx.T50, Side.Yes, 5, 2.25m)])).Orders);
    }
}
