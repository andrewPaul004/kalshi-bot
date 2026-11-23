using System.Text.Json.Nodes;
using KalshiBot.Core.Common;
using KalshiBot.Core.Kalshi;
using KalshiBot.Core.Recording;
using KalshiBot.Core.Research;
using KalshiBot.Core.Storage;
using KalshiBot.Core.Trading;

namespace KalshiBot.Core.Status;

/// <summary>
/// Frozen fixture replay (spec Phase 1 acceptance, AC-14): synthetic inputs, a manual clock, and a fixed sequence, so
/// two runs produce identical decisions, reservations, fills, and ledgers. It exercises candidate -> decision ->
/// reservation -> post-arrival paper fill -> settlement. Fixture profitability is meaningless (spec 8.1).
/// </summary>
public static class FixtureReplay
{
    public const string T50 = "KXSYNTHWX-99DEC31-T50";
    public const string T60 = "KXSYNTHWX-99DEC31-T60";
    public const string Account = "paper";
    public static readonly DateTimeOffset Start = new(2026, 1, 5, 15, 0, 0, TimeSpan.Zero);

    public static void Run(Db db, ManualClock clock, string fixturesRoot, Action<string> log)
    {
        clock.Set(Start);
        var model = SyntheticFixtureModel.Load(Path.Combine(fixturesRoot, "model.json"));
        var strategy = new EdgeThresholdStrategy(minEdge: 0.05m, contracts: 1, allowedSeries: ["KXSYNTHWX"]);
        var engine = new PaperEngine(db, clock, new RiskEngine(RiskLimits.PaperFixture));
        var ledger = new PaperLedger(db, clock);

        new Watchlist(db, clock).Add(T50);
        new Watchlist(db, clock).Add(T60);
        var r1 = Capture(db, clock, fixturesRoot, "kalshi-open");
        log($"recorded {r1.Snapshots} snapshots from kalshi-open (run #{r1.RunId}, {r1.Status}) at {Ts.ToText(clock.Now)}");

        ledger.EnsureAccount(Account, RiskLimits.PaperFixture.StartingBankroll, DataOrigin.Synthetic);
        log($"paper account '{Account}' with {Dec.Show(RiskLimits.PaperFixture.StartingBankroll)} PAPER dollars (spec 6.2 fixture)");

        clock.Advance(TimeSpan.FromSeconds(5));
        log($"paper run {T50} (decision on the first snapshot):");
        Describe(engine.Run(Account, T50, strategy, model), log);

        clock.Advance(TimeSpan.FromSeconds(1));
        log($"paper run {T60}:");
        Describe(engine.Run(Account, T60, strategy, model), log);

        clock.Advance(TimeSpan.FromSeconds(24));
        var r2 = Capture(db, clock, fixturesRoot, "kalshi-open");
        log($"recorded the next observation (run #{r2.RunId}) at {Ts.ToText(clock.Now)}: the order's simulated arrival evidence");

        clock.Advance(TimeSpan.FromSeconds(1));
        foreach (var o in engine.ResolvePending(Account))
            log($"  resolved order #{o.OrderId} {o.Ticker}: {o.Status.ToUpperInvariant()} {o.Filled} filled ({o.Reason})" +
                string.Concat(o.Fills.Select(f => $"; SIMULATED fill {f.Quantity} @ {Dec.Show(f.Price)} fee {Dec.Show(f.Fee)}")));

        clock.Advance(TimeSpan.FromSeconds(1));
        log($"paper run {T50} again on the new snapshot (same evidence):");
        Describe(engine.Run(Account, T50, strategy, model), log);

        var packet = new PacketExporter(db, clock).Export(T50);
        var record = SyntheticRecord(packet, clock);
        var imported = new ResearchImporter(db, clock).Import(record.ToJsonString(), "synthetic-fixture");
        log($"research packet {packet.PacketId}; SYNTHETIC research record '{imported.ResearchId}' imported " +
            $"(experimental p_yes {Dec.Show(imported.ExperimentalProbability!.Value)} stored for scoring only; no trading code reads it)");
        var hostile = (JsonObject)record.DeepClone();
        hostile["researchId"] = "replay-hostile";
        hostile["orderSize"] = 100;
        hostile["marketTicker"] = "KXFAKE-1";
        try { new ResearchImporter(db, clock).Import(hostile.ToJsonString(), "synthetic-fixture"); }
        catch (ResearchRejectedException e) { log($"strict import rejected a hostile record: {string.Join("; ", e.Errors)}"); }

        clock.Advance(TimeSpan.FromHours(1));
        var r3 = Capture(db, clock, fixturesRoot, "kalshi-settled");
        log($"recorded finalized markets (run #{r3.RunId}) at {Ts.ToText(clock.Now)}");
        var accountId = ledger.FindAccount(Account)!.Value;
        var store = new MarketStore(db);
        foreach (var t in new[] { T50, T60 })
            if (ledger.Settle(accountId, store.Latest(t)!) > 0) log($"  settled {t}: result {store.Latest(t)!.Result}");
        var problems = ledger.Verify(accountId);
        var state = ledger.State(accountId, false);
        log($"ledger verify: {(problems.Count == 0 ? "OK" : string.Join("; ", problems))}; cash {Dec.Show(state.Cash)}, realized {Dec.Show(state.RealizedPnl)} (SYNTHETIC, meaningless as performance)");
    }

    /// <summary>A stable fingerprint of everything the replay decided and booked, for reproducibility checks.</summary>
    public static string Fingerprint(Db db) => Hash.Sha256Hex(string.Join("\n",
        db.Query("SELECT decision_id, outcome, rationale FROM paper_decisions ORDER BY id", r => $"D|{r.S("decision_id")}|{r.S("outcome")}|{r.S("rationale")}")
        .Concat(db.Query("SELECT client_order_id, signal_key, status, filled_quantity, arrival_at, IFNULL(reasons,'') AS w FROM paper_orders ORDER BY id",
            r => $"O|{r.S("client_order_id")}|{r.S("signal_key")}|{r.S("status")}|{r.L("filled_quantity")}|{r.S("arrival_at")}|{r.S("w")}"))
        .Concat(db.Query("SELECT amount, status, consumed FROM risk_reservations ORDER BY id", r => $"R|{r.S("amount")}|{r.S("status")}|{r.S("consumed")}"))
        .Concat(db.Query("SELECT snapshot_id, quantity, price, fee, filled_at FROM paper_fills ORDER BY id",
            r => $"F|{r.L("snapshot_id")}|{r.L("quantity")}|{r.S("price")}|{r.S("fee")}|{r.S("filled_at")}"))
        .Concat(db.Query("SELECT ts, kind, IFNULL(ticker,'') AS t, quantity_delta, cash_delta, cost_delta FROM ledger_entries ORDER BY id",
            r => $"L|{r.S("ts")}|{r.S("kind")}|{r.S("t")}|{r.L("quantity_delta")}|{r.S("cash_delta")}|{r.S("cost_delta")}"))));

    private static CaptureSummary Capture(Db db, TimeProvider clock, string root, string set) =>
        new Recorder(db, new KalshiPublicClient(new HttpClient(new FixtureHandler(Path.Combine(root, set))), KalshiEndpoint.Fixture, clock,
            new KalshiClientOptions { MinInterval = TimeSpan.Zero }), clock).CaptureAsync([T50, T60]).GetAwaiter().GetResult();

    private static void Describe(PaperRunResult r, Action<string> log)
    {
        log($"  decision {r.DecisionId[..16]} on snapshot #{r.SnapshotId}: {r.Rationale}");
        if (r.AlreadyProcessed) log("  already processed (idempotent rerun)");
        foreach (var o in r.Orders)
            log($"  order {o.ClientOrderId}: {o.Intent.Action} {o.Intent.Quantity} {o.Intent.Side} @ {Dec.Show(o.Intent.LimitPrice)} IOC -> " +
                (o.Verdict.Approved ? $"PENDING, reserved {Dec.Show(o.Reserved)}, arrival {Ts.ToText(o.ArrivalAt)}" : "REJECTED: " + string.Join("; ", o.Verdict.Reasons)));
    }

    private static JsonObject SyntheticRecord(ExportedPacket p, TimeProvider clock)
    {
        var packet = JsonNode.Parse(p.Json)!;
        var now = clock.GetUtcNow();
        return new JsonObject
        {
            ["schemaVersion"] = ResearchContract.RecordSchema,
            ["researchId"] = "replay-synthetic-1",
            ["packetId"] = p.PacketId,
            ["packetHash"] = p.PacketHash,
            ["createdAtUtc"] = Ts.ToText(now),
            ["validUntilUtc"] = Ts.ToText(now.AddDays(1)),
            ["marketTicker"] = p.MarketTicker,
            ["eventTicker"] = packet["market"]!["eventTicker"]!.GetValue<string>(),
            ["seriesTicker"] = packet["market"]!["seriesTicker"]?.GetValue<string>(),
            ["marketRulesHash"] = packet["rules"]!["rulesSha256"]!.GetValue<string>(),
            ["disposition"] = "insufficient_evidence",
            ["evidence"] = new JsonArray(),
            ["uncertainties"] = new JsonArray("SYNTHETIC fixture market; nothing to research"),
            ["invalidationConditions"] = new JsonArray(),
            ["hypothesis"] = null,
            ["suggestedModelChange"] = null,
            ["experimentalProbabilityYes"] = new JsonObject
            {
                ["value"] = 0.99m, ["method"] = "SYNTHETIC fixture value", ["uncertainty"] = "not an estimate",
            },
            ["researcherVersion"] = new JsonObject
            {
                ["cli"] = null, ["model"] = null, ["promptVersion"] = ResearchContract.PromptVersion, ["toolConfig"] = null,
            },
        };
    }
}
