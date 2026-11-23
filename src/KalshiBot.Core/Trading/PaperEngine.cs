using System.Text.Json.Nodes;
using KalshiBot.Core.Common;
using KalshiBot.Core.Recording;
using KalshiBot.Core.Research;
using KalshiBot.Core.Storage;

namespace KalshiBot.Core.Trading;

public sealed record OrderOutcome(string ClientOrderId, OrderIntent Intent, RiskVerdict Verdict, string Status,
    int FilledQuantity, IReadOnlyList<SimulatedFill> Fills);

public sealed record PaperRunResult(string DecisionId, string StrategyId, long SnapshotId, string DataOrigin,
    ProbabilityEstimate? Estimate, string Rationale, bool AlreadyProcessed, IReadOnlyList<OrderOutcome> Orders);

public sealed class Controls(Db db, TimeProvider time)
{
    public bool Halted => db.Scalar<string>("SELECT value FROM controls WHERE key = 'halt'") == "1";

    public string? HaltReason => Halted ? db.Scalar<string>("SELECT value FROM controls WHERE key = 'halt_reason'") : null;

    public void SetHalt(bool halted, string reason) => db.InTransaction(() =>
    {
        Set("halt", halted ? "1" : "0");
        Set("halt_reason", reason);
    });

    private void Set(string key, string value) =>
        db.Exec("INSERT INTO controls (key, value, updated_at) VALUES ($k, $v, $t) ON CONFLICT(key) DO UPDATE SET value = $v, updated_at = $t",
            ("$k", key), ("$v", value), ("$t", Ts.ToText(time.GetUtcNow())));
}

/// <summary>
/// One deterministic paper decision cycle for one market: latest snapshot + latest research estimate +
/// account state -> strategy -> risk -> simulated fills -> ledger. The decision id is a hash of everything the
/// strategy saw, so re-running on unchanged inputs is a no-op (crash-safe rerun).
/// </summary>
public sealed class PaperEngine(Db db, TimeProvider time, RiskEngine risk)
{
    /// <summary>Test hook: throw at a named point inside the order transaction to prove rollback.</summary>
    internal Action<string>? FaultInjector { get; set; }

    public PaperRunResult Run(string accountName, string ticker, IStrategy strategy, ExecutionMode mode = ExecutionMode.Paper)
    {
        var store = new MarketStore(db);
        var ledger = new PaperLedger(db, time);
        var controls = new Controls(db, time);
        var accountId = ledger.FindAccount(accountName) ?? throw new KalshiBotException($"Paper account '{accountName}' not found. Run 'kb paper init'.");
        var snapshot = store.Latest(ticker) ?? throw new KalshiBotException($"No recorded snapshot for {ticker}. Run 'kb record'.");
        var rules = store.Rules(snapshot.RulesId)!;
        var estimate = new ResearchStore(db).LatestEstimate(ticker);
        var feeMultiplier = FeeMultiplierFor(store, snapshot);

        var state = ledger.State(accountId, controls.Halted);
        var positions = state.Positions.Where(p => p.Ticker == ticker).ToList();
        var input = new StrategyInput(snapshot, rules.Sha256, estimate, positions, feeMultiplier);
        var decisionId = DecisionId(strategy, input, accountId);

        var prior = db.Query("SELECT client_order_id FROM paper_orders WHERE decision_id = $d", r => r.S("client_order_id"), ("$d", decisionId));
        var decision = strategy.Decide(input);
        if (prior.Count > 0)
            return new PaperRunResult(decisionId, strategy.Id, snapshot.Id, snapshot.DataOrigin, estimate, decision.Rationale, true, []);

        var outcomes = new List<OrderOutcome>();
        for (var i = 0; i < decision.Orders.Count; i++)
        {
            var intent = decision.Orders[i];
            var clientOrderId = $"{decisionId[..24]}-{i}";
            outcomes.Add(db.InTransaction(() =>
            {
                var current = ledger.State(accountId, controls.Halted);
                var liquidityUsed = db.Scalar<long>("""
                    SELECT COUNT(*) FROM paper_fills f JOIN paper_orders o ON o.id = f.order_id
                    WHERE o.account_id = $a AND o.snapshot_id = $s AND o.side = $side AND o.action = $act
                    """, ("$a", accountId), ("$s", snapshot.Id), ("$side", intent.Side), ("$act", intent.Action)) > 0;
                var verdict = risk.Evaluate(intent, new RiskContext(mode, snapshot, rules.Sha256, estimate, current, time.GetUtcNow(),
                    feeMultiplier, liquidityUsed, ledger.PositionOrigins(accountId, intent.Ticker)));
                var fills = verdict.Approved && snapshot.Book is { } book ? PaperBroker.Simulate(intent, book, feeMultiplier) : [];
                if (intent.Action == OrderAction.Buy && fills.Sum(f => f.Quantity * f.Price + f.Fee) > RiskEngine.WorstCaseBuyCost(intent, feeMultiplier))
                    throw new InvalidOperationException("Simulated cost exceeded the risk engine's worst-case bound; refusing to book it.");
                var filled = fills.Sum(f => f.Quantity);
                var status = !verdict.Approved ? "rejected" : filled == 0 ? "unfilled" : filled < intent.Quantity ? "partially_filled" : "filled";

                var orderId = db.Insert("""
                    INSERT INTO paper_orders (account_id, client_order_id, decision_id, strategy, ticker, side, action, quantity,
                        limit_price, snapshot_id, research_result_id, data_origin, created_at, status, filled_quantity, reasons)
                    VALUES ($a, $c, $d, $s, $t, $side, $act, $q, $lp, $snap, $rr, $o, $ts, $st, $fq, $why)
                    """,
                    ("$a", accountId), ("$c", clientOrderId), ("$d", decisionId), ("$s", strategy.Id), ("$t", intent.Ticker),
                    ("$side", intent.Side), ("$act", intent.Action), ("$q", intent.Quantity), ("$lp", Dec.ToText(intent.LimitPrice)),
                    ("$snap", snapshot.Id), ("$rr", estimate?.ResearchResultId), ("$o", snapshot.DataOrigin),
                    ("$ts", Ts.ToText(time.GetUtcNow())), ("$st", status), ("$fq", filled),
                    ("$why", verdict.Approved ? decision.Rationale : string.Join("; ", verdict.Reasons)));
                FaultInjector?.Invoke("after-order-insert");

                foreach (var f in fills)
                {
                    var fillId = db.Insert("""
                        INSERT INTO paper_fills (order_id, ticker, side, action, quantity, price, fee, filled_at, simulated)
                        VALUES ($o, $t, $s, $a, $q, $p, $f, $ts, 1)
                        """,
                        ("$o", orderId), ("$t", intent.Ticker), ("$s", intent.Side), ("$a", intent.Action), ("$q", f.Quantity),
                        ("$p", Dec.ToText(f.Price)), ("$f", Dec.ToText(f.Fee)), ("$ts", Ts.ToText(time.GetUtcNow())));
                    FaultInjector?.Invoke("after-fill-insert");
                    ledger.RecordFill(accountId, fillId, intent.Ticker, intent.Side, intent.Action, f.Quantity, f.Price, f.Fee);
                }
                return new OrderOutcome(clientOrderId, intent, verdict, status, filled, fills);
            }));
        }
        return new PaperRunResult(decisionId, strategy.Id, snapshot.Id, snapshot.DataOrigin, estimate, decision.Rationale, false, outcomes);
    }

    /// <summary>
    /// One decision per (strategy, parameters, account, snapshot, estimate). Positions are deliberately excluded:
    /// a rerun on the same snapshot must not trade again just because the previous run changed the position.
    /// </summary>
    public static string DecisionId(IStrategy strategy, StrategyInput input, long accountId) =>
        CanonicalJson.HashOf(new JsonObject
        {
            ["strategy"] = strategy.Id,
            ["parameters"] = strategy.Parameters,
            ["account_id"] = accountId,
            ["snapshot_id"] = input.Snapshot.Id,
            ["rules_sha256"] = input.RulesSha256,
            ["research_result_id"] = input.Estimate?.ResearchResultId,
            ["fee_multiplier"] = input.FeeMultiplier is { } m ? Dec.ToText(Dec.Normalize(m)) : null,
        });

    private static decimal? FeeMultiplierFor(MarketStore store, MarketSnapshot s)
    {
        var ev = store.LatestEvent(s.EventTicker, s.ObservedAt);
        if (ev?.FeeMultiplierOverride is { } o) return o;
        return ev is null ? null : store.LatestSeries(ev.SeriesTicker, s.ObservedAt)?.FeeMultiplier;
    }
}
