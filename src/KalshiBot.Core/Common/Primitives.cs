using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace KalshiBot.Core.Common;

/// <summary>Canonical UTC timestamp format used everywhere we persist time. Lexically sortable.</summary>
public static class Ts
{
    public const string Format = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    public static string ToText(DateTimeOffset t) => t.UtcDateTime.ToString(Format, CultureInfo.InvariantCulture);

    public static DateTimeOffset Parse(string s) =>
        DateTimeOffset.ParseExact(s, Format, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    /// <summary>Parses RFC 3339 / ISO 8601 timestamps from Kalshi payloads and normalizes to UTC.</summary>
    public static DateTimeOffset? ParseExternal(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null
        : DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}

/// <summary>Exact decimal helpers. Money and prices are never binary floating point.</summary>
public static class Dec
{
    private const NumberStyles Strict = NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign;

    public static string ToText(decimal d) => d.ToString(CultureInfo.InvariantCulture);

    public static decimal Parse(string s) => decimal.Parse(s, Strict, CultureInfo.InvariantCulture);

    public static decimal? ParseOrNull(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : decimal.Parse(s, Strict, CultureInfo.InvariantCulture);

    /// <summary>Round up to the next whole cent (fees are charged in cents, rounded against the trader).</summary>
    public static decimal CeilCents(decimal d) => Math.Ceiling(d * 100m) / 100m;
}

public static class Hash
{
    public static string Sha256Hex(string text) => Sha256Hex(Encoding.UTF8.GetBytes(text));

    public static string Sha256Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}

/// <summary>Where a piece of market data came from. Synthetic data is never presented as an observation.</summary>
public static class DataOrigin
{
    public const string LiveProduction = "live:production";
    public const string LiveDemo = "live:demo";
    public const string Synthetic = "fixture:synthetic";

    public static bool IsSynthetic(string origin) => origin.StartsWith("fixture:", StringComparison.Ordinal);
}

public sealed class KalshiBotException(string message) : Exception(message);
