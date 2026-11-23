namespace KalshiBot.Core.Research;

/// <summary>The packet and result formats. Versioned; a breaking change gets a new schema id.</summary>
public static class ResearchContract
{
    public const string PacketSchema = "kalshibot/research-packet/v1";
    public const string ResultSchema = "kalshibot/research-result/v1";
    public const int MaxResultBytes = 64 * 1024;

    /// <summary>
    /// JSON Schema for the result, passed to `claude -p --json-schema` and embedded in each packet. It sticks to
    /// widely supported keywords (type/enum/const/required/additionalProperties/anyOf); ranges, lengths and hex
    /// formats are enforced by <see cref="ResultImporter"/>, which is the only authority on what gets stored.
    /// </summary>
    public const string ResultJsonSchema = """
    {
      "type": "object",
      "additionalProperties": false,
      "required": ["schema", "packet_id", "ticker", "rules_sha256", "probability_yes", "abstain_reason", "confidence",
                   "as_of", "summary", "key_factors", "resolution_risks", "sources"],
      "properties": {
        "schema": { "type": "string", "const": "kalshibot/research-result/v1" },
        "packet_id": { "type": "string" },
        "ticker": { "type": "string" },
        "rules_sha256": { "type": "string" },
        "probability_yes": { "anyOf": [ { "type": "number" }, { "type": "null" } ] },
        "abstain_reason": { "anyOf": [ { "type": "string" }, { "type": "null" } ] },
        "confidence": { "type": "string", "enum": ["low", "medium", "high"] },
        "as_of": { "type": "string" },
        "summary": { "type": "string" },
        "key_factors": { "type": "array", "items": { "type": "string" } },
        "resolution_risks": { "type": "array", "items": { "type": "string" } },
        "sources": {
          "type": "array",
          "items": {
            "type": "object",
            "additionalProperties": false,
            "required": ["title", "url", "retrieved_at"],
            "properties": {
              "title": { "type": "string" },
              "url": { "type": "string" },
              "retrieved_at": { "type": "string" }
            }
          }
        }
      }
    }
    """;

    public const string Task = """
    Estimate the probability that this Kalshi market resolves YES, judged strictly by the rules text in this packet.
    - Read rules_primary, rules_secondary and early_close_condition closely; resolution follows them, not the title.
    - Use only evidence you can cite with a URL. Prefer the listed settlement sources.
    - Current market prices are intentionally omitted so your estimate is independent of them.
    - If you cannot form a defensible estimate, set probability_yes to null and explain in abstain_reason.
      Otherwise set abstain_reason to null. Abstaining is better than guessing.
    - Echo packet_id, ticker and rules_sha256 exactly as given.
    - Return only a JSON object that matches result_contract.json_schema.
    This estimate is EXPERIMENTAL and unvalidated. It is used for paper trading research only.
    """;
}
