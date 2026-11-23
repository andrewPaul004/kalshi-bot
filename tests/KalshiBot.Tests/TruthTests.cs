using System.Net;
using System.Text.Json.Nodes;
using KalshiBot.Core.Common;
using KalshiBot.Core.Kalshi;
using KalshiBot.Core.Recording;
using KalshiBot.Core.Storage;
using KalshiBot.Core.Truth;
using Microsoft.Data.Sqlite;
using Xunit;

namespace KalshiBot.Tests;

/// <summary>Truth data for Phase 3 (docs/PHASE3_PLAN.md W2): settled values, the NWS climate-report cross-check, and their rules.</summary>
public class TruthTests
{
    private const string WeatherCompany = "If the maximum temperature recorded at New York City (CLINYC) for Nov 24, 2026, is between 49-50 fahrenheit according to The Weather Company, then the market resolves to Yes.";
    private const string NwsClimatological = "If the highest temperature recorded in Central Park, New York for July 31, 2026 as reported by the National Weather Service's Climatological Report (Daily), is greater than 89, then the market resolves to Yes.";
    private const string NwsDaily = "If the highest temperature recorded in Central Park, New York for February 09, 2025 as reported by the National Weather Service's Daily Climate Report, is between 35-36, then the market resolves to Yes.";
    private const string Unnamed = "If the highest temperature recorded in Central Park, New York on August 7, 2021 is strictly greater than 85, then the market resolves to Yes.";

    // The six markets of a real KXHIGHNY event (KXHIGHNY-26NOV24).
    private static readonly (string Suffix, string Type, int? Floor, int? Cap)[] Six =
    [
        ("T49", "less", null, 49), ("B49.5", "between", 49, 50), ("B51.5", "between", 51, 52),
        ("B53.5", "between", 53, 54), ("B55.5", "between", 55, 56), ("T56", "greater", 56, null),
    ];

    private static JsonObject Mk(string ticker, string ev, string? type, int? floor, int? cap, string result, string? value, string rules)
    {
        var o = new JsonObject
        {
            ["ticker"] = ticker, ["event_ticker"] = ev, ["status"] = "finalized", ["result"] = result,
            ["expiration_value"] = value ?? "", ["rules_primary"] = rules,
        };
        if (type is not null) o["strike_type"] = type;
        if (floor is not null) o["floor_strike"] = floor;
        if (cap is not null) o["cap_strike"] = cap;
        return o;
    }

    /// <summary>All six markets of an event settled on <paramref name="value"/>. Fresh nodes on every call.</summary>
    internal static List<JsonObject> Event(string ev, int value, string rules = WeatherCompany, Func<string, bool>? carriesValue = null, string? flip = null) =>
        Six.Select(s =>
        {
            var yes = OutcomeBucket.FromStrikes(s.Type, s.Floor, s.Cap)!.Contains(value);
            if (s.Suffix == flip) yes = !yes;
            var shown = carriesValue is null || carriesValue(s.Suffix) ? value.ToString() + ".00" : null;
            return Mk($"{ev}-{s.Suffix}", ev, s.Type, s.Floor, s.Cap, yes ? "yes" : "no", shown, rules);
        }).ToList();

    internal static string Page(IEnumerable<JsonObject> markets, string cursor = "") =>
        new JsonObject { ["markets"] = new JsonArray(markets.Select(m => (JsonNode)m).ToArray()), ["cursor"] = cursor }.ToJsonString();

    private static IReadOnlyList<(SettledMarket, long)> Parsed(IEnumerable<JsonObject> markets) =>
        TruthParse.SettledMarkets(Page(markets)).Markets.Select(m => (m, 1L)).ToList();

    // ---- pure parsing -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("KXHIGHNY-26NOV24", 2026, 11, 24)]
    [InlineData("HIGHNY-21AUG07", 2021, 8, 7)]
    [InlineData("KXHIGHNY-25FEB09", 2025, 2, 9)]
    public void Event_dates_come_from_the_event_ticker(string ticker, int y, int m, int d) =>
        Assert.Equal(new DateOnly(y, m, d), TruthParse.EventDate(ticker));

    [Theory]
    [InlineData("KXHIGHNY")]
    [InlineData("KXHIGHNY-26XXX24")]
    [InlineData("KXHIGHNY-26FEB30")]
    [InlineData("KXHIGHNY-26NOV24-B49.5")]
    public void Anything_else_has_no_date(string ticker) => Assert.Null(TruthParse.EventDate(ticker));

    [Fact]
    public void The_regime_is_read_from_the_source_the_rules_text_names()
    {
        Assert.Equal(SettlementRegime.WeatherCompany, SettlementRegime.Classify(WeatherCompany));
        Assert.Equal(SettlementRegime.NwsClimatologicalReport, SettlementRegime.Classify(NwsClimatological));
        Assert.Equal(SettlementRegime.NwsDailyClimateReport, SettlementRegime.Classify(NwsDaily));
        Assert.Equal(SettlementRegime.Unspecified, SettlementRegime.Classify(Unnamed));
        Assert.Equal(SettlementRegime.Unspecified, SettlementRegime.Classify(null));
    }

    [Fact]
    public void An_event_reduces_to_one_value_even_when_only_some_markets_state_it()
    {
        // Real data: finalized markets of one event often leave expiration_value empty.
        var markets = Event("KXHIGHNY-26NOV24", 52, carriesValue: s => s == "B51.5");
        var t = TruthParse.Aggregate("KXHIGHNY", "KXHIGHNY-26NOV24", Parsed(markets))!;
        Assert.Equal(52, t.Value);
        Assert.Equal(TruthStatus.Ok, t.Status);
        Assert.Equal(new DateOnly(2026, 11, 24), t.ClimateDate);
        Assert.Equal(6, t.MarketsSeen);
        Assert.Equal(6, t.MarketsChecked);   // every market's result was checked against the value
        Assert.Equal(0, t.MarketsInconsistent);
        Assert.Equal(SettlementRegime.WeatherCompany, t.Regime);
    }

    [Fact]
    public void Two_different_values_in_one_event_is_a_conflict_with_no_value()
    {
        var markets = Event("KXHIGHNY-26NOV24", 52);
        markets[1]["expiration_value"] = "53.00";
        var t = TruthParse.Aggregate("KXHIGHNY", "KXHIGHNY-26NOV24", Parsed(markets))!;
        Assert.Equal(TruthStatus.Conflict, t.Status);
        Assert.Null(t.Value);
    }

    [Theory]
    [InlineData(" 52")]      // real 2021 data: leading space
    [InlineData("52")]       // real 2021 data: no decimals
    [InlineData("52.00 ")]
    public void Real_value_formats_are_accepted(string raw)
    {
        var markets = Event("KXHIGHNY-26NOV24", 52, carriesValue: _ => false);
        markets[2]["expiration_value"] = raw;
        var t = TruthParse.Aggregate("KXHIGHNY", "KXHIGHNY-26NOV24", Parsed(markets))!;
        Assert.Equal((TruthStatus.Ok, 52), (t.Status, t.Value));
    }

    [Fact]
    public void A_value_that_is_not_a_plausible_whole_degree_is_a_conflict()
    {
        foreach (var bad in new[] { "52.5", "abc", "400.00" })
        {
            var markets = Event("KXHIGHNY-26NOV24", 52);
            markets[2]["expiration_value"] = bad;
            var t = TruthParse.Aggregate("KXHIGHNY", "KXHIGHNY-26NOV24", Parsed(markets))!;
            Assert.Equal(TruthStatus.Conflict, t.Status);
            Assert.Null(t.Value);
        }
    }

    [Fact]
    public void A_result_that_contradicts_the_value_under_the_strike_edges_flags_the_event()
    {
        var t = TruthParse.Aggregate("KXHIGHNY", "KXHIGHNY-26NOV24", Parsed(Event("KXHIGHNY-26NOV24", 52, flip: "B53.5")))!;
        Assert.Equal(TruthStatus.Inconsistent, t.Status);
        Assert.Equal(52, t.Value);            // kept, but flagged: scoring excludes it
        Assert.Equal(1, t.MarketsInconsistent);
    }

    [Fact]
    public void Events_with_no_settled_market_or_no_stated_value_have_no_truth()
    {
        var unsettled = Event("KXHIGHNY-26NOV24", 52);
        foreach (var m in unsettled) m["result"] = "";
        Assert.Null(TruthParse.Aggregate("KXHIGHNY", "KXHIGHNY-26NOV24", Parsed(unsettled)));

        var noValue = Event("KXHIGHNY-26NOV24", 52, carriesValue: _ => false);
        Assert.Null(TruthParse.Aggregate("KXHIGHNY", "KXHIGHNY-26NOV24", Parsed(noValue)));
    }

    [Fact]
    public void Markets_of_an_event_that_name_different_sources_are_marked_mixed()
    {
        var markets = Event("KXHIGHNY-26NOV24", 52);
        markets[0]["rules_primary"] = NwsClimatological;
        Assert.Equal(SettlementRegime.Mixed, TruthParse.Aggregate("KXHIGHNY", "KXHIGHNY-26NOV24", Parsed(markets))!.Regime);
    }

    [Fact]
    public void Climate_report_rows_without_a_whole_degree_high_are_skipped_and_a_bad_date_is_an_error()
    {
        const string body = """
            {"results":[
              {"station":"KNYC","valid":"2024-01-01","high":47,"product":"p1"},
              {"station":"KNYC","valid":"2024-01-02","high":null,"product":"p2"},
              {"station":"KNYC","valid":"2024-01-03","high":"M","product":"p3"},
              {"station":"KNYC","valid":"2024-01-04","high":47.5,"product":"p4"},
              {"station":"KNYC","valid":"2024-01-05","high":-3,"product":null}]}
            """;
        var rows = TruthParse.CliHighs(body);
        Assert.Equal([new CliHigh("KNYC", new DateOnly(2024, 1, 1), 47, "p1"), new CliHigh("KNYC", new DateOnly(2024, 1, 5), -3, null)], rows);
        Assert.Throws<FormatException>(() => TruthParse.CliHighs("""{"results":[{"station":"KNYC","valid":"yesterday","high":47}]}"""));
    }

    // ---- collectors ---------------------------------------------------------------------------------------------

    internal static KalshiPublicClient Client(HttpMessageHandler h, TimeProvider clock) =>
        new(new HttpClient(h), KalshiEndpoint.Production, clock, new KalshiClientOptions { MinInterval = TimeSpan.Zero, MaxRetries = 0 });

    internal static HttpResponseMessage Route(HttpRequestMessage r, Func<string> live, Func<string?, HttpResponseMessage> hist)
    {
        var path = r.RequestUri!.AbsolutePath;
        var q = System.Web.HttpUtility.ParseQueryString(r.RequestUri.Query);
        if (path.EndsWith("/historical/markets", StringComparison.Ordinal)) return hist(q["cursor"]);
        if (path.EndsWith("/markets", StringComparison.Ordinal) && q["status"] == "settled") return ScriptedHandler.Json(HttpStatusCode.OK, live());
        return ScriptedHandler.Json(HttpStatusCode.NotFound, "{}");
    }

    [Fact]
    public void Kalshi_collection_stores_pages_verbatim_dedupes_markets_and_appends_only_new_vintages()
    {
        using var t = new TempDb();
        var clock = FixedClock.Default();
        var nov22 = 61;
        string? firstLive = null;
        var handler = new ScriptedHandler(r => Route(r,
            live: () => firstLive = Page(Event("KXHIGHNY-26NOV22", nov22)),
            hist: cursor => ScriptedHandler.Json(HttpStatusCode.OK, cursor switch
            {
                null => Page(Event("KXHIGHNY-26NOV21", 55).Concat(Event("KXHIGHNY-26NOV22", nov22).Take(1)), cursor: "c2"), // one market also on the live list
                "c2" => Page(Event("KXHIGHNY-26NOV20", 52)),
                _ => "{}",
            })));
        var collector = new KalshiTruthCollector(t.Db, Client(handler, clock), clock);

        var first = collector.CollectAsync("KXHIGHNY").Sync();
        Assert.Equal("completed", first.Status);
        Assert.Equal(3, first.Pages);
        Assert.Equal(18, first.MarketsSeen);            // 3 events x 6; the duplicated market is counted once
        Assert.Equal(3, first.EventsWithValue);
        Assert.Equal(3, first.NewVintages);
        Assert.Equal(TruthOrigin.Database, DatabaseOrigin.Get(t.Db));
        Assert.All(handler.Requests, r => Assert.StartsWith("GET ", r));
        Assert.Contains(handler.Requests, r => r.Contains("/historical/markets") && r.Contains("cursor=c2"));

        // Raw body stored verbatim with its hash, and the derived row points at it.
        var body = t.Db.Scalar<string>("SELECT body FROM truth_raw WHERE id = 1")!;
        Assert.Equal(firstLive, body);
        Assert.Equal(Hash.Sha256Hex(body), t.Db.Scalar<string>("SELECT body_sha256 FROM truth_raw WHERE id = 1"));
        Assert.Equal(1, t.Db.Scalar<long>("SELECT raw_id FROM settled_values WHERE event_ticker = 'KXHIGHNY-26NOV22'"));

        // Same data again: nothing new is appended.
        var second = collector.CollectAsync("KXHIGHNY").Sync();
        Assert.Equal(0, second.NewVintages);
        Assert.Equal(3, second.Unchanged);
        Assert.Equal(3, t.Db.Scalar<long>("SELECT COUNT(*) FROM settled_values"));
        Assert.Equal(6, t.Db.Scalar<long>("SELECT COUNT(*) FROM truth_raw"));

        // A revised value is kept beside the original.
        nov22 = 62;
        var third = collector.CollectAsync("KXHIGHNY").Sync();
        Assert.Equal(1, third.NewVintages);
        Assert.Equal(["61", "62"], t.Db.Query("SELECT expiration_value FROM settled_values WHERE event_ticker = 'KXHIGHNY-26NOV22' ORDER BY id", r => r.S("expiration_value")));
        Assert.Equal(62, TruthStore.CurrentSettled(t.Db).Single(s => s.EventTicker == "KXHIGHNY-26NOV22").Value);
    }

    [Fact]
    public void A_failing_historical_listing_leaves_the_live_events_stored_and_the_run_partial()
    {
        using var t = new TempDb();
        var clock = FixedClock.Default();
        var handler = new ScriptedHandler(r => Route(r,
            live: () => Page(Event("KXHIGHNY-26NOV22", 61)),
            hist: _ => new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("{}") }));
        var s = new KalshiTruthCollector(t.Db, Client(handler, clock), clock).CollectAsync("KXHIGHNY").Sync();

        Assert.Equal("partial", s.Status);
        Assert.Contains(s.Errors, e => e.Contains("historical") && e.Contains("500"));
        Assert.Equal(1, s.EventsWithValue);
        Assert.Equal("partial", t.Db.Scalar<string>("SELECT status FROM truth_runs WHERE id = $id", ("$id", s.RunId)));
    }

    [Fact]
    public void Truth_never_enters_a_recording_database_and_a_truth_database_refuses_recordings()
    {
        var clock = FixedClock.Default();
        var handler = new ScriptedHandler(r => Route(r, () => Page(Event("KXHIGHNY-26NOV22", 61)), _ => ScriptedHandler.Json(HttpStatusCode.OK, Page([]))));

        using var recording = new TempDb();
        Fx.Record(recording.Db, clock, Fx.Open); // bound to fixture:synthetic
        Assert.Throws<KalshiBotException>(() => new KalshiTruthCollector(recording.Db, Client(handler, clock), clock).CollectAsync("KXHIGHNY").Sync());
        Assert.Equal(0, recording.Db.Scalar<long>("SELECT COUNT(*) FROM settled_values"));

        using var truth = new TempDb();
        new KalshiTruthCollector(truth.Db, Client(handler, clock), clock).CollectAsync("KXHIGHNY").Sync();
        Assert.Throws<KalshiBotException>(() => Fx.Record(truth.Db, clock, Fx.Open));
    }

    [Fact]
    public void Truth_history_cannot_be_rewritten()
    {
        using var t = new TempDb();
        var clock = FixedClock.Default();
        var handler = new ScriptedHandler(r => Route(r, () => Page(Event("KXHIGHNY-26NOV22", 61)), _ => ScriptedHandler.Json(HttpStatusCode.OK, Page([]))));
        new KalshiTruthCollector(t.Db, Client(handler, clock), clock).CollectAsync("KXHIGHNY").Sync();
        new IemCliCollector(t.Db, new HttpClient(new ScriptedHandler(_ => ScriptedHandler.Json(HttpStatusCode.OK,
            """{"results":[{"station":"KNYC","valid":"2026-11-22","high":61,"product":"p"}]}"""))), clock, minInterval: TimeSpan.Zero)
            .CollectAsync("KNYC", 2026, 2026).Sync();

        foreach (var sql in new[]
        {
            "UPDATE settled_values SET expiration_value = '99'", "DELETE FROM settled_values",
            "UPDATE truth_raw SET body = 'x'", "DELETE FROM truth_raw",
            "UPDATE cli_highs SET high = '99'", "DELETE FROM cli_highs",
        })
        {
            // Name the trigger's message: a foreign-key error (raw rows are referenced) must not satisfy this.
            var ex = Assert.Throws<SqliteException>(() => t.Db.Exec(sql));
            Assert.Contains("append-only", ex.Message);
        }
    }

    [Fact]
    public void Climate_report_collection_appends_only_changed_vintages_and_ignores_other_stations()
    {
        using var t = new TempDb();
        var clock = FixedClock.Default();
        var high = 47;
        var handler = new ScriptedHandler(r =>
        {
            var year = System.Web.HttpUtility.ParseQueryString(r.RequestUri!.Query)["year"];
            return year == "2024"
                ? ScriptedHandler.Json(HttpStatusCode.OK, $$"""
                    {"results":[{"station":"KNYC","valid":"2024-01-01","high":{{high}},"product":"p1"},
                                {"station":"KJFK","valid":"2024-01-01","high":50,"product":"p9"}]}
                    """)
                : ScriptedHandler.Json(HttpStatusCode.OK, """{"results":[{"station":"KNYC","valid":"2025-01-01","high":40,"product":"p2"}]}""");
        });
        var collector = new IemCliCollector(t.Db, new HttpClient(handler), clock, minInterval: TimeSpan.Zero);

        var a = collector.CollectAsync("KNYC", 2024, 2025).Sync();
        Assert.Equal("completed", a.Status);
        Assert.Equal((2, 2, 2), (a.Years, a.Days, a.NewVintages)); // the KJFK row is not counted
        Assert.Contains(handler.Requests, r => r.Contains("station=KNYC") && r.Contains("year=2025"));

        var b = collector.CollectAsync("KNYC", 2024, 2025).Sync();
        Assert.Equal((0, 2), (b.NewVintages, b.Unchanged));

        high = 48; // the report was corrected
        var c = collector.CollectAsync("KNYC", 2024, 2025).Sync();
        Assert.Equal(1, c.NewVintages);
        Assert.Equal(48, TruthStore.CurrentCli(t.Db, "KNYC").Single(x => x.ClimateDate == new DateOnly(2024, 1, 1)).High);
        Assert.Equal(3, t.Db.Scalar<long>("SELECT COUNT(*) FROM cli_highs"));
    }

    [Fact]
    public void Climate_report_collection_rejects_bad_input_and_reports_http_failures()
    {
        using var t = new TempDb();
        var clock = FixedClock.Default();
        var down = new IemCliCollector(t.Db, new HttpClient(new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("x") })),
            clock, minInterval: TimeSpan.Zero);
        Assert.Throws<KalshiBotException>(() => down.CollectAsync("knyc; drop", 2024, 2024).Sync());
        Assert.Throws<KalshiBotException>(() => down.CollectAsync("KNYC", 2025, 2024).Sync());
        var r = down.CollectAsync("KNYC", 2024, 2024).Sync();
        Assert.Equal("failed", r.Status);
        Assert.Contains("502", r.Errors.Single());
        Assert.Equal(1, t.Db.Scalar<long>("SELECT COUNT(*) FROM truth_raw")); // the failed response is still recorded
    }

    // ---- reconciliation and the scoring series --------------------------------------------------------------------

    [Fact]
    public void Kalshi_is_authoritative_flagged_and_missing_days_are_excluded_and_the_proxy_stops_where_Kalshi_starts()
    {
        using var t = new TempDb();
        var clock = FixedClock.Default();
        var handler = new ScriptedHandler(r => Route(r,
            live: () => Page(Event("KXHIGHNY-26NOV18", 50).Concat(Event("KXHIGHNY-26NOV19", 60, NwsClimatological)) // 11-20 never settled a value
                .Concat(Event("KXHIGHNY-26NOV21", 50).Select((m, i) => { if (i == 0) m["expiration_value"] = "51.00"; return m; })) // T49 says 51, the rest 50
                .Concat(Event("KXHIGHNY-26NOV22", 52, flip: "B53.5"))),
            hist: _ => ScriptedHandler.Json(HttpStatusCode.OK, Page([]))));
        var k = new KalshiTruthCollector(t.Db, Client(handler, clock), clock).CollectAsync("KXHIGHNY").Sync();
        Assert.Equal((4, 1, 1), (k.EventsWithValue, k.Conflicts, k.Inconsistent));

        static string Row(string date, int high) => $$"""{"station":"KNYC","valid":"{{date}}","high":{{high}},"product":"p{{date}}"}""";
        var cli = "{\"results\":[" + string.Join(",", new[]
        {
            Row("2026-11-15", 48), Row("2026-11-16", 49), Row("2026-11-17", 47),      // before Kalshi's first day: proxy
            Row("2026-11-18", 50), Row("2026-11-19", 59), Row("2026-11-20", 55),     // 18 agrees, 19 differs by one degree, 20 has no Kalshi value
            Row("2026-11-21", 50), Row("2026-11-22", 52),
        }) + "]}";
        new IemCliCollector(t.Db, new HttpClient(new ScriptedHandler(_ => ScriptedHandler.Json(HttpStatusCode.OK, cli))), clock, minInterval: TimeSpan.Zero)
            .CollectAsync("KNYC", 2026, 2026).Sync();

        var report = TruthStore.Report(t.Db, "KNYC");
        Assert.Equal((2, 1, 1), (report.KalshiOk, report.KalshiConflict, report.KalshiInconsistent));
        Assert.Equal((2, 1), (report.Compared, report.Agree));
        var diff = Assert.Single(report.Differences);
        Assert.Equal((new DateOnly(2026, 11, 19), 60, 59), (diff.ClimateDate, diff.Kalshi, diff.Cli));
        Assert.Equal(SettlementRegime.NwsClimatologicalReport, diff.Regime);
        Assert.Equal([new DateOnly(2026, 11, 20)], report.MissingKalshiDays);
        Assert.Contains(report.ByRegime, r => r.Regime == SettlementRegime.WeatherCompany && r is { Compared: 1, Agree: 1 });

        var resolved = TruthStore.Resolve(t.Db, "KNYC");
        Assert.Equal(
            [("2026-11-15", 48, "nws-cli"), ("2026-11-16", 49, "nws-cli"), ("2026-11-17", 47, "nws-cli"), ("2026-11-18", 50, "kalshi"), ("2026-11-19", 60, "kalshi")],
            resolved.Select(d => (d.ClimateDate.ToString("yyyy-MM-dd"), d.Value, d.Source)));
        Assert.Equal((2, 3), (report.ResolvedKalshi, report.ResolvedCliProxy));
        Assert.Contains("differs 2026-11-19", report.ToText());
    }
}

/// <summary>Blocks on a task from inside a helper, which keeps the xUnit1031 analyzer quiet about test methods.</summary>
internal static class TaskSyncExtensions
{
    public static T Sync<T>(this Task<T> task) => task.GetAwaiter().GetResult();
}
