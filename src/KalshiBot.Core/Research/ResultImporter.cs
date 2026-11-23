using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using KalshiBot.Core.Common;
using KalshiBot.Core.Storage;

namespace KalshiBot.Core.Research;

public sealed record ImportOutcome(long ResultId, string PacketId, string Ticker, decimal? ProbabilityYes, bool AlreadyImported);

public sealed class ResultRejectedException(IReadOnlyList<string> errors)
    : Exception("Research result rejected: " + string.Join("; ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>
/// Strict, fail-closed import of research results. Anything not exactly matching research-result/v1 is rejected
/// with every reason listed: unknown or duplicate keys, wrong JSON types, out-of-range values, mismatched packet,
/// ticker or rules hash. Accepted results are stored as unvalidated and labeled experimental.
/// </summary>
public sealed partial class ResultImporter(Db db, TimeProvider time)
{
    public static readonly string[] SourceKinds = ["claude-subscription-cli", "manual", "synthetic-fixture"];

    private static readonly string[] TopKeys =
        ["schema", "packet_id", "ticker", "rules_sha256", "probability_yes", "abstain_reason", "confidence",
         "as_of", "summary", "key_factors", "resolution_risks", "sources"];
    private static readonly string[] SourceKeys = ["title", "url", "retrieved_at"];

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Hex64();

    public ImportOutcome Import(string json, string sourceKind)
    {
        if (!SourceKinds.Contains(sourceKind)) throw new ResultRejectedException([$"unknown source kind '{sourceKind}'"]);
        var errors = new List<string>();
        if (Encoding.UTF8.GetByteCount(json) > ResearchContract.MaxResultBytes)
            throw new ResultRejectedException([$"result exceeds {ResearchContract.MaxResultBytes} bytes"]);

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8,
            });
        }
        catch (JsonException e) { throw new ResultRejectedException([$"not valid JSON: {e.Message}"]); }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new ResultRejectedException(["root must be a JSON object"]);
            CheckKeys(root, TopKeys, "", errors);

            var schema = Str(root, "schema", errors, 1, 64);
            if (schema is not null && schema != ResearchContract.ResultSchema) errors.Add($"schema must be '{ResearchContract.ResultSchema}'");
            var packetId = Str(root, "packet_id", errors, 64, 64);
            if (packetId is not null && !Hex64().IsMatch(packetId)) errors.Add("packet_id must be 64 lowercase hex chars");
            var ticker = Str(root, "ticker", errors, 1, 128);
            var rulesSha = Str(root, "rules_sha256", errors, 64, 64);
            if (rulesSha is not null && !Hex64().IsMatch(rulesSha)) errors.Add("rules_sha256 must be 64 lowercase hex chars");

            decimal? probability = null;
            if (root.TryGetProperty("probability_yes", out var pEl))
            {
                if (pEl.ValueKind == JsonValueKind.Number)
                {
                    if (!pEl.TryGetDecimal(out var p)) errors.Add("probability_yes is not a representable decimal");
                    else if (p < 0m || p > 1m) errors.Add("probability_yes must be within [0, 1]");
                    else if (p.Scale > 4 && decimal.Round(p, 4) != p) errors.Add("probability_yes may have at most 4 decimal places");
                    else probability = p;
                }
                else if (pEl.ValueKind != JsonValueKind.Null) errors.Add("probability_yes must be a number or null");
            }

            string? abstain = null;
            if (root.TryGetProperty("abstain_reason", out var aEl))
            {
                if (aEl.ValueKind == JsonValueKind.String) abstain = Str(root, "abstain_reason", errors, 1, 1000);
                else if (aEl.ValueKind != JsonValueKind.Null) errors.Add("abstain_reason must be a string or null");
            }
            var pIsNull = root.TryGetProperty("probability_yes", out var pv) && pv.ValueKind == JsonValueKind.Null;
            if (pIsNull && abstain is null) errors.Add("abstain_reason is required when probability_yes is null");
            if (!pIsNull && root.TryGetProperty("abstain_reason", out var av) && av.ValueKind != JsonValueKind.Null)
                errors.Add("abstain_reason must be null when probability_yes is given");

            var confidence = Str(root, "confidence", errors, 1, 16);
            if (confidence is not null && confidence is not ("low" or "medium" or "high")) errors.Add("confidence must be low, medium or high");
            var asOf = Str(root, "as_of", errors, 1, 64);
            if (asOf is not null && !IsTimestamp(asOf)) errors.Add("as_of must be an ISO 8601 timestamp with offset");
            if (asOf is not null && IsTimestamp(asOf) && DateTimeOffset.Parse(asOf, CultureInfo.InvariantCulture) > time.GetUtcNow().AddMinutes(5))
                errors.Add("as_of is in the future");
            Str(root, "summary", errors, 1, 4000);
            StrArray(root, "key_factors", errors, 10, 500);
            StrArray(root, "resolution_risks", errors, 10, 500);

            if (root.TryGetProperty("sources", out var sEl))
            {
                if (sEl.ValueKind != JsonValueKind.Array) errors.Add("sources must be an array");
                else if (sEl.GetArrayLength() > 20) errors.Add("sources may have at most 20 items");
                else
                {
                    var i = 0;
                    foreach (var src in sEl.EnumerateArray())
                    {
                        var at = $"sources[{i++}]";
                        if (src.ValueKind != JsonValueKind.Object) { errors.Add($"{at} must be an object"); continue; }
                        CheckKeys(src, SourceKeys, at + ".", errors);
                        Str(src, "title", errors, 1, 300, at + ".");
                        var url = Str(src, "url", errors, 1, 2000, at + ".");
                        if (url is not null && !(Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "https" or "http"))
                            errors.Add($"{at}.url must be an absolute http(s) URL");
                        var ra = Str(src, "retrieved_at", errors, 1, 64, at + ".");
                        if (ra is not null && !IsTimestamp(ra)) errors.Add($"{at}.retrieved_at must be an ISO 8601 timestamp with offset");
                    }
                }
            }

            // Cross-checks against the packet this result claims to answer.
            StoredPacket? packet = null;
            if (packetId is not null && Hex64().IsMatch(packetId))
            {
                packet = new ResearchStore(db).Packet(packetId);
                if (packet is null) errors.Add($"packet {packetId} is unknown (export it first)");
            }
            if (packet is not null)
            {
                if (ticker is not null && ticker != packet.Ticker) errors.Add($"ticker '{ticker}' does not match packet ticker '{packet.Ticker}'");
                if (rulesSha is not null && rulesSha != packet.RulesSha256) errors.Add("rules_sha256 does not match the packet's rules version");
                // A made-up fixture estimate must never attach to live market data.
                if (sourceKind == "synthetic-fixture" && !DataOrigin.IsSynthetic(packet.DataOrigin))
                    errors.Add("synthetic-fixture results may only answer packets built from synthetic data");
            }

            if (errors.Count > 0) throw new ResultRejectedException(errors);

            var canonical = CanonicalJson.Serialize(JsonNode.Parse(json));
            var sha = Hash.Sha256Hex(canonical);
            var existing = db.Scalar<long?>("SELECT id FROM research_results WHERE result_sha256 = $h", ("$h", sha));
            if (existing is { } eid) return new ImportOutcome(eid, packetId!, ticker!, probability, true);

            var id = db.Insert("""
                INSERT INTO research_results (packet_id, result_sha256, ticker, rules_sha256, probability_yes, abstain_reason,
                    confidence, source_kind, validation_status, imported_at, result_json)
                VALUES ($p, $h, $t, $r, $prob, $ab, $c, $sk, 'unvalidated', $at, $j)
                """,
                ("$p", packetId), ("$h", sha), ("$t", ticker), ("$r", rulesSha),
                ("$prob", probability is null ? null : Dec.ToText(probability.Value)), ("$ab", abstain), ("$c", confidence),
                ("$sk", sourceKind), ("$at", Ts.ToText(time.GetUtcNow())), ("$j", canonical));
            return new ImportOutcome(id, packetId!, ticker!, probability, false);
        }
    }

    private static void CheckKeys(JsonElement obj, string[] allowed, string prefix, List<string> errors)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in obj.EnumerateObject())
        {
            if (!seen.Add(p.Name)) errors.Add($"duplicate key '{prefix}{p.Name}'");
            if (!allowed.Contains(p.Name)) errors.Add($"unknown key '{prefix}{p.Name}'");
        }
        foreach (var k in allowed.Where(k => !seen.Contains(k))) errors.Add($"missing key '{prefix}{k}'");
    }

    private static string? Str(JsonElement obj, string key, List<string> errors, int min, int max, string prefix = "")
    {
        if (!obj.TryGetProperty(key, out var el)) return null; // reported by CheckKeys
        if (el.ValueKind != JsonValueKind.String) { errors.Add($"{prefix}{key} must be a string"); return null; }
        var s = el.GetString()!;
        if (s.Length < min || s.Length > max) { errors.Add($"{prefix}{key} length must be {min}..{max}"); return null; }
        if (s.Any(char.IsControl) && key is not ("summary" or "abstain_reason")) { errors.Add($"{prefix}{key} contains control characters"); return null; }
        return s;
    }

    private static void StrArray(JsonElement obj, string key, List<string> errors, int maxItems, int maxLen)
    {
        if (!obj.TryGetProperty(key, out var el)) return;
        if (el.ValueKind != JsonValueKind.Array) { errors.Add($"{key} must be an array"); return; }
        if (el.GetArrayLength() > maxItems) errors.Add($"{key} may have at most {maxItems} items");
        var i = 0;
        foreach (var item in el.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString()!.Length is 0 || item.GetString()!.Length > maxLen)
                errors.Add($"{key}[{i}] must be a non-empty string of at most {maxLen} chars");
            i++;
        }
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,9})?(Z|[+-]\d{2}:\d{2})$")]
    private static partial Regex Rfc3339();

    private static bool IsTimestamp(string s) =>
        Rfc3339().IsMatch(s) && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
}
