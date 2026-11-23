using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KalshiBot.Core.Common;
using KalshiBot.Core.Storage;

namespace KalshiBot.Core.Research;

public sealed record ImportOutcome(long RecordId, string ResearchId, string PacketId, string MarketTicker, string Disposition,
    decimal? ExperimentalProbability, bool AlreadyImported);

public sealed class ResearchRejectedException(IReadOnlyList<string> errors)
    : Exception("Research record rejected: " + string.Join("; ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>
/// Strict, fail-closed import of research records (spec 4.5, AC-04). Structure is validated against the embedded
/// schemas/research-record.schema.json (unknown keys, so no action/order/limit fields, can exist); then linkage to the
/// exported packet, dates, evidence and uniqueness are checked here. Every reason is reported. An accepted record
/// is evidence for review; its optional probability goes to experimental_probabilities, which no trading code reads.
/// </summary>
public sealed class ResearchImporter(Db db, TimeProvider time)
{
    public static readonly string[] SourceKinds = ["claude-subscription-cli", "manual", "synthetic-fixture"];

    public ImportOutcome Import(string json, string sourceKind, string? observedCliVersion = null, string? observedModel = null)
    {
        if (!SourceKinds.Contains(sourceKind)) throw new ResearchRejectedException([$"unknown source kind '{sourceKind}'"]);
        if (Encoding.UTF8.GetByteCount(json) > ResearchContract.MaxRecordBytes)
            throw new ResearchRejectedException([$"record exceeds {ResearchContract.MaxRecordBytes} bytes"]);

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 10,
            });
        }
        catch (JsonException e) { throw new ResearchRejectedException([$"not valid JSON: {e.Message}"]); }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new ResearchRejectedException(["root must be a JSON object"]);
            var errors = new List<string>();
            JsonSchemaSubset.Validate(root, ResearchContract.Schema, "$", errors);
            if (errors.Count > 0) throw new ResearchRejectedException(errors);

            string S(string k) => root.GetProperty(k).GetString()!;
            string? N(JsonElement o, string k) => o.GetProperty(k).ValueKind == JsonValueKind.Null ? null : o.GetProperty(k).GetString();
            var now = time.GetUtcNow();
            var researchId = S("researchId");
            var packetId = S("packetId");
            var disposition = S("disposition");
            var created = Utc(S("createdAtUtc"));
            var validUntil = Utc(S("validUntilUtc"));

            // Exact input linkage (fabricated or swapped tickers are rejected).
            var packet = new ResearchStore(db).Packet(packetId);
            if (packet is null) errors.Add($"packet {packetId} is unknown (export it first)");
            else
            {
                if (S("packetHash") != packet.PacketHash) errors.Add("packetHash does not match the exported packet");
                if (S("marketTicker") != packet.MarketTicker) errors.Add($"marketTicker does not match packet ({packet.MarketTicker})");
                if (S("eventTicker") != packet.EventTicker) errors.Add($"eventTicker does not match packet ({packet.EventTicker})");
                if (N(root, "seriesTicker") != packet.SeriesTicker) errors.Add($"seriesTicker does not match packet ({packet.SeriesTicker ?? "null"})");
                if (S("marketRulesHash") != packet.RulesSha256) errors.Add("marketRulesHash does not match the packet's rules version");
                if (root.GetProperty("researcherVersion").GetProperty("promptVersion").GetString() != packet.PromptVersion)
                    errors.Add($"researcherVersion.promptVersion must be {packet.PromptVersion}");
                if (created < packet.CreatedAt - ResearchContract.ClockSkew) errors.Add("createdAtUtc is before the packet existed");
                // A made-up fixture record must never attach to real market data.
                if (sourceKind == "synthetic-fixture" && !DataOrigin.IsSynthetic(packet.DataOrigin))
                    errors.Add("synthetic-fixture records may only answer packets built from synthetic data");
            }

            // Lifetime.
            if (created > now + ResearchContract.ClockSkew) errors.Add("createdAtUtc is in the future");
            if (validUntil <= created) errors.Add("validUntilUtc must be after createdAtUtc");
            if (validUntil - created > ResearchContract.MaxLifetime) errors.Add($"validUntilUtc may be at most {ResearchContract.MaxLifetime.TotalDays} days after createdAtUtc");
            if (validUntil <= now) errors.Add("record has expired (validUntilUtc is not in the future)");

            // Evidence.
            var evidence = root.GetProperty("evidence");
            if (disposition != "insufficient_evidence" && evidence.GetArrayLength() == 0)
                errors.Add("evidence is required unless disposition is insufficient_evidence");
            var i = 0;
            foreach (var e in evidence.EnumerateArray())
            {
                var at = $"evidence[{i++}]";
                var url = e.GetProperty("url").GetString()!;
                if (!(Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "https" or "http")) errors.Add($"{at}.url must be an absolute http(s) URL");
                var retrieved = Utc(e.GetProperty("retrievedAtUtc").GetString()!);
                if (retrieved > created + ResearchContract.ClockSkew) errors.Add($"{at}.retrievedAtUtc is after the record was created");
                if (N(e, "publishedAtUtc") is { } pub && Utc(pub) > retrieved + ResearchContract.ClockSkew) errors.Add($"{at}.publishedAtUtc is after it was retrieved");
            }

            // Experimental probability: plain decimal, at most 4 places.
            decimal? probability = null;
            var ep = root.GetProperty("experimentalProbabilityYes");
            if (ep.ValueKind == JsonValueKind.Object)
            {
                var v = ep.GetProperty("value").GetDecimal();
                if (v.Scale > 4 && decimal.Round(v, 4) != v) errors.Add("experimentalProbabilityYes.value may have at most 4 decimal places");
                else probability = v;
            }

            if (errors.Count > 0) throw new ResearchRejectedException(errors);

            var canonical = CanonicalJson.Serialize(JsonNode.Parse(json));
            var sha = Hash.Sha256Hex(canonical);
            var existing = db.QuerySingle("SELECT id, record_sha256 FROM research_records WHERE research_id = $r",
                r => new { Id = r.L("id"), Sha = r.S("record_sha256") }, ("$r", researchId));
            if (existing is not null)
            {
                if (existing.Sha != sha) throw new ResearchRejectedException([$"researchId '{researchId}' already exists with different content"]);
                return new ImportOutcome(existing.Id, researchId, packetId, S("marketTicker"), disposition, probability, true);
            }

            var rv = root.GetProperty("researcherVersion");
            var id = db.InTransaction(() =>
            {
                var recordId = db.Insert("""
                    INSERT INTO research_records (research_id, packet_id, record_sha256, market_ticker, disposition, created_at_utc,
                        valid_until_utc, source_kind, prompt_version, reported_cli, reported_model, observed_cli_version,
                        observed_model, imported_at, record_json)
                    VALUES ($rid, $p, $h, $t, $d, $c, $v, $sk, $pv, $rc, $rm, $oc, $om, $at, $j)
                    """,
                    ("$rid", researchId), ("$p", packetId), ("$h", sha), ("$t", S("marketTicker")), ("$d", disposition),
                    ("$c", Ts.ToText(created)), ("$v", Ts.ToText(validUntil)), ("$sk", sourceKind),
                    ("$pv", rv.GetProperty("promptVersion").GetString()), ("$rc", N(rv, "cli")), ("$rm", N(rv, "model")),
                    ("$oc", observedCliVersion), ("$om", observedModel), ("$at", Ts.ToText(now)), ("$j", canonical));
                if (probability is { } pv)
                    db.Exec("""
                        INSERT INTO experimental_probabilities (research_record_id, probability_yes, method, uncertainty, validation_status)
                        VALUES ($id, $p, $m, $u, 'unvalidated')
                        """, ("$id", recordId), ("$p", Dec.ToText(pv)), ("$m", ep.GetProperty("method").GetString()),
                        ("$u", ep.GetProperty("uncertainty").GetString()));
                return recordId;
            });
            return new ImportOutcome(id, researchId, packetId, S("marketTicker"), disposition, probability, false);
        }
    }

    private static DateTimeOffset Utc(string s) =>
        DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
