using KalshiBot.Core.Common;
using KalshiBot.Core.Kalshi;
using KalshiBot.Core.Recording;
using KalshiBot.Core.Trading;
using Xunit;

namespace KalshiBot.Tests;

public class FeeAndFillTests
{
    private static readonly FeeContext Fees = new("test", 0.07m, null, "quadratic");

    [Theory]
    [InlineData(1, "0.45", "0.02")]   // 0.07*0.2475 = 0.017325 -> up to 0.02
    [InlineData(10, "0.50", "0.18")]  // 0.175 -> 0.18
    [InlineData(100, "0.50", "1.75")] // exact cents stay exact
    [InlineData(1, "0.01", "0.01")]   // tiny fee still rounds up to a cent
    public void Taker_fee_rounds_up_to_the_cent(int contracts, string price, string expected) =>
        Assert.Equal(decimal.Parse(expected), FeeModel.TakerFee(contracts, decimal.Parse(price), Fees));

    [Fact]
    public void Fee_multiplier_can_only_raise_the_fee() =>
        Assert.Equal(FeeModel.TakerFee(10, 0.5m, Fees), FeeModel.TakerFee(10, 0.5m, Fees with { Multiplier = 0.5m }));

    [Fact] // DATA-01
    public void Live_data_has_no_fee_schedule_until_one_is_verified()
    {
        Assert.NotNull(FeeSchedules.Resolve(DataOrigin.Synthetic, "quadratic", 1m, DateTimeOffset.UtcNow, out _));
        Assert.Null(FeeSchedules.Resolve(DataOrigin.LiveProduction, "quadratic", 1m, DateTimeOffset.UtcNow, out var why));
        Assert.Contains("no verified fee schedule", why);
        Assert.Null(FeeSchedules.Resolve(DataOrigin.Synthetic, "flat", 1m, DateTimeOffset.UtcNow, out _));
        Assert.Null(FeeSchedules.Resolve(DataOrigin.Synthetic, null, 1m, DateTimeOffset.UtcNow, out _));
    }

    private static readonly OrderBook Book = new(
        [new PriceLevel(0.40m, 25m), new PriceLevel(0.39m, 50m)],
        [new PriceLevel(0.55m, 3.5m), new PriceLevel(0.54m, 4m)]); // yes asks 0.45 x3.5, 0.46 x4

    [Fact]
    public void Buy_walks_derived_asks_up_to_the_limit_in_whole_contracts()
    {
        var fills = PaperBroker.Simulate(new OrderIntent("T", Side.Yes, OrderAction.Buy, 10, 0.46m), Book, Fees);
        Assert.Equal([new SimulatedFill(3, 0.45m, FeeModel.TakerFee(3, 0.45m, Fees)), new SimulatedFill(4, 0.46m, FeeModel.TakerFee(4, 0.46m, Fees))], fills);
    }

    [Fact] // Spec 8.4: recorded liquidity cannot fill an unlimited sequence of paper orders.
    public void Liquidity_already_consumed_on_the_snapshot_is_not_reused()
    {
        var consumed = new Dictionary<decimal, long> { [0.45m] = 3, [0.46m] = 1 };
        var fills = PaperBroker.Simulate(new OrderIntent("T", Side.Yes, OrderAction.Buy, 10, 0.46m), Book, Fees, consumed);
        Assert.Equal(new SimulatedFill(3, 0.46m, FeeModel.TakerFee(3, 0.46m, Fees)), Assert.Single(fills));
    }

    [Fact]
    public void Limit_below_best_ask_does_not_fill_and_nothing_rests()
    {
        Assert.Empty(PaperBroker.Simulate(new OrderIntent("T", Side.Yes, OrderAction.Buy, 1, 0.44m), Book, Fees));
        Assert.Empty(PaperBroker.Simulate(new OrderIntent("T", Side.Yes, OrderAction.Buy, 1, 0.98m), OrderBook.Empty, Fees));
    }

    [Fact]
    public void Sell_hits_bids_at_or_above_limit()
    {
        var fills = PaperBroker.Simulate(new OrderIntent("T", Side.Yes, OrderAction.Sell, 30, 0.39m), Book, Fees);
        Assert.Equal((25, 0.40m), (fills[0].Quantity, fills[0].Price));
        Assert.Equal((5, 0.39m), (fills[1].Quantity, fills[1].Price));
    }
}

public class LedgerTests
{
    private static (TempDb T, FixedClock Clock, PaperLedger Ledger, long Account) Setup()
    {
        var (t, clock) = Fx.Ready();
        var ledger = new PaperLedger(t.Db, clock);
        return (t, clock, ledger, ledger.FindAccount("paper")!.Value);
    }

    /// <summary>Books one simulated fill with its order, reservation and decision rows, as ResolvePending would.</summary>
    private static void Fill(TempDb t, PaperLedger ledger, long account, string side, string action, int qty, decimal price, decimal fee) =>
        t.Db.InTransaction(() =>
        {
            var snap = t.Db.Scalar<long>("SELECT MAX(id) FROM market_snapshots");
            var now = "2026-11-23T18:00:00.0000000Z";
            var key = Guid.NewGuid().ToString("N");
            var decision = t.Db.Insert("""
                INSERT INTO paper_decisions (account_id, decision_id, strategy, config_hash, snapshot_id, data_origin, outcome, rationale, created_at)
                VALUES ($a, $k, 'test', 'h', $s, 'fixture:synthetic', 'candidates', 'test', $t)
                """, ("$a", account), ("$k", key), ("$s", snap), ("$t", now));
            var order = t.Db.Insert("""
                INSERT INTO paper_orders (account_id, decision_row_id, client_order_id, signal_key, strategy, ticker, event_ticker, side, action,
                    quantity, limit_price, time_in_force, decision_snapshot_id, data_origin, created_at, arrival_at, status, filled_quantity, fill_snapshot_id)
                VALUES ($a, $d, $k, $k, 'test', $t, 'KXSYNTHWX-99DEC31', $side, $act, $q, $p, 'ioc', $s, 'fixture:synthetic', $now, $now, 'filled', $q, $s)
                """, ("$a", account), ("$d", decision), ("$k", key), ("$t", Fx.T50), ("$side", side), ("$act", action), ("$q", qty),
                ("$p", Dec.ToText(price)), ("$s", snap), ("$now", now));
            var cost = qty * price + fee;
            t.Db.Exec("INSERT INTO risk_reservations (account_id, order_id, amount, status, consumed, created_at) VALUES ($a, $o, $c, 'resolved', $c, $t)",
                ("$a", account), ("$o", order), ("$c", Dec.ToText(cost)), ("$t", now));
            var fill = t.Db.Insert("""
                INSERT INTO paper_fills (order_id, snapshot_id, ticker, side, action, quantity, price, fee, filled_at, simulated)
                VALUES ($o, $s, $t, $side, $act, $q, $p, $f, $now, 1)
                """, ("$o", order), ("$s", snap), ("$t", Fx.T50), ("$side", side), ("$act", action), ("$q", qty), ("$p", Dec.ToText(price)),
                ("$f", Dec.ToText(fee)), ("$now", now));
            ledger.RecordFill(account, fill, Fx.T50, side, action, qty, price, fee);
        });

    [Fact]
    public void Buy_then_partial_sell_tracks_cash_cost_basis_and_realized_pnl()
    {
        var (t, _, ledger, acct) = Setup();
        using var __ = t;
        Fill(t, ledger, acct, Side.Yes, OrderAction.Buy, 3, 0.45m, 0.05m);
        var s1 = ledger.State(acct, false);
        Assert.Equal(500m - 1.35m - 0.05m, s1.Cash);
        Assert.Equal(new PositionView(Fx.T50, Side.Yes, 3, 1.35m), Assert.Single(s1.Positions));
        Assert.Equal(-0.05m, s1.RealizedPnl);

        Fill(t, ledger, acct, Side.Yes, OrderAction.Sell, 1, 0.50m, 0.02m);
        var s2 = ledger.State(acct, false);
        Assert.Equal(498.60m + 0.50m - 0.02m, s2.Cash);
        Assert.Equal(new PositionView(Fx.T50, Side.Yes, 2, 0.90m), Assert.Single(s2.Positions));
        Assert.Equal(-0.05m + (0.50m - 0.45m) - 0.02m, s2.RealizedPnl);
        Assert.Empty(ledger.Verify(acct));
    }

    [Fact]
    public void Uneven_average_cost_closes_to_exactly_zero()
    {
        var (t, _, ledger, acct) = Setup();
        using var __ = t;
        Fill(t, ledger, acct, Side.Yes, OrderAction.Buy, 1, 0.33m, 0.01m);
        Fill(t, ledger, acct, Side.Yes, OrderAction.Buy, 2, 0.34m, 0.01m);
        Fill(t, ledger, acct, Side.Yes, OrderAction.Sell, 1, 0.40m, 0.01m);
        Fill(t, ledger, acct, Side.Yes, OrderAction.Sell, 2, 0.40m, 0.01m);
        var s = ledger.State(acct, false);
        Assert.Empty(s.Positions);
        Assert.Equal(1.20m - 1.01m - 0.04m, s.RealizedPnl);
        Assert.Empty(ledger.Verify(acct));
    }

    [Fact]
    public void Ledger_refuses_to_sell_more_than_held()
    {
        var (t, _, ledger, acct) = Setup();
        using var __ = t;
        Fill(t, ledger, acct, Side.Yes, OrderAction.Buy, 1, 0.45m, 0.02m);
        Assert.Throws<KalshiBotException>(() => Fill(t, ledger, acct, Side.Yes, OrderAction.Sell, 2, 0.45m, 0.01m));
        Assert.Equal(1, ledger.State(acct, false).Positions.Single().Quantity); // rolled back
    }

    [Fact] // AC-13
    public void Settlement_pays_winners_zeroes_losers_and_is_idempotent()
    {
        var (t, clock, ledger, acct) = Setup();
        using var __ = t;
        Fill(t, ledger, acct, Side.Yes, OrderAction.Buy, 2, 0.45m, 0.04m);
        Fill(t, ledger, acct, Side.No, OrderAction.Buy, 1, 0.60m, 0.02m);
        var before = ledger.State(acct, false).Cash;
        Assert.Equal(0, ledger.Settle(acct, new MarketStore(t.Db).Latest(Fx.T50)!)); // active: no settlement

        Fx.Record(t.Db, clock, Fx.Settled, Fx.T50);
        var final = new MarketStore(t.Db).Latest(Fx.T50)!;
        Assert.Equal(("finalized", "yes"), (final.Status, final.Result));
        Assert.Equal(2, ledger.Settle(acct, final));
        Assert.Equal(0, ledger.Settle(acct, final));

        var s = ledger.State(acct, false);
        Assert.Equal(before + 2m, s.Cash);
        Assert.Empty(s.Positions);
        Assert.Equal(2m - 0.90m - 0.60m - 0.06m, s.RealizedPnl);
        Assert.Empty(ledger.Verify(acct));
    }

    [Fact]
    public void Accounts_are_bound_to_one_data_origin()
    {
        var (t, _, ledger, acct) = Setup();
        using var __ = t;
        Assert.Equal(acct, ledger.EnsureAccount("paper", 500m, DataOrigin.Synthetic));
        Assert.Throws<KalshiBotException>(() => ledger.EnsureAccount("paper", 100m, DataOrigin.Synthetic));
        Assert.Throws<KalshiBotException>(() => ledger.EnsureAccount("paper", 500m, DataOrigin.LiveProduction));
        Assert.Throws<KalshiBotException>(() => ledger.EnsureAccount("other", 0m, DataOrigin.Synthetic));
    }

    [Fact]
    public void Settlement_refuses_a_snapshot_from_another_data_origin()
    {
        var (t, clock, ledger, _) = Setup();
        using var __ = t;
        var live = ledger.EnsureAccount("live-paper", 500m, DataOrigin.LiveProduction);
        Fx.Record(t.Db, clock, Fx.Settled, Fx.T50);
        var e = Assert.Throws<KalshiBotException>(() => ledger.Settle(live, new MarketStore(t.Db).Latest(Fx.T50)!));
        Assert.Contains("Refusing to settle", e.Message);
    }

    [Theory] // Verify rebuilds the ledger from independent records.
    [InlineData("INSERT INTO ledger_entries (account_id, ts, kind, ticker, side, quantity_delta, cash_delta, cost_delta, ref_kind, ref_id) VALUES (1, '2026-11-23T18:00:00.0000000Z', 'trade', 'KXSYNTHWX-99DEC31-T50', 'yes', 9, '0', '0', 'fill', 999)", "does not reference a fill")]
    [InlineData("INSERT INTO ledger_entries (account_id, ts, kind, ticker, side, quantity_delta, cash_delta, cost_delta, ref_kind, ref_id) VALUES (1, '2026-11-23T18:00:00.0000000Z', 'settlement', 'KXSYNTHWX-99DEC31-T50', 'yes', 0, '50', '0', 'snapshot', 1)", "settlement entry without a matching settlement record")]
    [InlineData("INSERT INTO ledger_entries (account_id, ts, kind, ticker, side, quantity_delta, cash_delta, cost_delta, ref_kind, ref_id) VALUES (1, '2026-11-23T18:00:00.0000000Z', 'trade', 'KXSYNTHWX-99DEC31-T50', 'yes', 5, '-0.45', '0.45', 'fill', 1)", "expected exactly one trade entry")]
    [InlineData("INSERT INTO ledger_entries (account_id, ts, kind, cash_delta, ref_kind, ref_id) VALUES (1, '2026-11-23T18:00:00.0000000Z', 'deposit', '50', 'account', 1)", "exactly one deposit")]
    [InlineData("UPDATE paper_orders SET filled_quantity = 3", "filled_quantity 3 but fills sum to 1")]
    [InlineData("UPDATE risk_reservations SET consumed = '0.10'", "reservation consumed")]
    public void Verify_detects_entries_that_do_not_match_independent_records(string tamper, string expected)
    {
        var (t, clock) = Fx.Ready();
        using var _ = t;
        Fx.DecideAndFill(t, clock);
        var ledger = new PaperLedger(t.Db, clock);
        var acct = ledger.FindAccount("paper")!.Value;
        Assert.Empty(ledger.Verify(acct));
        t.Db.Exec(tamper);
        Assert.Contains(ledger.Verify(acct), p => p.Contains(expected));
    }
}
