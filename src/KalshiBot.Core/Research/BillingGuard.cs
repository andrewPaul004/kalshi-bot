using System.Text.Json;

namespace KalshiBot.Core.Research;

public sealed record AuthVerdict(bool Allowed, string? AuthMethod, string? SubscriptionType, IReadOnlyList<string> Reasons);

public sealed record SettingsFile(string Path, string? Content, string? ReadError = null);

/// <summary>
/// Keeps every Claude call on the user's Claude subscription. Three layers, all fail-closed, no override flag:
/// 1. The child environment is built from an allowlist (system basics, proxies/CA, CLAUDE_CONFIG_DIR and
///    CLAUDE_CODE_OAUTH_TOKEN). Nothing that can switch Claude Code to API-key, gateway or cloud-provider billing
///    survives (precedence verified: code.claude.com/docs/en/authentication, Nov 2026).
/// 2. Settings files Claude Code would load (user + managed) must not set apiKeyHelper or a billing-relevant `env`
///    variable; env scrubbing cannot reach those, and `claude auth status` does not reveal a base-URL gateway.
/// 3. `claude auth status` (JSON) run in that environment must report first-party subscription auth.
/// This system makes no direct model API calls anywhere.
/// </summary>
public static class BillingGuard
{
    private static readonly HashSet<string> AllowedEnv = new(StringComparer.OrdinalIgnoreCase)
    {
        "PATH", "HOME", "USER", "LOGNAME", "SHELL", "LANG", "LANGUAGE", "TERM", "TZ", "TMPDIR", "TMP", "TEMP",
        "XDG_CONFIG_HOME", "XDG_DATA_HOME", "XDG_CACHE_HOME", "XDG_STATE_HOME", "XDG_RUNTIME_DIR",
        "HTTPS_PROXY", "HTTP_PROXY", "NO_PROXY", "ALL_PROXY", "SSL_CERT_FILE", "SSL_CERT_DIR", "NODE_EXTRA_CA_CERTS",
        "USERPROFILE", "APPDATA", "LOCALAPPDATA", "SYSTEMROOT", "WINDIR", "COMSPEC", "PATHEXT", "PROGRAMDATA",
        "PROGRAMFILES", "HOMEDRIVE", "HOMEPATH", "USERNAME", "COMPUTERNAME",
        "SYSTEMDRIVE", "OS", "PROCESSOR_ARCHITECTURE", "NUMBER_OF_PROCESSORS", "PUBLIC", "ALLUSERSPROFILE",
        "COMMONPROGRAMFILES", "PROGRAMFILES(X86)", "PROGRAMW6432",
        "CLAUDE_CONFIG_DIR", "CLAUDE_CODE_OAUTH_TOKEN",
        "CLAUDE_CODE_GIT_BASH_PATH", // Windows: where Claude Code finds Git Bash; not a credential or endpoint
    };

    /// <summary>Builds the child environment from the allowlist. Everything else is dropped and reported.</summary>
    public static Dictionary<string, string> SanitizedEnvironment(IReadOnlyDictionary<string, string> parent, out List<string> removed)
    {
        removed = [];
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in parent)
        {
            if (AllowedEnv.Contains(k) || k.StartsWith("LC_", StringComparison.Ordinal)) env[k] = v;
            else removed.Add(k);
        }
        removed.Sort(StringComparer.Ordinal);
        return env;
    }

    /// <summary>Variables that select a credential, endpoint, or provider. Model-name variables (*_MODEL) are harmless.</summary>
    public static bool IsBillingSwitch(string name) =>
        (name.StartsWith("ANTHROPIC_", StringComparison.OrdinalIgnoreCase) && !name.EndsWith("_MODEL", StringComparison.OrdinalIgnoreCase))
        || name.StartsWith("CLAUDE_CODE_USE_", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("OPENROUTER_", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("OPENAI_", StringComparison.OrdinalIgnoreCase)
        || name.Equals("AWS_BEARER_TOKEN_BEDROCK", StringComparison.OrdinalIgnoreCase);

    /// <summary>Refuses settings that could route around the subscription. Unreadable or invalid files also refuse.</summary>
    public static List<string> InspectSettings(IEnumerable<SettingsFile> files)
    {
        var reasons = new List<string>();
        foreach (var f in files)
        {
            if (f.ReadError is not null) { reasons.Add($"cannot read {f.Path} ({f.ReadError}); it may configure credentials"); continue; }
            JsonElement root;
            try
            {
                using var doc = JsonDocument.Parse(f.Content ?? "");
                root = doc.RootElement.Clone();
            }
            catch (JsonException) { reasons.Add($"{f.Path} is not valid JSON; cannot verify it sets no credentials"); continue; }
            if (root.ValueKind != JsonValueKind.Object) { reasons.Add($"{f.Path} is not a JSON object"); continue; }

            if (root.TryGetProperty("apiKeyHelper", out var helper) && helper.ValueKind != JsonValueKind.Null)
                reasons.Add($"{f.Path} sets apiKeyHelper (API-key billing)");
            if (root.TryGetProperty("env", out var env) && env.ValueKind == JsonValueKind.Object)
                foreach (var p in env.EnumerateObject().Where(p => IsBillingSwitch(p.Name)))
                    reasons.Add($"{f.Path} env sets {p.Name}, which can switch Claude Code off the subscription");
        }
        return reasons;
    }

    /// <summary>
    /// Accepts only:
    /// - authMethod "claude.ai" with a subscriptionType (Pro/Max/Team/Enterprise/etc. claude.ai login), or
    /// - authMethod "oauth_token" when a non-empty CLAUDE_CODE_OAUTH_TOKEN (a `claude setup-token` subscription token)
    ///   is the only token source left in the allowlisted environment and settings files (checked separately).
    /// and always apiProvider "firstParty", loggedIn true, and no apiKeySource present.
    /// authMethod values verified against Claude Code 2.1.330: none, third_party, claude.ai, api_key_helper, oauth_token, api_key.
    /// </summary>
    public static AuthVerdict Evaluate(string authStatusJson, IReadOnlyDictionary<string, string> childEnv)
    {
        var reasons = new List<string>();
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(authStatusJson);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return new AuthVerdict(false, null, null, ["`claude auth status` did not return JSON; cannot verify billing route"]);
        }
        if (root.ValueKind != JsonValueKind.Object)
            return new AuthVerdict(false, null, null, ["`claude auth status` returned non-object JSON"]);

        var method = Str(root, "authMethod");
        var provider = Str(root, "apiProvider");
        var subscription = Str(root, "subscriptionType");

        if (!(root.TryGetProperty("loggedIn", out var li) && li.ValueKind == JsonValueKind.True))
            reasons.Add("not logged in (run `claude auth login` with your Claude subscription account)");
        if (provider != "firstParty")
            reasons.Add($"apiProvider is '{provider ?? "missing"}', not 'firstParty' (cloud provider or gateway billing)");
        if (root.TryGetProperty("apiKeySource", out var aks) && aks.ValueKind != JsonValueKind.Null)
            reasons.Add($"an API key source is configured ({aks}); remove it so API billing cannot apply");

        switch (method)
        {
            case "claude.ai":
                if (string.IsNullOrEmpty(subscription)) reasons.Add("claude.ai login reports no subscriptionType");
                break;
            case "oauth_token":
                if (!childEnv.TryGetValue("CLAUDE_CODE_OAUTH_TOKEN", out var token) || string.IsNullOrWhiteSpace(token))
                    reasons.Add("authMethod 'oauth_token' but CLAUDE_CODE_OAUTH_TOKEN is not set; token source cannot be verified as a subscription token");
                break;
            default:
                reasons.Add($"authMethod '{method ?? "missing"}' is not a Claude subscription login");
                break;
        }
        return new AuthVerdict(reasons.Count == 0, method, subscription, reasons);
    }

    /// <summary>Arguments that would bypass subscription auth. --bare never reads OAuth credentials.</summary>
    public static IReadOnlyList<string> ForbiddenArguments { get; } = ["--bare", "--api-key", "--settings"];

    private static string? Str(JsonElement o, string key) =>
        o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
