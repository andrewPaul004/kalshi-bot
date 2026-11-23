using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using KalshiBot.Core.Kalshi;

namespace KalshiBot.Core.Truth;

/// <summary>
/// Which source a market's rules text names for the settled value. Classified from <c>rules_primary</c> only, because that
/// is what the market says it settles on. KXHIGHNY's wording changed over time (docs/INTEGRATION_NOTES.md).
/// </summary>
public static class SettlementRegime
{
    public const string WeatherCompany = "weather-company";
    public const string NwsClimatologicalReport = "nws-climatological-report";
    public const string NwsDailyClimateReport = "nws-daily-climate-report";
    public const string Unspecified = "unspecified";
    public const string Mixed = "mixed";

    public static string Classify(string? rulesPrimary)
    {
        if (string.IsNullOrEmpty(rulesPrimary)) return Unspecified;
        if (rulesPrimary.Contains("The Weather Company", StringComparison.Ordinal)) return WeatherCompany;
        if (rulesPrimary.Contains("Climatological Report", StringComparison.Ordinal)) return NwsClimatologicalReport;
        if (rulesPrimary.Contains("Daily Climate Report", StringComparison.Ordinal)) return NwsDailyClimateReport;
        return Unspecified;
    }
}

/// <summary>One settled market from a /markets or /historical/markets page.</summary>
/// <param name="Value">Whole-degree <c>expiration_value</c>; null when empty (many finalized markets leave it empty).</param>
/// <param name="InvalidValue">The raw text when a value was present but not a whole number in a plausible range.</param>
public sealed record SettledMarket(string Ticker, string EventTicker, string Status, string? Result, int? Value,
    string? InvalidValue, string RulesPrimary, OutcomeBucket? Bucket);

public enum TruthStatus { Ok, Conflict, Inconsistent }

/// <summary>What one event settled on, with the checks that were run on it.</summary>
public sealed record EventTruth(string EventTicker, string SeriesTicker, DateOnly ClimateDate, int? Value, string Regime,
    int MarketsSeen, int MarketsChecked, int MarketsInconsistent, TruthStatus Status, long RawId)
{
    public string StatusText => Status switch { TruthStatus.Ok => "ok", TruthStatus.Conflict => "conflict", _ => "inconsistent" };
}

public static class TruthParse
{
    private static readonly string[] Months = ["JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"];

    /// <summary>"KXHIGHNY-26NOV24" and the older "HIGHNY-21AUG07" -> the climate date. Anything else -> null.</summary>
    public static DateOnly? EventDate(string eventTicker)
    {
        var parts = eventTicker.Split('-');
        if (parts.Length != 2 || parts[1].Length != 7) return null;
        var s = parts[1];
        var month = Array.IndexOf(Months, s.Substring(2, 3)) + 1;
        if (month == 0 || !int.TryParse(s.AsSpan(0, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var yy)
                       || !int.TryParse(s.AsSpan(5, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var dd)) return null;
        try { return new DateOnly(2000 + yy, month, dd); } catch (ArgumentOutOfRangeException) { return null; }
    }

    /// <summary>A page of /markets or /historical/markets. Unknown fields are ignored; the raw body is the record.</summary>
    public static (List<SettledMarket> Markets, string? Cursor) SettledMarkets(string body)
    {
        var root = JsonNode.Parse(body)?.AsObject() ?? throw new FormatException("Empty body.");
        var arr = root["markets"] as JsonArray ?? throw new FormatException("Missing 'markets'.");
        var list = new List<SettledMarket>(arr.Count);
        foreach (var node in arr)
        {
            var m = node as JsonObject ?? throw new FormatException("Market entry is not an object.");
            var ticker = Str(m, "ticker") ?? throw new FormatException("Market without ticker.");
            var eventTicker = Str(m, "event_ticker") ?? throw new FormatException($"{ticker}: missing event_ticker.");
            var rules = Str(m, "rules_primary") ?? "";
            var raw = Str(m, "expiration_value");
            int? value = null;
            string? invalid = null;
            if (!string.IsNullOrWhiteSpace(raw))
            {
                // Padding is real: some 2021 values arrive as " 88".
                if (decimal.TryParse(raw, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite,
                        CultureInfo.InvariantCulture, out var d)
                    && d == decimal.Truncate(d) && d is >= -50m and <= 150m) value = (int)d;
                else invalid = raw;
            }
            var bucket = OutcomeBucket.Resolve(Str(m, "strike_type"), Num(m, "floor_strike"), Num(m, "cap_strike"), rules);
            list.Add(new SettledMarket(ticker, eventTicker, Str(m, "status") ?? "", Str(m, "result"), value, invalid, rules, bucket));
        }
        var cursor = Str(root, "cursor");
        return (list, string.IsNullOrEmpty(cursor) ? null : cursor);
    }

    /// <summary>
    /// Reduces the markets of one event to the value it settled on. Returns null while the event has no settled market,
    /// or when no market states a value (we then know the result but not the temperature, so there is no truth to record).
    /// Different values within one event are a 'conflict' with no value; a market whose yes/no result contradicts the
    /// value under its own strike edges makes the event 'inconsistent'. Neither is repaired or guessed.
    /// </summary>
    public static EventTruth? Aggregate(string seriesTicker, string eventTicker, IReadOnlyList<(SettledMarket Market, long RawId)> markets)
    {
        if (EventDate(eventTicker) is not { } date) return null;
        var settled = markets.Where(m => m.Market.Result is "yes" or "no").ToList();
        if (settled.Count == 0) return null;

        var values = settled.Where(m => m.Market.Value is not null).Select(m => m.Market.Value!.Value).Distinct().ToList();
        var anyInvalid = settled.Any(m => m.Market.InvalidValue is not null);
        if (values.Count == 0 && !anyInvalid) return null;

        var regimes = settled.Select(m => SettlementRegime.Classify(m.Market.RulesPrimary)).Distinct().ToList();
        var regime = regimes.Count == 1 ? regimes[0] : SettlementRegime.Mixed;
        var rawId = settled[0].RawId;

        if (anyInvalid || values.Count > 1)
            return new EventTruth(eventTicker, seriesTicker, date, null, regime, settled.Count, 0, 0, TruthStatus.Conflict, rawId);

        var value = values[0];
        var checkedCount = 0;
        var bad = 0;
        foreach (var (m, _) in settled)
        {
            if (m.Bucket is not { } bucket) continue;
            checkedCount++;
            if (bucket.Contains(value) != (m.Result == "yes")) bad++;
        }
        return new EventTruth(eventTicker, seriesTicker, date, value, regime, settled.Count, checkedCount, bad,
            bad == 0 ? TruthStatus.Ok : TruthStatus.Inconsistent, rawId);
    }

    /// <summary>IEM <c>cli.py</c> results: one row per climate date. Rows without a numeric whole-degree high are skipped.</summary>
    public static List<CliHigh> CliHighs(string body)
    {
        var root = JsonNode.Parse(body)?.AsObject() ?? throw new FormatException("Empty body.");
        var arr = root["results"] as JsonArray ?? throw new FormatException("Missing 'results'.");
        var list = new List<CliHigh>();
        foreach (var node in arr)
        {
            var r = node as JsonObject ?? throw new FormatException("Result entry is not an object.");
            var station = Str(r, "station");
            var valid = Str(r, "valid");
            if (station is null || valid is null || !DateOnly.TryParseExact(valid, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                throw new FormatException("Result without station or a valid yyyy-MM-dd date.");
            if (r["high"] is not JsonValue hv || !hv.TryGetValue<decimal>(out var high) || high != decimal.Truncate(high) || high is < -50m or > 150m)
                continue; // missing ("M"/null) or not a whole degree: not a usable high
            list.Add(new CliHigh(station, date, (int)high, Str(r, "product")));
        }
        return list;
    }

    private static string? Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    private static decimal? Num(JsonObject o, string key) =>
        o[key] is JsonValue v && v.GetValueKind() == JsonValueKind.Number ? v.GetValue<decimal>() : null;
}

public sealed record CliHigh(string Station, DateOnly ClimateDate, int High, string? Product);
