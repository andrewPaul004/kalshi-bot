using KalshiBot.Core.Archive;
using KalshiBot.Core.Common;
using KalshiBot.Core.Kalshi;
using KalshiBot.Core.Recording;
using KalshiBot.Core.Storage;
using KalshiBot.Core.Trading;
using KalshiBot.Core.Truth;
using Xunit;

namespace KalshiBot.Tests;

/// <summary>Point-in-time evidence for Phase 3 (docs/PHASE3_PLAN.md W4): a model cannot see a vintage newer than its decision time.</summary>
public class PointInTimeTests
{
    private static DateTimeOffset Z(string s) => DateTimeOffset.Parse(s, System.Globalization.CultureInfo.InvariantCulture,
        System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal);

    private static readonly DateOnly Date = new(2026, 11, 24);

    private static void Mos(Db db, string runtime, string? nx, DateOnly? date = null, string station = "KNYC")
    {
        DatabaseOrigin.BindOrThrow(db, ArchiveOrigin.Database, FixedClock.Default());
        var run = ArchiveStore.StartRun(db, FixedClock.Default(), "iem-mos", TruthOrigin.Iem, "test");
        var raw = ArchiveStore.StoreRaw(db, run, new RawHttpResponse("GET", "u", 200, FixedClock.Default().Now, FixedClock.Default().Now, null, "x"));
        ArchiveStore.AppendMos(db, run, raw, TruthOrigin.Iem,
            new MosRow(station, "GFS", Z(runtime), DecisionClock.MosMaxForecastTime(date ?? Date), nx is null ? null : decimal.Parse(nx, System.Globalization.CultureInfo.InvariantCulture), null));
    }

    /// <summary>A pure model: its forecast is a function of the view alone.</summary>
    private sealed class ToyMosModel : IForecastModel
    {
        public string Id => "toy-mos/1";
        public Forecast? Forecast(MarketSnapshot snapshot, IEvidenceView evidence)
        {
            var m = evidence.LatestMosMax("KNYC", Date);
            if (m is null) return null;
            return new Forecast(Id, m.MaxTempF / 100m, 0.05m, "toy", evidence.AsOf, $"{m.Model}:{Ts.ToText(m.Runtime)}:{m.MaxTempF}", Synthetic: true);
        }
    }

    private static MarketSnapshot Snapshot(DateTimeOffset at)
    {
        using var t = new TempDb();
        var clock = new FixedClock(at);
        Fx.Record(t.Db, clock, Fx.Open, Fx.T50);
        return new MarketStore(t.Db).Latest(Fx.T50)!;
    }

    [Fact]
    public void A_run_is_visible_only_after_its_cycle_time_plus_the_lag()
    {
        using var t = new TempDb();
        Mos(t.Db, "2026-11-23T12:00:00Z", "52");
        Assert.Null(new ArchiveEvidence(t.Db, Z("2026-11-23T17:59:59Z")).LatestMosMax("KNYC", Date));
        var seen = new ArchiveEvidence(t.Db, Z("2026-11-23T18:00:00Z")).LatestMosMax("KNYC", Date)!;
        Assert.Equal((Z("2026-11-23T12:00:00Z"), 52m, "GFS"), (seen.Runtime, seen.MaxTempF, seen.Model));
    }

    [Fact]
    public void A_later_run_cannot_change_what_an_earlier_view_returns()
    {
        using var t = new TempDb();
        Mos(t.Db, "2026-11-23T12:00:00Z", "52");
        var decision = Z("2026-11-23T20:00:00Z");
        var before = new ArchiveEvidence(t.Db, decision).LatestMosMax("KNYC", Date);

        Mos(t.Db, "2026-11-23T18:00:00Z", "61");   // published after the decision (18Z + 6 h = 24Z)
        Mos(t.Db, "2026-11-24T00:00:00Z", "63");

        Assert.Equal(before, new ArchiveEvidence(t.Db, decision).LatestMosMax("KNYC", Date));
        Assert.Equal(52m, new ArchiveEvidence(t.Db, decision).LatestMosMax("KNYC", Date)!.MaxTempF);
        // The same data seen from a later decision time does move, so the view is doing the filtering, not the data.
        Assert.Equal(63m, new ArchiveEvidence(t.Db, Z("2026-11-24T06:00:00Z")).LatestMosMax("KNYC", Date)!.MaxTempF);
    }

    [Fact]
    public void A_model_forecast_is_identical_before_and_after_later_vintages_arrive()
    {
        using var t = new TempDb();
        var snap = Snapshot(Z("2026-11-23T20:00:00Z"));
        Mos(t.Db, "2026-11-23T12:00:00Z", "52");
        var view = new ArchiveEvidence(t.Db, snap.ObservedAt);
        var before = EvidenceGuard.Run(new ToyMosModel(), snap, view);

        Mos(t.Db, "2026-11-24T00:00:00Z", "90");
        Mos(t.Db, "2026-11-24T12:00:00Z", "95");
        var after = EvidenceGuard.Run(new ToyMosModel(), snap, new ArchiveEvidence(t.Db, snap.ObservedAt));

        Assert.NotNull(before);
        Assert.Equal(before, after);
        Assert.Equal(0.52m, after!.ProbabilityYes);
    }

    [Fact]
    public void A_run_with_no_daily_max_is_skipped_for_the_newest_run_that_has_one()
    {
        using var t = new TempDb();
        Mos(t.Db, "2026-11-23T00:00:00Z", "50");
        Mos(t.Db, "2026-11-23T06:00:00Z", null);   // newer, but no value for the date
        var seen = new ArchiveEvidence(t.Db, Z("2026-11-23T20:00:00Z")).LatestMosMax("KNYC", Date)!;
        Assert.Equal((Z("2026-11-23T00:00:00Z"), 50m), (seen.Runtime, seen.MaxTempF));
    }

    [Fact]
    public void Evidence_is_specific_to_the_station_and_the_climate_date()
    {
        using var t = new TempDb();
        Mos(t.Db, "2026-11-23T12:00:00Z", "52");
        var view = new ArchiveEvidence(t.Db, Z("2026-11-24T00:00:00Z"));
        Assert.Null(view.LatestMosMax("KJFK", Date));
        Assert.Null(view.LatestMosMax("KNYC", Date.AddDays(1)));
    }

    [Fact]
    public void The_lag_is_honoured_when_a_measured_value_replaces_the_assumption()
    {
        using var t = new TempDb();
        Mos(t.Db, "2026-11-23T12:00:00Z", "52");
        var at = Z("2026-11-23T16:30:00Z");
        Assert.Null(new ArchiveEvidence(t.Db, at).LatestMosMax("KNYC", Date));
        Assert.NotNull(new ArchiveEvidence(t.Db, at, TimeSpan.FromHours(4)).LatestMosMax("KNYC", Date));
    }

    [Fact]
    public void Evidence_dated_after_the_snapshot_is_refused()
    {
        var snap = Snapshot(Z("2026-11-23T20:00:00Z"));
        var ex = Assert.Throws<KalshiBotException>(() => EvidenceGuard.Run(new ToyMosModel(), snap, new EmptyEvidence(snap.ObservedAt.AddSeconds(1))));
        Assert.Contains("look-ahead", ex.Message);
        Assert.Null(EvidenceGuard.Run(new ToyMosModel(), snap, new EmptyEvidence(snap.ObservedAt)));   // equal is allowed
        Assert.Null(EvidenceGuard.Run(new ToyMosModel(), snap, new EmptyEvidence(snap.ObservedAt.AddHours(-3))));
    }

    [Fact]
    public void The_paper_engine_refuses_look_ahead_evidence()
    {
        var (t, clock) = Fx.Ready();
        using var _ = t;
        var future = new EmptyEvidence(clock.Now.AddDays(1));
        Assert.Throws<KalshiBotException>(() => Fx.Engine(t.Db, clock).Run("paper", Fx.T50, Fx.Strategy(), new NoValidatedModel(), evidence: future));
        Assert.Equal(0L, t.Db.Scalar<long>("SELECT COUNT(*) FROM paper_decisions"));
    }
}
