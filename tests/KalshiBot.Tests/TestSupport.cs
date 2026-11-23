using System.Net;
using System.Text.Json.Nodes;
using KalshiBot.Core.Common;
using KalshiBot.Core.Kalshi;
using KalshiBot.Core.Recording;
using KalshiBot.Core.Research;
using KalshiBot.Core.Storage;

namespace KalshiBot.Tests;

public sealed class FixedClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan by) => Now += by;
    public static FixedClock Default() => new(new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero));
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
}

public static class Fx
{
    public const string T50 = "KXSYNTHWX-99DEC31-T50";
    public const string T60 = "KXSYNTHWX-99DEC31-T60";

    public static string Root => System.IO.Path.Combine(AppContext.BaseDirectory, "fixtures", "synthetic");
    public static string Open => System.IO.Path.Combine(Root, "kalshi-open");
    public static string Settled => System.IO.Path.Combine(Root, "kalshi-settled");

    public static KalshiPublicClient Client(string dir, TimeProvider clock) =>
        new(new HttpClient(new FixtureHandler(dir)), KalshiEndpoint.Fixture, clock, new KalshiClientOptions { MinInterval = TimeSpan.Zero });

    public static CaptureSummary Record(Db db, TimeProvider clock, string dir, params string[] tickers) =>
        new Recorder(db, Client(dir, clock), clock).CaptureAsync(tickers.Length == 0 ? [T50, T60] : tickers).GetAwaiter().GetResult();

    public static string MarketJson(string dir, string ticker) => File.ReadAllText(System.IO.Path.Combine(dir, "markets", ticker + ".json"));

    /// <summary>A well-formed result for a packet; tests mutate it to probe the strict importer.</summary>
    public static JsonObject ValidResult(ExportedPacket p, TimeProvider clock, decimal? prob = 0.6m) => new()
    {
        ["schema"] = ResearchContract.ResultSchema,
        ["packet_id"] = p.PacketId,
        ["ticker"] = p.Ticker,
        ["rules_sha256"] = JsonNode.Parse(p.Json)!["rules"]!["rules_sha256"]!.GetValue<string>(),
        ["probability_yes"] = prob,
        ["abstain_reason"] = prob is null ? "insufficient evidence" : null,
        ["confidence"] = "low",
        ["as_of"] = Ts.ToText(clock.GetUtcNow()),
        ["summary"] = "test summary",
        ["key_factors"] = new JsonArray("factor"),
        ["resolution_risks"] = new JsonArray("risk"),
        ["sources"] = new JsonArray(new JsonObject
        {
            ["title"] = "Example", ["url"] = "https://example.com/a", ["retrieved_at"] = "2026-11-23T11:00:00Z",
        }),
    };
}
