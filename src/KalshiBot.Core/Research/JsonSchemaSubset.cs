using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace KalshiBot.Core.Research;

/// <summary>
/// Validator for the small JSON Schema subset used by schemas/*.json. A schema using any other keyword is rejected
/// when loaded (fail closed), so the file and the validator cannot silently drift apart.
/// </summary>
public static class JsonSchemaSubset
{
    private static readonly HashSet<string> Validation =
        ["type", "const", "enum", "anyOf", "properties", "required", "additionalProperties", "items",
         "minItems", "maxItems", "minLength", "maxLength", "pattern", "minimum", "maximum"];
    private static readonly HashSet<string> Annotations = ["$schema", "$id", "title", "description"];

    /// <summary>Keywords kept when deriving a schema for `claude --json-schema` (structure only; limits stay here).</summary>
    private static readonly HashSet<string> Portable =
        ["type", "const", "enum", "anyOf", "properties", "required", "additionalProperties", "items", "description"];

    public static void EnsureSupported(JsonElement schema, string path = "#")
    {
        if (schema.ValueKind != JsonValueKind.Object) throw new InvalidOperationException($"schema at {path} must be an object");
        foreach (var p in schema.EnumerateObject())
        {
            if (!Validation.Contains(p.Name) && !Annotations.Contains(p.Name))
                throw new InvalidOperationException($"unsupported schema keyword '{p.Name}' at {path}");
            if (p.Name == "additionalProperties" && p.Value.ValueKind != JsonValueKind.False)
                throw new InvalidOperationException($"only additionalProperties: false is supported ({path})");
        }
        if (schema.TryGetProperty("properties", out var props))
            foreach (var p in props.EnumerateObject()) EnsureSupported(p.Value, $"{path}/properties/{p.Name}");
        if (schema.TryGetProperty("items", out var items)) EnsureSupported(items, $"{path}/items");
        if (schema.TryGetProperty("anyOf", out var anyOf))
            foreach (var (s, i) in anyOf.EnumerateArray().Select((s, i) => (s, i))) EnsureSupported(s, $"{path}/anyOf/{i}");
    }

    public static void Validate(JsonElement value, JsonElement schema, string path, List<string> errors)
    {
        if (schema.TryGetProperty("anyOf", out var anyOf))
        {
            var branches = anyOf.EnumerateArray().Select(b =>
            {
                var e = new List<string>();
                Validate(value, b, path, e);
                var typeMatches = !b.TryGetProperty("type", out var bt) || TypeMatches(value, bt.GetString()!);
                return (Errors: e, TypeMatches: typeMatches);
            }).ToList();
            // Report the branch the value was evidently meant for (its type matches), so errors stay specific.
            if (branches.All(b => b.Errors.Count > 0))
                errors.AddRange(branches.OrderByDescending(b => b.TypeMatches).ThenBy(b => b.Errors.Count).First().Errors);
            return;
        }
        if (schema.TryGetProperty("type", out var type) && !TypeMatches(value, type.GetString()!))
        {
            errors.Add($"{path} must be of type {type.GetString()}");
            return;
        }
        if (schema.TryGetProperty("const", out var c) && !JsonElement.DeepEquals(value, c)) errors.Add($"{path} must equal {c.GetRawText()}");
        if (schema.TryGetProperty("enum", out var en) && !en.EnumerateArray().Any(e => JsonElement.DeepEquals(value, e)))
            errors.Add($"{path} must be one of {en.GetRawText()}");

        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                var s = value.GetString()!;
                if (schema.TryGetProperty("minLength", out var minL) && s.Length < minL.GetInt32()) errors.Add($"{path} is shorter than {minL.GetInt32()}");
                if (schema.TryGetProperty("maxLength", out var maxL) && s.Length > maxL.GetInt32()) errors.Add($"{path} is longer than {maxL.GetInt32()}");
                if (schema.TryGetProperty("pattern", out var pat) && !Regex.IsMatch(s, pat.GetString()!, RegexOptions.None, TimeSpan.FromSeconds(1)))
                    errors.Add($"{path} does not match the required format");
                break;
            case JsonValueKind.Number:
                if (!value.TryGetDecimal(out var d)) { errors.Add($"{path} is not a representable decimal"); break; }
                if (schema.TryGetProperty("minimum", out var min) && d < min.GetDecimal()) errors.Add($"{path} is below {min.GetRawText()}");
                if (schema.TryGetProperty("maximum", out var max) && d > max.GetDecimal()) errors.Add($"{path} is above {max.GetRawText()}");
                break;
            case JsonValueKind.Array:
                var n = value.GetArrayLength();
                if (schema.TryGetProperty("minItems", out var minI) && n < minI.GetInt32()) errors.Add($"{path} needs at least {minI.GetInt32()} items");
                if (schema.TryGetProperty("maxItems", out var maxI) && n > maxI.GetInt32()) errors.Add($"{path} may have at most {maxI.GetInt32()} items");
                if (schema.TryGetProperty("items", out var itemSchema))
                {
                    var i = 0;
                    foreach (var item in value.EnumerateArray()) Validate(item, itemSchema, $"{path}[{i++}]", errors);
                }
                break;
            case JsonValueKind.Object:
                var seen = new HashSet<string>(StringComparer.Ordinal);
                schema.TryGetProperty("properties", out var props);
                foreach (var p in value.EnumerateObject())
                {
                    if (!seen.Add(p.Name)) errors.Add($"duplicate key '{path}.{p.Name}'");
                    if (props.ValueKind == JsonValueKind.Object && props.TryGetProperty(p.Name, out var ps)) Validate(p.Value, ps, $"{path}.{p.Name}", errors);
                    else if (schema.TryGetProperty("additionalProperties", out _)) errors.Add($"unknown key '{path}.{p.Name}'");
                }
                if (schema.TryGetProperty("required", out var req))
                    foreach (var r in req.EnumerateArray().Select(r => r.GetString()!).Where(r => !seen.Contains(r)))
                        errors.Add($"missing key '{path}.{r}'");
                break;
        }
    }

    /// <summary>Structure-only copy of a schema for `claude --json-schema`; the importer still enforces every limit.</summary>
    public static JsonNode PortableCopy(JsonNode node)
    {
        if (node is not JsonObject o) return node.DeepClone();
        var copy = new JsonObject();
        foreach (var (key, value) in o)
        {
            if (!Portable.Contains(key) || value is null) continue;
            copy[key] = key switch
            {
                // Keys under "properties" are property names, not keywords: copy each property's schema.
                "properties" => new JsonObject(value.AsObject().Select(p => KeyValuePair.Create(p.Key, (JsonNode?)PortableCopy(p.Value!)))),
                "items" => PortableCopy(value),
                "anyOf" => new JsonArray(value.AsArray().Select(x => (JsonNode?)PortableCopy(x!)).ToArray()),
                _ => value.DeepClone(),
            };
        }
        return copy;
    }

    private static bool TypeMatches(JsonElement v, string type) => type switch
    {
        "object" => v.ValueKind == JsonValueKind.Object,
        "array" => v.ValueKind == JsonValueKind.Array,
        "string" => v.ValueKind == JsonValueKind.String,
        "number" => v.ValueKind == JsonValueKind.Number,
        "integer" => v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out _),
        "boolean" => v.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => v.ValueKind == JsonValueKind.Null,
        _ => throw new InvalidOperationException($"unsupported type '{type}'"),
    };
}
