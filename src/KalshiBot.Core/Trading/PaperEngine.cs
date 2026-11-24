using System.Text.Json.Nodes;
using KalshiBot.Core.Common;
using KalshiBot.Core.Recording;
using KalshiBot.Core.Storage;

namespace KalshiBot.Core.Trading;

/// <summary>Declared paper execution assumptions (spec 8.4).</summary>
/// <param name="ArrivalLatency">Simulated time from decision to order arrival at the exchange.</param>
/// <param name="MaxArrivalGap">If the first observation after arrival is later than this, what was executable at
/// arrival is unknown, so the order resolves as no-fill (expired_no_evidence).</param>
public sealed record PaperFillPolicy(TimeSpan ArrivalLatency, TimeSpan MaxArrivalGap)
{
    public static readonly PaperFillPolicy Default = new(TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(2));
}

public sealed record OrderOutcome(long OrderId, string ClientOrderId, OrderIntent Intent, RiskVerdict Verdict, string Status,
    DateTimeOffset ArrivalAt, decimal Reserved);

public sealed record ResolvedOrder(long OrderId, string Ticker, string Status, int Filled, long? FillSnapshotId,
    IReadOnlyList<SimulatedFill> Fills, string Reason);

public sealed record PaperRunResult(string DecisionId, string StrategyId, long SnapshotId, string DataOrigin, Forecast? Forecast,
    string? FeeScheduleId, string Rationale, bool AlreadyProcessed, IReadOnlyList<ResolvedOrder> Resolved,
    IReadOnlyList<OrderOutcome> Orders, string? HaltRaised);

public sealed record Valuation(decimal Cash, decimal MarkValue, IReadOnlyList<string> Problems)
{
    public decimal Equity => Cash + MarkValue;
    public bool Certain => Problems.Count == 0;
}

public sealed class Controls(Db db, TimeProvider time)
{
    public bool Halted => db.Scalar<string>("SELECT value FROM controls WHERE key = 'halt'") == "1";
    public string? HaltReason => Halted ? db.Scalar<string>("SELECT value FROM controls WHERE key = 'halt_reason'") : null;

    /// <summary>Persisted, so restarts and crossing midnight never clear it (spec 6.3, AC-11).</summary>
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
/// Paper executor. <see cref="Run"/> evaluates one market: deterministic forecast and strategy, then for each candidate
/// risk check + order intent + worst-case reservation in one IMMEDIATE transaction. Orders resolve later in
/// <see cref="ResolvePending"/> against the first observation at or after simulated arrival. The decision id hashes
/// everything the strategy saw, so a rerun on unchanged inputs is a no-op.
/// </summary>
public sealed class PaperEngine(Db db, TimeProvider time, RiskEngine risk, PaperFillPolicy? policy = null)
{
    private readonly PaperFillPolicy _policy = policy ?? PaperFillPolicy.Default;
    private readonly MarketStore _store = new(db);
    private readonly PaperLedger _ledger = new(db, time);
    private readonly Controls _controls = new(db, time);

    /// <summary>Test hook: throw at a named point inside a transaction to prove rollback.</summary>
    internal Action<string>? FaultInjector { get; set; }

    public PaperRunResult Run(string accountName, string ticker, IStrategy strategy, IForecastModel model, ExecutionMode mode = ExecutionMode.Paper, IEvidenceView? evidence = null)
    {
        var accountId = _ledger.FindAccount(accountName) ?? throw new KalshiBotException($"Paper account '{accountName}' not found. Run 'kb paper init'.");
        var resolved = ResolvePending(accountId);
        var now = time.GetUtcNow();
        var snapshot = _store.Latest(ticker) ?? throw new KalshiBotException($"No recorded snapshot for {ticker}. Run 'kb record'.");

        var previous = _store.Previous(snapshot);
        var rulesChanged = previous is not null && previous.RulesId != snapshot.RulesId;
        var ev = _store.EventFor(snapshot);
        var series = ev is null ? null : _store.SeriesFor(ev.SeriesTicker, snapshot);
        var fees = FeeSchedules.Resolve(snapshot.DataOrigin, ev?.FeeTypeOverride ?? series?.FeeType,
            ev?.FeeMultiplierOverride ?? series?.FeeMultiplier, now, out var feeReason);
        var forecast = EvidenceGuard.Run(model, snapshot, evidence ?? new EmptyEvidence(snapshot.ObservedAt));
        var haltRaised = CheckDailyLoss(accountId, now);

        var state = _ledger.State(accountId, _controls.Halted);
        var input = new StrategyInput(snapshot, ev?.SeriesTicker, forecast, fees, state.Positions.Where(p => p.Ticker == ticker).ToList());
        var decision = strategy.Decide(input);
        var rationale = decision.Rationale + (fees is null ? $" [fees: {feeReason}]" : "");
        var decisionId = CanonicalJson.HashOf(new JsonObject
        {
            ["strategy"] = strategy.Id,
            ["config_hash"] = strategy.ConfigHash,
            ["account_id"] = accountId,
            ["snapshot_id"] = snapshot.Id,
            ["model"] = model.Id,
            ["evidence"] = forecast?.EvidenceRevision,
            ["fee_schedule"] = fees?.ScheduleId,
        });

        return db.InTransaction(() =>
        {
            if (db.Scalar<long>("SELECT COUNT(*) FROM paper_decisions WHERE account_id = $a AND decision_id = $d",
                    ("$a", accountId), ("$d", decisionId)) > 0)
                return new PaperRunResult(decisionId, strategy.Id, snapshot.Id, snapshot.DataOrigin, forecast, fees?.ScheduleId,
                    rationale, true, resolved, [], haltRaised);

            var decisionRow = db.Insert("""
                INSERT INTO paper_decisions (account_id, decision_id, strategy, config_hash, snapshot_id, data_origin, model_id,
                    forecast_probability, forecast_uncertainty, fee_schedule_id, outcome, rationale, created_at)
                VALUES ($a, $d, $s, $h, $snap, $o, $m, $p, $u, $f, $out, $why, $t)
                """,
                ("$a", accountId), ("$d", decisionId), ("$s", strategy.Id), ("$h", strategy.ConfigHash), ("$snap", snapshot.Id),
                ("$o", snapshot.DataOrigin), ("$m", forecast?.ModelId), ("$p", forecast is null ? null : Dec.ToText(forecast.ProbabilityYes)),
                ("$u", forecast is null ? null : Dec.ToText(forecast.Uncertainty)), ("$f", fees?.ScheduleId),
                ("$out", decision.Candidates.Count == 0 ? "abstain" : "candidates"), ("$why", rationale), ("$t", Ts.ToText(now)));

            var outcomes = new List<OrderOutcome>();
            for (var i = 0; i < decision.Candidates.Count; i++)
            {
                var (intent, signalKey) = (decision.Candidates[i].Intent, decision.Candidates[i].SignalKey);
                var current = _ledger.State(accountId, _controls.Halted);
                var valuation = Value(current, now);
                var entered = db.Scalar<long>("""
                    SELECT COUNT(*) FROM paper_orders WHERE account_id = $a AND signal_key = $k AND status IN ('pending','filled','partially_filled')
                    """, ("$a", accountId), ("$k", signalKey)) > 0;
                var verdict = risk.Evaluate(intent, new RiskContext(mode, snapshot, forecast, fees, current, now, rulesChanged,
                    valuation.Certain ? null : string.Join("; ", valuation.Problems), entered));
                var arrival = now + _policy.ArrivalLatency;
                var clientOrderId = $"{decisionId[..24]}-{i}";
                var orderId = db.Insert("""
                    INSERT INTO paper_orders (account_id, decision_row_id, client_order_id, signal_key, strategy, ticker, event_ticker,
                        side, action, quantity, limit_price, time_in_force, fee_schedule_id, fee_coefficient, fee_multiplier,
                        decision_snapshot_id, data_origin, created_at, arrival_at, status, reasons)
                    VALUES ($a, $dr, $c, $k, $s, $t, $e, $side, $act, $q, $lp, 'ioc', $fs, $fc, $fm, $snap, $o, $ts, $arr, $st, $why)
                    """,
                    ("$a", accountId), ("$dr", decisionRow), ("$c", clientOrderId), ("$k", signalKey), ("$s", strategy.Id),
                    ("$t", intent.Ticker), ("$e", snapshot.EventTicker), ("$side", intent.Side), ("$act", intent.Action),
                    ("$q", intent.Quantity), ("$lp", Dec.ToText(intent.LimitPrice)), ("$fs", fees?.ScheduleId),
                    ("$fc", fees is null ? null : Dec.ToText(fees.TakerCoefficient)),
                    ("$fm", fees?.Multiplier is { } m ? Dec.ToText(m) : null), ("$snap", snapshot.Id), ("$o", snapshot.DataOrigin),
                    ("$ts", Ts.ToText(now)), ("$arr", Ts.ToText(arrival)), ("$st", verdict.Approved ? "pending" : "rejected"),
                    ("$why", verdict.Approved ? null : string.Join("; ", verdict.Reasons)));
                FaultInjector?.Invoke("after-order-insert");

                var reserved = 0m;
                if (verdict.Approved)
                {
                    reserved = intent.Action == OrderAction.Buy ? RiskEngine.WorstCaseBuyCost(intent, fees!) : 0m;
                    db.Exec("""
                        INSERT INTO risk_reservations (account_id, order_id, amount, status, created_at)
                        VALUES ($a, $o, $amt, 'held', $t)
                        """, ("$a", accountId), ("$o", orderId), ("$amt", Dec.ToText(reserved)), ("$t", Ts.ToText(now)));
                    FaultInjector?.Invoke("after-reservation");
                }
                outcomes.Add(new OrderOutcome(orderId, clientOrderId, intent, verdict, verdict.Approved ? "pending" : "rejected", arrival, reserved));
            }
            return new PaperRunResult(decisionId, strategy.Id, snapshot.Id, snapshot.DataOrigin, forecast, fees?.ScheduleId,
                rationale, false, resolved, outcomes, haltRaised);
        });
    }

    public IReadOnlyList<ResolvedOrder> ResolvePending(string accountName) =>
        ResolvePending(_ledger.FindAccount(accountName) ?? throw new KalshiBotException($"Paper account '{accountName}' not found."));

    /// <summary>
    /// Resolves pending orders against the first observation at or after their simulated arrival (spec 8.4, AC-22).
    /// A dispatched order cannot be recalled, so this runs even while halted (spec 7.2). Each order resolves in its
    /// own transaction: fills, ledger entries and the reservation release commit together or not at all.
    /// </summary>
    public IReadOnlyList<ResolvedOrder> ResolvePending(long accountId)
    {
        var pending = db.Query("""
            SELECT o.*, r.amount AS reserved FROM paper_orders o JOIN risk_reservations r ON r.order_id = o.id
            WHERE o.account_id = $a AND o.status = 'pending' ORDER BY o.id
            """, r => new
            {
                Id = r.L("id"), Ticker = r.S("ticker"), Side = r.S("side"), Action = r.S("action"), Qty = (int)r.L("quantity"),
                Limit = r.D("limit_price"), Arrival = Ts.Parse(r.S("arrival_at")), Origin = r.S("data_origin"),
                Fees = r.Str("fee_schedule_id") is { } fs ? new FeeContext(fs, r.D("fee_coefficient"), r.DN("fee_multiplier"), "recorded") : null,
                Reserved = r.D("reserved"),
            }, ("$a", accountId));

        var results = new List<ResolvedOrder>();
        foreach (var o in pending)
        {
            var now = time.GetUtcNow();
            var snap = _store.FirstAtOrAfter(o.Ticker, o.Arrival);
            if (snap is null && now - o.Arrival <= _policy.MaxArrivalGap) continue; // evidence may still arrive

            var intent = new OrderIntent(o.Ticker, o.Side, o.Action, o.Qty, o.Limit);
            results.Add(db.InTransaction(() =>
            {
                IReadOnlyList<SimulatedFill> fills = [];
                string status, reason;
                if (snap is null) (status, reason) = ("expired_no_evidence", $"no observation within {_policy.MaxArrivalGap} after arrival; no fill");
                else if (snap.DataOrigin != o.Origin) (status, reason) = ("expired_no_evidence", "post-arrival observation has a different data origin");
                else if (snap.ObservedAt - o.Arrival > _policy.MaxArrivalGap)
                    (status, reason) = ("expired_no_evidence", $"first observation after arrival was {snap.ObservedAt - o.Arrival} later; executable depth at arrival unknown; no fill");
                else if (snap.Status != "active") (status, reason) = ("unfilled", $"market '{snap.Status}' at arrival");
                else if (snap.Book is not { } book || o.Fees is null) (status, reason) = ("unfilled", "no order book (or fee context) at arrival");
                else
                {
                    var consumed = db.Query("""
                        SELECT price, SUM(quantity) AS q FROM paper_fills
                        WHERE snapshot_id = $s AND ticker = $t AND side = $side AND action = $act GROUP BY price
                        """, r => (Price: r.D("price"), Qty: r.L("q")),
                        ("$s", snap.Id), ("$t", o.Ticker), ("$side", o.Side), ("$act", o.Action))
                        .GroupBy(x => x.Price).ToDictionary(g => g.Key, g => g.Sum(x => x.Qty));
                    fills = PaperBroker.Simulate(intent, book, o.Fees, consumed);
                    var filledQty = fills.Sum(f => f.Quantity);
                    status = filledQty == 0 ? "unfilled" : filledQty < o.Qty ? "partially_filled" : "filled";
                    reason = $"IOC against snapshot #{snap.Id} observed {Ts.ToText(snap.ObservedAt)}";
                }

                var cost = fills.Sum(f => f.Quantity * f.Price + f.Fee);
                if (o.Action == OrderAction.Buy && cost > o.Reserved)
                    throw new InvalidOperationException($"Paper order {o.Id} would cost {cost} but reserved {o.Reserved}; refusing to book it.");
                foreach (var f in fills)
                {
                    var fillId = db.Insert("""
                        INSERT INTO paper_fills (order_id, snapshot_id, ticker, side, action, quantity, price, fee, filled_at, simulated)
                        VALUES ($o, $s, $t, $side, $act, $q, $p, $f, $ts, 1)
                        """,
                        ("$o", o.Id), ("$s", snap!.Id), ("$t", o.Ticker), ("$side", o.Side), ("$act", o.Action), ("$q", f.Quantity),
                        ("$p", Dec.ToText(f.Price)), ("$f", Dec.ToText(f.Fee)), ("$ts", Ts.ToText(snap.ObservedAt)));
                    FaultInjector?.Invoke("after-fill-insert");
                    _ledger.RecordFill(accountId, fillId, o.Ticker, o.Side, o.Action, f.Quantity, f.Price, f.Fee);
                }
                var filled = fills.Sum(f => f.Quantity);
                db.Exec("""
                    UPDATE paper_orders SET status = $st, filled_quantity = $fq, fill_snapshot_id = $fs, resolved_at = $t, reasons = $why
                    WHERE id = $id AND status = 'pending'
                    """, ("$st", status), ("$fq", filled), ("$fs", snap?.Id), ("$t", Ts.ToText(now)), ("$why", reason), ("$id", o.Id));
                db.Exec("UPDATE risk_reservations SET status = 'resolved', consumed = $c, resolved_at = $t WHERE order_id = $id AND status = 'held'",
                    ("$c", Dec.ToText(cost)), ("$t", Ts.ToText(now)), ("$id", o.Id));
                return new ResolvedOrder(o.Id, o.Ticker, status, filled, snap?.Id, fills, reason);
            }));
        }
        return results;
    }

    /// <summary>
    /// Conservative executable-liquidation marks (spec 6.3): walk recorded bids for the held quantity; depth beyond the
    /// book is valued at zero. Missing or stale snapshots make valuation uncertain, which blocks new risk.
    /// </summary>
    public Valuation Value(AccountState state, DateTimeOffset now)
    {
        var problems = new List<string>();
        var marks = 0m;
        foreach (var p in state.Positions.Where(p => p.Quantity > 0))
        {
            var s = _store.Latest(p.Ticker);
            if (s is null) { problems.Add($"no snapshot to value {p.Ticker}"); continue; }
            if (s.Status == "finalized" && s.Result is "yes" or "no") { marks += p.Side == s.Result ? p.Quantity : 0m; continue; }
            if (now - s.ObservedAt > risk.Limits.MaxSnapshotAge) { problems.Add($"mark for {p.Ticker} is stale ({Ts.ToText(s.ObservedAt)})"); continue; }
            var remaining = p.Quantity;
            foreach (var level in s.Book?.BidsFor(p.Side) ?? [])
            {
                var take = Math.Min(remaining, (long)Math.Floor(level.Size));
                marks += take * level.Price;
                remaining -= take;
                if (remaining == 0) break;
            }
        }
        return new Valuation(state.Cash, marks, problems);
    }

    /// <summary>
    /// Daily loss trigger (spec 6.3): opening equity is persisted per America/Chicago day; when the loss since then reaches
    /// the limit, the halt is persisted before any further submission. Only an operator reset clears it.
    /// </summary>
    private string? CheckDailyLoss(long accountId, DateTimeOffset now)
    {
        var valuation = Value(_ledger.State(accountId, _controls.Halted), now);
        if (!valuation.Certain) return null; // risk rejects new entries on uncertain valuation; no opening equity recorded
        var day = ReportingDay.Text(ReportingDay.For(now));
        db.Exec("INSERT OR IGNORE INTO paper_daily_equity (account_id, reporting_day, opening_equity, recorded_at) VALUES ($a, $d, $e, $t)",
            ("$a", accountId), ("$d", day), ("$e", Dec.ToText(valuation.Equity)), ("$t", Ts.ToText(now)));
        var opening = Dec.Parse(db.Scalar<string>("SELECT opening_equity FROM paper_daily_equity WHERE account_id = $a AND reporting_day = $d",
            ("$a", accountId), ("$d", day))!);
        var loss = opening - valuation.Equity;
        if (loss < risk.Limits.DailyLossTrigger || _controls.Halted) return null;
        var reason = $"daily loss trigger: equity {Dec.Show(valuation.Equity)} is {Dec.Show(loss)} below opening {Dec.Show(opening)} ({day} America/Chicago)";
        _controls.SetHalt(true, reason);
        return reason;
    }
}
