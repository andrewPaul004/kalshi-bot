using System.Net;
using System.Text.Json.Nodes;
using KalshiBot.Core.Common;
using KalshiBot.Core.Kalshi;
using KalshiBot.Core.Recording;
using KalshiBot.Core.Research;
using KalshiBot.Core.Status;
using KalshiBot.Core.Storage;
using KalshiBot.Core.Trading;
using Microsoft.Data.Sqlite;
using Xunit;

namespace KalshiBot.Tests;

public class PaperWorkflowTests
{
    /// <summary>Records fixtures, exports a packet, imports a labeled synthetic estimate, opens a paper account.</summary>
    internal static (TempDb T, FixedClock Clock) Ready(decimal probability = 0.60m)
    {
        var t = new TempDb();
        var clock = FixedClock.Default();
        Fx.Record(t.Db, clock, Fx.Open);
        var packet = new PacketExporter(t.Db, clock).Export(Fx.T50);
        new ResultImporter(t.Db, clock).Import(Fx.ValidResult(packet, clock, probability).ToJsonString(), "synthetic-fixture");
        new PaperLedger(t.Db, clock).EnsureAccount("paper", 100m);
        return (t, clock);
    }

    internal static PaperEngine Engine(Db db, TimeProvider clock) => new(db, clock, new RiskEngine(RiskLimits.PaperDefaults));

    [Fact]
    public void Paper_cycle_fills_labels_everything_and_balances()
    {
        var (t, clock) = Ready();
        using var _ = t;
        var r = Engine(t.Db, clock).Run("paper", Fx.T50, new EdgeThresholdStrategy());

        var o = Assert.Single(r.Orders);
        Assert.Equal("filled", o.Status);
        Assert.Equal(DataOrigin.Synthetic, r.DataOrigin);
        Assert.Equal("EXPERIMENTAL (unvalidated)", r.Estimate!.Label);
        Assert.Equal(1, t.Db.Scalar<long>("SELECT COUNT(*) FROM paper_fills WHERE simulated = 1"));
        Assert.Equal("fixture:synthetic", t.Db.Scalar<string>("SELECT data_origin FROM paper_orders"));

        var ledger = new PaperLedger(t.Db, clock);
        var acct = ledger.FindAccount("paper")!.Value;
        Assert.Equal(100m - 0.45m - 0.02m, ledger.State(acct, false).Cash);
        Assert.Empty(ledger.Verify(acct));

        var report = StatusReport.Build(t.Db, clock);
        Assert.Contains("SYNTHETIC", report.ToText());
        Assert.Contains("EXPERIMENTAL", report.ToHtml());
        Assert.DoesNotContain("<script", report.ToHtml());
    }

    [Fact]
    public void Kill_switch_rejects_orders_and_leaves_the_ledger_untouched()
    {
        var (t, clock) = Ready();
        using var _ = t;
        new Controls(t.Db, clock).SetHalt(true, "test halt");
        t.Reopen(); // persisted, not in-memory
        var r = Engine(t.Db, clock).Run("paper", Fx.T50, new EdgeThresholdStrategy());
        var o = Assert.Single(r.Orders);
        Assert.Equal("rejected", o.Status);
        Assert.Contains(o.Verdict.Reasons, x => x.StartsWith("kill switch engaged"));
        Assert.Equal(1, t.Db.Scalar<long>("SELECT COUNT(*) FROM ledger_entries")); // deposit only
    }

    [Fact]
    public async Task Rules_change_after_research_blocks_trading_on_that_estimate()
    {
        var (t, clock) = Ready();
        using var _ = t;
        var amended = JsonNode.Parse(Fx.MarketJson(Fx.Open, Fx.T50))!;
        amended["market"]!["rules_primary"] = "AMENDED rules.";
        var handler = new ScriptedHandler(req => req.RequestUri!.AbsolutePath.EndsWith(Fx.T50)
            ? ScriptedHandler.Json(HttpStatusCode.OK, amended.ToJsonString())
            : new FixtureHandlerProxy(Fx.Open).Get(req));
        clock.Advance(TimeSpan.FromMinutes(1));
        await new Recorder(t.Db, new KalshiPublicClient(new HttpClient(handler), KalshiEndpoint.Fixture, clock,
            new KalshiClientOptions { MinInterval = TimeSpan.Zero }), clock).CaptureAsync([Fx.T50]);

        var r = Engine(t.Db, clock).Run("paper", Fx.T50, new EdgeThresholdStrategy());
        var o = Assert.Single(r.Orders);
        Assert.Equal("rejected", o.Status);
        Assert.Contains("market rules changed since the research packet was built", o.Verdict.Reasons);
    }

    [Fact]
    public void Stale_snapshot_is_rejected()
    {
        var (t, clock) = Ready();
        using var _ = t;
        clock.Advance(TimeSpan.FromMinutes(30));
        var o = Assert.Single(Engine(t.Db, clock).Run("paper", Fx.T50, new EdgeThresholdStrategy()).Orders);
        Assert.Equal("rejected", o.Status);
        Assert.Contains(o.Verdict.Reasons, x => x.StartsWith("snapshot older than"));
    }
}

public class RecoveryTests
{
    [Theory]
    [InlineData("after-order-insert")]
    [InlineData("after-fill-insert")]
    public void Crash_inside_an_order_rolls_back_completely_and_rerun_fills_exactly_once(string faultPoint)
    {
        var (t, clock) = PaperWorkflowTests.Ready();
        using var _ = t;
        var engine = PaperWorkflowTests.Engine(t.Db, clock);
        engine.FaultInjector = point => { if (point == faultPoint) throw new IOException("simulated crash"); };
        Assert.Throws<IOException>(() => engine.Run("paper", Fx.T50, new EdgeThresholdStrategy()));

        Assert.Equal(0, t.Db.Scalar<long>("SELECT COUNT(*) FROM paper_orders"));
        Assert.Equal(0, t.Db.Scalar<long>("SELECT COUNT(*) FROM paper_fills"));
        Assert.Equal(1, t.Db.Scalar<long>("SELECT COUNT(*) FROM ledger_entries"));

        // Restart the process (reopen the database) and rerun the same cycle.
        var db = t.Reopen();
        var rerun = PaperWorkflowTests.Engine(db, clock).Run("paper", Fx.T50, new EdgeThresholdStrategy());
        Assert.Equal("filled", Assert.Single(rerun.Orders).Status);
        var again = PaperWorkflowTests.Engine(db, clock).Run("paper", Fx.T50, new EdgeThresholdStrategy());
        Assert.True(again.AlreadyProcessed);
        Assert.Equal(rerun.DecisionId, again.DecisionId);
        Assert.Equal(1, db.Scalar<long>("SELECT COUNT(*) FROM paper_orders"));
        Assert.Equal(1, db.Scalar<long>("SELECT COUNT(*) FROM paper_fills"));
        var ledger = new PaperLedger(db, clock);
        Assert.Empty(ledger.Verify(ledger.FindAccount("paper")!.Value));
    }

    [Fact]
    public void Client_order_ids_are_unique_at_the_database_level()
    {
        var (t, clock) = PaperWorkflowTests.Ready();
        using var _ = t;
        PaperWorkflowTests.Engine(t.Db, clock).Run("paper", Fx.T50, new EdgeThresholdStrategy());
        Assert.Throws<SqliteException>(() => t.Db.Exec(
            "INSERT INTO paper_orders SELECT NULL, account_id, client_order_id, decision_id, strategy, ticker, side, action, quantity, limit_price, snapshot_id, research_result_id, data_origin, created_at, status, filled_quantity, reasons FROM paper_orders"));
    }

    [Theory]
    [InlineData("UPDATE ledger_entries SET cash_delta = '1000000'")]
    [InlineData("DELETE FROM ledger_entries")]
    [InlineData("UPDATE paper_fills SET price = '0.01'")]
    [InlineData("DELETE FROM paper_fills")]
    [InlineData("UPDATE raw_responses SET body = '{}'")]
    [InlineData("DELETE FROM raw_responses")]
    public void History_tables_are_append_only(string sql)
    {
        var (t, clock) = PaperWorkflowTests.Ready();
        using var _ = t;
        PaperWorkflowTests.Engine(t.Db, clock).Run("paper", Fx.T50, new EdgeThresholdStrategy());
        var e = Assert.Throws<SqliteException>(() => t.Db.Exec(sql));
        Assert.Contains("append-only", e.Message);
    }

    [Fact]
    public void Migrations_are_idempotent_across_restarts()
    {
        using var t = new TempDb();
        t.Reopen();
        t.Reopen();
        Assert.Equal(Schema.LatestVersion, t.Db.Scalar<long>("SELECT MAX(version) FROM schema_migrations"));
        Assert.Equal(1, t.Db.Scalar<long>("SELECT COUNT(*) FROM schema_migrations"));
        Assert.Equal("wal", t.Db.Scalar<string>("PRAGMA journal_mode"));
        Assert.Equal(1, t.Db.Scalar<long>("PRAGMA foreign_keys"));
    }

    [Theory]
    [InlineData("100.5300", "100.53")]
    [InlineData("0", "0.00")]
    [InlineData("0.123456", "0.123456")]
    [InlineData("-0.0200", "-0.02")]
    public void Display_formatting_never_changes_stored_precision(string stored, string shown)
    {
        var d = Dec.Parse(stored);
        Assert.Equal(shown, Dec.Show(d));
        Assert.Equal(stored, Dec.ToText(d)); // storage text keeps the original scale
    }

    [Fact]
    public void Timestamps_round_trip_exactly_and_sort_lexically()
    {
        var a = new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.FromHours(-5)).AddTicks(1234567);
        var text = Ts.ToText(a);
        Assert.EndsWith("Z", text);
        Assert.Equal(a, Ts.Parse(text));
        Assert.True(string.CompareOrdinal(Ts.ToText(a), Ts.ToText(a.AddTicks(1))) < 0);
    }
}
