using System.Text.Json.Nodes;
using KalshiBot.Core.Research;
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
    private const string SubscriptionStatus = """{"loggedIn":true,"authMethod":"claude.ai","apiProvider":"firstParty","subscriptionType":"pro"}""";

    private static (TempDb T, FixedClock Clock, string PacketId, Core.Research.ExportedPacket Packet) Setup()
    {
        var t = new TempDb();
        var clock = FixedClock.Default();
        Fx.Record(t.Db, clock, Fx.Open);
        var p = new PacketExporter(t.Db, clock).Export(Fx.T50);
        return (t, clock, p.PacketId, p);
    }

    private static readonly Dictionary<string, string> ParentEnv = new()
    {
        ["PATH"] = "/usr/bin", ["HOME"] = "/home/u", ["ANTHROPIC_API_KEY"] = "sk-ant-should-never-reach-claude",
    };

    [Fact]
    public async Task Refused_auth_never_invokes_the_model_and_is_audited()
    {
        var (t, clock, packetId, _) = Setup();
        using var _ = t;
        var runner = new FakeProcessRunner(spec => new ProcessResult(0,
            """{"loggedIn":true,"authMethod":"api_key","apiProvider":"firstParty","apiKeySource":"ANTHROPIC_API_KEY"}""", "", false));
        var r = await new ClaudeResearchRunner(t.Db, clock, runner).RunAsync(packetId, ParentEnv);

        Assert.Equal("refused", r.Outcome);
        Assert.Single(runner.Calls); // only `claude auth status`
        Assert.Equal(["auth", "status"], runner.Calls[0].Arguments);
        Assert.Equal("refused", t.Db.Scalar<string>("SELECT outcome FROM research_runs"));
        Assert.Equal(0, t.Db.Scalar<long>("SELECT COUNT(*) FROM research_results"));
    }

    [Fact]
    public async Task Missing_claude_binary_is_a_refusal_not_a_crash()
    {
        var (t, clock, packetId, _) = Setup();
        using var _ = t;
        var r = await new ClaudeResearchRunner(t.Db, clock, new ProcessRunner(),
            new ClaudeRunOptions { ClaudePath = "/nonexistent/claude-binary" }).RunAsync(packetId, ParentEnv);
        Assert.Equal("refused", r.Outcome);
        Assert.Contains("could not start", r.Detail);
    }

    [Fact]
    public async Task Subscription_round_trip_uses_sanitized_env_safe_flags_and_strict_import()
    {
        var (t, clock, packetId, packet) = Setup();
        using var _ = t;
        var structured = Fx.ValidResult(packet, clock, 0.55m);
        var runner = new FakeProcessRunner(spec => spec.Arguments[0] == "auth"
            ? new ProcessResult(0, SubscriptionStatus, "", false)
            : new ProcessResult(0, new JsonObject
            {
                ["type"] = "result", ["is_error"] = false, ["result"] = "", ["structured_output"] = structured.DeepClone(),
            }.ToJsonString(), "", false));

        var r = await new ClaudeResearchRunner(t.Db, clock, runner).RunAsync(packetId, ParentEnv);

        Assert.Equal("imported", r.Outcome);
        Assert.Equal(0.55m, r.Imported!.ProbabilityYes);
        Assert.Equal("claude-subscription-cli", t.Db.Scalar<string>("SELECT source_kind FROM research_results"));
        Assert.Contains("ANTHROPIC_API_KEY", r.Detail); // reported as removed

        Assert.Equal(2, runner.Calls.Count);
        foreach (var call in runner.Calls)
            Assert.DoesNotContain(call.Environment.Keys, k => k.StartsWith("ANTHROPIC_", StringComparison.OrdinalIgnoreCase));
        // Claude runs in an empty directory: no project CLAUDE.md, hooks, or .mcp.json can load.
        Assert.All(runner.WorkDirEntries, Assert.Empty);
        var model = runner.Calls[1];
        Assert.Equal(packet.Json, model.Stdin);
        Assert.DoesNotContain("--bare", model.Arguments);
        Assert.DoesNotContain("--settings", model.Arguments);
        Assert.Equal("-p", model.Arguments[0]);
        Assert.Contains("--json-schema", model.Arguments);
        Assert.Contains("--strict-mcp-config", model.Arguments);
        Assert.Contains("--no-session-persistence", model.Arguments);
        Assert.Equal("WebSearch,WebFetch", model.Arguments[model.Arguments.ToList().IndexOf("--tools") + 1]);
        Assert.Equal("dontAsk", model.Arguments[model.Arguments.ToList().IndexOf("--permission-mode") + 1]);
        Assert.False(Directory.Exists(model.WorkingDirectory)); // temp dir cleaned up
    }

    [Fact]
    public async Task Malformed_or_missing_structured_output_is_rejected_and_audited()
    {
        var (t, clock, packetId, packet) = Setup();
        using var _ = t;
        var bad = Fx.ValidResult(packet, clock);
        bad["probability_yes"] = 2.0m;
        foreach (var stdout in new[]
        {
            new JsonObject { ["type"] = "result", ["is_error"] = false, ["structured_output"] = bad }.ToJsonString(),
            """{"type":"result","is_error":false,"result":"I can't help with that."}""",
            """{"type":"result","is_error":true,"result":"Login expired"}""",
            "not json",
        })
        {
            var runner = new FakeProcessRunner(spec => spec.Arguments[0] == "auth"
                ? new ProcessResult(0, SubscriptionStatus, "", false)
                : new ProcessResult(0, stdout, "", false));
            var r = await new ClaudeResearchRunner(t.Db, clock, runner).RunAsync(packetId, ParentEnv);
            Assert.Equal("failed", r.Outcome);
        }
        Assert.Equal(0, t.Db.Scalar<long>("SELECT COUNT(*) FROM research_results"));
        Assert.Equal(4, t.Db.Scalar<long>("SELECT COUNT(*) FROM research_runs WHERE outcome = 'failed'"));
    }
}

/// <summary>Static boundary checks over the source tree: no paid-model path can be added silently.</summary>
public class NoDirectModelApiTests
{
    private static string RepoRoot()
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
