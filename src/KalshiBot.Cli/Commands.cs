using System.Globalization;
using System.Text.Json.Nodes;
using KalshiBot.Core.Common;
using KalshiBot.Core.Kalshi;
using KalshiBot.Core.Recording;
using KalshiBot.Core.Research;
using KalshiBot.Core.Status;
using KalshiBot.Core.Storage;
using KalshiBot.Core.Trading;

namespace KalshiBot.Cli;

public static class Commands
{
    private static readonly HashSet<string> Flags = ["demo", "force", "usage-credits-disabled"];
    private const string DefaultAccount = "paper";
    private static readonly TimeProvider Clock = TimeProvider.System;

    public const string Usage = """
        kb — Kalshi research + paper trading (OBSERVE/PAPER only; no live execution exists)
        spec: kalshi-claude-subscription-spec.md

        global:  --db PATH            SQLite file (default $KALSHIBOT_DB or data/kalshibot.db). One data origin per file.

        watch add TICKER...           add markets to record          watch list | watch remove TICKER
        watch add-series SERIES       record every open market in a series each capture   watch remove-series SERIES
        discover --series SERIES      list open tickers in a series (live, read-only, not stored) [--demo]
        record                        record watched markets once from Kalshi production (public endpoints)
               [--demo]               ...from Kalshi demo instead (use a separate --db)
               [--fixtures DIR]       ...from SYNTHETIC fixture files instead (use a separate --db)
               [--ticker T]...        ...these tickers instead of the watchlist
               [--repeat N --interval SECONDS]
        markets                       latest snapshot per market
        rules TICKER                  rules versions recorded for a market

        research export TICKER [--out DIR]          packet JSON + paste-ready prompt (default data/research)
        research import FILE [--source manual|synthetic-fixture]   strict import of a research record
        research attest-billing --usage-credits-disabled            record that YOU checked usage credits are off
        research run PACKET_ID [--claude PATH] [--model NAME] [--force]   one guarded subscription round trip
        research list
        research score                              Brier score of research probabilities vs settled outcomes

        paper init [--account NAME] [--cash AMOUNT]  paper account bound to this db's data origin (default 500.00)
        paper run TICKER [--account NAME] [--min-edge X] [--series S]...
                                      decide -> risk -> reserve; fills only on a later observation (paper resolve)
        paper resolve [--account NAME]               fill/expire pending orders against post-arrival observations
        paper settle [--account NAME]                settle finalized markets
        paper verify [--account NAME]                rebuild and check the ledger
        halt REASON | resume                         safety halt (persists across restarts and days)

        status [--html FILE]          text status, or write the HTML status page
        serve [--port 5080]           serve the status page on http://127.0.0.1:PORT (read-only)
        replay [--db PATH]            frozen SYNTHETIC fixture replay, deterministic (alias: demo)
        """;

    public static async Task<int> RunAsync(string[] argv)
    {
        var a = new Args(argv, Flags);
        if (a.Positionals.Count == 0 || a.Positionals[0] is "help" or "-h" or "--help") { Console.WriteLine(Usage); return 0; }
        var dbPath = a.Opt("db") ?? Environment.GetEnvironmentVariable("KALSHIBOT_DB") ?? Path.Combine("data", "kalshibot.db");
        var cmd = a.Positionals[0];
        var sub = a.Positionals.Count > 1 ? a.Positionals[1] : null;

        if (cmd is "replay" or "demo") return Demo.Run(a);

        using var db = Db.Open(dbPath);
        switch (cmd)
        {
            case "watch": return Watch(db, a, sub);
            case "discover": return await Discover(a);
            case "record": return await Record(db, a);
            case "markets": a.EnsureAllUsed(); return Markets(db);
            case "rules": a.EnsureAllUsed(); return Rules(db, a.Pos(1, "TICKER"));
            case "research": return await Research(db, a, sub);
            case "paper": return Paper(db, a, sub);
            case "halt":
                a.EnsureAllUsed();
                new Controls(db, Clock).SetHalt(true, string.Join(' ', a.Positionals.Skip(1)) is { Length: > 0 } r ? r : "manual halt");
                Console.WriteLine("kill switch ENGAGED: all paper orders will be rejected");
                return 0;
            case "resume":
                a.EnsureAllUsed();
                new Controls(db, Clock).SetHalt(false, "resumed");
                Console.WriteLine("kill switch cleared");
                return 0;
            case "status":
            {
                var html = a.Opt("html");
                a.EnsureAllUsed();
                var report = StatusReport.Build(db, Clock);
                if (html is null) Console.Write(report.ToText());
                else { File.WriteAllText(html, report.ToHtml()); Console.WriteLine($"wrote {Path.GetFullPath(html)}"); }
                return 0;
            }
            case "serve":
            {
                var port = int.Parse(a.Opt("port") ?? "5080", CultureInfo.InvariantCulture);
                a.EnsureAllUsed();
                db.Dispose();
                await StatusServer.RunAsync(dbPath, port);
                return 0;
            }
            default: throw new UsageException($"Unknown command '{cmd}'.");
        }
    }

    private static int Watch(Db db, Args a, string? sub)
    {
        a.EnsureAllUsed();
        var w = new Watchlist(db, Clock);
        switch (sub)
        {
            case "add":
                foreach (var t in a.Positionals.Skip(2)) Console.WriteLine(w.Add(t) ? $"watching {t}" : $"already watching {t}");
                return 0;
            case "remove":
                Console.WriteLine(w.Remove(a.Pos(2, "TICKER")) ? "removed" : "not on watchlist");
                return 0;
            case "add-series":
                foreach (var sr in a.Positionals.Skip(2)) Console.WriteLine(w.AddSeries(sr) ? $"watching series {sr} (all open markets)" : $"already watching series {sr}");
                return 0;
            case "remove-series":
                Console.WriteLine(w.RemoveSeries(a.Pos(2, "SERIES")) ? "removed" : "series not on watchlist");
                return 0;
            case "list" or null:
                foreach (var t in w.List()) Console.WriteLine(t);
                foreach (var sr in w.ListSeries()) Console.WriteLine($"{sr} (series)");
                return 0;
            default: throw new UsageException($"Unknown watch subcommand '{sub}'.");
        }
    }

    internal static KalshiPublicClient MakeClient(Args a)
    {
        var fixtures = a.Opt("fixtures");
        var demo = a.Flag("demo");
        if (fixtures is not null && demo) throw new UsageException("Use either --demo or --fixtures, not both.");
        if (fixtures is not null)
        {
            if (!Directory.Exists(fixtures)) throw new UsageException($"Fixture directory not found: {fixtures}");
            return new KalshiPublicClient(new HttpClient(new FixtureHandler(fixtures)), KalshiEndpoint.Fixture, Clock,
                new KalshiClientOptions { MinInterval = TimeSpan.Zero });
        }
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("kalshi-bot/0.2 (personal research; read-only)");
        return new KalshiPublicClient(http, demo ? KalshiEndpoint.Demo : KalshiEndpoint.Production, Clock);
    }

    private static async Task<int> Discover(Args a)
    {
        var series = a.Opt("series") ?? throw new UsageException("discover needs --series SERIES.");
        var client = MakeClient(a);
        a.EnsureAllUsed();
        string? cursor = null;
        var count = 0;
        do
        {
            var raw = await client.ListOpenMarketsAsync(series, cursor, CancellationToken.None);
            if (!raw.IsSuccess) throw new KalshiBotException($"GET {raw.Url} -> HTTP {raw.Status}");
            var (tickers, next) = KalshiParse.MarketList(raw.Body);
            foreach (var t in tickers) Console.WriteLine(t);
            count += tickers.Count;
            cursor = next;
        } while (cursor is not null && count < 2000);
        return 0;
    }

    private static async Task<int> Record(Db db, Args a)
    {
        var client = MakeClient(a);
        var tickers = a.Many("ticker");
        var repeat = int.Parse(a.Opt("repeat") ?? "1", CultureInfo.InvariantCulture);
        var interval = TimeSpan.FromSeconds(int.Parse(a.Opt("interval") ?? "60", CultureInfo.InvariantCulture));
        a.EnsureAllUsed();
        var series = new List<string>();
        if (tickers.Count == 0)
        {
            tickers = new Watchlist(db, Clock).List();
            series = new Watchlist(db, Clock).ListSeries();
        }
        if (tickers.Count == 0 && series.Count == 0)
            throw new UsageException("Nothing to record: add markets with 'kb watch add TICKER' or 'kb watch add-series SERIES', or pass --ticker.");
        using var recorderLock = ProcessLock.Acquire(LockPath(db, "recorder"), "recorder");
        if (repeat < 1 || interval < TimeSpan.FromSeconds(5)) throw new UsageException("--repeat must be >= 1 and --interval >= 5 seconds.");

        var recorder = new Recorder(db, client, Clock);
        var exit = 0;
        for (var i = 0; i < repeat; i++)
        {
            if (i > 0) await Task.Delay(interval);
            var s = await recorder.CaptureAsync(tickers, series);
            Console.WriteLine($"capture run #{s.RunId}: {s.Status} [{(DataOrigin.IsSynthetic(s.DataOrigin) ? "SYNTHETIC" : s.DataOrigin)}] " +
                              $"{s.Snapshots} snapshot(s), {s.RulesChanges} new rules version(s)");
            foreach (var e in s.Errors) Console.WriteLine($"  ! {e}");
            if (s.Status == "failed") exit = 1;
        }
        return exit;
    }

    private static int Markets(Db db)
    {
        foreach (var s in new MarketStore(db).LatestPerTicker())
            Console.WriteLine($"{s.Ticker} [{(s.IsSynthetic ? "SYNTHETIC" : s.DataOrigin)}] {s.Status} " +
                              $"yes {P(s.YesBid)}/{P(s.YesAsk)} no {P(s.NoBid)}/{P(s.NoAsk)} observed {Ts.ToText(s.ObservedAt)} snapshot #{s.Id}");
        return 0;
    }

    private static int Rules(Db db, string ticker)
    {
        var history = new MarketStore(db).RulesHistory(ticker);
        if (history.Count == 0) throw new KalshiBotException($"No rules recorded for {ticker}.");
        foreach (var r in history)
        {
            Console.WriteLine($"rules #{r.Id} sha256 {r.Sha256[..16]} seen {Ts.ToText(r.FirstSeenAt)} .. {Ts.ToText(r.LastSeenAt)} (raw #{r.FirstRawId}..#{r.LastRawId})");
            Console.WriteLine($"  primary:   {r.Primary}");
            Console.WriteLine($"  secondary: {r.Secondary}");
            if (r.EarlyCloseCondition is { } e) Console.WriteLine($"  early close: {e}");
        }
        return 0;
    }

    private static async Task<int> Research(Db db, Args a, string? sub)
    {
        switch (sub)
        {
            case "export":
            {
                var ticker = a.Pos(2, "TICKER");
                var outDir = a.Opt("out") ?? Path.Combine("data", "research");
                a.EnsureAllUsed();
                var p = new PacketExporter(db, Clock).Export(ticker);
                Directory.CreateDirectory(outDir);
                var file = Path.Combine(outDir, $"{p.PacketId}.packet.json");
                var prompt = Path.Combine(outDir, $"{p.PacketId}.prompt.md");
                File.WriteAllText(file, p.Json);
                File.WriteAllText(prompt, PacketExporter.ManualPrompt(p.Json));
                Console.WriteLine($"packet {p.PacketId} [{(DataOrigin.IsSynthetic(p.DataOrigin) ? "SYNTHETIC" : p.DataOrigin)}]{(p.AlreadyExisted ? " (unchanged, already exported)" : "")}");
                Console.WriteLine($"  {Path.GetFullPath(file)}");
                Console.WriteLine($"  {Path.GetFullPath(prompt)}   <- paste into Claude for a manual round trip");
                Console.WriteLine($"next: kb research run {p.PacketId}   or   kb research import RESULT.json");
                return 0;
            }
            case "import":
            {
                var file = a.Pos(2, "FILE");
                var source = a.Opt("source") ?? "manual";
                a.EnsureAllUsed();
                if (source == "claude-subscription-cli") throw new UsageException("That source kind is reserved for 'kb research run'.");
                if (!File.Exists(file)) throw new UsageException($"File not found: {Path.GetFullPath(file)}");
                try
                {
                    var r = new ResearchImporter(db, Clock).Import(File.ReadAllText(file), source);
                    Console.WriteLine(r.AlreadyImported
                        ? $"already imported as record #{r.RecordId} ({r.ResearchId})"
                        : $"imported record #{r.RecordId} '{r.ResearchId}' for {r.MarketTicker}: {r.Disposition}" +
                          (r.ExperimentalProbability is { } p ? $", experimental p_yes={Dec.Show(p)} (scoring only)" : "") + " — EXPERIMENTAL");
                    return 0;
                }
                catch (ResearchRejectedException e)
                {
                    Console.Error.WriteLine("REJECTED:");
                    foreach (var err in e.Errors) Console.Error.WriteLine($"  - {err}");
                    return 1;
                }
            }
            case "attest-billing":
            {
                if (!a.Flag("usage-credits-disabled"))
                    throw new UsageException("Check claude.ai → Settings → Usage/Billing yourself first, then pass --usage-credits-disabled to record that statement.");
                a.EnsureAllUsed();
                var store = new ResearchStore(db);
                store.Attest(Clock.GetUtcNow());
                Console.WriteLine($"recorded owner attestation: \"{ResearchStore.UsageCreditsStatement}\" ({Ts.ToText(Clock.GetUtcNow())}).");
                Console.WriteLine("This is your statement, not a machine verification; re-check and re-attest at least every 30 days.");
                return 0;
            }
            case "run":
            {
                var packetId = a.Pos(2, "PACKET_ID");
                var opts = new ClaudeRunOptions
                {
                    ClaudePath = a.Opt("claude") ?? Environment.GetEnvironmentVariable("KALSHIBOT_CLAUDE") ?? "claude",
                    Model = a.Opt("model"),
                    Force = a.Flag("force"),
                };
                a.EnsureAllUsed();
                var runner = new ClaudeResearchRunner(db, Clock, new ProcessRunner(), opts);
                Console.WriteLine("checking: attestation, single-run lock, environment, settings files, CLI version, `claude auth status` (subscription only)...");
                var r = await runner.RunAsync(packetId, ClaudeResearchRunner.CurrentEnvironment());
                Console.WriteLine($"research run #{r.RunId}: {r.Outcome.ToUpperInvariant()} (auth {r.Verdict.AuthMethod ?? "not checked"}{(r.Verdict.SubscriptionType is { } st ? ", " + st : "")})");
                Console.WriteLine($"  {r.Detail}");
                if (r.Imported is { } imp)
                    Console.WriteLine($"  record #{imp.RecordId} '{imp.ResearchId}': {imp.Disposition}" +
                                      (imp.ExperimentalProbability is { } p ? $", experimental p_yes={Dec.Show(p)} (scoring only)" : "") + " — EXPERIMENTAL");
                return r.Outcome is "imported" or "skipped" ? 0 : 1;
            }
            case "list":
                a.EnsureAllUsed();
                foreach (var r in new ResearchStore(db).Records(50))
                    Console.WriteLine($"#{r.Id} {Ts.ToText(r.ImportedAt)} {r.ResearchId} {r.MarketTicker} {r.Disposition} " +
                                      $"p_yes={(r.ExperimentalProbability is { } p ? Dec.Show(p) + " (scoring only)" : "-")} via {r.SourceKind} " +
                                      $"[{(DataOrigin.IsSynthetic(r.DataOrigin) ? "SYNTHETIC" : r.DataOrigin)}] valid until {Ts.ToText(r.ValidUntilUtc)}");
                return 0;
            case "score":
            {
                a.EnsureAllUsed();
                var score = ResearchScoring.Score(db);
                Console.WriteLine($"research records: {score.Records}; with an experimental probability: {score.WithProbability}; awaiting settlement: {score.Unresolved}");
                foreach (var r in score.Scored)
                    Console.WriteLine($"  #{r.RecordId} {r.MarketTicker} p_yes={Dec.Show(r.Probability)} outcome={(r.Outcome == 1 ? "yes" : "no")} " +
                                      $"brier={Dec.Show(Math.Round(r.Brier, 4))} market_mid={(r.MarketMid is { } m ? Dec.Show(m) : "-")} " +
                                      $"market_brier={(r.MarketBrier is { } mb ? Dec.Show(Math.Round(mb, 4)) : "-")} via {r.SourceKind}" +
                                      (DataOrigin.IsSynthetic(r.DataOrigin) ? " [SYNTHETIC]" : ""));
                if (score.Scored.Count > 0)
                    Console.WriteLine($"mean brier {Dec.Show(Math.Round(score.MeanBrier!.Value, 4))} vs market {(score.MeanMarketBrier is { } mm ? Dec.Show(Math.Round(mm, 4)) : "-")} " +
                                      $"over {score.Scored.Count} record(s) in {score.IndependentEvents} independent event(s). " +
                                      "Lower is better. A handful of events is not evidence of skill.");
                return 0;
            }
            default: throw new UsageException($"Unknown research subcommand '{sub}'.");
        }
    }

    private static int Paper(Db db, Args a, string? sub)
    {
        var account = a.Opt("account") ?? DefaultAccount;
        var ledger = new PaperLedger(db, Clock);
        var origin = DatabaseOrigin.Get(db);
        switch (sub)
        {
            case "init":
            {
                var cash = Dec.Parse(a.Opt("cash") ?? Dec.ToText(RiskLimits.PaperFixture.StartingBankroll));
                a.EnsureAllUsed();
                if (origin is null) throw new KalshiBotException("Record data first ('kb record ...') so this database's data origin is known.");
                var id = ledger.EnsureAccount(account, cash, origin);
                Console.WriteLine($"paper account '{account}' (#{id}) with {Dec.Show(cash)} PAPER dollars on {(DataOrigin.IsSynthetic(origin) ? "SYNTHETIC" : origin)} data");
                return 0;
            }
            case "run":
            {
                var ticker = a.Pos(2, "TICKER");
                var synthetic = origin is not null && DataOrigin.IsSynthetic(origin);
                var minEdgeText = a.Opt("min-edge") ?? (synthetic ? "0.05" : null);
                var series = a.Many("series");
                if (series.Count == 0 && synthetic) series = ["KXSYNTHWX"];
                a.EnsureAllUsed();
                if (minEdgeText is null) throw new UsageException("--min-edge is required: thresholds are strategy-specific and must be predeclared (spec 5.4).");
                IForecastModel model = synthetic ? SyntheticFixtureModel.Load(Path.Combine(Demo.FindFixtures(), "model.json")) : new NoValidatedModel();
                using var executor = ProcessLock.Acquire(LockPath(db, $"paper-{account}"), $"paper executor for '{account}'");
                var engine = new PaperEngine(db, Clock, new RiskEngine(RiskLimits.PaperFixture));
                PrintRun(engine.Run(account, ticker, new EdgeThresholdStrategy(Dec.Parse(minEdgeText), 1, series), model));
                return 0;
            }
            case "resolve":
            {
                a.EnsureAllUsed();
                using var executor = ProcessLock.Acquire(LockPath(db, $"paper-{account}"), $"paper executor for '{account}'");
                var resolved = new PaperEngine(db, Clock, new RiskEngine(RiskLimits.PaperFixture)).ResolvePending(account);
                PrintResolved(resolved);
                if (resolved.Count == 0) Console.WriteLine("nothing to resolve (pending orders wait for an observation at or after their arrival time)");
                return 0;
            }
            case "settle":
            {
                a.EnsureAllUsed();
                var id = ledger.FindAccount(account) ?? throw new KalshiBotException($"No paper account '{account}'.");
                var store = new MarketStore(db);
                var n = 0;
                foreach (var t in ledger.State(id, false).Positions.Select(p => p.Ticker).Distinct().ToList())
                    if (store.Latest(t) is { } s && ledger.Settle(id, s) > 0) { n++; Console.WriteLine($"settled {t}: result {s.Result} [{(s.IsSynthetic ? "SYNTHETIC" : s.DataOrigin)}]"); }
                Console.WriteLine(n == 0 ? "nothing to settle (only markets recorded as 'finalized' with a yes/no result settle)" : $"{n} market(s) settled");
                return 0;
            }
            case "verify":
            {
                a.EnsureAllUsed();
                var id = ledger.FindAccount(account) ?? throw new KalshiBotException($"No paper account '{account}'.");
                var problems = ledger.Verify(id);
                Console.WriteLine(problems.Count == 0 ? "ledger OK" : "LEDGER PROBLEMS:\n  " + string.Join("\n  ", problems));
                return problems.Count == 0 ? 0 : 1;
            }
            default: throw new UsageException($"Unknown paper subcommand '{sub}'.");
        }
    }

    private static string LockPath(Db db, string name) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(db.Path))!, ".locks", $"{Path.GetFileName(db.Path)}.{name}.lock");

    internal static void PrintRun(PaperRunResult r)
    {
        var origin = DataOrigin.IsSynthetic(r.DataOrigin) ? "SYNTHETIC" : r.DataOrigin;
        PrintResolved(r.Resolved);
        if (r.HaltRaised is { } h) Console.WriteLine($"  SAFETY HALT RAISED: {h}");
        Console.WriteLine($"decision {r.DecisionId[..16]} ({r.StrategyId}) on snapshot #{r.SnapshotId} [{origin}]");
        Console.WriteLine($"  forecast: {(r.Forecast is { } f ? $"{f.ModelId} p_yes={Dec.Show(f.ProbabilityYes)}±{Dec.Show(f.Uncertainty)}{(f.Synthetic ? " SYNTHETIC" : "")}" : "none (no validated model: observe/abstain)")}; fees: {r.FeeScheduleId ?? "unknown"}");
        Console.WriteLine($"  strategy: {r.Rationale}");
        if (r.AlreadyProcessed) Console.WriteLine("  already processed for this snapshot and evidence; nothing new submitted (idempotent rerun)");
        foreach (var o in r.Orders)
        {
            Console.WriteLine($"  order {o.ClientOrderId}: {o.Intent.Action} {o.Intent.Quantity} {o.Intent.Side} @ {Dec.Show(o.Intent.LimitPrice)} IOC -> " +
                              (o.Verdict.Approved ? $"PENDING (PAPER), reserved {Dec.Show(o.Reserved)}, simulated arrival {Ts.ToText(o.ArrivalAt)}" : "REJECTED"));
            foreach (var reason in o.Verdict.Reasons) Console.WriteLine($"    risk: {reason}");
        }
        if (r.Orders.Any(o => o.Verdict.Approved))
            Console.WriteLine("  next: record again, then 'kb paper resolve' — fills use only observations at or after arrival");
    }

    private static void PrintResolved(IReadOnlyList<ResolvedOrder> resolved)
    {
        foreach (var o in resolved)
        {
            Console.WriteLine($"resolved order #{o.OrderId} {o.Ticker}: {o.Status.ToUpperInvariant()} ({o.Filled} filled) — {o.Reason}");
            foreach (var f in o.Fills) Console.WriteLine($"    SIMULATED fill {f.Quantity} @ {Dec.Show(f.Price)} fee {Dec.Show(f.Fee)}");
        }
    }

    private static string P(decimal? d) => d is null ? "-" : Dec.Show(d.Value);
}
