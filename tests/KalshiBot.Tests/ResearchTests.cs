using System.Text.Json;
using System.Text.Json.Nodes;
using KalshiBot.Core.Common;
using KalshiBot.Core.Research;
using KalshiBot.Core.Status;
using Xunit;

namespace KalshiBot.Tests;

public class ResearchExportTests
{
    [Fact]
    public void Packet_is_deterministic_self_describing_and_excludes_quotes()
    {
        using var t = new TempDb();
        var clock = FixedClock.Default();
        Fx.Record(t.Db, clock, Fx.Open);
        var a = new PacketExporter(t.Db, clock).Export(Fx.T50);
        clock.Advance(TimeSpan.FromHours(1));
        var b = new PacketExporter(t.Db, clock).Export(Fx.T50);

        Assert.Equal(a.Json, b.Json);
        Assert.True(b.AlreadyExisted);
        Assert.Matches("^pkt_[0-9]+_[0-9a-f]{12}$", a.PacketId);

        var p = JsonNode.Parse(a.Json)!;
        Assert.Equal(ResearchContract.PacketSchema, (string)p["schemaVersion"]!);
        Assert.Equal(ResearchContract.PromptVersion, (string)p["promptVersion"]!);
        Assert.Equal(a.PacketId, (string)p["packetId"]!);
        Assert.Equal(DataOrigin.Synthetic, (string)p["dataOrigin"]!);
        Assert.Contains("SYNTHETIC", (string)p["syntheticWarning"]!);
        Assert.Equal("KXSYNTHWX", (string)p["market"]!["seriesTicker"]!);
        Assert.Equal(4, p["sources"]!.AsArray().Count);
        Assert.All(p["sources"]!.AsArray(), s => Assert.Matches("^[0-9a-f]{64}$", (string)s!["bodySha256"]!));
        Assert.Equal(ResearchContract.RecordSchema, (string)p["resultContract"]!["schemaVersion"]!);

        // Same bytes on every OS: no CRLF from a Windows checkout of the source.
        Assert.DoesNotContain('\r', a.Json);
        Assert.DoesNotContain('\r', ResearchContract.Task);

        // Prices are left out so research cannot anchor on them.
        Assert.DoesNotContain("yes_bid", a.Json);
        Assert.DoesNotContain("0.4500", a.Json);

        // packetHash is the hash of the body without packetId/packetHash.
        var body = p.DeepClone().AsObject();
        body.Remove("packetId");
        body.Remove("packetHash");
        Assert.Equal(a.PacketHash, CanonicalJson.HashOf(body));
    }

    [Fact]
    public void Schema_file_uses_only_the_validated_subset_and_matches_the_contract_version()
    {
        // Loading succeeds only if every keyword is supported (fail closed on drift).
        Assert.Equal(JsonValueKind.Object, ResearchContract.Schema.ValueKind);
        var portable = JsonNode.Parse(ResearchContract.PortableSchemaJson)!.ToJsonString();
        foreach (var limit in new[] { "maxLength", "maxItems", "pattern", "minimum", "maximum" })
            Assert.DoesNotContain($"\"{limit}\"", portable);
        Assert.Contains("\"additionalProperties\":false", portable);

        using var bad = JsonDocument.Parse("""{"type":"object","properties":{"x":{"type":"string","format":"email"}}}""");
        Assert.Throws<InvalidOperationException>(() => JsonSchemaSubset.EnsureSupported(bad.RootElement));
    }
}

public class ResearchImportTests
{
    private static (TempDb T, FixedClock Clock, ExportedPacket Packet) Setup()
    {
        var t = new TempDb();
        var clock = FixedClock.Default();
        Fx.Record(t.Db, clock, Fx.Open);
        return (t, clock, new PacketExporter(t.Db, clock).Export(Fx.T50));
    }

    private static List<string> Reject(TempDb t, FixedClock clock, string json, string source = "manual") =>
        Assert.Throws<ResearchRejectedException>(() => new ResearchImporter(t.Db, clock).Import(json, source)).Errors.ToList();

    [Fact]
    public void Valid_record_is_stored_with_its_probability_in_a_separate_unvalidated_table()
    {
        var (t, clock, p) = Setup();
        using var _ = t;
        var json = Fx.ValidRecord(p, clock).ToJsonString();
        var first = new ResearchImporter(t.Db, clock).Import(json, "manual");
        var again = new ResearchImporter(t.Db, clock).Import(json, "manual");
        Assert.False(first.AlreadyImported);
        Assert.True(again.AlreadyImported);
        Assert.Equal(0.6m, first.ExperimentalProbability);
        Assert.Equal("0.6", t.Db.Scalar<string>("SELECT probability_yes FROM experimental_probabilities"));
        Assert.Equal("unvalidated", t.Db.Scalar<string>("SELECT validation_status FROM experimental_probabilities"));
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => t.Db.Exec("UPDATE experimental_probabilities SET validation_status = 'validated'"));
    }

    [Fact]
    public void Reusing_a_research_id_for_different_content_is_rejected()
    {
        var (t, clock, p) = Setup();
        using var _ = t;
        new ResearchImporter(t.Db, clock).Import(Fx.ValidRecord(p, clock).ToJsonString(), "manual");
        var changed = Fx.ValidRecord(p, clock, 0.4m);
        Assert.Contains("researchId 'r-1' already exists with different content", Reject(t, clock, changed.ToJsonString()));
    }

    [Theory] // AC-04: action commands, live flags, order sizes, risk overrides cannot exist as fields.
    [InlineData("orderSize")]
    [InlineData("action")]
    [InlineData("live")]
    [InlineData("riskLimitOverride")]
    [InlineData("command")]
    public void Unknown_fields_are_rejected(string key)
    {
        var (t, clock, p) = Setup();
        using var _ = t;
        var r = Fx.ValidRecord(p, clock);
        r[key] = "x";
        Assert.Contains($"unknown key '$.{key}'", Reject(t, clock, r.ToJsonString()));
    }

    [Fact]
    public void Nested_unknown_fields_and_missing_keys_are_rejected()
    {
        var (t, clock, p) = Setup();
        using var _ = t;
        var r = Fx.ValidRecord(p, clock);
        r["experimentalProbabilityYes"]!["size"] = 5;
        r.Remove("disposition");
        var errors = Reject(t, clock, r.ToJsonString());
        Assert.Contains(errors, e => e.Contains("size"));
        Assert.Contains("missing key '$.disposition'", errors);
    }

    [Fact]
    public void Duplicate_keys_comments_and_trailing_commas_are_rejected()
    {
        var (t, clock, p) = Setup();
        using var _ = t;
        var json = Fx.ValidRecord(p, clock).ToJsonString();
        Assert.Contains("duplicate key '$.disposition'", Reject(t, clock, json.Replace("\"disposition\":\"no_issue_found\"", "\"disposition\":\"no_issue_found\",\"disposition\":\"needs_review\"")));
        Assert.Contains(Reject(t, clock, "/* c */" + json), e => e.StartsWith("not valid JSON"));
        Assert.Contains(Reject(t, clock, json[..^1] + ",}"), e => e.StartsWith("not valid JSON"));
        Assert.Contains(Reject(t, clock, new string(' ', ResearchContract.MaxRecordBytes + 1)), e => e.Contains("exceeds"));
    }

    [Theory] // malformed numbers
    [InlineData("\"0.6\"")]
    [InlineData("1.5")]
    [InlineData("-0.1")]
    [InlineData("0.123456")]
    [InlineData("true")]
    public void Experimental_probability_must_be_a_bounded_plain_number(string raw)
    {
        var (t, clock, p) = Setup();
        using var _ = t;
        var json = Fx.ValidRecord(p, clock).ToJsonString().Replace("\"value\":0.6", "\"value\":" + raw);
        Assert.NotEmpty(Reject(t, clock, json));
    }

    [Fact]
    public void Linkage_to_the_exported_packet_is_exact()
    {
        var (t, clock, p) = Setup();
        using var _ = t;
        var r = Fx.ValidRecord(p, clock);
        r["marketTicker"] = "KXFAKE-26JAN01";
        r["eventTicker"] = "KXFAKE";
        r["seriesTicker"] = null;
        r["marketRulesHash"] = new string('a', 64);
        r["packetHash"] = new string('b', 64);
        r["researcherVersion"]!["promptVersion"] = "other";
        var errors = Reject(t, clock, r.ToJsonString());
        Assert.Contains(errors, e => e.StartsWith("marketTicker does not match"));
        Assert.Contains(errors, e => e.StartsWith("eventTicker does not match"));
        Assert.Contains(errors, e => e.StartsWith("seriesTicker does not match"));
        Assert.Contains("marketRulesHash does not match the packet's rules version", errors);
        Assert.Contains("packetHash does not match the exported packet", errors);
        Assert.Contains(errors, e => e.StartsWith("researcherVersion.promptVersion must be"));

        var unknown = Fx.ValidRecord(p, clock);
        unknown["packetId"] = "pkt_999_000000000000";
        Assert.Contains(Reject(t, clock, unknown.ToJsonString()), e => e.Contains("is unknown"));
    }

    [Fact]
    public void Impossible_and_expired_dates_are_rejected()
    {
        var (t, clock, p) = Setup();
        using var _ = t;
        var future = Fx.ValidRecord(p, clock);
        future["createdAtUtc"] = Ts.ToText(clock.Now.AddHours(1));
        Assert.Contains("createdAtUtc is in the future", Reject(t, clock, future.ToJsonString()));

        var tooLong = Fx.ValidRecord(p, clock);
        tooLong["validUntilUtc"] = Ts.ToText(clock.Now.AddDays(30));
        Assert.Contains(Reject(t, clock, tooLong.ToJsonString()), e => e.StartsWith("validUntilUtc may be at most"));

        var beforePacket = Fx.ValidRecord(p, clock);
        beforePacket["createdAtUtc"] = Ts.ToText(clock.Now.AddDays(-1));
        Assert.Contains("createdAtUtc is before the packet existed", Reject(t, clock, beforePacket.ToJsonString()));

        var r = Fx.ValidRecord(p, clock);
        clock.Advance(TimeSpan.FromDays(3)); // validUntil was +2 days
        Assert.Contains("record has expired (validUntilUtc is not in the future)", Reject(t, clock, r.ToJsonString()));

        var offset = Fx.ValidRecord(p, clock).ToJsonString().Replace("Z\",\"validUntilUtc", "+00:00\",\"validUntilUtc");
        Assert.Contains(Reject(t, clock, offset), e => e.Contains("createdAtUtc"));
    }

    [Fact]
    public void Evidence_is_required_and_checked_unless_insufficient()
    {
        var (t, clock, p) = Setup();
        using var _ = t;
        var none = Fx.ValidRecord(p, clock);
        none["evidence"] = new JsonArray();
        Assert.Contains("evidence is required unless disposition is insufficient_evidence", Reject(t, clock, none.ToJsonString()));
        none["disposition"] = "insufficient_evidence";
        Assert.False(new ResearchImporter(t.Db, clock).Import(none.ToJsonString(), "manual").AlreadyImported);

        var bad = Fx.ValidRecord(p, clock, researchId: "r-2");
        bad["evidence"]![0]!["url"] = "javascript:alert(1)";
        bad["evidence"]![0]!["retrievedAtUtc"] = Ts.ToText(clock.Now.AddHours(1));
        bad["evidence"]![0]!["dataHash"] = "not-a-hash";
        var errors = Reject(t, clock, bad.ToJsonString());
        Assert.Contains(errors, e => e.Contains("dataHash"));
    }

    [Fact]
    public void Evidence_semantics_are_checked_after_structure()
    {
        var (t, clock, p) = Setup();
        using var _ = t;
        var bad = Fx.ValidRecord(p, clock);
        bad["evidence"]![0]!["url"] = "javascript:alert(1)";
        bad["evidence"]![0]!["retrievedAtUtc"] = Ts.ToText(clock.Now.AddHours(1));
        var errors = Reject(t, clock, bad.ToJsonString());
        Assert.Contains("evidence[0].url must be an absolute http(s) URL", errors);
        Assert.Contains("evidence[0].retrievedAtUtc is after the record was created", errors);
    }

    [Fact]
    public void Synthetic_fixture_records_cannot_attach_to_live_packets()
    {
        using var t = new TempDb();
        var clock = FixedClock.Default();
        Fx.Record(t.Db, clock, Fx.Open, Fx.T50);
        t.Db.Exec("UPDATE market_snapshots SET data_origin = 'live:production'");
        var live = new PacketExporter(t.Db, clock).Export(Fx.T50);
        Assert.Contains("synthetic-fixture records may only answer packets built from synthetic data",
            Reject(t, clock, Fx.ValidRecord(live, clock).ToJsonString(), "synthetic-fixture"));
    }
}

public class ResearchScoringTests
{
    [Fact]
    public void Settled_records_are_scored_against_outcome_and_same_time_market_mid()
    {
        using var t = new TempDb();
        FixtureReplay.Run(t.Db, new ManualClock(FixtureReplay.Start), Fx.Root, _ => { });
        var score = ResearchScoring.Score(t.Db);
        var r = Assert.Single(score.Scored);
        // Replay: SYNTHETIC record p=0.99 on T50 (packet snapshot yes 0.40/0.45), T50 settles yes.
        Assert.Equal(1, r.Outcome);
        Assert.Equal(0.0001m, r.Brier);
        Assert.Equal(0.425m, r.MarketMid);
        Assert.Equal(0.330625m, r.MarketBrier);
        Assert.Equal(1, score.IndependentEvents);
        Assert.Equal(0, score.Unresolved);
    }

    [Fact]
    public void Unsettled_and_probability_free_records_are_counted_not_scored()
    {
        using var t = new TempDb();
        var clock = FixedClock.Default();
        Fx.Record(t.Db, clock, Fx.Open);
        var p = new PacketExporter(t.Db, clock).Export(Fx.T50);
        new ResearchImporter(t.Db, clock).Import(Fx.ValidRecord(p, clock, 0.6m).ToJsonString(), "manual");
        new ResearchImporter(t.Db, clock).Import(Fx.ValidRecord(p, clock, prob: null, researchId: "r-2").ToJsonString(), "manual");
        var score = ResearchScoring.Score(t.Db);
        Assert.Equal((2, 1, 1), (score.Records, score.WithProbability, score.Unresolved));
        Assert.Empty(score.Scored);
        Assert.Null(score.MeanBrier);
    }
}
