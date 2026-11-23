using System.Globalization;

namespace KalshiBot.Core.Common;

/// <summary>Reporting day in America/Chicago (spec 6.3). Storage stays UTC; only day boundaries use this zone.</summary>
public static class ReportingDay
{
    public static readonly TimeZoneInfo Zone = FindZone();

    private static TimeZoneInfo FindZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("America/Chicago"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time"); }
    }

    public static DateOnly For(DateTimeOffset utc) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utc, Zone).DateTime);

    /// <summary>UTC instant of local midnight. Chicago DST changes happen at 02:00, so midnight is never ambiguous.</summary>
    public static DateTimeOffset StartUtc(DateOnly day)
    {
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, Zone.GetUtcOffset(local)).ToUniversalTime();
    }

    public static string Text(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

/// <summary>
/// Exclusive OS file lock, released by the OS if the process dies (no stale-lock cleanup needed).
/// Used for "one research process at a time" and one paper executor per account (spec 3.1, 4.3).
/// </summary>
public sealed class ProcessLock : IDisposable
{
    private readonly FileStream _stream;
    public string Path { get; }

    private ProcessLock(FileStream stream, string path)
    {
        _stream = stream;
        Path = path;
    }

    public static ProcessLock? TryAcquire(string path)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        try
        {
            var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            fs.SetLength(0);
            var pid = System.Text.Encoding.UTF8.GetBytes(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            fs.Write(pid);
            fs.Flush();
            return new ProcessLock(fs, path);
        }
        catch (IOException)
        {
            return null;
        }
    }

    public static ProcessLock Acquire(string path, string what) =>
        TryAcquire(path) ?? throw new KalshiBotException($"Another process holds the {what} lock ({path}); refusing to run concurrently.");

    public void Dispose() => _stream.Dispose();
}

/// <summary>A clock that only moves when told to. Used by the frozen fixture replay so its output is reproducible.</summary>
public sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; private set; } = start;
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan by) => Now += by;
    public void Set(DateTimeOffset to) => Now = to;
}
