using System.Text.Json.Nodes;
using KalshiBot.Core.Common;
using KalshiBot.Core.Kalshi;
using KalshiBot.Core.Recording;
using KalshiBot.Core.Research;
using KalshiBot.Core.Status;
using KalshiBot.Core.Storage;
using KalshiBot.Core.Trading;

namespace KalshiBot.Cli;

/// <summary>
/// Offline end-to-end run on SYNTHETIC fixtures. Proves the plumbing (record -> packet -> strict import ->
/// strategy -> risk -> simulated fill -> settlement -> ledger check). The probability it imports is a fixed,
/// labeled fixture value, not a forecast; the resulting trade demonstrates mechanics, not edge.
/// </summary>
public static class Demo
{
    private const string T50 = "KXSYNTHWX-99DEC31-T50";
    private const string T60 = "KXSYNTHWX-99DEC31-T60";

    public static async Task<int> RunAsync(Args a)
    {
        var dbPath = a.Opt("db") ?? Path.Combine("data", "demo.db");
        var fixturesRoot = a.Opt("fixtures-root") ?? FindFixtures();
        a.EnsureAllUsed();
        var mainDb = Path.GetFullPath(Environment.GetEnvironmentVariable("KALSHIBOT_DB") ?? Path.Combine("data", "kalshibot.db"));
        if (Path.GetFullPath(dbPath) == mainDb) throw new UsageException("demo resets its database; point --db somewhere other than your main database.");
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            if (File.Exists(dbPath + suffix)) File.Delete(dbPath + suffix);

        var clock = TimeProvider.System;
        using var db = Db.Open(dbPath);
        Step($"fresh demo database {Path.GetFullPath(dbPath)} — everything below is SYNTHETIC");

        new Watchlist(db, clock).Add(T50);
        new Watchlist(db, clock).Add(T60);
        var open = await new Recorder(db, Fixture(fixturesRoot, "kalshi-open", clock), clock).CaptureAsync([T50, T60]);
        Step($"recorded {open.Snapshots} snapshots from fixtures/synthetic/kalshi-open (run #{open.RunId}, {open.Status})");

        var packet = new PacketExporter(db, clock).Export(T50);
        var outDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dbPath))!, "demo-research");
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, $"{packet.PacketId}.packet.json"), packet.Json);
        Step($"exported research packet {packet.PacketId[..16]} -> {outDir}");

        var rules = JsonNode.Parse(packet.Json)!["rules"]!["rules_sha256"]!.GetValue<string>();
        var result = new JsonObject
        {
            ["schema"] = ResearchContract.ResultSchema,
            ["packet_id"] = packet.PacketId,
            ["ticker"] = T50,
            ["rules_sha256"] = rules,
            ["probability_yes"] = 0.60m,
            ["abstain_reason"] = null,
            ["confidence"] = "low",
            ["as_of"] = Ts.ToText(clock.GetUtcNow()),
            ["summary"] = "SYNTHETIC FIXTURE RESULT. Fixed value used to exercise the pipeline; not a forecast.",
            ["key_factors"] = new JsonArray("synthetic fixture"),
            ["resolution_risks"] = new JsonArray("not a real market"),
            ["sources"] = new JsonArray(),
        };
        var imported = new ResultImporter(db, clock).Import(result.ToJsonString(), "synthetic-fixture");
        Step($"strict import accepted synthetic result #{imported.ResultId} (p_yes=0.60, source synthetic-fixture, unvalidated)");

        var bad = (JsonObject)result.DeepClone();
        bad["probability_yes"] = "0.6";
        bad["extra"] = "unexpected";
        try { new ResultImporter(db, clock).Import(bad.ToJsonString(), "synthetic-fixture"); }
        catch (ResultRejectedException e) { Step($"strict import rejected a malformed result: {string.Join("; ", e.Errors)}"); }

        new PaperLedger(db, clock).EnsureAccount("paper", 100m);
        Step("paper account 'paper' with 100.00 PAPER dollars");

        var engine = new PaperEngine(db, clock, new RiskEngine(RiskLimits.PaperDefaults));
        Step($"paper run {T50}:");
        Commands.PrintRun(engine.Run("paper", T50, new EdgeThresholdStrategy()));
        Step($"paper run {T50} again (same snapshot + estimate):");
        Commands.PrintRun(engine.Run("paper", T50, new EdgeThresholdStrategy()));
        Step($"paper run {T60} (no research estimate):");
        Commands.PrintRun(engine.Run("paper", T60, new EdgeThresholdStrategy()));

        var settled = await new Recorder(db, Fixture(fixturesRoot, "kalshi-settled", clock), clock).CaptureAsync([T50, T60]);
        Step($"recorded finalized markets from fixtures/synthetic/kalshi-settled (run #{settled.RunId}, {settled.Status})");
        var ledger = new PaperLedger(db, clock);
        var accountId = ledger.FindAccount("paper")!.Value;
        var store = new MarketStore(db);
        foreach (var t in new[] { T50, T60 }) ledger.Settle(accountId, store.Latest(t)!);
        var problems = ledger.Verify(accountId);
        Step($"settled; ledger verify: {(problems.Count == 0 ? "OK" : string.Join("; ", problems))}");

        var report = StatusReport.Build(db, clock);
        var html = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dbPath))!, "demo-status.html");
        File.WriteAllText(html, report.ToHtml());
        Console.WriteLine();
        Console.Write(report.ToText());
        Console.WriteLine();
        Console.WriteLine($"status page: {html}");
        return problems.Count == 0 ? 0 : 1;
    }

    private static KalshiPublicClient Fixture(string root, string set, TimeProvider clock) =>
        new(new HttpClient(new FixtureHandler(Path.Combine(root, set))), KalshiEndpoint.Fixture, clock,
            new KalshiClientOptions { MinInterval = TimeSpan.Zero });

    private static string FindFixtures()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "fixtures", "synthetic");
                if (Directory.Exists(candidate)) return candidate;
            }
        }
        throw new UsageException("Could not find fixtures/synthetic; pass --fixtures-root DIR.");
    }

    private static void Step(string text) => Console.WriteLine($"==> {text}");
}
