using System.Text.Json;

namespace KalshiBot.Core.Research;

public sealed record AuthVerdict(bool Allowed, string? AuthMethod, string? SubscriptionType, IReadOnlyList<string> Reasons);

/// <summary>
/// Keeps every Claude call on the user's Claude subscription. Two layers, both fail-closed:
/// 1. The child environment is rebuilt without any variable that can switch Claude Code to API-key, gateway,
///    or cloud-provider billing (verified precedence: code.claude.com/docs/en/authentication, Nov 2026).
/// 2. `claude auth status` (JSON) run in that environment must report first-party subscription auth.
///    Settings-file credentials such as apiKeyHelper cannot be removed by env scrubbing; layer 2 catches them.
/// There is no override flag. This system makes no direct model API calls anywhere.
/// </summary>
public static class BillingGuard
{
    /// <summary>
    /// Removed from the child env. ANTHROPIC_* covers ANTHROPIC_API_KEY, ANTHROPIC_AUTH_TOKEN, ANTHROPIC_BASE_URL,
    /// ANTHROPIC_PROFILE and the federation variables. The others select a non-subscription provider.
    /// </summary>
    private static readonly string[] RemovedPrefixes = ["ANTHROPIC_", "CLAUDE_CODE_USE_", "OPENROUTER_", "OPENAI_", "AWS_BEARER_TOKEN_BEDROCK"];

    public static Dictionary<string, string> SanitizedEnvironment(IReadOnlyDictionary<string, string> parent, out List<string> removed)
    {
        removed = [];
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in parent)
        {
            if (RemovedPrefixes.Any(p => k.StartsWith(p, StringComparison.OrdinalIgnoreCase))) { removed.Add(k); continue; }
            env[k] = v;
        }
        removed.Sort(StringComparer.Ordinal);
        return env;
    }

    /// <summary>
    /// Accepts only:
    /// - authMethod "claude.ai" with a subscriptionType (Pro/Max/Team/Enterprise/etc. claude.ai login), or
    /// - authMethod "oauth_token" when CLAUDE_CODE_OAUTH_TOKEN (a `claude setup-token` subscription token) is the
    ///   only token source left in the sanitized environment.
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
                if (!childEnv.ContainsKey("CLAUDE_CODE_OAUTH_TOKEN"))
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
