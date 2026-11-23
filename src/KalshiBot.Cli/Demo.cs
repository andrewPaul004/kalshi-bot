using KalshiBot.Core.Common;
using KalshiBot.Core.Status;
using KalshiBot.Core.Storage;

namespace KalshiBot.Cli;

/// <summary>`kb replay` (alias `demo`): the frozen fixture replay in a fresh database. Everything in it is SYNTHETIC.</summary>
public static class Demo
{
    public static int Run(Args a)
    {
        var dbPath = a.Opt("db") ?? Path.Combine("data", "replay.db");
        var fixturesRoot = a.Opt("fixtures-root") ?? FindFixtures();
        a.EnsureAllUsed();
        var mainDb = Path.GetFullPath(Environment.GetEnvironmentVariable("KALSHIBOT_DB") ?? Path.Combine("data", "kalshibot.db"));
        if (Path.GetFullPath(dbPath) == mainDb) throw new UsageException("replay resets its database; point --db somewhere other than your main database.");
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            if (File.Exists(dbPath + suffix)) File.Delete(dbPath + suffix);

        var clock = new ManualClock(FixtureReplay.Start);
        using var db = Db.Open(dbPath);
        Console.WriteLine($"==> frozen fixture replay into {Path.GetFullPath(dbPath)} — everything below is SYNTHETIC; fixture P&L is meaningless");
        FixtureReplay.Run(db, clock, fixturesRoot, line => Console.WriteLine(line.StartsWith("  ", StringComparison.Ordinal) ? line : "==> " + line));
        Console.WriteLine($"==> replay fingerprint {FixtureReplay.Fingerprint(db)[..16]} (identical on every run)");

        var report = StatusReport.Build(db, clock);
        var html = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dbPath))!, "replay-status.html");
        File.WriteAllText(html, report.ToHtml());
        Console.WriteLine();
        Console.Write(report.ToText());
        Console.WriteLine();
        Console.WriteLine($"status page: {html}");
        return 0;
    }

    public static string FindFixtures()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "fixtures", "synthetic");
                if (Directory.Exists(candidate)) return candidate;
            }
        throw new UsageException("Could not find fixtures/synthetic; pass --fixtures-root DIR.");
    }
}
