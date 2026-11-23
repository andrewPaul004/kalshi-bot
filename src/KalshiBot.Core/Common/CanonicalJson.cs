using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KalshiBot.Core.Common;

/// <summary>
/// Deterministic JSON: object keys sorted ordinally, no insignificant whitespace, stable escaping.
/// Used wherever a hash identifies content (packets, decisions), so identical inputs give identical bytes.
/// </summary>
public static class CanonicalJson
{
    private static readonly JsonSerializerOptions Compact = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize(JsonNode? node, bool indented = false) =>
        Sort(node)?.ToJsonString(indented ? Indented : Compact) ?? "null";

    public static string HashOf(JsonNode? node) => Hash.Sha256Hex(Serialize(node));

    private static JsonNode? Sort(JsonNode? node) => node switch
    {
        JsonObject o => new JsonObject(o.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => KeyValuePair.Create(p.Key, Sort(p.Value)))),
        JsonArray a => new JsonArray(a.Select(Sort).ToArray()),
        JsonValue v => JsonNode.Parse(v.ToJsonString()),
        null => null,
        _ => throw new InvalidOperationException("Unexpected JSON node type."),
    };

    public static byte[] Utf8(string s) => new UTF8Encoding(false).GetBytes(s);
}
