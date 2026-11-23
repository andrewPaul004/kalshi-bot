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
        var runner = new FakeProcessRunner(_ => new ProcessResult(0, FakeProcessRunner.SubscriptionStatus, "", false));
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
        var runner = new FakeProcessRunner(_ => new ProcessResult(0, FakeProcessRunner.SubscriptionStatus, "", false));
        var r = await new ClaudeResearchRunner(t.Db, clock, runner, Options(t) with { ManagedSettingsDir = managed })
            .RunAsync(packet.PacketId, new Dictionary<string, string> { ["PATH"] = "/bin", ["HOME"] = t.Dir });
        Assert.Equal("refused", r.Outcome);
        Assert.Empty(runner.Calls);
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
        new ResearchStore(t.Db).Attest(clock.Now);
        return (t, clock, new PacketExporter(t.Db, clock).Export(Fx.T50));
    }

    private static ClaudeRunOptions Options(TempDb t) =>
        new() { ManagedSettingsDir = Path.Combine(t.Dir, "no-managed"), LockPath = Path.Combine(t.Dir, "research.lock") };
}
