using KalshiBot.Core.Status;
using KalshiBot.Core.Storage;

namespace KalshiBot.Cli;

/// <summary>Read-only local status page. Binds to loopback only; there are no write endpoints.</summary>
public static class StatusServer
{
    public static async Task RunAsync(string dbPath, int port)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        var app = builder.Build();
        app.MapGet("/", () =>
        {
            using var db = Db.Open(dbPath);
            return Results.Content(StatusReport.Build(db, TimeProvider.System).ToHtml(), "text/html; charset=utf-8");
        });
        Console.WriteLine($"status page: http://127.0.0.1:{port}/  (Ctrl+C to stop)");
        await app.RunAsync();
    }
}
