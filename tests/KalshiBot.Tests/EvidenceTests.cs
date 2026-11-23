using System.Net;
using KalshiBot.Core.Common;
using KalshiBot.Core.Evidence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace KalshiBot.Tests;

public class NwsForecastCollectorTests
{
    private static string Nws => Path.Combine(Fx.Root, "nws");

    private static NwsForecastCollector Collector(TempDb t, TimeProvider clock, HttpMessageHandler? handler = null, string origin = DataOrigin.Synthetic) =>
        new(t.Db, new HttpClient(handler ?? new NwsFixtureHandler(Nws)), clock, origin, "https://fixture.invalid");

    [Fact]
    public async Task Collects_periods_with_publication_and_retrieval_times_kept_apart()
    {
        using var t = new TempDb();
        var clock = FixedClock.Default();
        var r = await Collector(t, clock).CollectAsync(ForecastLocation.CentralPark);
        Assert.Equal("completed", r.Status);
        Assert.Equal(3, r.Periods);
        Assert.Equal(DateTimeOffset.Parse("2099-12-30T08:13:42Z"), r.UpdateTime);
        Assert.Equal(2, t.Db.Scalar<long>("SELECT COUNT(*) FROM forecast_raw"));
        Assert.Equal("OKX/33,37", t.Db.Scalar<string>("SELECT DISTINCT grid FROM nws_forecast_periods"));

        var highs = NwsForecastCollector.DailyHighs(t.Db, "KNYC");
        Assert.Equal(["2099-12-31", "2099-12-30"], highs.Select(h => h.LocalDate).ToArray()); // local date from the period's own offset
        Assert.Equal(52, highs[0].Temperature);
        Assert.Equal(clock.Now, highs[0].ReceivedAt);                                       // retrieval
        Assert.Equal(DateTimeOffset.Parse("2099-12-30T08:13:42Z"), highs[0].UpdateTime);   // publication
        Assert.Throws<SqliteException>(() => t.Db.Exec("UPDATE forecast_raw SET body = '{}'"));
    }

    [Fact]
    public async Task A_points_response_naming_a_non_nws_url_is_not_followed()
    {
        using var t = new TempDb();
        var handler = new ScriptedHandler(req => ScriptedHandler.Json(HttpStatusCode.OK,
            """{"properties":{"gridId":"OKX","gridX":1,"gridY":2,"forecast":"https://evil.example/forecast"}}"""));
        var r = await Collector(t, FixedClock.Default(), handler).CollectAsync(ForecastLocation.CentralPark);
        Assert.Equal("failed", r.Status);
        Assert.Contains(r.Errors, e => e.Contains("refusing to follow non-NWS forecast URL"));
        Assert.Single(handler.Requests);
        Assert.Equal(0, t.Db.Scalar<long>("SELECT COUNT(*) FROM nws_forecast_periods"));
    }

    [Fact]
    public async Task Forecasts_never_mix_with_a_database_of_another_origin()
    {
        using var t = new TempDb();
        var clock = FixedClock.Default();
        Fx.Record(t.Db, clock, Fx.Open, Fx.T50); // binds the database to synthetic data
        await Assert.ThrowsAsync<KalshiBotException>(() => Collector(t, clock, origin: "live:nws").CollectAsync(ForecastLocation.CentralPark));
        Assert.Equal("completed", (await Collector(t, clock).CollectAsync(ForecastLocation.CentralPark)).Status);
    }
}
