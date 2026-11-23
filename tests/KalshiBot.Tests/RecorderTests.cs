using System.Net;
using System.Text.Json.Nodes;
using KalshiBot.Core.Common;
using KalshiBot.Core.Kalshi;
using KalshiBot.Core.Recording;
using Xunit;

namespace KalshiBot.Tests;

public class RecorderTests
{
    [Fact]
    public void Records_snapshots_with_source_references_and_injected_timestamps()
    {
        using var t = new TempDb();
        var clock = FixedClock.Default();
        var summary = Fx.Record(t.Db, clock, Fx.Open);

        Assert.Equal("completed", summary.Status);
        Assert.Equal(2, summary.Snapshots);
        Assert.Equal(DataOrigin.Synthetic, summary.DataOrigin);

        var s = new MarketStore(t.Db).Latest(Fx.T50)!;
        Assert.True(s.IsSynthetic);
        Assert.Equal(clock.Now, s.ObservedAt);
        Assert.Equal("active", s.Status);
        Assert.Equal(0.4000m, s.YesBid);
        Assert.Equal(0.4500m, s.YesAsk);
        Assert.Equal(new DateTimeOffset(2099, 12, 31, 23, 0, 0, TimeSpan.Zero), s.CloseTime);

        // Order book normalized best-first; YES asks derived from NO bids (1 - p).
        Assert.Equal(0.40m, s.Book!.YesBids[0].Price);
        Assert.Equal(0.45m, s.Book.YesAsks[0].Price);
        Assert.Equal(30m, s.Book.YesAsks[0].Size);

        // Raw source reference: verbatim body + hash + URL.
        var raw = new MarketStore(t.Db).Raw(s.RawMarketId)!;
        Assert.EndsWith($"/markets/{Fx.T50}", raw.Url);
        var body = t.Db.Scalar<string>("SELECT body FROM raw_responses WHERE id = $id", ("$id", s.RawMarketId))!;
        Assert.Equal(Fx.MarketJson(Fx.Open, Fx.T50), body);
        Assert.Equal(Hash.Sha256Hex(body), raw.BodySha256);

        // Event and series metadata captured once per run.
        Assert.Equal(1, t.Db.Scalar<long>("SELECT COUNT(*) FROM event_snapshots"));
        Assert.Equal(1, t.Db.Scalar<long>("SELECT COUNT(*) FROM series_snapshots"));
        Assert.Equal("KXSYNTHWX", new MarketStore(t.Db).LatestEvent(s.EventTicker, clock.Now)!.SeriesTicker);
    }

    [Fact]
    public async Task Rules_get_a_new_version_only_when_text_changes()
    {
        using var t = new TempDb();
        var clock = FixedClock.Default();
        Fx.Record(t.Db, clock, Fx.Open, Fx.T50);
        clock.Advance(TimeSpan.FromMinutes(5));
        var again = Fx.Record(t.Db, clock, Fx.Open, Fx.T50);
        Assert.Equal(0, again.RulesChanges);
        var history = new MarketStore(t.Db).RulesHistory(Fx.T50);
        Assert.Single(history);
        Assert.Equal(clock.Now, history[0].LastSeenAt);

        // Kalshi amends the rules text.
        var amended = JsonNode.Parse(Fx.MarketJson(Fx.Open, Fx.T50))!;
        amended["market"]!["rules_secondary"] = "AMENDED secondary rules.";
        var handler = new ScriptedHandler(req => req.RequestUri!.AbsolutePath.EndsWith(Fx.T50)
            ? ScriptedHandler.Json(HttpStatusCode.OK, amended.ToJsonString())
            : new FixtureHandlerProxy(Fx.Open).Get(req));
        clock.Advance(TimeSpan.FromMinutes(5));
        var changed = await new Recorder(t.Db, Client(handler, clock), clock).CaptureAsync([Fx.T50]);
        Assert.Equal(1, changed.RulesChanges);
        history = new MarketStore(t.Db).RulesHistory(Fx.T50);
        Assert.Equal(2, history.Count);
        Assert.Equal("AMENDED secondary rules.", history[1].Secondary);
        Assert.Equal(history[1].Id, new MarketStore(t.Db).Latest(Fx.T50)!.RulesId);
    }

    [Fact]
    public void One_failing_market_yields_partial_run_and_keeps_the_error_body()
    {
        using var t = new TempDb();
        var clock = FixedClock.Default();
        var summary = Fx.Record(t.Db, clock, Fx.Open, Fx.T50, "KXSYNTH-DOES-NOT-EXIST");
        Assert.Equal("partial", summary.Status);
        Assert.Equal(1, summary.Snapshots);
        Assert.Contains(summary.Errors, e => e.Contains("HTTP 404"));
        Assert.Equal(1, t.Db.Scalar<long>("SELECT COUNT(*) FROM raw_responses WHERE http_status = 404"));
        Assert.Equal("partial", t.Db.Scalar<string>("SELECT status FROM capture_runs WHERE id = $id", ("$id", summary.RunId)));
    }

    [Fact]
    public async Task Unparseable_market_is_not_snapshotted_but_raw_evidence_is_kept()
    {
        using var t = new TempDb();
        var clock = FixedClock.Default();
        var broken = JsonNode.Parse(Fx.MarketJson(Fx.Open, Fx.T50))!;
        broken["market"]!.AsObject().Remove("rules_primary");
        var handler = new ScriptedHandler(req => req.RequestUri!.AbsolutePath.EndsWith(Fx.T50)
            ? ScriptedHandler.Json(HttpStatusCode.OK, broken.ToJsonString())
            : new FixtureHandlerProxy(Fx.Open).Get(req));
        var summary = await new Recorder(t.Db, Client(handler, clock), clock).CaptureAsync([Fx.T50]);
        Assert.Equal("failed", summary.Status);
        Assert.Contains(summary.Errors, e => e.Contains("rules_primary"));
        Assert.Equal(0, t.Db.Scalar<long>("SELECT COUNT(*) FROM market_snapshots"));
        Assert.Equal(1, t.Db.Scalar<long>("SELECT COUNT(*) FROM raw_responses"));
    }

    [Fact]
    public async Task Response_for_a_different_ticker_is_rejected()
    {
        using var t = new TempDb();
        var clock = FixedClock.Default();
        var handler = new ScriptedHandler(req => req.RequestUri!.AbsolutePath.EndsWith(Fx.T50)
            ? ScriptedHandler.Json(HttpStatusCode.OK, Fx.MarketJson(Fx.Open, Fx.T60))
            : new FixtureHandlerProxy(Fx.Open).Get(req));
        var summary = await new Recorder(t.Db, Client(handler, clock), clock).CaptureAsync([Fx.T50]);
        Assert.Contains(summary.Errors, e => e.Contains("does not match request"));
        Assert.Equal(0, t.Db.Scalar<long>("SELECT COUNT(*) FROM market_snapshots"));
    }

    [Fact]
    public async Task Rate_limited_request_is_retried_and_no_credentials_are_sent()
    {
        var first = true;
        var handler = new ScriptedHandler(req =>
        {
            Assert.Equal(HttpMethod.Get, req.Method);
            Assert.DoesNotContain(req.Headers, h => h.Key.StartsWith("KALSHI-ACCESS", StringComparison.OrdinalIgnoreCase));
            if (first)
            {
                first = false;
                var r = ScriptedHandler.Json(HttpStatusCode.TooManyRequests, "{}");
                r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
                return r;
            }
            return new FixtureHandlerProxy(Fx.Open).Get(req);
        });
        var client = Client(handler, TimeProvider.System);
        var resp = await client.GetMarketAsync(Fx.T50, CancellationToken.None);
        Assert.Equal(200, resp.Status);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public void Run_left_running_by_a_crash_is_marked_abandoned_on_next_capture()
    {
        using var t = new TempDb();
        var clock = FixedClock.Default();
        t.Db.Exec("INSERT INTO capture_runs (started_at, status, data_origin, base_url, app_version) VALUES ('2026-11-23T11:00:00.0000000Z', 'running', 'fixture:synthetic', 'x', 'test')");
        var crashed = t.Db.Scalar<long>("SELECT id FROM capture_runs");
        t.Reopen();
        Fx.Record(t.Db, clock, Fx.Open, Fx.T50);
        Assert.Equal("abandoned", t.Db.Scalar<string>("SELECT status FROM capture_runs WHERE id = $id", ("$id", crashed)));
    }

    [Fact]
    public void Watchlist_rejects_malformed_tickers()
    {
        using var t = new TempDb();
        var w = new Watchlist(t.Db, FixedClock.Default());
        Assert.True(w.Add(Fx.T50));
        Assert.False(w.Add(Fx.T50));
        Assert.Throws<KalshiBotException>(() => w.Add("../../etc/passwd"));
        Assert.Throws<KalshiBotException>(() => w.Add("A B"));
    }

    private static KalshiPublicClient Client(HttpMessageHandler h, TimeProvider clock) =>
        new(new HttpClient(h), KalshiEndpoint.Fixture, clock,
            new KalshiClientOptions { MinInterval = TimeSpan.Zero, MaxBackoff = TimeSpan.FromMilliseconds(10) });
}

/// <summary>Synchronous access to fixture responses for composing scripted handlers.</summary>
internal sealed class FixtureHandlerProxy(string dir)
{
    public HttpResponseMessage Get(HttpRequestMessage req) =>
        new HttpMessageInvoker(new FixtureHandler(dir)).SendAsync(req, CancellationToken.None).GetAwaiter().GetResult();
}
