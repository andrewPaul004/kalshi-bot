using System.Text.Json.Nodes;
using KalshiBot.Core.Common;
using KalshiBot.Core.Research;
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

        Assert.Equal(a.PacketId, b.PacketId);
        Assert.Equal(a.Json, b.Json);
        Assert.True(b.AlreadyExisted);

        var p = JsonNode.Parse(a.Json)!;
        Assert.Equal(ResearchContract.PacketSchema, (string)p["schema"]!);
        Assert.Equal(a.PacketId, (string)p["packet_id"]!);
        Assert.Equal(DataOrigin.Synthetic, (string)p["data_origin"]!);
        Assert.Contains("SYNTHETIC", (string)p["synthetic_warning"]!);
        Assert.StartsWith("SYNTHETIC FIXTURE", (string)p["rules"]!["rules_primary"]!);
        Assert.Equal(4, p["sources"]!.AsArray().Count); // market, orderbook, event, series
        Assert.All(p["sources"]!.AsArray(), s => Assert.Matches("^[0-9a-f]{64}$", (string)s!["body_sha256"]!));

        // Prices are left out so a research estimate cannot anchor on them.
        Assert.DoesNotContain("yes_bid", a.Json);
        Assert.DoesNotContain("0.4500", a.Json);

        // The id is the hash of the body without packet_id.
        var body = p.DeepClone().AsObject();
        body.Remove("packet_id");
        Assert.Equal(a.PacketId, CanonicalJson.HashOf(body));
    }

    [Fact]
    public void New_snapshot_gives_a_new_packet()
    {
        using var t = new TempDb();
        var clock = FixedClock.Default();
        Fx.Record(t.Db, clock, Fx.Open, Fx.T50);
        var a = new PacketExporter(t.Db, clock).Export(Fx.T50);
        clock.Advance(TimeSpan.FromMinutes(1));
        Fx.Record(t.Db, clock, Fx.Open, Fx.T50);
        var b = new PacketExporter(t.Db, clock).Export(Fx.T50);
        Assert.NotEqual(a.PacketId, b.PacketId);
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
        Assert.Throws<ResultRejectedException>(() => new ResultImporter(t.Db, clock).Import(json, source)).Errors.ToList();

    [Fact]
    public void Valid_result_is_stored_as_unvalidated_and_reimport_is_idempotent()
    {
        var (t, clock, p) = Setup();
        using var _ = t;
        var json = Fx.ValidResult(p, clock).ToJsonString();
        var first = new ResultImporter(t.Db, clock).Import(json, "manual");
        var again = new ResultImporter(t.Db, clock).Import(json, "manual");
        Assert.False(first.AlreadyImported);
        Assert.True(again.AlreadyImported);
        Assert.Equal(first.ResultId, again.ResultId);
        Assert.Equal(0.6m, first.ProbabilityYes);
        Assert.Equal("unvalidated", t.Db.Scalar<string>("SELECT validation_status FROM research_results"));
        Assert.Equal(1, t.Db.Scalar<long>("SELECT COUNT(*) FROM research_results"));
    }

    [Fact]
    public void Schema_forbids_marking_a_result_validated()
    {
        var (t, clock, p) = Setup();
        using var _ = t;
        new ResultImporter(t.Db, clock).Import(Fx.ValidResult(p, clock).ToJsonString(), "manual");
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() =>
            t.Db.Exec("UPDATE research_results SET validation_status = 'validated'"));
    }

    [Theory]
    [InlineData("extra", "unknown key 'extra'")]
    public void Unknown_keys_are_rejected(string key, string expected)
    {
        var (t, clock, p) = Setup();
        using var _ = t;
        var r = Fx.ValidResult(p, clock);
        r[key] = "x";
        Assert.Contains(expected, Reject(t, clock, r.ToJsonString()));
    }

    [Fact]
    public void Each_missing_key_is_reported()
    {
        var (t, clock, p) = Setup();
        using var _ = t;
        var r = Fx.ValidResult(p, clock);
        r.Remove("confidence");
        r.Remove("sources");
        var errors = Reject(t, clock, r.ToJsonString());
        Assert.Contains("missing key 'confidence'", errors);
        Assert.Contains("missing key 'sources'", errors);
    }

    [Fact]
    public void Duplicate_keys_comments_and_trailing_commas_are_rejected()
    {
        var (t, clock, p) = Setup();
        using var _ = t;
        var json = Fx.ValidResult(p, clock).ToJsonString();
        Assert.Contains("duplicate key 'confidence'", Reject(t, clock, json.Replace("\"confidence\":\"low\"", "\"confidence\":\"low\",\"confidence\":\"high\"")));
        Assert.Contains(Reject(t, clock, "/* c */" + json), e => e.StartsWith("not valid JSON"));
        Assert.Contains(Reject(t, clock, json[..^1] + ",}"), e => e.StartsWith("not valid JSON"));
    }

    [Theory]
    [InlineData("\"0.6\"", "probability_yes must be a number or null")]
    [InlineData("1.5", "probability_yes must be within [0, 1]")]
    [InlineData("-0.1", "probability_yes must be within [0, 1]")]
    [InlineData("0.123456", "probability_yes may have at most 4 decimal places")]
    [InlineData("true", "probability_yes must be a number or null")]
    public void Probability_must_be_a_bounded_number(string raw, string expected)
    {
        var (t, clock, p) = Setup();
        using var _ = t;
        var json = Fx.ValidResult(p, clock).ToJsonString().Replace("\"probability_yes\":0.6", "\"probability_yes\":" + raw);
        Assert.Contains(expected, Reject(t, clock, json));
    }

    [Fact]
    public void Abstention_requires_a_reason_and_yields_no_estimate()
    {
        var (t, clock, p) = Setup();
        using var _ = t;
        var noReason = Fx.ValidResult(p, clock, prob: null);
        noReason["abstain_reason"] = null;
        Assert.Contains("abstain_reason is required when probability_yes is null", Reject(t, clock, noReason.ToJsonString()));

        var both = Fx.ValidResult(p, clock);
        both["abstain_reason"] = "but also a number";
        Assert.Contains("abstain_reason must be null when probability_yes is given", Reject(t, clock, both.ToJsonString()));

        // A valid estimate followed by an abstention: the older estimate must not be used.
        new ResultImporter(t.Db, clock).Import(Fx.ValidResult(p, clock).ToJsonString(), "manual");
        Assert.NotNull(new ResearchStore(t.Db).LatestEstimate(Fx.T50));
        clock.Advance(TimeSpan.FromMinutes(1));
        new ResultImporter(t.Db, clock).Import(Fx.ValidResult(p, clock, prob: null).ToJsonString(), "manual");
        Assert.Null(new ResearchStore(t.Db).LatestEstimate(Fx.T50));
    }

    [Fact]
    public void Result_must_match_its_packet_ticker_and_rules()
    {
        var (t, clock, p) = Setup();
        using var _ = t;
        var wrongTicker = Fx.ValidResult(p, clock);
        wrongTicker["ticker"] = Fx.T60;
        Assert.Contains(Reject(t, clock, wrongTicker.ToJsonString()), e => e.Contains("does not match packet ticker"));

        var wrongRules = Fx.ValidResult(p, clock);
        wrongRules["rules_sha256"] = new string('a', 64);
        Assert.Contains("rules_sha256 does not match the packet's rules version", Reject(t, clock, wrongRules.ToJsonString()));

        var unknownPacket = Fx.ValidResult(p, clock);
        unknownPacket["packet_id"] = new string('b', 64);
        Assert.Contains(Reject(t, clock, unknownPacket.ToJsonString()), e => e.Contains("is unknown"));
    }

    [Fact]
    public void Field_level_validation_covers_urls_timestamps_and_enums()
    {
        var (t, clock, p) = Setup();
        using var _ = t;
        var r = Fx.ValidResult(p, clock);
        r["confidence"] = "certain";
        r["as_of"] = "yesterday";
        r["sources"]![0]!["url"] = "javascript:alert(1)";
        r["sources"]![0]!["retrieved_at"] = "2026-11-23";
        r["key_factors"] = new JsonArray(42);
        var errors = Reject(t, clock, r.ToJsonString());
        Assert.Contains("confidence must be low, medium or high", errors);
        Assert.Contains("as_of must be an ISO 8601 timestamp with offset", errors);
        Assert.Contains("sources[0].url must be an absolute http(s) URL", errors);
        Assert.Contains("sources[0].retrieved_at must be an ISO 8601 timestamp with offset", errors);
        Assert.Contains(errors, e => e.StartsWith("key_factors[0]"));
    }

    [Fact]
    public void Oversized_and_non_object_inputs_are_rejected()
    {
        var (t, clock, _) = Setup();
        using var __ = t;
        Assert.Contains(Reject(t, clock, new string(' ', ResearchContract.MaxResultBytes + 1)), e => e.Contains("exceeds"));
        Assert.Contains("root must be a JSON object", Reject(t, clock, "[1,2]"));
    }

    [Fact]
    public void Synthetic_fixture_results_cannot_attach_to_live_packets()
    {
        using var t = new TempDb();
        var clock = FixedClock.Default();
        Fx.Record(t.Db, clock, Fx.Open, Fx.T50);
        // Relabel the recorded data as live to simulate a live packet.
        t.Db.Exec("UPDATE market_snapshots SET data_origin = 'live:production'");
        var live = new PacketExporter(t.Db, clock).Export(Fx.T50);
        Assert.Equal(DataOrigin.LiveProduction, live.DataOrigin);
        var errors = Reject(t, clock, Fx.ValidResult(live, clock).ToJsonString(), "synthetic-fixture");
        Assert.Contains("synthetic-fixture results may only answer packets built from synthetic data", errors);
    }
}
