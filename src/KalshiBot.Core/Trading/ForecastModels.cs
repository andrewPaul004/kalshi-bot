using System.Text.Json;
using KalshiBot.Core.Common;
using KalshiBot.Core.Recording;

namespace KalshiBot.Core.Trading;

/// <summary>
/// Real observations have no validated model yet, so the system observes and abstains (spec Phase 1).
/// Replacing this requires a Phase 3 model with a frozen evaluation protocol.
/// </summary>
public sealed class NoValidatedModel : IForecastModel
{
    public string Id => "none";
    public Forecast? Forecast(MarketSnapshot snapshot, IEvidenceView evidence) => null;
}

/// <summary>
/// SYNTHETIC fixture model for controlled acceptance tests (spec Phase 1). Returns fixed, hand-written probabilities
/// from fixtures/synthetic/model.json, and only for synthetic snapshots. These numbers are not forecasts.
/// </summary>
public sealed class SyntheticFixtureModel : IForecastModel
{
    private readonly IReadOnlyDictionary<string, (decimal P, decimal U)> _table;
    private readonly string _tableHash;

    public SyntheticFixtureModel(IReadOnlyDictionary<string, (decimal P, decimal U)> table)
    {
        foreach (var (t, (p, u)) in table)
            if (p is < 0m or > 1m || u is < 0m or > 0.5m) throw new KalshiBotException($"Fixture model entry for {t} out of range.");
        _table = table;
        _tableHash = Hash.Sha256Hex(string.Join("\n", table.OrderBy(k => k.Key, StringComparer.Ordinal)
            .Select(k => $"{k.Key}={Dec.ToText(Dec.Normalize(k.Value.P))}/{Dec.ToText(Dec.Normalize(k.Value.U))}")));
    }

    public string Id => "synthetic-fixture-model/1";

    public Forecast? Forecast(MarketSnapshot snapshot, IEvidenceView evidence)
    {
        if (!snapshot.IsSynthetic || !_table.TryGetValue(snapshot.Ticker, out var e)) return null;
        return new Forecast(Id, e.P, e.U, "SYNTHETIC fixed fixture value (not a forecast)", snapshot.ObservedAt,
            EvidenceRevision: $"{_tableHash[..16]}:{snapshot.Ticker}", Synthetic: true);
    }

    /// <summary>Reads {"TICKER": {"probability_yes": "0.60", "uncertainty": "0.05"}, ...}.</summary>
    public static SyntheticFixtureModel Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var table = new Dictionary<string, (decimal, decimal)>(StringComparer.Ordinal);
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            if (p.Name.StartsWith('_')) continue; // comments such as "_note"
            table[p.Name] = (Dec.Parse(p.Value.GetProperty("probability_yes").GetString()!),
                Dec.Parse(p.Value.GetProperty("uncertainty").GetString()!));
        }
        return new SyntheticFixtureModel(table);
    }
}
