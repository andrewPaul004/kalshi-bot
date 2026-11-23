using KalshiBot.Core.Common;
using KalshiBot.Core.Recording;
using KalshiBot.Core.Status;
using KalshiBot.Core.Storage;
using KalshiBot.Core.Trading;
using Microsoft.Data.Sqlite;
using Xunit;

namespace KalshiBot.Tests;

public class PaperWorkflowTests
{
    [Fact] // Phase 1: candidate -> decision -> reservation -> post-arrival fill -> settlement
    public void Decision_reserves_worst_case_cost_and_fills_only_on_a_later_observation()
    {
        var (t, clock) = Fx.Ready();
        using var _ = t;
        var engine = Fx.Engine(t.Db, clock);
        var r = engine.Run("paper", Fx.T50, Fx.Strategy(), Fx.Model());
        var o = Assert.Single(r.Orders);
        Assert.Equal("pending", o.Status);
        Assert.Equal(0.47m, o.Reserved);
        Assert.Equal(0, t.Db.Scalar<long>("SELECT COUNT(*) FROM paper_fills"));
        var state = new PaperLedger(t.Db, clock).State(1, false);
        Assert.Equal(0.47m, state.HeldReservations);
        Assert.Equal(500m - 0.47m, state.AvailableCash);

        // No observation at or after arrival yet: stays pending.
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Empty(engine.ResolvePending("paper"));

        Fx.Record(t.Db, clock, Fx.Open, Fx.T50);
        var resolved = Assert.Single(engine.ResolvePending("paper"));
        Assert.Equal("filled", resolved.Status);
        Assert.NotEqual(r.SnapshotId, resolved.FillSnapshotId); // never the decision snapshot
        var after = new PaperLedger(t.Db, clock).State(1, false);
        Assert.Equal(0m, after.HeldReservations);
        Assert.Equal(500m - 0.45m - 0.02m, after.Cash);
        Assert.Equal("resolved", t.Db.Scalar<string>("SELECT status FROM risk_reservations"));
        Assert.Equal(0.47m, Dec.Parse(t.Db.Scalar<string>("SELECT consumed FROM risk_reservations")!));
        Assert.Empty(new PaperLedger(t.Db, clock).Verify(1));
    }

    [Fact] // AC-22
    public void A_quote_that_disappears_before_arrival_does_not_fill()
    {
        var (t, clock) = Fx.Ready();
        using var _ = t;
        Fx.DecideAndFill(t, clock, nextObservation: Fx.Moved); // best YES ask moved from 0.45 to 0.50
        Assert.Equal("unfilled", t.Db.Scalar<string>("SELECT status FROM paper_orders"));
        Assert.Equal(0, t.Db.Scalar<long>("SELECT COUNT(*) FROM paper_fills"));
        Assert.Equal("0", t.Db.Scalar<string>("SELECT consumed FROM risk_reservations"));
        Assert.Equal(500m, new PaperLedger(t.Db, clock).State(1, false).Cash);
    }

    [Fact] // Spec 8.4: missing post-arrival evidence -> conservative no fill
    public void Missing_post_arrival_evidence_expires_without_a_fill()
    {
        var (t, clock) = Fx.Ready();
        using var _ = t;
        var engine = Fx.Engine(t.Db, clock);
        engine.Run("paper", Fx.T50, Fx.Strategy(), Fx.Model());
        clock.Advance(TimeSpan.FromMinutes(10));
        Fx.Record(t.Db, clock, Fx.Open, Fx.T50); // first observation is 10 minutes after arrival
        var resolved = Assert.Single(engine.ResolvePending("paper"));
        Assert.Equal("expired_no_evidence", resolved.Status);
        Assert.Equal(0, t.Db.Scalar<long>("SELECT COUNT(*) FROM paper_fills"));
    }

    [Fact] // AC-20
    public void The_same_signal_is_not_entered_again_across_scans_and_restarts()
    {
        var (t, clock) = Fx.Ready();
        using var _ = t;
        Fx.DecideAndFill(t, clock);
        var db = t.Reopen(); // restart
        clock.Advance(TimeSpan.FromSeconds(5));
        var rescan = Fx.Engine(db, clock).Run("paper", Fx.T50, Fx.Strategy(), Fx.Model());
        Assert.False(rescan.AlreadyProcessed); // new snapshot -> new decision...
        var o = Assert.Single(rescan.Orders);
        Assert.Equal("rejected", o.Status);   // ...but the same economic signal
        Assert.Contains("signal already entered; no re-entry rule is approved", o.Verdict.Reasons);
        Assert.Throws<SqliteException>(() => db.Exec("UPDATE paper_orders SET status = 'filled' WHERE status = 'rejected'")); // unique index backstop
    }

    [Fact] // AC-06
    public async Task Concurrent_signals_cannot_both_spend_the_same_capacity()
    {
        var (t, clock) = Fx.Ready();
        using var _ = t;
        // Portfolio cap fits one 0.47 reservation but not two.
        var limits = RiskLimits.PaperFixture with { MaxPortfolioCapitalAtRisk = 0.60m };
        using var db1 = Db.Open(t.Path);
        using var db2 = Db.Open(t.Path);
        var strategyA = Fx.Strategy(minEdge: 0.05m);
        var strategyB = Fx.Strategy(minEdge: 0.04m); // a different signal for the same market
        var tasks = new[]
        {
            Task.Run(() => Fx.Engine(db1, clock, limits).Run("paper", Fx.T50, strategyA, Fx.Model())),
            Task.Run(() => Fx.Engine(db2, clock, limits).Run("paper", Fx.T50, strategyB, Fx.Model())),
        };
        var results = await Task.WhenAll(tasks);
        var orders = results.SelectMany(r => r.Orders).ToList();
        Assert.Equal(2, orders.Count);
        Assert.Single(orders, o => o.Status == "pending");
        Assert.Contains(orders.Single(o => o.Status == "rejected").Verdict.Reasons, r => r.StartsWith("portfolio capital at risk would be"));
        Assert.Equal(1, t.Db.Scalar<long>("SELECT COUNT(*) FROM risk_reservations WHERE status = 'held'"));
    }

    [Fact] // Spec 6.3
    public void Daily_loss_trigger_persists_a_halt_that_survives_restart_and_midnight()
    {
        var (t, clock) = Fx.Ready();
        using var _ = t;
        // A 1.00 trigger so one losing fixture trade crosses it.
        var limits = RiskLimits.PaperFixture with { DailyLossTrigger = 0.40m };
        var engine = Fx.Engine(t.Db, clock, limits);
        engine.Run("paper", Fx.T60, Fx.Strategy(), Fx.Model()); // abstains, but records today's opening equity 500
        Fx.DecideAndFill(t, clock);                              // buy 1 YES T50 @0.45 (+0.02 fee)
        clock.Advance(TimeSpan.FromSeconds(10));
        Fx.Record(t.Db, clock, Fx.Moved, Fx.T50);                // bid unchanged at 0.40: equity 500 - 0.47 + 0.40 = 499.93
        Assert.Null(Fx.Engine(t.Db, clock, limits with { DailyLossTrigger = 0.40m }).Run("paper", Fx.T60, Fx.Strategy(), Fx.Model()).HaltRaised);

        var strict = limits with { DailyLossTrigger = 0.05m };
        clock.Advance(TimeSpan.FromSeconds(1));
        Fx.Record(t.Db, clock, Fx.Open, Fx.T60);
        var r = Fx.Engine(t.Db, clock, strict).Run("paper", Fx.T60, Fx.Strategy(), Fx.Model());
        Assert.NotNull(r.HaltRaised);
        Assert.True(new Controls(t.Db, clock).Halted);

        // Restart and cross midnight (America/Chicago): the halt stays until an operator resets it.
        var db = t.Reopen();
        clock.Advance(TimeSpan.FromHours(12));
        Fx.Record(db, clock, Fx.Open);
        var next = Fx.Engine(db, clock).Run("paper", Fx.T50, Fx.Strategy(minEdge: 0.03m), Fx.Model());
        Assert.Contains(next.Orders.Single().Verdict.Reasons, x => x.StartsWith("safety halt engaged"));
    }

    [Fact]
    public void Stale_marks_make_valuation_uncertain_and_block_new_risk()
    {
        var (t, clock) = Fx.Ready();
        using var _ = t;
        Fx.DecideAndFill(t, clock);
        clock.Advance(TimeSpan.FromMinutes(30));
        Fx.Record(t.Db, clock, Fx.Open, Fx.T60); // fresh T60, but the T50 position's mark is now stale
        // A fixture model entry that gives T60 a NO candidate, so the risk check actually runs.
        var model = new SyntheticFixtureModel(new Dictionary<string, (decimal, decimal)> { [Fx.T60] = (0.05m, 0m) });
        var r = Fx.Engine(t.Db, clock).Run("paper", Fx.T60, Fx.Strategy(), model);
        Assert.False(Fx.Engine(t.Db, clock).Value(new PaperLedger(t.Db, clock).State(1, false), clock.Now).Certain);
        var o = Assert.Single(r.Orders);
        Assert.Equal("rejected", o.Status);
        Assert.Contains(o.Verdict.Reasons, x => x.StartsWith("valuation uncertain: mark for KXSYNTHWX-99DEC31-T50 is stale"));
    }

    [Fact] // AC-21
    public void A_halt_set_after_one_decision_blocks_the_next_dispatch_but_not_resolution()
    {
        var (t, clock) = Fx.Ready();
        using var _ = t;
        Fx.Engine(t.Db, clock).Run("paper", Fx.T50, Fx.Strategy(), Fx.Model());
        new Controls(t.Db, clock).SetHalt(true, "operator pause");
        clock.Advance(TimeSpan.FromSeconds(10));
        Fx.Record(t.Db, clock, Fx.Open);
        // The already-dispatched order still resolves (it cannot be recalled); a new decision is blocked.
        var r = Fx.Engine(t.Db, clock).Run("paper", Fx.T50, Fx.Strategy(minEdge: 0.03m), Fx.Model());
        Assert.Equal("filled", Assert.Single(r.Resolved).Status);
        Assert.Contains(Assert.Single(r.Orders).Verdict.Reasons, x => x.StartsWith("safety halt engaged"));
        Assert.Equal(0, t.Db.Scalar<long>("SELECT COUNT(*) FROM risk_reservations WHERE status = 'held'"));
    }
}

public class RecoveryTests
{
    [Theory] // AC-09
    [InlineData("after-order-insert")]
    [InlineData("after-reservation")]
    public void Crash_while_reserving_rolls_back_the_decision_and_rerun_reserves_exactly_once(string faultPoint)
    {
        var (t, clock) = Fx.Ready();
        using var _ = t;
        var engine = Fx.Engine(t.Db, clock);
        engine.FaultInjector = point => { if (point == faultPoint) throw new IOException("simulated crash"); };
        Assert.Throws<IOException>(() => engine.Run("paper", Fx.T50, Fx.Strategy(), Fx.Model()));
        Assert.Equal(0, t.Db.Scalar<long>("SELECT COUNT(*) FROM paper_decisions"));
        Assert.Equal(0, t.Db.Scalar<long>("SELECT COUNT(*) FROM paper_orders"));
        Assert.Equal(0, t.Db.Scalar<long>("SELECT COUNT(*) FROM risk_reservations"));

        var db = t.Reopen();
        var rerun = Fx.Engine(db, clock).Run("paper", Fx.T50, Fx.Strategy(), Fx.Model());
        Assert.Equal("pending", Assert.Single(rerun.Orders).Status);
        Assert.True(Fx.Engine(db, clock).Run("paper", Fx.T50, Fx.Strategy(), Fx.Model()).AlreadyProcessed);
        Assert.Equal(1, db.Scalar<long>("SELECT COUNT(*) FROM risk_reservations"));
    }

    [Fact] // AC-09
    public void Crash_while_filling_keeps_the_reservation_and_resolves_once_after_restart()
    {
        var (t, clock) = Fx.Ready();
        using var _ = t;
        Fx.Engine(t.Db, clock).Run("paper", Fx.T50, Fx.Strategy(), Fx.Model());
        clock.Advance(TimeSpan.FromSeconds(10));
        Fx.Record(t.Db, clock, Fx.Open, Fx.T50);
        var engine = Fx.Engine(t.Db, clock);
        engine.FaultInjector = point => { if (point == "after-fill-insert") throw new IOException("simulated crash"); };
        Assert.Throws<IOException>(() => engine.ResolvePending("paper"));
        Assert.Equal(0, t.Db.Scalar<long>("SELECT COUNT(*) FROM paper_fills"));
        Assert.Equal("held", t.Db.Scalar<string>("SELECT status FROM risk_reservations")); // unknown outcome keeps capacity reserved

        var db = t.Reopen();
        Assert.Equal("filled", Assert.Single(Fx.Engine(db, clock).ResolvePending("paper")).Status);
        Assert.Empty(Fx.Engine(db, clock).ResolvePending("paper"));
        Assert.Equal(1, db.Scalar<long>("SELECT COUNT(*) FROM paper_fills"));
        Assert.Empty(new PaperLedger(db, clock).Verify(1));
    }

    [Theory]
    [InlineData("UPDATE ledger_entries SET cash_delta = '1000000'")]
    [InlineData("DELETE FROM ledger_entries")]
    [InlineData("UPDATE paper_fills SET price = '0.01'")]
    [InlineData("DELETE FROM paper_fills")]
    [InlineData("UPDATE raw_responses SET body = '{}'")]
    [InlineData("DELETE FROM raw_responses")]
    [InlineData("DELETE FROM billing_attestations")]
    [InlineData("INSERT OR REPLACE INTO ledger_entries (id, account_id, ts, kind, cash_delta) SELECT id, account_id, ts, kind, '1000000' FROM ledger_entries WHERE kind = 'deposit'")]
    [InlineData("REPLACE INTO paper_fills SELECT id, order_id, snapshot_id, ticker, side, action, quantity, '0.01', fee, filled_at, simulated FROM paper_fills")]
    public void History_tables_are_append_only(string sql)
    {
        var (t, clock) = Fx.Ready();
        using var _ = t;
        Fx.DecideAndFill(t, clock);
        new KalshiBot.Core.Research.ResearchStore(t.Db).Attest(clock.Now);
        var e = Assert.Throws<SqliteException>(() => t.Db.Exec(sql));
        Assert.Contains("append-only", e.Message);
    }

    [Fact]
    public void Migrations_are_idempotent_and_retire_pre_spec_tables_without_deleting_them()
    {
        using var t = new TempDb();
        t.Reopen();
        t.Reopen();
        Assert.Equal(Schema.LatestVersion, t.Db.Scalar<long>("SELECT MAX(version) FROM schema_migrations"));
        Assert.Equal(Schema.LatestVersion, t.Db.Scalar<long>("SELECT COUNT(*) FROM schema_migrations"));
        Assert.Equal(1, t.Db.Scalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE name = 'legacy_paper_orders_v1'"));
        Assert.Equal("wal", t.Db.Scalar<string>("PRAGMA journal_mode"));
        Assert.Equal(1, t.Db.Scalar<long>("PRAGMA foreign_keys"));
        Assert.Empty(t.Db.Query("PRAGMA foreign_key_check", r => r.GetString(0)));
    }

    [Fact] // AC-14
    public void Frozen_fixture_replay_is_exactly_reproducible()
    {
        string Run()
        {
            using var t = new TempDb();
            FixtureReplay.Run(t.Db, new ManualClock(FixtureReplay.Start), Fx.Root, _ => { });
            Assert.Empty(new PaperLedger(t.Db, TimeProvider.System).Verify(1));
            Assert.Equal(1, t.Db.Scalar<long>("SELECT COUNT(*) FROM paper_fills"));
            return FixtureReplay.Fingerprint(t.Db);
        }
        Assert.Equal(Run(), Run());
    }

    [Fact] // AC-19 (paper executor) and spec 4.3 (one research process)
    public void Process_lock_admits_one_holder()
    {
        var dir = Directory.CreateTempSubdirectory("kb-lock-").FullName;
        var path = Path.Combine(dir, "x.lock");
        using (var first = ProcessLock.TryAcquire(path))
        {
            Assert.NotNull(first);
            Assert.Null(ProcessLock.TryAcquire(path));
            Assert.Throws<KalshiBotException>(() => ProcessLock.Acquire(path, "test"));
        }
        using var again = ProcessLock.TryAcquire(path);
        Assert.NotNull(again);
    }

    [Fact] // Spec 6.3: America/Chicago reporting day with DST
    public void Reporting_day_follows_america_chicago_including_dst()
    {
        // 2026-03-08 is the DST start in the US: midnight is UTC-6, the next midnight UTC-5.
        Assert.Equal(new DateTimeOffset(2026, 3, 8, 6, 0, 0, TimeSpan.Zero), ReportingDay.StartUtc(new DateOnly(2026, 3, 8)));
        Assert.Equal(new DateTimeOffset(2026, 3, 9, 5, 0, 0, TimeSpan.Zero), ReportingDay.StartUtc(new DateOnly(2026, 3, 9)));
        // 04:30 UTC on Nov 24 is still Nov 23 in Chicago.
        Assert.Equal(new DateOnly(2026, 11, 23), ReportingDay.For(new DateTimeOffset(2026, 11, 24, 4, 30, 0, TimeSpan.Zero)));
    }

    [Theory]
    [InlineData("100.5300", "100.53")]
    [InlineData("0", "0.00")]
    [InlineData("0.123456", "0.123456")]
    public void Display_formatting_never_changes_stored_precision(string stored, string shown)
    {
        var d = Dec.Parse(stored);
        Assert.Equal(shown, Dec.Show(d));
        Assert.Equal(stored, Dec.ToText(d));
    }

    [Fact]
    public void Timestamps_round_trip_exactly_and_sort_lexically()
    {
        var a = new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.FromHours(-5)).AddTicks(1234567);
        Assert.Equal(a, Ts.Parse(Ts.ToText(a)));
        Assert.True(string.CompareOrdinal(Ts.ToText(a), Ts.ToText(a.AddTicks(1))) < 0);
    }
}
