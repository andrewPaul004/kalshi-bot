using System.Net;
using System.Text.Json.Nodes;
using KalshiBot.Core.Common;
using KalshiBot.Core.Kalshi;
using KalshiBot.Core.Recording;
using KalshiBot.Core.Research;
using KalshiBot.Core.Trading;
using Xunit;

namespace KalshiBot.Tests;

/// <summary>Regression tests for defects found by the independent review of the Phase 1-2 milestone.</summary>
public class ReviewRegressionTests
{
    private const string SubscriptionStatus = """{"loggedIn":true,"authMethod":"claude.ai","apiProvider":"firstParty","subscriptionType":"pro"}""";

    // Finding 1: empty token, denylist gaps, settings-file credentials.
    [Fact]
    public void Empty_setup_token_does_not_satisfy_the_guard()
    {
        const string status = """{"loggedIn":true,"authMethod":"oauth_token","apiProvider":"firstParty"}""";
        Assert.False(BillingGuard.Evaluate(status, new Dictionary<string, string> { ["CLAUDE_CODE_OAUTH_TOKEN"] = "" }).Allowed);
        Assert.False(BillingGuard.Evaluate(status, new Dictionary<string, string> { ["CLAUDE_CODE_OAUTH_TOKEN"] = "   " }).Allowed);
    }

    [Fact]
    public void Child_environment_is_an_allowlist()
    {
        var env = BillingGuard.SanitizedEnvironment(new Dictionary<string, string>
        {
            ["PATH"] = "/bin", ["HOME"] = "/h", ["LC_ALL"] = "C", ["CLAUDE_CONFIG_DIR"] = "/c", ["CLAUDE_CODE_OAUTH_TOKEN"] = "t",
            ["CLAUDE_SESSION_INGRESS_TOKEN_FILE"] = "/run/token", ["SOME_GATEWAY_KEY"] = "x", ["ANTHROPIC_MODEL"] = "m",
        }, out var removed);
        Assert.Equal(["CLAUDE_CODE_OAUTH_TOKEN", "CLAUDE_CONFIG_DIR", "HOME", "LC_ALL", "PATH"], env.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(["ANTHROPIC_MODEL", "CLAUDE_SESSION_INGRESS_TOKEN_FILE", "SOME_GATEWAY_KEY"], removed);
    }

    [Fact]
    public void Settings_that_can_reroute_billing_are_refused()
    {
        var reasons = BillingGuard.InspectSettings(
        [
            new SettingsFile("/u/settings.json", """{"env":{"ANTHROPIC_BASE_URL":"https://gw","ANTHROPIC_AUTH_TOKEN":"t","ANTHROPIC_MODEL":"ok"}}"""),
            new SettingsFile("/m/managed-settings.json", """{"apiKeyHelper":"/bin/key"}"""),
            new SettingsFile("/m/managed-settings.d/10.json", """{"env":{"CLAUDE_CODE_USE_BEDROCK":"1"}}"""),
            new SettingsFile("/u/broken.json", "{ not json"),
            new SettingsFile("/u/locked.json", null, "permission denied"),
        ]);
        Assert.Contains(reasons, r => r.Contains("ANTHROPIC_BASE_URL"));
        Assert.Contains(reasons, r => r.Contains("ANTHROPIC_AUTH_TOKEN"));
        Assert.DoesNotContain(reasons, r => r.Contains("ANTHROPIC_MODEL"));
        Assert.Contains(reasons, r => r.Contains("apiKeyHelper"));
        Assert.Contains(reasons, r => r.Contains("CLAUDE_CODE_USE_BEDROCK"));
        Assert.Contains(reasons, r => r.Contains("not valid JSON"));
        Assert.Contains(reasons, r => r.Contains("cannot read"));
        Assert.Empty(BillingGuard.InspectSettings([new SettingsFile("/u/settings.json", """{"theme":"dark","env":{"ANTHROPIC_MODEL":"x"}}""")]));
    }

    [Fact]
    public async Task Gateway_in_user_settings_refuses_before_any_process_runs()
    {
        var (t, clock, packet) = Setup();
        using var _ = t;
        var config = Directory.CreateDirectory(Path.Combine(t.Dir, "claude-config")).FullName;
        File.WriteAllText(Path.Combine(config, "settings.json"), """{"env":{"ANTHROPIC_BASE_URL":"https://gateway.example"}}""");
        var runner = new FakeProcessRunner(_ => new ProcessResult(0, SubscriptionStatus, "", false));
        var r = await new ClaudeResearchRunner(t.Db, clock, runner, Options(t))
            .RunAsync(packet.PacketId, new Dictionary<string, string> { ["PATH"] = "/bin", ["HOME"] = t.Dir, ["CLAUDE_CONFIG_DIR"] = config });
        Assert.Equal("refused", r.Outcome);
        Assert.Contains("ANTHROPIC_BASE_URL", r.Detail);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Managed_drop_in_with_api_key_helper_refuses()
    {
        var (t, clock, packet) = Setup();
        using var _ = t;
        var managed = Path.Combine(t.Dir, "managed");
        Directory.CreateDirectory(Path.Combine(managed, "managed-settings.d"));
        File.WriteAllText(Path.Combine(managed, "managed-settings.d", "20-keys.json"), """{"apiKeyHelper":"/opt/key.sh"}""");
        var runner = new FakeProcessRunner(_ => new ProcessResult(0, SubscriptionStatus, "", false));
        var r = await new ClaudeResearchRunner(t.Db, clock, runner, new ClaudeRunOptions { ManagedSettingsDir = managed })
            .RunAsync(packet.PacketId, new Dictionary<string, string> { ["PATH"] = "/bin", ["HOME"] = t.Dir });
        Assert.Equal("refused", r.Outcome);
        Assert.Empty(runner.Calls);
    }

    // Finding 2: a result for another packet must not be imported.
    [Fact]
    public async Task Result_for_a_different_packet_is_rejected_by_the_runner()
    {
        var (t, clock, packetT50) = Setup();
        using var _ = t;
        var packetT60 = new PacketExporter(t.Db, clock).Export(Fx.T60);
        var redirected = Fx.ValidResult(packetT60, clock, 0.99m);
        var runner = new FakeProcessRunner(spec => spec.Arguments[0] == "auth"
            ? new ProcessResult(0, SubscriptionStatus, "", false)
            : new ProcessResult(0, new JsonObject { ["type"] = "result", ["is_error"] = false, ["structured_output"] = redirected }.ToJsonString(), "", false));
        var r = await new ClaudeResearchRunner(t.Db, clock, runner, Options(t))
            .RunAsync(packetT50.PacketId, new Dictionary<string, string> { ["PATH"] = "/bin", ["HOME"] = t.Dir });
        Assert.Equal("failed", r.Outcome);
        Assert.Contains("not the packet that was run", r.Detail);
        Assert.Equal(0, t.Db.Scalar<long>("SELECT COUNT(*) FROM research_results"));
    }

    // Finding 5: re-trading the same snapshot via cosmetic parameter or number changes.
    [Fact]
    public void Same_snapshot_cannot_be_traded_twice_through_cosmetic_changes()
    {
        var (t, clock) = PaperWorkflowTests.Ready();
        using var _ = t;
        var engine = PaperWorkflowTests.Engine(t.Db, clock);
        var first = engine.Run("paper", Fx.T50, new EdgeThresholdStrategy(minEdge: 0.05m));
        Assert.Equal("filled", Assert.Single(first.Orders).Status);

        // 0.050 is the same parameter: same decision, nothing new.
        Assert.True(engine.Run("paper", Fx.T50, new EdgeThresholdStrategy(minEdge: 0.050m)).AlreadyProcessed);

        // Re-importing the same estimate written as 0.60 is the same result.
        var packet = new PacketExporter(t.Db, clock).Export(Fx.T50);
        var json = Fx.ValidResult(packet, clock, 0.6m).ToJsonString().Replace("\"probability_yes\":0.6", "\"probability_yes\":0.60");
        Assert.True(new ResultImporter(t.Db, clock).Import(json, "synthetic-fixture").AlreadyImported);

        // A genuinely different parameter makes a new decision, but the snapshot's liquidity is already spent.
        var other = engine.Run("paper", Fx.T50, new EdgeThresholdStrategy(minEdge: 0.04m));
        var o = Assert.Single(other.Orders);
        Assert.Equal("rejected", o.Status);
        Assert.Contains(o.Verdict.Reasons, r => r.StartsWith("this snapshot's liquidity was already used"));
        Assert.Equal(1, t.Db.Scalar<long>("SELECT COUNT(*) FROM paper_fills"));
    }

    // Finding 7: settlement from a different data origin.
    [Fact]
    public void Settlement_refuses_a_snapshot_from_a_different_data_origin()
    {
        var (t, clock) = PaperWorkflowTests.Ready();
        using var _ = t;
        PaperWorkflowTests.Engine(t.Db, clock).Run("paper", Fx.T50, new EdgeThresholdStrategy());
        t.Db.Exec("UPDATE paper_orders SET data_origin = 'live:production'"); // position opened on live data
        Fx.Record(t.Db, clock, Fx.Settled, Fx.T50);                          // synthetic finalized snapshot
        var ledger = new PaperLedger(t.Db, clock);
        var acct = ledger.FindAccount("paper")!.Value;
        var e = Assert.Throws<KalshiBotException>(() => ledger.Settle(acct, new MarketStore(t.Db).Latest(Fx.T50)!));
        Assert.Contains("Refusing to settle", e.Message);
        Assert.Equal(0, t.Db.Scalar<long>("SELECT COUNT(*) FROM ledger_entries WHERE kind = 'settlement'"));
    }

    // Finding 8: Verify must catch forged or inconsistent entries.
    [Theory]
    [InlineData("INSERT INTO ledger_entries (account_id, ts, kind, ticker, side, quantity_delta, cash_delta, cost_delta, ref_kind, ref_id) VALUES (1, '2026-11-23T12:00:00.0000000Z', 'trade', 'KXSYNTHWX-99DEC31-T50', 'yes', 9, '0', '0', 'fill', 999)", "does not reference a fill")]
    [InlineData("INSERT INTO ledger_entries (account_id, ts, kind, ticker, side, quantity_delta, cash_delta, cost_delta, ref_kind, ref_id) VALUES (1, '2026-11-23T12:00:00.0000000Z', 'settlement', 'KXSYNTHWX-99DEC31-T50', 'yes', 0, '50', '0', 'snapshot', 1)", "settlement entry without a matching settlement record")]
    [InlineData("INSERT INTO ledger_entries (account_id, ts, kind, ticker, side, quantity_delta, cash_delta, cost_delta, ref_kind, ref_id) VALUES (1, '2026-11-23T12:00:00.0000000Z', 'trade', 'KXSYNTHWX-99DEC31-T50', 'yes', 5, '-0.45', '0.45', 'fill', 1)", "expected exactly one trade entry")]
    [InlineData("INSERT INTO ledger_entries (account_id, ts, kind, cash_delta, ref_kind, ref_id) VALUES (1, '2026-11-23T12:00:00.0000000Z', 'deposit', '50', 'account', 1)", "exactly one deposit")]
    [InlineData("UPDATE paper_orders SET filled_quantity = 3", "filled_quantity 3 but fills sum to 1")]
    public void Verify_detects_entries_that_do_not_match_independent_records(string tamper, string expected)
    {
        var (t, clock) = PaperWorkflowTests.Ready();
        using var _ = t;
        PaperWorkflowTests.Engine(t.Db, clock).Run("paper", Fx.T50, new EdgeThresholdStrategy());
        var ledger = new PaperLedger(t.Db, clock);
        var acct = ledger.FindAccount("paper")!.Value;
        Assert.Empty(ledger.Verify(acct));
        t.Db.Exec(tamper);
        Assert.Contains(ledger.Verify(acct), p => p.Contains(expected));
    }

    [Fact]
    public void Verify_passes_a_full_buy_sell_settle_lifecycle()
    {
        var (t, clock) = PaperWorkflowTests.Ready();
        using var _ = t;
        PaperWorkflowTests.Engine(t.Db, clock).Run("paper", Fx.T50, new EdgeThresholdStrategy(contracts: 2));
        Fx.Record(t.Db, clock, Fx.Settled, Fx.T50);
        var ledger = new PaperLedger(t.Db, clock);
        var acct = ledger.FindAccount("paper")!.Value;
        Assert.Equal(1, ledger.Settle(acct, new MarketStore(t.Db).Latest(Fx.T50)!));
        Assert.Empty(ledger.Verify(acct));
    }

    // Finding 10: an HTTP timeout must not abort the run or leave it 'running'.
    [Fact]
    public async Task Request_timeout_is_recorded_as_a_ticker_error()
    {
        using var t = new TempDb();
        var clock = FixedClock.Default();
        var handler = new ScriptedHandler(req => req.RequestUri!.AbsolutePath.EndsWith(Fx.T50)
            ? throw new TaskCanceledException("timeout")
            : new FixtureHandlerProxy(Fx.Open).Get(req));
        var client = new KalshiPublicClient(new HttpClient(handler), KalshiEndpoint.Fixture, clock, new KalshiClientOptions { MinInterval = TimeSpan.Zero });
        var summary = await new Recorder(t.Db, client, clock).CaptureAsync([Fx.T50, Fx.T60]);
        Assert.Equal("partial", summary.Status);
        Assert.Contains($"{Fx.T50}: request timed out", summary.Errors);
        Assert.Equal(0, t.Db.Scalar<long>("SELECT COUNT(*) FROM capture_runs WHERE status = 'running'"));
    }

    [Fact]
    public async Task Unexpected_failure_marks_the_run_failed_instead_of_running()
    {
        using var t = new TempDb();
        var clock = FixedClock.Default();
        var handler = new ScriptedHandler(_ => throw new UnauthorizedAccessException("boom"));
        var client = new KalshiPublicClient(new HttpClient(handler), KalshiEndpoint.Fixture, clock, new KalshiClientOptions { MinInterval = TimeSpan.Zero });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new Recorder(t.Db, client, clock).CaptureAsync([Fx.T50]));
        Assert.Equal("failed", t.Db.Scalar<string>("SELECT status FROM capture_runs"));
    }

    private static (TempDb T, FixedClock Clock, ExportedPacket Packet) Setup()
    {
        var t = new TempDb();
        var clock = FixedClock.Default();
        Fx.Record(t.Db, clock, Fx.Open);
        return (t, clock, new PacketExporter(t.Db, clock).Export(Fx.T50));
    }

    private static ClaudeRunOptions Options(TempDb t) => new() { ManagedSettingsDir = Path.Combine(t.Dir, "no-managed") };
}
