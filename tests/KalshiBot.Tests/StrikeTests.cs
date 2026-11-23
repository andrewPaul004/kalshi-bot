using KalshiBot.Core.Kalshi;
using KalshiBot.Core.Recording;
using KalshiBot.Core.Storage;
using Xunit;

namespace KalshiBot.Tests;

/// <summary>
/// Bucket edges for threshold markets (docs/PHASE3_PLAN.md V3/W1). The expected values come from real KXHIGHNY markets
/// recorded 2026-11-23 and were cross-checked against 4,280 settled markets' results.
/// </summary>
public class StrikeTests
{
    // KXHIGHNY-26NOV24: the six markets of one real event.
    private static readonly (string Type, decimal? Floor, decimal? Cap)[] RealEvent =
    [
        ("less", null, 49m),       // T49    "48° or below"
        ("between", 49m, 50m),     // B49.5
        ("between", 51m, 52m),     // B51.5
        ("between", 53m, 54m),     // B53.5
        ("between", 55m, 56m),     // B55.5
        ("greater", 56m, null),    // T56    "57° or above"
    ];

    [Fact]
    public void Edges_match_the_sub_titles_Kalshi_publishes()
    {
        var less = OutcomeBucket.FromStrikes("less", null, 49m)!;
        Assert.True(less.Contains(48)); Assert.False(less.Contains(49)); // "48° or below"

        var between = OutcomeBucket.FromStrikes("between", 49m, 50m)!;
        Assert.False(between.Contains(48)); Assert.True(between.Contains(49));
        Assert.True(between.Contains(50)); Assert.False(between.Contains(51)); // "49° to 50°", both ends inclusive

        var greater = OutcomeBucket.FromStrikes("greater", 56m, null)!;
        Assert.False(greater.Contains(56)); Assert.True(greater.Contains(57)); // "57° or above"
    }

    [Fact]
    public void The_six_markets_of_an_event_cover_every_whole_degree_exactly_once()
    {
        var buckets = RealEvent.Select(e => OutcomeBucket.FromStrikes(e.Type, e.Floor, e.Cap)!).ToList();
        for (var deg = -20; deg <= 130; deg++)
            Assert.Equal(1, buckets.Count(b => b.Contains(deg)));
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("", null, 49.0)]
    [InlineData("custom", null, 49.0)]
    [InlineData("between", 49.0, null)]    // missing cap
    [InlineData("between", null, 50.0)]    // missing floor
    [InlineData("between", 51.0, 50.0)]    // floor above cap
    [InlineData("between", 49.5, 50.0)]    // not whole degrees
    [InlineData("less", null, null)]
    [InlineData("less", 40.0, 49.0)]       // unexpected extra field
    [InlineData("less", null, 48.5)]
    [InlineData("greater", null, null)]
    [InlineData("greater", 56.0, 60.0)]    // unexpected extra field
    public void Anything_unrecognised_or_inconsistent_is_unknown(string? type, double? floor, double? cap) =>
        Assert.Null(OutcomeBucket.FromStrikes(type, (decimal?)floor, (decimal?)cap));

    [Theory]
    // Weather Company wording (from 2026-08-14).
    [InlineData("If the maximum temperature recorded at New York City (CLINYC) for Nov 24, 2026, is less than 49° fahrenheit according to The Weather Company, then the market resolves to Yes.", null, 48)]
    [InlineData("If the maximum temperature recorded at New York City (CLINYC) for Nov 24, 2026, is between 49-50° fahrenheit according to The Weather Company, then the market resolves to Yes.", 49, 50)]
    [InlineData("If the maximum temperature recorded at New York City (CLINYC) for Nov 24, 2026, is greater than 56° fahrenheit according to The Weather Company, then the market resolves to Yes.", 57, null)]
    // NWS wording (2022-03 to 2026-08).
    [InlineData("If the highest temperature recorded in Central Park, New York for February 09, 2025 as reported by the National Weather Service's Daily Climate Report, is between 35-36°, then the market resolves to Yes.", 35, 36)]
    [InlineData("If the highest temperature recorded in Central Park, New York for July 31, 2026 as reported by the National Weather Service's Climatological Report (Daily), is greater than 89°, then the market resolves to Yes.", 90, null)]
    // 2021 wording.
    [InlineData("If the highest temperature recorded in Central Park, New York on August 7, 2021 is strictly greater than 85°F, then the market resolves to Yes.", 86, null)]
    [InlineData("If the highest temperature recorded in Central Park, New York for March 03, 2022, is between 85 and 86°, then the market resolves to Yes.", 85, 86)]
    [InlineData("If the highest temperature recorded in Central Park, New York on August 7, 2021 is less than 60°F, then the market resolves to Yes.", null, 59)]
    public void Rules_text_gives_the_same_edges_for_markets_without_strike_fields(string rules, int? lower, int? upper)
    {
        var b = OutcomeBucket.FromRules(rules)!;
        Assert.NotNull(b);
        Assert.Equal(lower, b.LowerInclusive);
        Assert.Equal(upper, b.UpperInclusive);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("SYNTHETIC FIXTURE: nothing to parse here.")]
    [InlineData("If the high is above 85°F, then the market resolves to Yes.")]                       // wording not validated
    [InlineData("If the high is between 85 and 86 (exactly 85), then the market resolves to Yes.")]       // odd variant
    [InlineData("If the high is greater than 85 and less than 90°, then the market resolves to Yes.")] // ambiguous
    public void Unrecognised_rules_text_is_unknown(string? rules) => Assert.Null(OutcomeBucket.FromRules(rules));

    [Fact]
    public void A_present_but_unusable_strike_type_does_not_fall_back_to_the_rules_text()
    {
        const string rules = "If the high, is between 49-50°, then the market resolves to Yes.";
        Assert.Null(OutcomeBucket.Resolve("custom", null, null, rules));
        Assert.NotNull(OutcomeBucket.Resolve(null, null, null, rules));
        Assert.Equal(new OutcomeBucket(51, null), OutcomeBucket.Resolve("greater", 50m, null, rules)); // structured wins
    }

    [Fact]
    public void Recorder_stores_the_strike_fields_and_the_snapshot_exposes_the_bucket()
    {
        using var t = new TempDb();
        Fx.Record(t.Db, FixedClock.Default(), Fx.Open);

        var s = new MarketStore(t.Db).Latest(Fx.T50)!; // synthetic fixture: strike_type greater, floor_strike 50
        Assert.Equal("greater", s.StrikeType);
        Assert.Equal(50m, s.FloorStrike);
        Assert.Null(s.CapStrike);
        Assert.Equal(new OutcomeBucket(51, null), s.Bucket);
    }

    [Fact]
    public void Upgrading_a_version_5_database_backfills_strikes_from_the_stored_raw_bodies()
    {
        var dir = Directory.CreateTempSubdirectory("kb-test-").FullName;
        var path = Path.Combine(dir, "v5.db");
        try
        {
            using (var old = Db.Open(path, maxSchemaVersion: 5))
            {
                Assert.Equal(5, old.Scalar<long>("SELECT MAX(version) FROM schema_migrations"));
                Assert.DoesNotContain("strike_type", ColumnNames(old));

                old.Exec("INSERT INTO capture_runs (id, started_at, status, data_origin, base_url, app_version) VALUES (1, '2026-11-23T18:00:00Z', 'completed', 'production', 'x', 'test')");
                void Raw(int id, string body) => old.Exec(
                    "INSERT INTO raw_responses (id, capture_run_id, method, url, http_status, requested_at, received_at, body_sha256, body) VALUES ($i, 1, 'GET', 'u', 200, 't', 't', 'h', $b)",
                    ("$i", id), ("$b", body));
                Raw(1, """{"market":{"ticker":"KXHIGHNY-26NOV24-B49.5","strike_type":"between","floor_strike":49,"cap_strike":50}}""");
                Raw(2, """{"market":{"ticker":"KXHIGHNY-25FEB09-B35.5"}}"""); // pre-strike era: no fields
                Raw(3, """{"market":{"ticker":"KXHIGHNY-26NOV24-T49","strike_type":"less","cap_strike":49}}""");
                old.Exec("INSERT INTO market_rules (id, ticker, rules_sha256, rules_primary, rules_secondary, first_seen_at, last_seen_at, first_raw_id, last_raw_id) VALUES (1, 'T', 'h', 'p', 's', 't', 't', 1, 1)");
                foreach (var (id, raw) in new[] { (1, 1), (2, 2), (3, 3) })
                    old.Exec("INSERT INTO market_snapshots (id, capture_run_id, raw_market_id, rules_id, data_origin, observed_at, ticker, event_ticker, market_type, status) VALUES ($i, 1, $r, 1, 'production', '2026-11-23T18:00:00.0000000Z', 'T', 'E', 'binary', 'active')",
                        ("$i", id), ("$r", raw));
            }

            using var db = Db.Open(path);
            Assert.Equal(Schema.LatestVersion, (int)db.Scalar<long>("SELECT MAX(version) FROM schema_migrations"));
            var store = new MarketStore(db);
            Assert.Equal(new OutcomeBucket(49, 50), store.ById(1)!.Bucket);
            Assert.Null(store.ById(2)!.StrikeType);
            Assert.Null(store.ById(2)!.Bucket);
            Assert.Equal(new OutcomeBucket(null, 48), store.ById(3)!.Bucket);
            Assert.Equal(3, db.Scalar<long>("SELECT COUNT(*) FROM raw_responses")); // raw history untouched
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    private static List<string> ColumnNames(Db db) => db.Query("PRAGMA table_info(market_snapshots)", r => r.S("name"));
}
