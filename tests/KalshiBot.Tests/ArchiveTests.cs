using System.Net;
using System.Text.Json.Nodes;
using KalshiBot.Core.Archive;
using KalshiBot.Core.Common;
using KalshiBot.Core.Kalshi;
using KalshiBot.Core.Recording;
using KalshiBot.Core.Storage;
using KalshiBot.Core.Truth;
using Microsoft.Data.Sqlite;
using Xunit;

namespace KalshiBot.Tests;

/// <summary>Historical inputs for Phase 3 Stage A (docs/PHASE3_PLAN.md W3): GFS MOS runs and Kalshi hourly candles.</summary>
public class ArchiveTests
{
    private const string MosHeader = "runtime,ftime,model,n_x,tmp,dpt,cld,wdr,wsp,p06,p12,q06,q12,t06_1,t06_2,t12_1,t12_2,cig,vis,obv,station,t06,t12";

    private static string MosRow(string runtime, string ftime, string? nx, string? tmp, string station = "KNYC") =>
        $"{runtime},{ftime},GFS,{nx},{tmp},50,FW,280,4,,,,,8.0,,,,8,7,N ,{station},,";

    private static string Csv(params string[] rows) => MosHeader + "\n" + string.Join("\n", rows) + "\n";

    private static DateTimeOffset Z(string s) => DateTimeOffset.Parse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal);

    // ---- pure parsing -------------------------------------------------------------------------------------------

    [Fact]
    public void Mos_csv_parses_the_layout_IEM_serves()
    {
        var rows = MosCsv.Parse(Csv(
            MosRow("2024-06-10 12:00:00", "2024-06-11 00:00:00", "78.0", "67"),
            MosRow("2024-06-10 12:00:00", "2024-06-11 06:00:00", "", "64")));
        Assert.Equal(2, rows.Count);
        Assert.Equal(new MosRow("KNYC", "GFS", Z("2024-06-10T12:00:00Z"), Z("2024-06-11T00:00:00Z"), 78.0m, 67m), rows[0]);
        Assert.Null(rows[1].NX);
        Assert.Equal(64m, rows[1].Tmp);
    }

    [Fact]
    public void Mos_csv_rejects_an_error_page_a_ragged_row_and_a_bad_time_instead_of_skipping_them()
    {
        Assert.Throws<FormatException>(() => MosCsv.Parse("ERROR: rate limited\n"));
        Assert.Throws<FormatException>(() => MosCsv.Parse(""));
        Assert.Contains("line 2", Assert.Throws<FormatException>(() => MosCsv.Parse(MosHeader + "\n2024-06-10 12:00:00,2024-06-11 00:00:00,GFS,78\n")).Message);
        Assert.Throws<FormatException>(() => MosCsv.Parse(Csv(MosRow("yesterday", "2024-06-11 00:00:00", "78", "67"))));
        Assert.Throws<FormatException>(() => MosCsv.Parse(Csv(MosRow("2024-06-10 12:00:00", "2024-06-11 00:00:00", "hot", "67"))));
    }

    private static string CandlePage(bool historical, params (long Ts, string? Bid, string? Ask)[] c)
    {
        string K(string name) => historical ? name : name + "_dollars";
        return new JsonObject
        {
            ["candlesticks"] = new JsonArray(c.Select(x => (JsonNode)new JsonObject
            {
                ["end_period_ts"] = x.Ts,
                ["yes_bid"] = new JsonObject { [K("close")] = x.Bid },
                ["yes_ask"] = new JsonObject { [K("close")] = x.Ask },
                ["price"] = new JsonObject { [K("close")] = x.Ask },
                [historical ? "volume" : "volume_fp"] = "10.00",
                [historical ? "open_interest" : "open_interest_fp"] = "5.00",
            }).ToArray()),
        }.ToJsonString();
    }

    [Fact]
    public void Candles_parse_the_same_from_the_series_and_the_historical_field_names()
    {
        var ts = Z("2026-11-23T19:00:00Z").ToUnixTimeSeconds();
        var series = CandleParse.Page(CandlePage(false, (ts, "0.4000", "0.4500")));
        var hist = CandleParse.Page(CandlePage(true, (ts, "0.4000", "0.4500")));
        Assert.Equal(series, hist);
        Assert.Equal(new CandleRow(Z("2026-11-23T19:00:00Z"), 0.40m, 0.45m, 0.45m, 10m, 5m), series[0]);
    }

    [Fact]
    public void A_candle_without_quotes_has_null_sides_and_an_impossible_price_is_an_error()
    {
        var ts = Z("2026-11-23T19:00:00Z").ToUnixTimeSeconds();
        var c = CandleParse.Page(CandlePage(false, (ts, null, null)))[0];
        Assert.Null(c.YesBidClose);
        Assert.Null(c.YesAskClose);
        Assert.Throws<FormatException>(() => CandleParse.Page(CandlePage(false, (ts, "0.4", "1.5"))));
        Assert.Throws<FormatException>(() => CandleParse.Page("""{"candlesticks":[{"yes_bid":{}}]}"""));
        Assert.Throws<FormatException>(() => CandleParse.Page("{}"));
    }

    [Fact]
    public void The_decision_time_is_15_00_Eastern_the_day_before_in_winter_and_summer()
    {
        Assert.Equal(Z("2026-11-23T20:00:00Z"), DecisionClock.DecisionTimeUtc(new DateOnly(2026, 11, 24))); // EST
        Assert.Equal(Z("2026-07-15T19:00:00Z"), DecisionClock.DecisionTimeUtc(new DateOnly(2026, 7, 16)));  // EDT
        Assert.Equal(Z("2026-03-07T20:00:00Z"), DecisionClock.DecisionTimeUtc(new DateOnly(2026, 3, 8)));   // last day before the spring change
        Assert.Equal(Z("2026-03-08T19:00:00Z"), DecisionClock.DecisionTimeUtc(new DateOnly(2026, 3, 9)));   // first day after it
    }

    [Fact]
    public void The_MOS_inputs_are_the_12Z_run_the_day_before_and_the_00Z_forecast_time_after_the_day()
    {
        var d = new DateOnly(2026, 11, 24);
        Assert.Equal(Z("2026-11-23T12:00:00Z"), DecisionClock.MosRuntime(d));
        Assert.Equal(Z("2026-11-25T00:00:00Z"), DecisionClock.MosMaxForecastTime(d));
        var runtime = DecisionClock.MosRuntime(d);
        Assert.False(DecisionClock.MosAvailable(runtime, Z("2026-11-23T17:59:59Z")));
        Assert.True(DecisionClock.MosAvailable(runtime, Z("2026-11-23T18:00:00Z")));
        Assert.True(DecisionClock.MosAvailable(runtime, DecisionClock.DecisionTimeUtc(d))); // available well before the decision, in winter and summer
        var summer = new DateOnly(2026, 7, 16);
        Assert.True(DecisionClock.MosAvailable(DecisionClock.MosRuntime(summer), DecisionClock.DecisionTimeUtc(summer)));
        var (start, end) = DecisionClock.CandleWindow(d);
        Assert.Equal((Z("2026-11-23T12:00:00Z"), Z("2026-11-24T06:00:00Z")), (start, end));
    }

    // ---- MOS collection -----------------------------------------------------------------------------------------

    private static MosOptions Instant => new() { MinInterval = TimeSpan.Zero, RetryDelay = TimeSpan.Zero, MaxRetries = 2 };

    [Fact]
    public void Mos_collection_stores_each_year_verbatim_and_only_appends_new_rows()
    {
        using var t = new TempDb();
        var clock = FixedClock.Default();
        var nx = "78.0";
        string? lastBody = null;
        var handler = new ScriptedHandler(r =>
        {
            var q = System.Web.HttpUtility.ParseQueryString(r.RequestUri!.Query);
            lastBody = q["sts"]!.StartsWith("2023") ? Csv(
                    MosRow("2023-06-10 12:00:00", "2023-06-11 00:00:00", nx, "67"),
                    MosRow("2023-06-10 12:00:00", "2023-06-11 12:00:00", "59.0", "60"),
                    MosRow("2023-06-10 12:00:00", "2023-06-11 00:00:00", "99.0", "99", station: "KJFK")) // another station: ignored
                : Csv(MosRow("2024-06-10 12:00:00", "2024-06-11 00:00:00", "80.0", "70"));
            return ScriptedHandler.Json(HttpStatusCode.OK, lastBody);
        });
        var collector = new IemMosCollector(t.Db, new HttpClient(handler), clock, options: Instant);

        var a = collector.CollectAsync("KNYC", "GFS", 2023, 2024).Sync();
        Assert.Equal("completed", a.Status);
        Assert.Equal((2, 3, 3, 0, 0), (a.Years, a.Rows, a.Inserted, a.Unchanged, a.Conflicts));
        Assert.Contains(handler.Requests, r => r.Contains("sts=2023-01-01T00:00Z") && r.Contains("ets=2024-01-01T00:00Z") && r.Contains("model=GFS") && r.Contains("format=csv"));
        Assert.Equal(ArchiveOrigin.Database, DatabaseOrigin.Get(t.Db));

        // The stored body is exactly what was served, and rows point at it.
        var body = t.Db.Scalar<string>("SELECT body FROM archive_raw WHERE id = 2")!;
        Assert.Equal(lastBody, body);
        Assert.Equal(Hash.Sha256Hex(body), t.Db.Scalar<string>("SELECT body_sha256 FROM archive_raw WHERE id = 2"));
        Assert.Equal(2, t.Db.Scalar<long>("SELECT raw_id FROM mos_rows WHERE runtime = $r", ("$r", Ts.ToText(Z("2024-06-10T12:00:00Z")))));

        var again = collector.CollectAsync("KNYC", "GFS", 2023, 2024).Sync();
        Assert.Equal((0, 3, 0), (again.Inserted, again.Unchanged, again.Conflicts));
        Assert.Equal(3, t.Db.Scalar<long>("SELECT COUNT(*) FROM mos_rows"));

        // A stored run is immutable: a download that disagrees is reported, never written over the original.
        nx = "81.0";
        var changed = collector.CollectAsync("KNYC", "GFS", 2023, 2023).Sync();
        Assert.Equal("partial", changed.Status);
        Assert.Equal(1, changed.Conflicts);
        Assert.Contains("differs", changed.Errors[0]);
        Assert.Equal(78.0m, AdminMax(t.Db, "2023-06-10T12:00:00Z", "2023-06-11T00:00:00Z"));
    }

    private static decimal? AdminMax(Db db, string runtime, string ftime) =>
        db.Query("SELECT n_x FROM mos_rows WHERE runtime = $r AND ftime = $f", r => r.DN("n_x"), ("$r", Ts.ToText(Z(runtime))), ("$f", Ts.ToText(Z(ftime)))).Single();

    [Fact]
    public void A_rate_limit_is_waited_out_and_an_error_page_fails_the_run_without_losing_the_response()
    {
        using var t = new TempDb();
        var clock = FixedClock.Default();
        var calls = 0;
        var limited = new ScriptedHandler(_ => ++calls == 1
            ? new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("slow down") }
            : ScriptedHandler.Json(HttpStatusCode.OK, Csv(MosRow("2024-06-10 12:00:00", "2024-06-11 00:00:00", "80.0", "70"))));
        var ok = new IemMosCollector(t.Db, new HttpClient(limited), clock, options: Instant).CollectAsync("KNYC", "GFS", 2024, 2024).Sync();
        Assert.Equal(("completed", 1, 2), (ok.Status, ok.Inserted, calls));

        using var bad = new TempDb();
        var page = new IemMosCollector(bad.Db, new HttpClient(new ScriptedHandler(_ => ScriptedHandler.Json(HttpStatusCode.OK, "ERROR: could not find station"))), clock, options: Instant)
            .CollectAsync("KNYC", "GFS", 2024, 2024).Sync();
        Assert.Equal("failed", page.Status);
        Assert.Contains("header", page.Errors.Single());
        Assert.Equal(1, bad.Db.Scalar<long>("SELECT COUNT(*) FROM archive_raw")); // the response is kept for inspection
        Assert.Equal(0, bad.Db.Scalar<long>("SELECT COUNT(*) FROM mos_rows"));
    }

    [Fact]
    public void Mos_collection_rejects_unsupported_models_bad_stations_and_unreasonable_ranges()
    {
        using var t = new TempDb();
        var c = new IemMosCollector(t.Db, new HttpClient(new ScriptedHandler(_ => ScriptedHandler.Json(HttpStatusCode.OK, Csv()))), FixedClock.Default(), options: Instant);
        Assert.Throws<KalshiBotException>(() => c.CollectAsync("KNYC", "NBS", 2024, 2024).Sync());
        Assert.Throws<KalshiBotException>(() => c.CollectAsync("knyc;x", "GFS", 2024, 2024).Sync());
        Assert.Throws<KalshiBotException>(() => c.CollectAsync("KNYC", "GFS", 2025, 2024).Sync());
    }

    [Fact]
    public void The_daily_max_forecast_comes_from_the_forecast_time_after_the_day_not_the_day_itself()
    {
        using var t = new TempDb();
        var clock = FixedClock.Default();
        new IemMosCollector(t.Db, new HttpClient(new ScriptedHandler(_ => ScriptedHandler.Json(HttpStatusCode.OK, Csv(
            MosRow("2026-11-23 12:00:00", "2026-11-24 00:00:00", "61.0", "60"),    // the max for Nov 23 (today)
            MosRow("2026-11-23 12:00:00", "2026-11-25 00:00:00", "55.0", "50"))))), // the max for Nov 24
            clock, options: Instant).CollectAsync("KNYC", "GFS", 2026, 2026).Sync();

        var runtime = DecisionClock.MosRuntime(new DateOnly(2026, 11, 24));
        Assert.Equal(55.0m, ArchiveStore.MosDailyMax(t.Db, "KNYC", "GFS", runtime, new DateOnly(2026, 11, 24)));
        Assert.Null(ArchiveStore.MosDailyMax(t.Db, "KNYC", "GFS", runtime, new DateOnly(2026, 11, 30)));
    }

    // ---- candle collection --------------------------------------------------------------------------------------

    private static (ScriptedHandler Handler, Dictionary<string, int> Hits) CandleServer(Func<string, string, HttpResponseMessage?> answer)
    {
        var hits = new Dictionary<string, int>();
        var handler = new ScriptedHandler(r =>
        {
            var path = r.RequestUri!.AbsolutePath;
            if (path.EndsWith("/historical/cutoff", StringComparison.Ordinal))
                return ScriptedHandler.Json(HttpStatusCode.OK, """{"market_settled_ts":"2026-09-24T00:00:00Z"}""");
            var kind = path.Contains("/historical/markets/") ? "historical" : "series";
            var ticker = path.Split('/')[^2];
            hits[$"{kind}:{ticker}"] = hits.GetValueOrDefault($"{kind}:{ticker}") + 1;
            return answer(kind, ticker) ?? ScriptedHandler.Json(HttpStatusCode.NotFound, "{}");
        });
        return (handler, hits);
    }

    private static long Ts_(string iso) => Z(iso).ToUnixTimeSeconds();

    [Fact]
    public void Candle_collection_picks_the_endpoint_from_the_cutoff_falls_back_on_404_and_resumes_without_refetching()
    {
        using var t = new TempDb();
        var clock = FixedClock.Default();
        var (handler, hits) = CandleServer((kind, ticker) => ticker switch
        {
            "HIGHNY-25MAR10-T50" when kind == "historical" => ScriptedHandler.Json(HttpStatusCode.OK, CandlePage(true, (Ts_("2025-03-09T19:00:00Z"), "0.4100", "0.4200"))),
            "KXHIGHNY-26NOV20-B51.5" when kind == "series" => ScriptedHandler.Json(HttpStatusCode.OK, CandlePage(false, (Ts_("2026-11-19T20:00:00Z"), "0.5000", "0.5100"), (Ts_("2026-11-19T21:00:00Z"), "0.5100", "0.5200"))),
            "KXHIGHNY-26SEP23-T60" when kind == "historical" => ScriptedHandler.Json(HttpStatusCode.OK, CandlePage(true)), // exists only on the historical side, no candles
            _ => null,
        });
        var targets = new List<CandleTarget>
        {
            new("HIGHNY-25MAR10-T50", "HIGHNY-25MAR10", new DateOnly(2025, 3, 10)),
            new("KXHIGHNY-26NOV20-B51.5", "KXHIGHNY-26NOV20", new DateOnly(2026, 11, 20)),
            new("KXHIGHNY-26SEP23-T60", "KXHIGHNY-26SEP23", new DateOnly(2026, 9, 23)),   // at the cutoff: series first, 404, then historical
            new("KXHIGHNY-26NOV24-T49", "KXHIGHNY-26NOV24", new DateOnly(2026, 11, 24)),  // window ends after "now": not fetched yet
            new("KXHIGHNY-26NOV19-T40", "KXHIGHNY-26NOV19", new DateOnly(2026, 11, 19)),  // not found anywhere
        };
        var collector = new KalshiCandleCollector(t.Db, Fx_Client(handler, clock), clock);

        var first = collector.CollectAsync(targets).Sync();
        Assert.Equal("partial", first.Status);
        Assert.Equal((5, 3, 0, 1, 1, 3), (first.Targets, first.Fetched, first.AlreadyHave, first.NotYetComplete, first.Failed, first.Candles));
        Assert.Contains(first.Errors, e => e.Contains("KXHIGHNY-26NOV19-T40") && e.Contains("404"));
        Assert.Equal(1, hits["historical:HIGHNY-25MAR10-T50"]);                                   // old market: historical first, no series call
        Assert.DoesNotContain("series:HIGHNY-25MAR10-T50", hits.Keys);
        Assert.Equal(1, hits["series:KXHIGHNY-26NOV20-B51.5"]);                                   // recent: series first
        Assert.Equal((1, 1), (hits["series:KXHIGHNY-26SEP23-T60"], hits["historical:KXHIGHNY-26SEP23-T60"])); // fell back once
        Assert.DoesNotContain(hits.Keys, k => k.Contains("26NOV24"));

        Assert.Equal(
            new Dictionary<string, string>
            {
                ["HIGHNY-25MAR10-T50"] = "historical", ["KXHIGHNY-26NOV20-B51.5"] = "series", ["KXHIGHNY-26SEP23-T60"] = "historical",
            },
            t.Db.Query("SELECT market_ticker, endpoint FROM candle_fetches", r => (r.S("market_ticker"), r.S("endpoint"))).ToDictionary(x => x.Item1, x => x.Item2));
        Assert.Equal(3, t.Db.Scalar<long>("SELECT COUNT(*) FROM candles"));
        Assert.Equal(["0.5", "0.51"], t.Db.Query("SELECT yes_bid_close FROM candles WHERE market_ticker = 'KXHIGHNY-26NOV20-B51.5' ORDER BY end_period_ts", r => r.S("yes_bid_close")));
        Assert.Equal(0, t.Db.Scalar<long>("SELECT candle_count FROM candle_fetches WHERE market_ticker = 'KXHIGHNY-26SEP23-T60'")); // empty window still recorded

        // A second pass re-requests only the market that failed.
        hits.Clear();
        var second = collector.CollectAsync(targets).Sync();
        Assert.Equal((0, 3, 1), (second.Fetched, second.AlreadyHave, second.Failed));
        Assert.All(hits.Keys, k => Assert.Contains("26NOV19", k));
    }

    private static KalshiPublicClient Fx_Client(ScriptedHandler h, TimeProvider clock) => TruthTests.Client(h, clock);

    [Fact]
    public void Candle_collection_gives_up_after_repeated_consecutive_failures()
    {
        using var t = new TempDb();
        var clock = FixedClock.Default();
        var (handler, _) = CandleServer((_, _) => null);
        var targets = Enumerable.Range(1, KalshiCandleCollector.MaxConsecutiveFailures + 5)
            .Select(i => new CandleTarget($"KXHIGHNY-26NOV10-T{i}", "KXHIGHNY-26NOV10", new DateOnly(2026, 11, 10))).ToList();
        var s = new KalshiCandleCollector(t.Db, Fx_Client(handler, clock), clock).CollectAsync(targets).Sync();
        Assert.Equal("failed", s.Status);
        Assert.Equal(KalshiCandleCollector.MaxConsecutiveFailures, s.Failed);
        Assert.Contains(s.Errors, e => e.Contains("aborted"));
    }

    [Fact]
    public void A_quote_is_the_last_candle_at_or_before_the_time_and_not_older_than_the_limit()
    {
        using var t = new TempDb();
        Seed(t.Db, "KXHIGHNY-26NOV24-B49.5", "KXHIGHNY-26NOV24", (Z("2026-11-23T18:00:00Z"), 0.30m, 0.35m), (Z("2026-11-23T19:00:00Z"), 0.40m, 0.45m), (Z("2026-11-23T21:00:00Z"), 0.60m, 0.65m));

        var q = ArchiveStore.QuoteAtOrBefore(t.Db, "KXHIGHNY-26NOV24-B49.5", Z("2026-11-23T20:00:00Z"), TimeSpan.FromHours(3))!;
        Assert.Equal((Z("2026-11-23T19:00:00Z"), 0.40m, 0.45m), (q.EndPeriod, q.Bid, q.Ask)); // the 21:00 candle is in the future
        Assert.True(q.TwoSided);
        Assert.Equal(0.425m, q.Mid);
        Assert.Null(ArchiveStore.QuoteAtOrBefore(t.Db, "KXHIGHNY-26NOV24-B49.5", Z("2026-11-23T17:00:00Z"), TimeSpan.FromHours(3)));
        Assert.Null(ArchiveStore.QuoteAtOrBefore(t.Db, "KXHIGHNY-26NOV24-B49.5", Z("2026-11-24T00:30:00Z"), TimeSpan.FromHours(3))); // stale
        Assert.Null(ArchiveStore.QuoteAtOrBefore(t.Db, "KXHIGHNY-26NOV24-T56", Z("2026-11-23T20:00:00Z"), TimeSpan.FromHours(3)));
    }

    [Theory]
    [InlineData(0.40, 0.45, true)]
    [InlineData(0.00, 0.45, false)]   // no bid
    [InlineData(0.40, 1.00, false)]   // no ask
    [InlineData(0.50, 0.40, false)]   // crossed
    public void Only_a_real_bid_and_a_real_ask_make_a_two_sided_quote(double bid, double ask, bool expected) =>
        Assert.Equal(expected, new CandleQuote(Z("2026-11-23T19:00:00Z"), (decimal)bid, (decimal)ask).TwoSided);

    private static void Seed(Db db, string ticker, string ev, params (DateTimeOffset End, decimal Bid, decimal Ask)[] candles)
    {
        DatabaseOrigin.BindOrThrow(db, ArchiveOrigin.Database, FixedClock.Default());
        var run = ArchiveStore.StartRun(db, FixedClock.Default(), "kalshi-candles", TruthOrigin.Kalshi, "test");
        var raw = ArchiveStore.StoreRaw(db, run, new RawHttpResponse("GET", "u", 200, FixedClock.Default().Now, FixedClock.Default().Now, null, "{}"));
        var date = TruthParse.EventDate(ev)!.Value;
        var (start, end) = DecisionClock.CandleWindow(date);
        ArchiveStore.AppendFetch(db, run, raw, TruthOrigin.Kalshi, new CandleTarget(ticker, ev, date), start, end, 60, false,
            candles.Select(c => new CandleRow(c.End, c.Bid, c.Ask, c.Ask, 1m, 1m)).ToList(), FixedClock.Default().Now);
    }

    [Fact]
    public void The_report_counts_event_days_with_a_two_sided_quote_at_the_decision_time()
    {
        using var t = new TempDb();
        // Decision time for Nov 24 is 2026-11-23 20:00Z.
        Seed(t.Db, "KXHIGHNY-26NOV24-B49.5", "KXHIGHNY-26NOV24", (Z("2026-11-23T20:00:00Z"), 0.40m, 0.45m));
        Seed(t.Db, "KXHIGHNY-26NOV24-T56", "KXHIGHNY-26NOV24", (Z("2026-11-23T20:00:00Z"), 0.00m, 1.00m));      // nobody quoting
        Seed(t.Db, "KXHIGHNY-26NOV23-B49.5", "KXHIGHNY-26NOV23", (Z("2026-11-22T20:00:00Z"), 0.00m, 1.00m));    // a quiet day
        var r = ArchiveStore.Report(t.Db, "KNYC");
        Assert.Equal((2L, 1L, 0L), (r.EventDays, r.EventDaysAnyQuote, r.EventDaysAllQuoted));
        Assert.Equal(3L, r.CandleRows);
        Assert.Equal((2026, 2L, 1L), r.ByYear.Single());
        Assert.Contains("assumed", r.ToText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Targets_come_only_from_unflagged_events_and_each_market_once()
    {
        using var truth = new TempDb();
        var clock = FixedClock.Default();
        var handler = new ScriptedHandler(r => TruthTests.Route(r,
            live: () => TruthTests.Page(TruthTests.Event("KXHIGHNY-26NOV22", 61).Concat(TruthTests.Event("KXHIGHNY-26NOV21", 50).Select((m, i) => { if (i == 0) m["expiration_value"] = "51.00"; return m; }))),
            hist: _ => ScriptedHandler.Json(HttpStatusCode.OK, TruthTests.Page([]))));
        var collector = new KalshiTruthCollector(truth.Db, TruthTests.Client(handler, clock), clock);
        collector.CollectAsync("KXHIGHNY").Sync();
        collector.CollectAsync("KXHIGHNY").Sync(); // a second set of raw pages must not duplicate targets

        var targets = ArchiveStore.TargetsFromTruth(truth.Db);
        Assert.Equal(6, targets.Count);
        Assert.All(targets, x => Assert.Equal(("KXHIGHNY-26NOV22", new DateOnly(2026, 11, 22)), (x.EventTicker, x.ClimateDate))); // the conflict event is excluded
        Assert.Equal(6, targets.Select(x => x.MarketTicker).Distinct().Count());
    }

    // ---- separation and integrity ---------------------------------------------------------------------------------

    [Fact]
    public void Archive_data_stays_out_of_truth_and_recording_databases_and_the_reverse()
    {
        var clock = FixedClock.Default();
        var mos = new HttpClient(new ScriptedHandler(_ => ScriptedHandler.Json(HttpStatusCode.OK, Csv(MosRow("2024-06-10 12:00:00", "2024-06-11 00:00:00", "80.0", "70")))));

        using var recording = new TempDb();
        Fx.Record(recording.Db, clock, Fx.Open);
        Assert.Throws<KalshiBotException>(() => new IemMosCollector(recording.Db, mos, clock, options: Instant).CollectAsync("KNYC", "GFS", 2024, 2024).Sync());

        using var truth = new TempDb();
        var truthHandler = new ScriptedHandler(r => TruthTests.Route(r, () => TruthTests.Page(TruthTests.Event("KXHIGHNY-26NOV22", 61)), _ => ScriptedHandler.Json(HttpStatusCode.OK, TruthTests.Page([]))));
        new KalshiTruthCollector(truth.Db, TruthTests.Client(truthHandler, clock), clock).CollectAsync("KXHIGHNY").Sync();
        Assert.Throws<KalshiBotException>(() => new IemMosCollector(truth.Db, mos, clock, options: Instant).CollectAsync("KNYC", "GFS", 2024, 2024).Sync());
        var candles = CandleServer((_, _) => null).Handler;
        var target = new List<CandleTarget> { new("KXHIGHNY-26NOV20-B51.5", "KXHIGHNY-26NOV20", new DateOnly(2026, 11, 20)) };
        Assert.Throws<KalshiBotException>(() => new KalshiCandleCollector(truth.Db, Fx_Client(candles, clock), clock).CollectAsync(target).Sync());
        Assert.Throws<KalshiBotException>(() => new KalshiCandleCollector(recording.Db, Fx_Client(candles, clock), clock).CollectAsync(target).Sync());
        Assert.Equal(0, truth.Db.Scalar<long>("SELECT COUNT(*) FROM archive_runs"));

        using var archive = new TempDb();
        new IemMosCollector(archive.Db, mos, clock, options: Instant).CollectAsync("KNYC", "GFS", 2024, 2024).Sync();
        Assert.Throws<KalshiBotException>(() => new KalshiTruthCollector(archive.Db, TruthTests.Client(truthHandler, clock), clock).CollectAsync("KXHIGHNY").Sync());
        Assert.Throws<KalshiBotException>(() => Fx.Record(archive.Db, clock, Fx.Open));
    }

    [Fact]
    public void Archive_history_cannot_be_rewritten()
    {
        using var t = new TempDb();
        new IemMosCollector(t.Db, new HttpClient(new ScriptedHandler(_ => ScriptedHandler.Json(HttpStatusCode.OK, Csv(MosRow("2024-06-10 12:00:00", "2024-06-11 00:00:00", "80.0", "70"))))),
            FixedClock.Default(), options: Instant).CollectAsync("KNYC", "GFS", 2024, 2024).Sync();
        Seed(t.Db, "KXHIGHNY-26NOV24-B49.5", "KXHIGHNY-26NOV24", (Z("2026-11-23T20:00:00Z"), 0.40m, 0.45m));

        foreach (var sql in new[]
        {
            "UPDATE mos_rows SET n_x = '1'", "DELETE FROM mos_rows", "UPDATE candles SET yes_bid_close = '0.99'", "DELETE FROM candles",
            "UPDATE candle_fetches SET candle_count = 9", "DELETE FROM candle_fetches", "UPDATE archive_raw SET body = 'x'", "DELETE FROM archive_raw",
        })
        {
            var ex = Assert.Throws<SqliteException>(() => t.Db.Exec(sql));
            Assert.Contains("append-only", ex.Message);
        }
    }
}
