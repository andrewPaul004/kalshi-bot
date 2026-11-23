using System.Net;
using System.Text.Json.Nodes;
using KalshiBot.Core.Common;
using KalshiBot.Core.Kalshi;
using KalshiBot.Core.Recording;
using KalshiBot.Core.Research;
using KalshiBot.Core.Storage;
using KalshiBot.Core.Trading;

namespace KalshiBot.Tests;

public sealed class FixedClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan by) => Now += by;
    public static FixedClock Default() => new(new DateTimeOffset(2026, 11, 23, 18, 0, 0, TimeSpan.Zero));
}

/// <summary>Advances one millisecond on every read, like a real clock during a capture run.</summary>
public sealed class TickingClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    public override DateTimeOffset GetUtcNow() => _now = _now.AddMilliseconds(1);
}

/// <summary>A file-backed database in a private temp directory (file-backed so reopen/recovery is real).</summary>
public sealed class TempDb : IDisposable
{
    public string Dir { get; } = Directory.CreateTempSubdirectory("kb-test-").FullName;
    public string Path => System.IO.Path.Combine(Dir, "test.db");
    public Db Db { get; private set; }

    public TempDb() => Db = Db.Open(Path);

    public Db Reopen()
    {
        Db.Dispose();
        Db = Db.Open(Path);
        return Db;
    }

    public void Dispose()
    {
        Db.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Dir, true); } catch (IOException) { }
    }
}

public sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<string> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request.Method + " " + request.RequestUri!.PathAndQuery);
        return Task.FromResult(respond(request));
    }

    public static HttpResponseMessage Json(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
}

/// <summary>Synchronous access to fixture responses for composing scripted handlers.</summary>
internal sealed class FixtureHandlerProxy(string dir)
{
    public HttpResponseMessage Get(HttpRequestMessage req) =>
        new HttpMessageInvoker(new FixtureHandler(dir)).SendAsync(req, CancellationToken.None).GetAwaiter().GetResult();
}

public sealed class FakeProcessRunner(Func<ProcessSpec, ProcessResult> respond) : IProcessRunner
{
    public List<ProcessSpec> Calls { get; } = [];
    public List<string[]> WorkDirEntries { get; } = [];

    public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct)
    {
        Calls.Add(spec);
        WorkDirEntries.Add(Directory.GetFileSystemEntries(spec.WorkingDirectory));
        return Task.FromResult(respond(spec));
    }

    public const string SubscriptionStatus = """{"loggedIn":true,"authMethod":"claude.ai","apiProvider":"firstParty","subscriptionType":"pro"}""";

    /// <summary>A CLI that reports a version and a subscription login, and answers -p with <paramref name="stdout"/>.</summary>
    public static FakeProcessRunner Subscription(Func<ProcessSpec, string> stdout) => new(spec => spec.Arguments[0] switch
    {
        "--version" => new ProcessResult(0, "2.1.330 (Claude Code)", "", false),
        "auth" => new ProcessResult(0, SubscriptionStatus, "", false),
        _ => new ProcessResult(0, stdout(spec), "", false),
    });
}

public static class Fx
{
    public const string T50 = "KXSYNTHWX-99DEC31-T50";
    public const string T60 = "KXSYNTHWX-99DEC31-T60";

    public static string Root => System.IO.Path.Combine(AppContext.BaseDirectory, "fixtures", "synthetic");
    public static string Open => System.IO.Path.Combine(Root, "kalshi-open");
    public static string Moved => System.IO.Path.Combine(Root, "kalshi-moved");
    public static string Settled => System.IO.Path.Combine(Root, "kalshi-settled");

    public static KalshiPublicClient Client(string dir, TimeProvider clock) =>
        new(new HttpClient(new FixtureHandler(dir)), KalshiEndpoint.Fixture, clock, new KalshiClientOptions { MinInterval = TimeSpan.Zero });

    public static CaptureSummary Record(Db db, TimeProvider clock, string dir, params string[] tickers) =>
        new Recorder(db, Client(dir, clock), clock).CaptureAsync(tickers.Length == 0 ? [T50, T60] : tickers).GetAwaiter().GetResult();

    public static string MarketJson(string dir, string ticker) => File.ReadAllText(System.IO.Path.Combine(dir, "markets", ticker + ".json"));

    public static SyntheticFixtureModel Model() => SyntheticFixtureModel.Load(System.IO.Path.Combine(Root, "model.json"));

    public static EdgeThresholdStrategy Strategy(decimal minEdge = 0.05m, int contracts = 1) => new(minEdge, contracts, ["KXSYNTHWX"]);

    public static PaperEngine Engine(Db db, TimeProvider clock, RiskLimits? limits = null) =>
        new(db, clock, new RiskEngine(limits ?? RiskLimits.PaperFixture));

    /// <summary>Synthetic fixtures recorded and a 500.00 paper account opened (spec 6.2 fixture).</summary>
    public static (TempDb T, FixedClock Clock) Ready()
    {
        var t = new TempDb();
        var clock = FixedClock.Default();
        Record(t.Db, clock, Open);
        new PaperLedger(t.Db, clock).EnsureAccount("paper", 500m, DataOrigin.Synthetic);
        return (t, clock);
    }

    /// <summary>Decide on the current snapshot, record the next observation after arrival, resolve. Returns the run.</summary>
    public static PaperRunResult DecideAndFill(TempDb t, FixedClock clock, string ticker = T50, string nextObservation = "")
    {
        var r = Engine(t.Db, clock).Run("paper", ticker, Strategy(), Model());
        clock.Advance(TimeSpan.FromSeconds(10));
        Record(t.Db, clock, nextObservation == "" ? Open : nextObservation, ticker);
        Engine(t.Db, clock).ResolvePending("paper");
        return r;
    }

    /// <summary>A well-formed research record for a packet; tests mutate it to probe the strict importer.</summary>
    public static JsonObject ValidRecord(ExportedPacket p, TimeProvider clock, decimal? prob = 0.6m, string researchId = "r-1")
    {
        var packet = JsonNode.Parse(p.Json)!;
        var now = clock.GetUtcNow();
        return new JsonObject
        {
            ["schemaVersion"] = ResearchContract.RecordSchema,
            ["researchId"] = researchId,
            ["packetId"] = p.PacketId,
            ["packetHash"] = p.PacketHash,
            ["createdAtUtc"] = Ts.ToText(now),
            ["validUntilUtc"] = Ts.ToText(now.AddDays(2)),
            ["marketTicker"] = p.MarketTicker,
            ["eventTicker"] = packet["market"]!["eventTicker"]!.GetValue<string>(),
            ["seriesTicker"] = packet["market"]!["seriesTicker"]?.GetValue<string>(),
            ["marketRulesHash"] = packet["rules"]!["rulesSha256"]!.GetValue<string>(),
            ["disposition"] = "no_issue_found",
            ["evidence"] = new JsonArray(new JsonObject
            {
                ["sourceId"] = "s1", ["url"] = "https://example.com/a", ["publishedAtUtc"] = null,
                ["retrievedAtUtc"] = Ts.ToText(now.AddMinutes(-10)), ["dataHash"] = null, ["claim"] = "example claim",
            }),
            ["uncertainties"] = new JsonArray("u1"),
            ["invalidationConditions"] = new JsonArray(),
            ["hypothesis"] = null,
            ["suggestedModelChange"] = null,
            ["experimentalProbabilityYes"] = prob is null ? null : new JsonObject
            {
                ["value"] = prob, ["method"] = "test", ["uncertainty"] = "wide",
            },
            ["researcherVersion"] = new JsonObject
            {
                ["cli"] = null, ["model"] = null, ["promptVersion"] = ResearchContract.PromptVersion, ["toolConfig"] = null,
            },
        };
    }
}
