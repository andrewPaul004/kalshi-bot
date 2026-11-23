using System.Collections;
using System.Text.Json;
using System.Text.RegularExpressions;
using KalshiBot.Core.Common;
using KalshiBot.Core.Storage;

namespace KalshiBot.Core.Research;

public sealed record ClaudeRunOptions
{
    public string ClaudePath { get; init; } = "claude";
    public string? Model { get; init; }

    /// <summary>Process timeout per research batch (spec 4.3 initial target: five minutes).</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Managed settings directory; null = the OS default documented at code.claude.com/docs/en/managed-settings.</summary>
    public string? ManagedSettingsDir { get; init; }

    /// <summary>Lock file enforcing one research process at a time; null = next to the database.</summary>
    public string? LockPath { get; init; }

    /// <summary>How long an operator attestation that usage credits are disabled stays acceptable.</summary>
    public TimeSpan AttestationMaxAge { get; init; } = TimeSpan.FromDays(30);

    /// <summary>Re-run even if this packet already has a CLI research record for the current prompt version.</summary>
    public bool Force { get; init; }
}

public sealed record ClaudeRunResult(long RunId, string Outcome, AuthVerdict Verdict, ImportOutcome? Imported, string Detail);

/// <summary>
/// Manual, one-packet research round trip through the official Claude Code CLI under the user's subscription
/// (spec 4.1-4.4). Every step fails closed and every attempt is audited in research_runs:
/// cache check -> owner attestation (usage credits off) -> single-run lock -> allowlisted env -> settings inspection ->
/// clean temp workspace -> CLI version -> `claude auth status` -> `claude -p` (web tools only, bounded time) ->
/// packet linkage check -> strict import. Quota exhaustion or any error ends the run; there is no retry or fallback.
/// </summary>
public sealed class ClaudeResearchRunner(Db db, TimeProvider time, IProcessRunner processes, ClaudeRunOptions? options = null)
{
    private readonly ClaudeRunOptions _o = options ?? new ClaudeRunOptions();

    public static IReadOnlyDictionary<string, string> CurrentEnvironment() =>
        Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string?)e.Value ?? "", StringComparer.Ordinal);

    public async Task<ClaudeRunResult> RunAsync(string packetId, IReadOnlyDictionary<string, string> parentEnv, CancellationToken ct = default)
    {
        var store = new ResearchStore(db);
        var packet = store.Packet(packetId) ?? throw new KalshiBotException($"Unknown packet {packetId}.");
        var started = time.GetUtcNow();
        var none = new AuthVerdict(false, null, null, []);

        if (!_o.Force && store.HasCliRecordFor(packetId, ResearchContract.PromptVersion))
            return Audit(packetId, started, "skipped", none, null, "unchanged packet already researched with this prompt version (use --force to re-run)", null, null);

        var attestation = store.LatestAttestation();
        if (attestation is null || started - attestation.AttestedAt > _o.AttestationMaxAge)
            return Audit(packetId, started, "refused", none, null,
                attestation is null
                    ? "owner has not attested that usage credits/extra usage are disabled; check claude.ai billing settings, then run 'kb research attest-billing'"
                    : $"usage-credit attestation from {Ts.ToText(attestation.AttestedAt)} is older than {_o.AttestationMaxAge.TotalDays} days; re-check and re-attest",
                null, null);

        using var runLock = ProcessLock.TryAcquire(_o.LockPath ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(db.Path))!, ".locks", "research.lock"));
        if (runLock is null)
            return Audit(packetId, started, "refused", none, null, "another research process is running (one at a time)", null, null);

        var env = BillingGuard.SanitizedEnvironment(parentEnv, out var removed);
        var billingRemoved = removed.Where(BillingGuard.IsBillingSwitch).ToList();
        var envNote = removed.Count == 0 ? "" :
            $"child env: dropped {removed.Count} non-allowlisted variable(s)" +
            (billingRemoved.Count == 0 ? ". " : $", including billing-relevant {string.Join(", ", billingRemoved)}. ");

        var settingsReasons = BillingGuard.InspectSettings(SettingsFiles(env));
        if (settingsReasons.Count > 0)
            return Audit(packetId, started, "refused", none with { Reasons = settingsReasons }, null, envNote + string.Join("; ", settingsReasons), null, null);

        var workDir = Directory.CreateTempSubdirectory("kalshibot-research-").FullName;
        try
        {
            if (InsideGitRepository(workDir))
                return Audit(packetId, started, "refused", none, null, envNote + "temp directory is inside a git repository; project settings could load", null, null);

            ClaudeLaunch launch;
            try { launch = ClaudeLaunch.Resolve(_o.ClaudePath, env, OperatingSystem.IsWindows(), File.Exists, File.ReadAllText); }
            catch (KalshiBotException e) { return Audit(packetId, started, "refused", none, null, envNote + e.Message, null, null); }

            ProcessResult versionRun;
            try
            {
                versionRun = await processes.RunAsync(launch.Spec(["--version"], env, workDir, null, TimeSpan.FromSeconds(60)), ct);
            }
            catch (System.ComponentModel.Win32Exception e)
            {
                return Audit(packetId, started, "refused", none, null, envNote + $"could not start '{launch.FileName}': {e.Message}", null, null);
            }
            var cliVersion = Regex.Match(versionRun.Stdout, @"\b\d+\.\d+\.\d+\b") is { Success: true } m ? m.Value : null;
            if (versionRun.ExitCode != 0 || cliVersion is null)
                return Audit(packetId, started, "refused", none, null, envNote + "could not determine the installed Claude Code version", null, null);

            var status = await processes.RunAsync(launch.Spec(["auth", "status"], env, workDir, null, TimeSpan.FromSeconds(60)), ct);
            var verdict = status.TimedOut
                ? new AuthVerdict(false, null, null, ["`claude auth status` timed out"])
                : BillingGuard.Evaluate(status.Stdout, env);
            if (!verdict.Allowed)
                return Audit(packetId, started, "refused", verdict, cliVersion, envNote + string.Join("; ", verdict.Reasons), null, null);

            var args = BuildArguments();
            if (args.Any(a => BillingGuard.ForbiddenArguments.Contains(a)))
                throw new InvalidOperationException("Refusing to run Claude with a billing-bypass argument.");

            var run = await processes.RunAsync(launch.Spec(args, env, workDir, packet.Json, _o.Timeout), ct);
            if (run.TimedOut) return Audit(packetId, started, "failed", verdict, cliVersion, envNote + $"claude -p exceeded the {_o.Timeout} timeout", run.Stdout, null);

            string structured;
            string? observedModel = null;
            try
            {
                using var doc = JsonDocument.Parse(run.Stdout);
                var root = doc.RootElement;
                if (root.TryGetProperty("is_error", out var err) && err.ValueKind == JsonValueKind.True)
                {
                    var message = root.TryGetProperty("result", out var res) && res.ValueKind == JsonValueKind.String ? res.GetString() : null;
                    return Audit(packetId, started, "failed", verdict, cliVersion,
                        envNote + $"claude reported an error (exit {run.ExitCode}); research suspended, no retry: {Trim(message)}", run.Stdout, null);
                }
                if (root.TryGetProperty("modelUsage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                    observedModel = string.Join(",", usage.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
                if (!root.TryGetProperty("structured_output", out var so) || so.ValueKind != JsonValueKind.Object)
                    return Audit(packetId, started, "failed", verdict, cliVersion, envNote + "no structured_output in claude result (declined or schema not met)", run.Stdout, null);
                // The record must answer the packet that was run; web content must not redirect it to another market.
                string? Str(string k) => so.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                if (Str("packetId") != packet.PacketId || Str("packetHash") != packet.PacketHash || Str("marketTicker") != packet.MarketTicker)
                    return Audit(packetId, started, "failed", verdict, cliVersion,
                        envNote + $"record answers packet '{Str("packetId")}' / '{Str("marketTicker")}', not the packet that was run", run.Stdout, null);
                structured = so.GetRawText();
            }
            catch (JsonException)
            {
                return Audit(packetId, started, "failed", verdict, cliVersion, envNote + $"claude output was not JSON (exit {run.ExitCode})", run.Stdout, null);
            }

            try
            {
                var imported = new ResearchImporter(db, time).Import(structured, "claude-subscription-cli", cliVersion, observedModel);
                return Audit(packetId, started, "imported", verdict, cliVersion,
                    envNote + (imported.AlreadyImported ? "identical record already imported" : "imported as EXPERIMENTAL research (unvalidated)"),
                    run.Stdout, imported);
            }
            catch (ResearchRejectedException e)
            {
                return Audit(packetId, started, "failed", verdict, cliVersion, envNote + e.Message, run.Stdout, null);
            }
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>User settings and managed settings files Claude Code reads on this OS. Missing files are skipped.</summary>
    internal IEnumerable<SettingsFile> SettingsFiles(IReadOnlyDictionary<string, string> env)
    {
        var home = env.GetValueOrDefault("HOME") ?? env.GetValueOrDefault("USERPROFILE");
        var configDir = env.GetValueOrDefault("CLAUDE_CONFIG_DIR") ?? (home is null ? null : Path.Combine(home, ".claude"));
        if (configDir is null)
        {
            yield return new SettingsFile("~/.claude/settings.json", null, "HOME is not set, so user settings cannot be checked");
            yield break;
        }
        var managed = _o.ManagedSettingsDir
                      ?? (OperatingSystem.IsMacOS() ? "/Library/Application Support/ClaudeCode"
                          : OperatingSystem.IsWindows() ? @"C:\Program Files\ClaudeCode"
                          : "/etc/claude-code");
        var candidates = new List<string> { Path.Combine(configDir, "settings.json"), Path.Combine(managed, "managed-settings.json") };
        var dropIns = Path.Combine(managed, "managed-settings.d");
        if (Directory.Exists(dropIns))
            candidates.AddRange(Directory.GetFiles(dropIns, "*.json").Where(f => !Path.GetFileName(f).StartsWith('.')).Order(StringComparer.Ordinal));
        foreach (var path in candidates.Where(File.Exists))
        {
            string? content = null, error = null;
            try { content = File.ReadAllText(path); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { error = e.Message; }
            yield return new SettingsFile(path, content, error);
        }
    }

    private static bool InsideGitRepository(string dir)
    {
        for (var d = new DirectoryInfo(dir); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, ".git")) || File.Exists(Path.Combine(d.FullName, ".git"))) return true;
        return false;
    }

    /// <summary>
    /// Flags verified against Claude Code 2.1.330 `--help` and code.claude.com/docs/en/headless. 2.1.330 has no
    /// max-turns flag, so the bound is the process timeout plus a web-only tool set. The prompt comes first so variadic
    /// options cannot swallow it; the packet arrives on stdin. No --bare (it ignores subscription OAuth).
    /// </summary>
    internal List<string> BuildArguments()
    {
        var args = new List<string>
        {
            "-p", "Review the Kalshi market in the JSON packet on stdin. Follow its \"task\" field exactly and return the research record.",
            "--output-format", "json",
            "--json-schema", ResearchContract.PortableSchemaJson,
            "--no-session-persistence",
            "--strict-mcp-config",
            "--disable-slash-commands",
            "--permission-mode", "dontAsk",
        };
        if (_o.Model is { } m) args.AddRange(["--model", m]);
        args.AddRange(["--tools", "WebSearch,WebFetch", "--allowedTools", "WebSearch,WebFetch"]);
        return args;
    }

    private static string Trim(string? s) => s is null ? "(no message)" : s.Length <= 300 ? s : s[..300] + "…";

    private ClaudeRunResult Audit(string packetId, DateTimeOffset started, string outcome, AuthVerdict verdict, string? cliVersion,
        string detail, string? stdout, ImportOutcome? imported)
    {
        var id = db.Insert("""
            INSERT INTO research_runs (packet_id, started_at, finished_at, outcome, auth_method, subscription_type, cli_version, detail,
                stdout_sha256, stdout, research_record_id)
            VALUES ($p, $s, $f, $o, $am, $st, $cv, $d, $sh, $out, $rr)
            """,
            ("$p", packetId), ("$s", Ts.ToText(started)), ("$f", Ts.ToText(time.GetUtcNow())), ("$o", outcome),
            ("$am", verdict.AuthMethod), ("$st", verdict.SubscriptionType), ("$cv", cliVersion), ("$d", detail),
            ("$sh", stdout is null ? null : Hash.Sha256Hex(stdout)), ("$out", stdout), ("$rr", imported?.RecordId));
        return new ClaudeRunResult(id, outcome, verdict, imported, detail);
    }
}
