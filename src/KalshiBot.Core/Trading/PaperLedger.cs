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
            var costOut = qty == heldQty ? heldCost : Math.Round(heldCost * qty / heldQty, 6, MidpointRounding.ToEven);
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

    /// <summary>Recomputes invariants from raw entries and fills. Returns a list of violations (empty = consistent).</summary>
    public List<string> Verify(long accountId)
    {
        var problems = new List<string>();
        var entries = Entries(accountId);
        var deposits = entries.Where(e => e.Kind == "deposit").Sum(e => e.CashDelta);
        var state = State(accountId, halted: false);
        if (state.Cash < 0m) problems.Add($"negative cash {Dec.ToText(state.Cash)}");
        foreach (var p in state.Positions)
        {
            if (p.Quantity < 0) problems.Add($"negative position {p.Ticker} {p.Side} {p.Quantity}");
            if (p.Quantity == 0 && p.CostBasis != 0m) problems.Add($"closed position {p.Ticker} {p.Side} has residual cost {Dec.ToText(p.CostBasis)}");
        }
        if (deposits != state.StartingCash) problems.Add("deposits do not equal the account's starting cash");
        foreach (var p in state.Positions.Where(p => p.CostBasis < 0m))
            problems.Add($"negative cost basis {p.Ticker} {p.Side} {Dec.ToText(p.CostBasis)}");

        var fills = db.Query("""
            SELECT f.id, f.action, f.quantity, f.price, f.fee FROM paper_fills f
            JOIN paper_orders o ON o.id = f.order_id WHERE o.account_id = $a
            """, r => new { Id = r.L("id"), Action = r.S("action"), Qty = r.L("quantity"), Price = r.D("price"), Fee = r.D("fee") }, ("$a", accountId));
        foreach (var f in fills)
        {
            var linked = entries.Where(e => e.RefKind == "fill" && e.RefId == f.Id).ToList();
            var trade = linked.SingleOrDefault(e => e.Kind == "trade");
            var expectedCash = (f.Action == OrderAction.Buy ? -1 : 1) * f.Qty * f.Price;
            if (trade is null || trade.CashDelta != expectedCash) problems.Add($"fill {f.Id}: trade entry missing or wrong cash");
            if (-linked.Where(e => e.Kind == "fee").Sum(e => e.CashDelta) != f.Fee) problems.Add($"fill {f.Id}: fee entry mismatch");
        }
        return problems;
    }

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
