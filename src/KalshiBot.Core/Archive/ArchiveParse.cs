using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using KalshiBot.Core.Common;

namespace KalshiBot.Core.Archive;

/// <summary>One MOS forecast time. <see cref="NX"/> is the daily max/min (the guidance reports both alternately).</summary>
public sealed record MosRow(string Station, string Model, DateTimeOffset Runtime, DateTimeOffset ForecastTime, decimal? NX, decimal? Tmp);

/// <summary>One hourly candle. Values are quote closes (bid/ask) and the trade close; null where the source had none.</summary>
public sealed record CandleRow(DateTimeOffset EndPeriod, decimal? YesBidClose, decimal? YesAskClose, decimal? PriceClose, decimal? Volume, decimal? OpenInterest);

public static class MosCsv
{
    private static readonly string[] Required = ["runtime", "ftime", "model", "n_x", "tmp", "station"];

    /// <summary>IEM <c>mos.py</c> CSV. A header or row that does not fit is an error: nothing is skipped or guessed.</summary>
    public static List<MosRow> Parse(string body)
    {
        using var reader = new StringReader(body);
        var header = reader.ReadLine() ?? throw new FormatException("Empty MOS response.");
        var cols = header.Split(',');
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < cols.Length; i++) index.TryAdd(cols[i].Trim(), i);
        foreach (var r in Required)
            if (!index.ContainsKey(r)) throw new FormatException($"Unexpected MOS header (missing '{r}'): {Trim(header)}");

        var rows = new List<MosRow>();
        var lineNo = 1;
        for (var line = reader.ReadLine(); line is not null; line = reader.ReadLine())
        {
            lineNo++;
            if (line.Length == 0) continue;
            var f = line.Split(',');
            if (f.Length != cols.Length) throw new FormatException($"MOS line {lineNo}: expected {cols.Length} fields, got {f.Length}.");
            rows.Add(new MosRow(f[index["station"]], f[index["model"]], Time(f[index["runtime"]], lineNo), Time(f[index["ftime"]], lineNo),
                Number(f[index["n_x"]], lineNo, "n_x"), Number(f[index["tmp"]], lineNo, "tmp")));
        }
        return rows;
    }

    private static DateTimeOffset Time(string s, int line) =>
        DateTimeOffset.TryParseExact(s.Trim(), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t)
            ? t : throw new FormatException($"MOS line {line}: bad time '{s}'.");

    private static decimal? Number(string s, int line, string col) =>
        string.IsNullOrWhiteSpace(s) ? null
        : decimal.TryParse(s, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var d) && Math.Abs(d) < 1000m
            ? d : throw new FormatException($"MOS line {line}: bad {col} '{s}'.");

    private static string Trim(string s) => s.Length <= 120 ? s : s[..120] + "...";
}

public static class CandleParse
{
    /// <summary>
    /// A candlestick response. The series endpoint names fields <c>close_dollars</c> / <c>volume_fp</c>; the historical one
    /// <c>close</c> / <c>volume</c>. Both are accepted and mean the same thing.
    /// </summary>
    public static List<CandleRow> Page(string body)
    {
        var root = JsonNode.Parse(body)?.AsObject() ?? throw new FormatException("Empty body.");
        var arr = root["candlesticks"] as JsonArray ?? throw new FormatException("Missing 'candlesticks'.");
        var list = new List<CandleRow>(arr.Count);
        foreach (var node in arr)
        {
            var c = node as JsonObject ?? throw new FormatException("Candle is not an object.");
            if (c["end_period_ts"] is not JsonValue ts || !ts.TryGetValue<long>(out var seconds))
                throw new FormatException("Candle without end_period_ts.");
            list.Add(new CandleRow(DateTimeOffset.FromUnixTimeSeconds(seconds),
                Price(c["yes_bid"], "close"), Price(c["yes_ask"], "close"), Price(c["price"], "close"),
                Count(c, "volume_fp", "volume"), Count(c, "open_interest_fp", "open_interest")));
        }
        return list;
    }

    private static decimal? Price(JsonNode? obj, string name)
    {
        if (obj is not JsonObject o) return null;
        var d = Value(o[name + "_dollars"]) ?? Value(o[name]);
        if (d is { } v && (v < 0m || v > 1m)) throw new FormatException($"Candle price {v} outside 0..1.");
        return d;
    }

    private static decimal? Count(JsonObject c, string a, string b)
    {
        var d = Value(c[a]) ?? Value(c[b]);
        return d is < 0m ? throw new FormatException($"Negative count {d}.") : d;
    }

    private static decimal? Value(JsonNode? n) => n switch
    {
        JsonValue v when v.GetValueKind() == JsonValueKind.String => Dec.ParseOrNull(v.GetValue<string>()),
        JsonValue v when v.GetValueKind() == JsonValueKind.Number => v.GetValue<decimal>(),
        _ => null,
    };
}

/// <summary>
/// The Phase 3 decision time and the inputs available at it (docs/PHASE3_PLAN.md section 5.1). Pure functions of the
/// climate date, so retrospective and prospective runs ask the same question.
/// </summary>
public static class DecisionClock
{
    /// <summary>
    /// How long after its cycle time a GFS MOS run is assumed to be available. NOT verified: the archive carries only the
    /// cycle time, and NWS publication times could not be read from here. Six hours is deliberately generous (the product
    /// is normally out in about four), and leaves the 12Z run available an hour before the earliest decision time. The live
    /// collector records when each run is first seen so this can be measured and replaced.
    /// </summary>
    public static readonly TimeSpan AssumedMosLag = TimeSpan.FromHours(6);

    private static readonly TimeZoneInfo Eastern = Find();

    private static TimeZoneInfo Find()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("America/New_York"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); }
    }

    /// <summary>The day before the climate date at 15:00 America/New_York, in UTC (20:00Z in winter, 19:00Z in summer).</summary>
    public static DateTimeOffset DecisionTimeUtc(DateOnly climateDate) =>
        new(TimeZoneInfo.ConvertTimeToUtc(climateDate.AddDays(-1).ToDateTime(new TimeOnly(15, 0)), Eastern), TimeSpan.Zero);

    /// <summary>The model cycle used for the climate date: 12Z on the day before.</summary>
    public static DateTimeOffset MosRuntime(DateOnly climateDate) => Utc(climateDate.AddDays(-1), 12);

    /// <summary>
    /// The MOS forecast time carrying the max temperature for a climate date: 00Z the next day, the end of the 12Z to 00Z
    /// daytime window (covers 7 or 8 am to 7 or 8 pm local). Checked against settled highs on real data (see the plan).
    /// </summary>
    public static DateTimeOffset MosMaxForecastTime(DateOnly climateDate) => Utc(climateDate.AddDays(1), 0);

    public static bool MosAvailable(DateTimeOffset runtime, DateTimeOffset at) => runtime + AssumedMosLag <= at;

    /// <summary>Candles needed for a climate date: noon UTC the day before (before listing) to 06Z on the day (after local midnight).</summary>
    public static (DateTimeOffset Start, DateTimeOffset End) CandleWindow(DateOnly climateDate) =>
        (Utc(climateDate.AddDays(-1), 12), Utc(climateDate, 6));

    private static DateTimeOffset Utc(DateOnly d, int hour) => new(d.ToDateTime(new TimeOnly(hour, 0)), TimeSpan.Zero);
}
