using System.Text.RegularExpressions;

namespace KalshiBot.Core.Kalshi;

/// <summary>
/// The whole-degree outcomes for which a binary threshold market resolves YES, as inclusive bounds (null = unbounded).
/// Built from Kalshi's strike fields, or from the rules text for older markets that carry none. Anything not recognised
/// returns null so the caller abstains (spec DATA-01: unknown rules block new risk; nothing is guessed).
/// </summary>
/// <remarks>
/// Semantics verified 2026-11-23 against 4,280 settled KXHIGHNY markets (zero mismatches against Kalshi's result):
/// <c>between</c> = floor..cap inclusive; <c>less</c> = value &lt; cap (so at most cap-1 in whole degrees);
/// <c>greater</c> = value &gt; floor (so at least floor+1). Plain "greater than N" in rules text is strict as well:
/// "strictly greater than N" disagreed with "&gt;= N" on 68 settled markets, and modern markets use the plain wording.
/// </remarks>
public sealed record OutcomeBucket(int? LowerInclusive, int? UpperInclusive)
{
    public bool Contains(int degrees) =>
        (LowerInclusive is not { } lo || degrees >= lo) && (UpperInclusive is not { } hi || degrees <= hi);

    /// <summary>From <c>strike_type</c>, <c>floor_strike</c> and <c>cap_strike</c>. Null when absent, unknown or inconsistent.</summary>
    public static OutcomeBucket? FromStrikes(string? strikeType, decimal? floor, decimal? cap)
    {
        switch (strikeType)
        {
            case "between" when Whole(floor) is { } lo && Whole(cap) is { } hi && lo <= hi:
                return new OutcomeBucket(lo, hi);
            case "less" when floor is null && Whole(cap) is { } c:
                return new OutcomeBucket(null, c - 1);
            case "greater" when cap is null && Whole(floor) is { } f:
                return new OutcomeBucket(f + 1, null);
            default:
                return null;
        }
    }

    // The unit and source clause between the number and ", then" is optional; anything else fails closed.
    private const string Tail = @"\s*[\u00b0\ufffd]?\s*(?:F\b|fahrenheit\b)?\s*(?:according to [^,]*)?,\s*then the market resolves to Yes";
    private static readonly Regex BetweenRule = new(@"\bis between (?<a>\d{1,3})\s*(?:-|and)\s*(?<b>\d{1,3})" + Tail,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex GreaterRule = new(@"\bis (?:strictly )?greater than (?<n>\d{1,3})(?![\d.\-])" + Tail,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex LessRule = new(@"\bis (?:strictly )?less than (?<n>\d{1,3})(?![\d.\-])" + Tail,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// For markets without strike fields (2021-2022 and a few 2025 markets). Recognises "is between A-B" / "A and B",
    /// and "is [strictly] greater/less than N". Wording such as "above N" is not recognised on purpose.
    /// </summary>
    public static OutcomeBucket? FromRules(string? rulesPrimary)
    {
        if (string.IsNullOrEmpty(rulesPrimary)) return null;
        var matches = new List<OutcomeBucket>();
        if (BetweenRule.Match(rulesPrimary) is { Success: true } b
            && int.Parse(b.Groups["a"].Value) is var lo && int.Parse(b.Groups["b"].Value) is var hi && lo <= hi)
            matches.Add(new OutcomeBucket(lo, hi));
        if (GreaterRule.Match(rulesPrimary) is { Success: true } g) matches.Add(new OutcomeBucket(int.Parse(g.Groups["n"].Value) + 1, null));
        if (LessRule.Match(rulesPrimary) is { Success: true } l) matches.Add(new OutcomeBucket(null, int.Parse(l.Groups["n"].Value) - 1));
        return matches.Count == 1 ? matches[0] : null; // two different readings of one rule = ambiguous
    }

    /// <summary>Structured fields win. If a strike type is present but unusable, the rules text is not consulted.</summary>
    public static OutcomeBucket? Resolve(string? strikeType, decimal? floor, decimal? cap, string? rulesPrimary) =>
        string.IsNullOrEmpty(strikeType) ? FromRules(rulesPrimary) : FromStrikes(strikeType, floor, cap);

    private static int? Whole(decimal? v) =>
        v is { } d && d == decimal.Truncate(d) && Math.Abs(d) < 1000m ? (int)d : null;
}
