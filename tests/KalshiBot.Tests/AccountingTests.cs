using KalshiBot.Core.Kalshi;
using KalshiBot.Core.Recording;
using KalshiBot.Core.Research;
using KalshiBot.Core.Trading;
using Xunit;

namespace KalshiBot.Tests;

public class FeeAndFillTests
{
    [Theory]
    [InlineData(1, "0.45", "0.02")]   // 0.07*0.2475 = 0.017325 -> up to 0.02
    [InlineData(10, "0.50", "0.18")]  // 0.07*10*0.25 = 0.175 -> 0.18
    [InlineData(100, "0.50", "1.75")] // exact cents stay exact
    [InlineData(1, "0.01", "0.01")]   // tiny fee still rounds up to a cent
    public void Taker_fee_rounds_up_to_the_cent(int contracts, string price, string expected) =>
        Assert.Equal(decimal.Parse(expected), FeeModel.TakerFee(contracts, decimal.Parse(price)));

    [Fact]
    public void Fee_multiplier_can_only_raise_the_paper_fee()
    {
        Assert.Equal(FeeModel.TakerFee(10, 0.5m), FeeModel.TakerFee(10, 0.5m, 0.5m));
        Assert.True(FeeModel.TakerFee(10, 0.5m, 2m) > FeeModel.TakerFee(10, 0.5m));
    }

    private static readonly OrderBook Book = new(
        [new PriceLevel(0.40m, 25m), new PriceLevel(0.39m, 50m)],
        [new PriceLevel(0.55m, 3.5m), new PriceLevel(0.54m, 4m)]); // yes asks 0.45 x3.5, 0.46 x4

    [Fact]
    public void Buy_walks_derived_asks_up_to_the_limit_in_whole_contracts()
    {
        var fills = PaperBroker.Simulate(new OrderIntent("T", Side.Yes, OrderAction.Buy, 10, 0.46m), Book);
        Assert.Equal([new SimulatedFill(3, 0.45m, FeeModel.TakerFee(3, 0.45m)), new SimulatedFill(4, 0.46m, FeeModel.TakerFee(4, 0.46m))], fills);
    }

    [Fact]
    public void Limit_below_best_ask_does_not_fill_and_nothing_rests()
    {
        Assert.Empty(PaperBroker.Simulate(new OrderIntent("T", Side.Yes, OrderAction.Buy, 1, 0.44m), Book));
        Assert.Empty(PaperBroker.Simulate(new OrderIntent("T", Side.Yes, OrderAction.Buy, 1, 0.99m), OrderBook.Empty));
    }

    [Fact]
    public void Sell_hits_bids_at_or_above_limit()
    {
        var fills = PaperBroker.Simulate(new OrderIntent("T", Side.Yes, OrderAction.Sell, 30, 0.39m), Book);
        Assert.Equal(25, fills[0].Quantity);
        Assert.Equal(0.40m, fills[0].Price);
        Assert.Equal(5, fills[1].Quantity);
        Assert.Equal(0.39m, fills[1].Price);
    }
}

public class LedgerTests
{
    private static (TempDb T, FixedClock Clock, PaperLedger Ledger, long Account) Setup(decimal cash = 100m)
    {
        var t = new TempDb();
        var clock = FixedClock.Default();
        var ledger = new PaperLedger(t.Db, clock);
        return (t, clock, ledger, ledger.EnsureAccount("paper", cash));
    }

    private static long Fill(TempDb t, PaperLedger ledger, long account, string side, string action, int qty, decimal price, decimal fee)
    {
        // Minimal order + fill rows so the ledger cross-check has its independent source.
        return t.Db.InTransaction(() =>
        {
            Fx.Record(t.Db, FixedClock.Default(), Fx.Open, Fx.T50);
            var snap = t.Db.Scalar<long>("SELECT MAX(id) FROM market_snapshots");
            var orderId = t.Db.Insert("""
                INSERT INTO paper_orders (account_id, client_order_id, decision_id, strategy, ticker, side, action, quantity,
                    limit_price, snapshot_id, data_origin, created_at, status, filled_quantity)
                VALUES ($a, $c, 'd', 'test', $t, $s, $act, $q, $p, $snap, 'fixture:synthetic', 'x', 'filled', $q)
                """, ("$a", account), ("$c", Guid.NewGuid().ToString()), ("$t", Fx.T50), ("$s", side), ("$act", action),
                ("$q", qty), ("$p", price.ToString()), ("$snap", snap));
            var fillId = t.Db.Insert("""
                INSERT INTO paper_fills (order_id, ticker, side, action, quantity, price, fee, filled_at, simulated)
                VALUES ($o, $t, $s, $a, $q, $p, $f, 'x', 1)
                """, ("$o", orderId), ("$t", Fx.T50), ("$s", side), ("$a", action), ("$q", qty), ("$p", price.ToString()), ("$f", fee.ToString()));
            ledger.RecordFill(account, fillId, Fx.T50, side, action, qty, price, fee);
            return fillId;
        });
    }

    [Fact]
    public void Buy_then_partial_sell_tracks_cash_cost_basis_and_realized_pnl()
    {
        var (t, _, ledger, acct) = Setup();
        using var __ = t;
        Fill(t, ledger, acct, Side.Yes, OrderAction.Buy, 3, 0.45m, 0.05m);
        var s1 = ledger.State(acct, false);
        Assert.Equal(100m - 1.35m - 0.05m, s1.Cash);
        Assert.Equal(new PositionView(Fx.T50, Side.Yes, 3, 1.35m), Assert.Single(s1.Positions));
        Assert.Equal(-0.05m, s1.RealizedPnl);

        Fill(t, ledger, acct, Side.Yes, OrderAction.Sell, 1, 0.50m, 0.02m);
        var s2 = ledger.State(acct, false);
        Assert.Equal(98.60m + 0.50m - 0.02m, s2.Cash);
        Assert.Equal(new PositionView(Fx.T50, Side.Yes, 2, 0.90m), Assert.Single(s2.Positions));
        Assert.Equal(-0.05m + (0.50m - 0.45m) - 0.02m, s2.RealizedPnl);
        Assert.Empty(ledger.Verify(acct));
    }

    [Fact]
    public void Uneven_average_cost_closes_to_exactly_zero()
    {
        var (t, _, ledger, acct) = Setup();
        using var __ = t;
        Fill(t, ledger, acct, Side.Yes, OrderAction.Buy, 1, 0.33m, 0m);
        Fill(t, ledger, acct, Side.Yes, OrderAction.Buy, 2, 0.34m, 0m);
        Fill(t, ledger, acct, Side.Yes, OrderAction.Sell, 1, 0.40m, 0m);
        Fill(t, ledger, acct, Side.Yes, OrderAction.Sell, 2, 0.40m, 0m);
        var s = ledger.State(acct, false);
        Assert.Empty(s.Positions);
        Assert.Equal(1.20m - 1.01m, s.RealizedPnl);
        Assert.Empty(ledger.Verify(acct));
    }

    [Fact]
    public void Ledger_refuses_to_sell_more_than_held()
    {
        var (t, _, ledger, acct) = Setup();
        using var __ = t;
        Fill(t, ledger, acct, Side.Yes, OrderAction.Buy, 1, 0.45m, 0.02m);
        Assert.Throws<KalshiBot.Core.Common.KalshiBotException>(() => Fill(t, ledger, acct, Side.Yes, OrderAction.Sell, 2, 0.45m, 0m));
        Assert.Equal(1, ledger.State(acct, false).Positions.Single().Quantity); // the failed sell rolled back
    }

    [Fact]
    public void Settlement_pays_winners_zeroes_losers_and_is_idempotent()
    {
        var (t, clock, ledger, acct) = Setup();
        using var __ = t;
        Fill(t, ledger, acct, Side.Yes, OrderAction.Buy, 2, 0.45m, 0.04m);
        Fill(t, ledger, acct, Side.No, OrderAction.Buy, 1, 0.60m, 0.02m);
        var before = ledger.State(acct, false).Cash;

        // An active snapshot never settles.
        Assert.Equal(0, ledger.Settle(acct, new MarketStore(t.Db).Latest(Fx.T50)!));

        Fx.Record(t.Db, clock, Fx.Settled, Fx.T50);
        var final = new MarketStore(t.Db).Latest(Fx.T50)!;
        Assert.Equal(("finalized", "yes"), (final.Status, final.Result));
        Assert.Equal(2, ledger.Settle(acct, final));
        Assert.Equal(0, ledger.Settle(acct, final));

        var s = ledger.State(acct, false);
        Assert.Equal(before + 2m, s.Cash);
        Assert.Empty(s.Positions);
        // 2 YES won: +2.00 - 0.90 cost; 1 NO lost: -0.60; fees -0.06.
        Assert.Equal(2m - 0.90m - 0.60m - 0.06m, s.RealizedPnl);
        Assert.Empty(ledger.Verify(acct));
    }

    [Fact]
    public void Account_creation_is_idempotent_and_refuses_conflicting_cash()
    {
        var (t, _, ledger, acct) = Setup(100m);
        using var __ = t;
        Assert.Equal(acct, ledger.EnsureAccount("paper", 100m));
        Assert.Throws<KalshiBot.Core.Common.KalshiBotException>(() => ledger.EnsureAccount("paper", 500m));
        Assert.Throws<KalshiBot.Core.Common.KalshiBotException>(() => ledger.EnsureAccount("other", 0m));
        Assert.Single(ledger.Entries(acct));
    }
}
