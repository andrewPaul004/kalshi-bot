using System.Text.Json.Nodes;
using KalshiBot.Core.Common;
using KalshiBot.Core.Research;
using KalshiBot.Core.Trading;
using Xunit;

namespace KalshiBot.Tests;

public class BillingGuardTests
{
    private static readonly Dictionary<string, string> NoToken = new() { ["PATH"] = "/usr/bin", ["HOME"] = "/home/u" };

    [Fact]
    public void Child_environment_drops_every_variable_that_can_switch_billing()
    {
        var parent = new Dictionary<string, string>
        {
            ["PATH"] = "/usr/bin",
            ["HOME"] = "/home/u",
            ["HTTPS_PROXY"] = "http://proxy",
            ["CLAUDE_CODE_OAUTH_TOKEN"] = "sub-token",
            ["ANTHROPIC_API_KEY"] = "sk-ant-x",
            ["ANTHROPIC_AUTH_TOKEN"] = "bearer",
            ["ANTHROPIC_BASE_URL"] = "https://gateway.example",
            ["ANTHROPIC_PROFILE"] = "work",
            ["anthropic_api_key"] = "lowercase variant",
            ["CLAUDE_CODE_USE_BEDROCK"] = "1",
            ["CLAUDE_CODE_USE_VERTEX"] = "1",
            ["CLAUDE_CODE_USE_FOUNDRY"] = "1",
            ["OPENROUTER_API_KEY"] = "or",
            ["OPENAI_API_KEY"] = "oa",
            ["AWS_BEARER_TOKEN_BEDROCK"] = "aws",
        };
        var env = BillingGuard.SanitizedEnvironment(parent, out var removed);
        Assert.Equal(["CLAUDE_CODE_OAUTH_TOKEN", "HOME", "HTTPS_PROXY", "PATH"], env.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(11, removed.Count);
    }

    [Fact]
    public void Claude_ai_subscription_login_is_allowed()
    {
        var v = BillingGuard.Evaluate("""{"loggedIn":true,"authMethod":"claude.ai","apiProvider":"firstParty","subscriptionType":"max"}""", NoToken);
        Assert.True(v.Allowed, string.Join("; ", v.Reasons));
        Assert.Equal("max", v.SubscriptionType);
    }

    [Fact]
    public void Setup_token_is_allowed_only_when_it_is_the_env_token()
    {
        const string status = """{"loggedIn":true,"authMethod":"oauth_token","apiProvider":"firstParty"}""";
        Assert.True(BillingGuard.Evaluate(status, new Dictionary<string, string> { ["CLAUDE_CODE_OAUTH_TOKEN"] = "t" }).Allowed);
        var v = BillingGuard.Evaluate(status, NoToken);
        Assert.False(v.Allowed);
        Assert.Contains(v.Reasons, r => r.Contains("cannot be verified"));
    }

    [Theory]
    [InlineData("""{"loggedIn":true,"authMethod":"api_key","apiProvider":"firstParty","apiKeySource":"ANTHROPIC_API_KEY"}""")]
    [InlineData("""{"loggedIn":true,"authMethod":"api_key_helper","apiProvider":"firstParty","apiKeySource":"apiKeyHelper"}""")]
    [InlineData("""{"loggedIn":true,"authMethod":"api_key","apiProvider":"firstParty","subscriptionType":"max"}""")]
    [InlineData("""{"loggedIn":true,"authMethod":"api_key_helper","apiProvider":"firstParty","subscriptionType":"max"}""")]
    [InlineData("""{"loggedIn":true,"authMethod":"third_party","apiProvider":"bedrock"}""")]
    [InlineData("""{"loggedIn":true,"authMethod":"claude.ai","apiProvider":"vertex","subscriptionType":"pro"}""")]
    [InlineData("""{"loggedIn":true,"authMethod":"claude.ai","apiProvider":"firstParty","subscriptionType":"pro","apiKeySource":"/login managed key"}""")]
    [InlineData("""{"loggedIn":true,"authMethod":"claude.ai","apiProvider":"firstParty"}""")]
    [InlineData("""{"loggedIn":false,"authMethod":"none","apiProvider":"firstParty"}""")]
    [InlineData("""{"loggedIn":true,"authMethod":"something_new","apiProvider":"firstParty"}""")]
    [InlineData("""{"loggedIn":true}""")]
    [InlineData("Login method: Claude API account")]
    [InlineData("")]
    public void Anything_but_verified_subscription_auth_is_refused(string status)
    {
        var v = BillingGuard.Evaluate(status, NoToken);
        Assert.False(v.Allowed);
        Assert.NotEmpty(v.Reasons);
    }
}

public class ClaudeRunnerTests
{
    private static (TempDb T, FixedClock Clock, ExportedPacket Packet) Setup(bool attested = true)
    {
        var t = new TempDb();
        var clock = FixedClock.Default();
        Fx.Record(t.Db, clock, Fx.Open);
        var p = new PacketExporter(t.Db, clock).Export(Fx.T50);
        if (attested) new ResearchStore(t.Db).Attest(clock.Now);
        return (t, clock, p);
    }

    private static Dictionary<string, string> ParentEnv(TempDb t) => new()
    {
        ["PATH"] = "/usr/bin", ["HOME"] = t.Dir, ["ANTHROPIC_API_KEY"] = "sk-ant-should-never-reach-claude",
    };

    private static ClaudeRunOptions Options(TempDb t, bool force = false) => new()
    {
        ManagedSettingsDir = Path.Combine(t.Dir, "no-managed"), LockPath = Path.Combine(t.Dir, "research.lock"), Force = force,
    };

    private static string Envelope(JsonNode structured) =>
        new JsonObject { ["type"] = "result", ["is_error"] = false, ["result"] = "", ["structured_output"] = structured.DeepClone(),
            ["modelUsage"] = new JsonObject { ["claude-test-model"] = new JsonObject() } }.ToJsonString();

    [Fact]
    public async Task Refused_auth_never_invokes_the_model_and_is_audited()
    {
        var (t, clock, p) = Setup();
        using var _ = t;
        var runner = new FakeProcessRunner(spec => spec.Arguments[0] == "--version"
            ? new ProcessResult(0, "2.1.330 (Claude Code)", "", false)
            : new ProcessResult(0, """{"loggedIn":true,"authMethod":"api_key","apiProvider":"firstParty","apiKeySource":"ANTHROPIC_API_KEY"}""", "", false));
        var r = await new ClaudeResearchRunner(t.Db, clock, runner, Options(t)).RunAsync(p.PacketId, ParentEnv(t));

        Assert.Equal("refused", r.Outcome);
        Assert.Equal(2, runner.Calls.Count); // --version and auth status only; never -p
        Assert.DoesNotContain(runner.Calls, c => c.Arguments[0] == "-p");
        Assert.Equal("2.1.330", t.Db.Scalar<string>("SELECT cli_version FROM research_runs"));
        Assert.Equal(0, t.Db.Scalar<long>("SELECT COUNT(*) FROM research_records"));
    }

    [Fact]
    public async Task Without_an_owner_attestation_nothing_runs()
    {
        // Spec 4.2 step 4: usage-credit settings cannot be machine-verified; the owner must attest first.
        var (t, clock, p) = Setup(attested: false);
        using var _ = t;
        var runner = FakeProcessRunner.Subscription(_ => "{}");
        var r = await new ClaudeResearchRunner(t.Db, clock, runner, Options(t)).RunAsync(p.PacketId, ParentEnv(t));
        Assert.Equal("refused", r.Outcome);
        Assert.Contains("attest", r.Detail);
        Assert.Empty(runner.Calls);

        new ResearchStore(t.Db).Attest(clock.Now.AddDays(-31)); // stale
        r = await new ClaudeResearchRunner(t.Db, clock, runner, Options(t)).RunAsync(p.PacketId, ParentEnv(t));
        Assert.Equal("refused", r.Outcome);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Only_one_research_process_runs_at_a_time()
    {
        var (t, clock, p) = Setup();
        using var _ = t;
        using var held = ProcessLock.Acquire(Options(t).LockPath!, "test");
        var runner = FakeProcessRunner.Subscription(_ => "{}");
        var r = await new ClaudeResearchRunner(t.Db, clock, runner, Options(t)).RunAsync(p.PacketId, ParentEnv(t));
        Assert.Equal("refused", r.Outcome);
        Assert.Contains("another research process", r.Detail);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Missing_claude_binary_is_a_refusal_not_a_crash()
    {
        var (t, clock, p) = Setup();
        using var _ = t;
        var r = await new ClaudeResearchRunner(t.Db, clock, new ProcessRunner(), Options(t) with { ClaudePath = "/nonexistent/claude-binary" })
            .RunAsync(p.PacketId, ParentEnv(t));
        Assert.Equal("refused", r.Outcome);
        Assert.Contains("could not start", r.Detail);
    }

    [Fact]
    public async Task Subscription_round_trip_uses_safe_flags_strict_import_and_then_skips_the_unchanged_packet()
    {
        var (t, clock, p) = Setup();
        using var _ = t;
        var runner = FakeProcessRunner.Subscription(_ => Envelope(Fx.ValidRecord(p, clock, 0.55m)));
        var r = await new ClaudeResearchRunner(t.Db, clock, runner, Options(t)).RunAsync(p.PacketId, ParentEnv(t));

        Assert.Equal("imported", r.Outcome);
        Assert.Equal(0.55m, r.Imported!.ExperimentalProbability);
        Assert.Equal("claude-subscription-cli", t.Db.Scalar<string>("SELECT source_kind FROM research_records"));
        Assert.Equal("2.1.330", t.Db.Scalar<string>("SELECT observed_cli_version FROM research_records"));
        Assert.Equal("claude-test-model", t.Db.Scalar<string>("SELECT observed_model FROM research_records"));
        Assert.Contains("ANTHROPIC_API_KEY", r.Detail);

        Assert.Equal(3, runner.Calls.Count);
        foreach (var call in runner.Calls)
            Assert.DoesNotContain(call.Environment.Keys, k => k.StartsWith("ANTHROPIC_", StringComparison.OrdinalIgnoreCase));
        Assert.All(runner.WorkDirEntries, Assert.Empty); // clean workspace: no CLAUDE.md, hooks, or .mcp.json
        var model = runner.Calls[2];
        Assert.Equal(p.Json, model.Stdin);
        Assert.Equal(TimeSpan.FromMinutes(5), model.Timeout);
        Assert.DoesNotContain("--bare", model.Arguments);
        Assert.DoesNotContain("--settings", model.Arguments);
        Assert.Equal("-p", model.Arguments[0]);
        Assert.Contains("--strict-mcp-config", model.Arguments);
        Assert.Contains("--no-session-persistence", model.Arguments);
        Assert.Equal("WebSearch,WebFetch", model.Arguments[model.Arguments.ToList().IndexOf("--tools") + 1]);
        Assert.Equal("dontAsk", model.Arguments[model.Arguments.ToList().IndexOf("--permission-mode") + 1]);
        Assert.False(Directory.Exists(model.WorkingDirectory));

        // Spec 4.3: an unchanged packet is not researched again.
        var again = await new ClaudeResearchRunner(t.Db, clock, runner, Options(t)).RunAsync(p.PacketId, ParentEnv(t));
        Assert.Equal("skipped", again.Outcome);
        Assert.Equal(3, runner.Calls.Count);
    }

    [Fact]
    public async Task Quota_exhaustion_or_errors_suspend_without_retry_or_fallback()
    {
        // AC-03
        var (t, clock, p) = Setup();
        using var _ = t;
        var bad = Fx.ValidRecord(p, clock);
        bad["orderSize"] = 10;
        foreach (var stdout in new[]
        {
            """{"type":"result","is_error":true,"result":"Claude usage limit reached. Your limit will reset at 5pm."}""",
            Envelope(bad),
            """{"type":"result","is_error":false,"result":"I can't help with that."}""",
            "not json",
        })
        {
            var runner = FakeProcessRunner.Subscription(_ => stdout);
            var r = await new ClaudeResearchRunner(t.Db, clock, runner, Options(t, force: true)).RunAsync(p.PacketId, ParentEnv(t));
            Assert.Equal("failed", r.Outcome);
            Assert.Equal(1, runner.Calls.Count(c => c.Arguments[0] == "-p")); // exactly one attempt
        }
        Assert.Equal(0, t.Db.Scalar<long>("SELECT COUNT(*) FROM research_records"));
        Assert.Contains("usage limit reached", t.Db.Scalar<string>("SELECT detail FROM research_runs ORDER BY id LIMIT 1"));
    }
}

/// <summary>AC-15: paper mode has no exchange write path to misuse; the only HTTP client is GET-only.</summary>
public class NoExchangeWritePathTests
{
    [Fact]
    public void Source_contains_no_order_writes_or_exchange_credentials_handling()
    {
        var root = NoDirectModelApiTests.RepoRoot();
        foreach (var f in Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
        {
            var text = File.ReadAllText(f);
            foreach (var forbidden in new[] { "HttpMethod.Post", "HttpMethod.Put", "HttpMethod.Delete", "HttpMethod.Patch", "/portfolio", "KALSHI-ACCESS-SIGNATURE", "PostAsync", "PutAsync", "DeleteAsync" })
                Assert.False(text.Contains(forbidden, StringComparison.Ordinal), $"{Path.GetFileName(f)} contains '{forbidden}'");
        }
    }
}

/// <summary>AC-05: research probabilities have no path into trading decisions.</summary>
public class ResearchBoundaryTests
{
    [Fact]
    public void Trading_code_never_references_research_storage()
    {
        var root = NoDirectModelApiTests.RepoRoot();
        foreach (var f in Directory.GetFiles(Path.Combine(root, "src", "KalshiBot.Core", "Trading"), "*.cs"))
        {
            var text = File.ReadAllText(f);
            foreach (var forbidden in new[] { "KalshiBot.Core.Research", "research_records", "experimental_probabilities", "ResearchStore", "ResearchImporter" })
                Assert.False(text.Contains(forbidden, StringComparison.Ordinal), $"{Path.GetFileName(f)} references {forbidden}");
        }
    }

    [Fact]
    public void A_plausible_research_probability_changes_no_decision()
    {
        var (t, clock) = Fx.Ready();
        using var _ = t;
        // T60: the fixture model abstains (no edge). A research record claims p_yes = 0.99.
        var packet = new PacketExporter(t.Db, clock).Export(Fx.T60);
        new ResearchImporter(t.Db, clock).Import(Fx.ValidRecord(packet, clock, 0.99m).ToJsonString(), "synthetic-fixture");
        var r = Fx.Engine(t.Db, clock).Run("paper", Fx.T60, Fx.Strategy(), Fx.Model());
        Assert.Empty(r.Orders);
        Assert.Contains("no side clears min_edge", r.Rationale);
        Assert.Equal(0.17m, r.Forecast!.ProbabilityYes);
    }

    [Fact]
    public void Real_observations_without_a_validated_model_abstain()
    {
        var (t, clock) = Fx.Ready();
        using var _ = t;
        var r = Fx.Engine(t.Db, clock).Run("paper", Fx.T50, Fx.Strategy(), new NoValidatedModel());
        Assert.Empty(r.Orders);
        Assert.Contains("no validated forecast model", r.Rationale);
        Assert.Equal("abstain", t.Db.Scalar<string>("SELECT outcome FROM paper_decisions"));
    }
}

/// <summary>Static boundary checks over the source tree: no paid-model path can be added silently.</summary>
public class NoDirectModelApiTests
{
    internal static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "KalshiBot.slnx"))) return d.FullName;
        throw new InvalidOperationException("repo root not found");
    }

    [Fact]
    public void Source_has_no_model_api_endpoints_or_sdks()
    {
        var root = RepoRoot();
        var files = Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
        string[] forbidden = ["api.anthropic.com", "openrouter.ai", "api.openai.com", "x-api-key", "anthropic-version", "/v1/messages"];
        foreach (var f in files)
        {
            var text = File.ReadAllText(f);
            foreach (var s in forbidden) Assert.False(text.Contains(s, StringComparison.OrdinalIgnoreCase), $"{f} contains '{s}'");
        }

        var packages = File.ReadAllText(Path.Combine(root, "Directory.Packages.props"));
        foreach (var p in new[] { "Anthropic", "OpenAI", "OpenRouter", "Claude", "SemanticKernel", "Microsoft.Extensions.AI" })
            Assert.DoesNotContain(p, packages, StringComparison.OrdinalIgnoreCase);
    }
}
