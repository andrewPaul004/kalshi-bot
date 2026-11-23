using System.Collections;
using System.Text.Json;
using KalshiBot.Core.Common;
using KalshiBot.Core.Storage;

namespace KalshiBot.Core.Research;

public sealed record ClaudeRunOptions
{
    public string ClaudePath { get; init; } = "claude";
    public string? Model { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(10);
}

public sealed record ClaudeRunResult(long RunId, string Outcome, AuthVerdict Verdict, ImportOutcome? Imported, string Detail);

/// <summary>
/// Manual, one-packet-at-a-time research round trip through the official Claude Code CLI under the user's
/// subscription login. Order of operations: sanitize env -> verify auth -> run `claude -p` in an empty temp
/// directory with only web tools -> strict import. Every attempt, including refusals, is audited in research_runs.
/// </summary>
public sealed class ClaudeResearchRunner(Db db, TimeProvider time, IProcessRunner processes, ClaudeRunOptions? options = null)
{
    private readonly ClaudeRunOptions _o = options ?? new ClaudeRunOptions();

    public static IReadOnlyDictionary<string, string> CurrentEnvironment() =>
        Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string?)e.Value ?? "", StringComparer.Ordinal);

    public async Task<ClaudeRunResult> RunAsync(string packetId, IReadOnlyDictionary<string, string> parentEnv, CancellationToken ct = default)
    {
        var packet = new ResearchStore(db).Packet(packetId) ?? throw new KalshiBotException($"Unknown packet {packetId}.");
        var started = time.GetUtcNow();
        var env = BillingGuard.SanitizedEnvironment(parentEnv, out var removed);
        var removedNote = removed.Count == 0 ? "" : $"removed from child env: {string.Join(", ", removed)}. ";

        var workDir = Directory.CreateTempSubdirectory("kalshibot-research-").FullName;
        try
        {
            ProcessResult status;
            try
            {
                status = await processes.RunAsync(new ProcessSpec(_o.ClaudePath, ["auth", "status"], env, workDir, null, TimeSpan.FromSeconds(60)), ct);
            }
            catch (System.ComponentModel.Win32Exception e)
            {
                var v = new AuthVerdict(false, null, null, [$"could not start '{_o.ClaudePath}': {e.Message}"]);
                return Audit(packetId, started, "refused", v, removedNote + string.Join("; ", v.Reasons), null, null);
            }

            var verdict = status.TimedOut
                ? new AuthVerdict(false, null, null, ["`claude auth status` timed out"])
                : BillingGuard.Evaluate(status.Stdout, env);
            if (!verdict.Allowed)
                return Audit(packetId, started, "refused", verdict, removedNote + string.Join("; ", verdict.Reasons), null, null);

            var args = BuildArguments();
            if (args.Any(a => BillingGuard.ForbiddenArguments.Contains(a)))
                throw new InvalidOperationException("Refusing to run Claude with a billing-bypass argument.");

            var run = await processes.RunAsync(new ProcessSpec(_o.ClaudePath, args, env, workDir, packet.Json, _o.Timeout), ct);
            if (run.TimedOut) return Audit(packetId, started, "failed", verdict, removedNote + "claude -p timed out", run.Stdout, null);

            string structured;
            try
            {
                using var doc = JsonDocument.Parse(run.Stdout);
                var root = doc.RootElement;
                if (root.TryGetProperty("is_error", out var err) && err.ValueKind == JsonValueKind.True)
                    return Audit(packetId, started, "failed", verdict, removedNote + $"claude reported an error (exit {run.ExitCode})", run.Stdout, null);
                if (!root.TryGetProperty("structured_output", out var so) || so.ValueKind != JsonValueKind.Object)
                    return Audit(packetId, started, "failed", verdict, removedNote + "no structured_output in claude result (declined or schema not met)", run.Stdout, null);
                structured = so.GetRawText();
            }
            catch (JsonException)
            {
                return Audit(packetId, started, "failed", verdict, removedNote + $"claude output was not JSON (exit {run.ExitCode})", run.Stdout, null);
            }

            try
            {
                var imported = new ResultImporter(db, time).Import(structured, "claude-subscription-cli");
                return Audit(packetId, started, "imported", verdict,
                    removedNote + (imported.AlreadyImported ? "identical result already imported" : "imported as EXPERIMENTAL (unvalidated)"),
                    run.Stdout, imported);
            }
            catch (ResultRejectedException e)
            {
                return Audit(packetId, started, "failed", verdict, removedNote + e.Message, run.Stdout, null);
            }
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// Flags verified against Claude Code 2.1.330 `--help` and code.claude.com/docs/en/headless.
    /// The prompt comes first so variadic options cannot swallow it; the packet arrives on stdin.
    /// No --bare (it ignores subscription OAuth). Only web tools; no file, shell, MCP, or slash commands.
    /// </summary>
    internal List<string> BuildArguments()
    {
        var args = new List<string>
        {
            "-p", "Research the Kalshi market in the JSON packet on stdin. Follow its \"task\" field exactly and return the result object.",
            "--output-format", "json",
            "--json-schema", ResearchContract.ResultJsonSchema,
            "--no-session-persistence",
            "--strict-mcp-config",
            "--disable-slash-commands",
            "--permission-mode", "dontAsk",
        };
        if (_o.Model is { } m) args.AddRange(["--model", m]);
        args.AddRange(["--tools", "WebSearch,WebFetch", "--allowedTools", "WebSearch,WebFetch"]);
        return args;
    }

    private ClaudeRunResult Audit(string packetId, DateTimeOffset started, string outcome, AuthVerdict verdict, string detail,
        string? stdout, ImportOutcome? imported)
    {
        var id = db.Insert("""
            INSERT INTO research_runs (packet_id, started_at, finished_at, outcome, auth_method, subscription_type, detail,
                stdout_sha256, stdout, research_result_id)
            VALUES ($p, $s, $f, $o, $am, $st, $d, $sh, $out, $rr)
            """,
            ("$p", packetId), ("$s", Ts.ToText(started)), ("$f", Ts.ToText(time.GetUtcNow())), ("$o", outcome),
            ("$am", verdict.AuthMethod), ("$st", verdict.SubscriptionType), ("$d", detail),
            ("$sh", stdout is null ? null : Hash.Sha256Hex(stdout)), ("$out", stdout), ("$rr", imported?.ResultId));
        return new ClaudeRunResult(id, outcome, verdict, imported, detail);
    }
}
