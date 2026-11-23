using KalshiBot.Core.Common;
using KalshiBot.Core.Recording;
using KalshiBot.Core.Storage;

namespace KalshiBot.Core.Trading;

public sealed record LedgerEntry(long Id, long AccountId, DateTimeOffset Ts, string Kind, string? Ticker, string? Side,
    long QuantityDelta, decimal CashDelta, decimal CostDelta, string? RefKind, long? RefId, string? Memo);

/// <summary>
/// Append-only paper ledger. Balances are never stored; they are recomputed from entries, so a replay is the
/// only source of truth. Cost basis moves with cost_delta so realized P&amp;L = sum(cash_delta + cost_delta) over
/// non-deposit entries. Verify() cross-checks entries against the independent paper_fills table.
/// </summary>
public sealed class PaperLedger(Db db, TimeProvider time)
{
    public long EnsureAccount(string name, decimal startingCash)
    {
        if (startingCash <= 0m) throw new KalshiBotException("Starting paper cash must be positive.");
        return db.InTransaction(() =>
        {
            var existing = db.QuerySingle("SELECT id, starting_cash FROM paper_accounts WHERE name = $n",
                r => new { Id = r.L("id"), Cash = r.D("starting_cash") }, ("$n", name));
            if (existing is not null)
            {
                if (existing.Cash != startingCash)
                    throw new KalshiBotException($"Paper account '{name}' already exists with starting cash {Dec.ToText(existing.Cash)}.");
                return existing.Id;
            }
            var now = Ts.ToText(time.GetUtcNow());
            var id = db.Insert("INSERT INTO paper_accounts (name, created_at, starting_cash) VALUES ($n, $t, $c)",
                ("$n", name), ("$t", now), ("$c", Dec.ToText(startingCash)));
            Append(id, "deposit", null, null, 0, startingCash, 0m, "account", id, "PAPER starting cash");
            return id;
        });
    }

    public long? FindAccount(string name) => db.Scalar<long?>("SELECT id FROM paper_accounts WHERE name = $n", ("$n", name));

    public List<(long Id, string Name)> Accounts() =>
        db.Query("SELECT id, name FROM paper_accounts ORDER BY id", r => (r.L("id"), r.S("name")));

    public List<LedgerEntry> Entries(long accountId) =>
        db.Query("SELECT * FROM ledger_entries WHERE account_id = $a ORDER BY id", r => new LedgerEntry(
            r.L("id"), r.L("account_id"), Ts.Parse(r.S("ts")), r.S("kind"), r.Str("ticker"), r.Str("side"),
            r.L("quantity_delta"), r.D("cash_delta"), r.D("cost_delta"), r.Str("ref_kind"), r.LN("ref_id"), r.Str("memo")),
            ("$a", accountId));

    public AccountState State(long accountId, bool halted)
    {
        var acct = db.QuerySingle("SELECT name, starting_cash FROM paper_accounts WHERE id = $a",
            r => new { Name = r.S("name"), Cash = r.D("starting_cash") }, ("$a", accountId))
            ?? throw new KalshiBotException($"Paper account {accountId} not found.");
        var entries = Entries(accountId);
        var dayStart = new DateTimeOffset(time.GetUtcNow().UtcDateTime.Date, TimeSpan.Zero);

        var positions = entries.Where(e => e.Ticker is not null && e.Side is not null)
            .GroupBy(e => (e.Ticker!, e.Side!))
            .Select(g => new PositionView(g.Key.Item1, g.Key.Item2, g.Sum(e => e.QuantityDelta), g.Sum(e => e.CostDelta)))
            .Where(p => p.Quantity != 0 || p.CostBasis != 0m)
            .OrderBy(p => p.Ticker, StringComparer.Ordinal).ThenBy(p => p.Side, StringComparer.Ordinal)
            .ToList();

        var todayPremium = -entries.Where(e => e.Ts >= dayStart && e.Kind is "trade" or "fee" && e.CashDelta < 0m).Sum(e => e.CashDelta);
        var realized = entries.Where(e => e.Kind != "deposit").Sum(e => e.CashDelta + e.CostDelta);
        return new AccountState(accountId, acct.Name, acct.Cash, entries.Sum(e => e.CashDelta), positions, todayPremium, realized, halted);
    }

    /// <summary>Records one simulated fill: a trade entry and a fee entry. Must run inside the caller's transaction.</summary>
    internal void RecordFill(long accountId, long fillId, string ticker, string side, string action, int qty, decimal price, decimal fee)
    {
        if (action == OrderAction.Buy)
        {
            var cost = qty * price;
            Append(accountId, "trade", ticker, side, qty, -cost, cost, "fill", fillId, $"PAPER buy {qty} {side} @ {Dec.ToText(price)} (simulated)");
        }
        else
        {
            var (heldQty, heldCost) = Holding(accountId, ticker, side);
            if (qty > heldQty) throw new KalshiBotException("Ledger refuses to sell more than held.");
            var costOut = CostOut(heldQty, heldCost, qty);
            Append(accountId, "trade", ticker, side, -qty, qty * price, -costOut, "fill", fillId, $"PAPER sell {qty} {side} @ {Dec.ToText(price)} (simulated)");
        }
        if (fee > 0m) Append(accountId, "fee", ticker, side, 0, -fee, 0m, "fill", fillId, $"PAPER fee ({FeeModel.Description})");
    }

    /// <summary>
    /// Settles all open positions in a market once Kalshi reports it finalized with a yes/no result.
    /// Idempotent: a market settles at most once per account.
    /// </summary>
    public int Settle(long accountId, MarketSnapshot snapshot) => db.InTransaction(() =>
    {
        if (snapshot.Status != "finalized" || snapshot.Result is not ("yes" or "no")) return 0;
        var already = db.Scalar<long>("SELECT COUNT(*) FROM paper_settlements WHERE account_id = $a AND ticker = $t",
            ("$a", accountId), ("$t", snapshot.Ticker));
        if (already > 0) return 0;
        var origins = PositionOrigins(accountId, snapshot.Ticker);
        if (origins.Any(o => o != snapshot.DataOrigin))
            throw new KalshiBotException(
                $"Refusing to settle {snapshot.Ticker}: position came from {string.Join(", ", origins)} data but the finalized snapshot is {snapshot.DataOrigin}.");

        var settled = 0;
        foreach (var side in new[] { Side.Yes, Side.No })
        {
            var (qty, cost) = Holding(accountId, snapshot.Ticker, side);
            if (qty == 0 && cost == 0m) continue;
            var payout = side == snapshot.Result ? qty * 1m : 0m;
            Append(accountId, "settlement", snapshot.Ticker, side, -qty, payout, -cost, "snapshot", snapshot.Id,
                $"PAPER settlement: result={snapshot.Result}, {qty} {side} pays {Dec.ToText(payout)}");
            settled++;
        }
        db.Exec("INSERT INTO paper_settlements (account_id, ticker, result, settled_at, snapshot_id) VALUES ($a, $t, $r, $at, $s)",
            ("$a", accountId), ("$t", snapshot.Ticker), ("$r", snapshot.Result), ("$at", Ts.ToText(time.GetUtcNow())), ("$s", snapshot.Id));
        return settled;
    });

    /// <summary>
    /// Rebuilds what the ledger should contain from the independent tables (paper_fills, paper_orders,
    /// paper_settlements) and replays every entry in order. Returns every violation found; empty means consistent.
    /// </summary>
    public List<string> Verify(long accountId)
    {
        var problems = new List<string>();
        var entries = Entries(accountId);
        var startingCash = db.Scalar<string>("SELECT starting_cash FROM paper_accounts WHERE id = $a", ("$a", accountId)) is { } sc
            ? Dec.Parse(sc) : throw new KalshiBotException($"Paper account {accountId} not found.");

        var deposits = entries.Where(e => e.Kind == "deposit").ToList();
        if (deposits.Count != 1 || deposits[0].CashDelta != startingCash || deposits[0].RefKind != "account" || deposits[0].RefId != accountId
            || deposits[0].QuantityDelta != 0 || deposits[0].CostDelta != 0m || deposits[0].Ticker is not null)
            problems.Add("expected exactly one deposit entry equal to the account's starting cash");

        var fills = db.Query("""
            SELECT f.id, f.order_id, f.ticker, f.side, f.action, f.quantity, f.price, f.fee FROM paper_fills f
            JOIN paper_orders o ON o.id = f.order_id WHERE o.account_id = $a
            """, r => new Fill(r.L("id"), r.L("order_id"), r.S("ticker"), r.S("side"), r.S("action"), r.L("quantity"), r.D("price"), r.D("fee")),
            ("$a", accountId)).ToDictionary(f => f.Id);

        // Orders agree with their fills.
        foreach (var o in db.Query("SELECT id, quantity, status, filled_quantity FROM paper_orders WHERE account_id = $a",
                     r => (Id: r.L("id"), Qty: r.L("quantity"), Status: r.S("status"), Filled: r.L("filled_quantity")), ("$a", accountId)))
        {
            var filled = fills.Values.Where(f => f.OrderId == o.Id).Sum(f => f.Quantity);
            if (filled != o.Filled) problems.Add($"order {o.Id}: filled_quantity {o.Filled} but fills sum to {filled}");
            var expected = o.Status switch
            {
                "rejected" or "unfilled" => filled == 0,
                "filled" => filled == o.Qty,
                "partially_filled" => filled > 0 && filled < o.Qty,
                _ => false,
            };
            if (!expected) problems.Add($"order {o.Id}: status '{o.Status}' inconsistent with {filled}/{o.Qty} filled");
        }

        // Each fill has exactly one trade entry and fee entries summing to its fee.
        foreach (var f in fills.Values)
        {
            var linked = entries.Where(e => e.RefKind == "fill" && e.RefId == f.Id).ToList();
            if (linked.Count(e => e.Kind == "trade") != 1) problems.Add($"fill {f.Id}: expected exactly one trade entry");
            if (-linked.Where(e => e.Kind == "fee").Sum(e => e.CashDelta) != f.Fee) problems.Add($"fill {f.Id}: fee entries do not sum to the fill fee");
        }

        var settlements = db.Query("SELECT ticker, result, snapshot_id FROM paper_settlements WHERE account_id = $a",
            r => (Ticker: r.S("ticker"), Result: r.S("result"), Snapshot: r.L("snapshot_id")), ("$a", accountId)).ToDictionary(x => x.Ticker);

        // Replay in order with expectations rebuilt from the independent tables.
        var held = new Dictionary<(string, string), (long Qty, decimal Cost)>();
        var cash = 0m;
        foreach (var e in entries)
        {
            cash += e.CashDelta;
            if (cash < 0m) problems.Add($"entry {e.Id}: cash goes negative ({Dec.ToText(cash)})");
            if (e.Kind == "deposit") continue;
            if (e.Ticker is null || e.Side is null) { problems.Add($"entry {e.Id}: {e.Kind} entry without ticker/side"); continue; }
            var key = (e.Ticker, e.Side);
            var (qty, cost) = held.GetValueOrDefault(key);

            switch (e.Kind)
            {
                case "trade" or "fee":
                    if (e.RefKind != "fill" || e.RefId is not { } fid || !fills.TryGetValue(fid, out var f))
                    {
                        problems.Add($"entry {e.Id}: {e.Kind} entry does not reference a fill of this account");
                        break;
                    }
                    if (f.Ticker != e.Ticker || f.Side != e.Side) problems.Add($"entry {e.Id}: ticker/side differ from fill {f.Id}");
                    if (e.Kind == "fee")
                    {
                        if (e.QuantityDelta != 0 || e.CostDelta != 0m || e.CashDelta >= 0m) problems.Add($"entry {e.Id}: malformed fee entry");
                        break;
                    }
                    var buy = f.Action == OrderAction.Buy;
                    var expQty = buy ? f.Quantity : -f.Quantity;
                    var expCash = buy ? -f.Quantity * f.Price : f.Quantity * f.Price;
                    var expCost = buy ? f.Quantity * f.Price : -CostOut(qty, cost, f.Quantity);
                    if (e.QuantityDelta != expQty || e.CashDelta != expCash || e.CostDelta != expCost)
                        problems.Add($"entry {e.Id}: does not match fill {f.Id} (qty/cash/cost)");
                    break;
                case "settlement":
                    if (!settlements.TryGetValue(e.Ticker, out var st) || e.RefKind != "snapshot" || e.RefId != st.Snapshot)
                    {
                        problems.Add($"entry {e.Id}: settlement entry without a matching settlement record");
                        break;
                    }
                    var payout = e.Side == st.Result ? qty : 0m;
                    if (e.QuantityDelta != -qty || e.CostDelta != -cost || e.CashDelta != payout)
                        problems.Add($"entry {e.Id}: settlement does not close the position at the recorded result");
                    break;
                default:
                    problems.Add($"entry {e.Id}: unknown kind '{e.Kind}'");
                    break;
            }

            qty += e.QuantityDelta;
            cost += e.CostDelta;
            if (qty < 0) problems.Add($"entry {e.Id}: position {e.Ticker} {e.Side} goes negative");
            if (cost < 0m || (qty == 0 && cost != 0m)) problems.Add($"entry {e.Id}: cost basis {Dec.ToText(cost)} invalid for quantity {qty}");
            held[key] = (qty, cost);
        }

        foreach (var ((ticker, side), (qty, _)) in held)
            if (settlements.ContainsKey(ticker) && qty != 0) problems.Add($"{ticker} {side}: open quantity {qty} after settlement");
        return problems;
    }

    private sealed record Fill(long Id, long OrderId, string Ticker, string Side, string Action, long Quantity, decimal Price, decimal Fee);

    /// <summary>Average-cost removal for a partial close; a full close removes exactly the remaining cost.</summary>
    internal static decimal CostOut(long heldQty, decimal heldCost, long qty) =>
        qty == heldQty ? heldCost : Math.Round(heldCost * qty / heldQty, 6, MidpointRounding.ToEven);

    /// <summary>Data origins of the filled paper orders in this market for this account.</summary>
    public IReadOnlyCollection<string> PositionOrigins(long accountId, string ticker) =>
        db.Query("SELECT DISTINCT data_origin FROM paper_orders WHERE account_id = $a AND ticker = $t AND filled_quantity > 0 ORDER BY data_origin",
            r => r.S("data_origin"), ("$a", accountId), ("$t", ticker));

    private (long Qty, decimal Cost) Holding(long accountId, string ticker, string side)
    {
        var rows = db.Query("SELECT quantity_delta, cost_delta FROM ledger_entries WHERE account_id = $a AND ticker = $t AND side = $s",
            r => (r.L("quantity_delta"), r.D("cost_delta")), ("$a", accountId), ("$t", ticker), ("$s", side));
        return (rows.Sum(x => x.Item1), rows.Sum(x => x.Item2));
    }

    private void Append(long accountId, string kind, string? ticker, string? side, long qtyDelta, decimal cashDelta,
        decimal costDelta, string refKind, long refId, string memo) =>
        db.Exec("""
            INSERT INTO ledger_entries (account_id, ts, kind, ticker, side, quantity_delta, cash_delta, cost_delta, ref_kind, ref_id, memo)
            VALUES ($a, $ts, $k, $t, $s, $q, $c, $cd, $rk, $ri, $m)
            """,
            ("$a", accountId), ("$ts", Ts.ToText(time.GetUtcNow())), ("$k", kind), ("$t", ticker), ("$s", side),
            ("$q", qtyDelta), ("$c", Dec.ToText(cashDelta)), ("$cd", Dec.ToText(costDelta)), ("$rk", refKind),
            ("$ri", refId), ("$m", memo));
}
