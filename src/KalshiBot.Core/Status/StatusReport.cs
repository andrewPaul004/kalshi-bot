using System.Net;
using System.Text;
using KalshiBot.Core.Common;
using KalshiBot.Core.Recording;
using KalshiBot.Core.Research;
using KalshiBot.Core.Storage;
using KalshiBot.Core.Trading;

namespace KalshiBot.Core.Status;

public sealed record RunRow(long Id, string StartedAt, string? FinishedAt, string Status, string DataOrigin, string? Detail);
public sealed record MarketRow(MarketSnapshot Snapshot, int RulesVersions, TimeSpan Age);
public sealed record AccountRow(AccountState State, decimal MarkValue, decimal Equity, IReadOnlyList<string> Problems);
public sealed record OrderRow(string CreatedAt, string Ticker, string Side, string Action, long Quantity, string Limit,
    string Status, long Filled, string DataOrigin, string? Reasons);
public sealed record ResearchRunRow(long Id, string PacketId, string FinishedAt, string Outcome, string? AuthMethod, string? Detail);

public sealed record StatusReport(DateTimeOffset GeneratedAt, string DbPath, long SchemaVersion, bool Halted, string? HaltReason,
    IReadOnlyList<string> Watchlist, IReadOnlyList<RunRow> Runs, IReadOnlyList<MarketRow> Markets,
    IReadOnlyList<StoredResult> Research, IReadOnlyList<ResearchRunRow> ResearchRuns,
    IReadOnlyList<AccountRow> Accounts, IReadOnlyList<OrderRow> Orders)
{
    public static StatusReport Build(Db db, TimeProvider time)
    {
        var now = time.GetUtcNow();
        var controls = new Controls(db, time);
        var store = new MarketStore(db);
        var ledger = new PaperLedger(db, time);
        var latest = store.LatestPerTicker();

        var markets = latest.Select(s => new MarketRow(s,
            (int)db.Scalar<long>("SELECT COUNT(*) FROM market_rules WHERE ticker = $t", ("$t", s.Ticker)), now - s.ObservedAt)).ToList();

        var accounts = ledger.Accounts().Select(a =>
        {
            var st = ledger.State(a.Id, controls.Halted);
            var mark = st.Positions.Sum(p => p.Quantity * MarkPrice(latest.FirstOrDefault(s => s.Ticker == p.Ticker), p.Side));
            return new AccountRow(st, mark, st.Cash + mark, ledger.Verify(a.Id));
        }).ToList();

        return new StatusReport(now, db.Path, db.Scalar<long>("SELECT MAX(version) FROM schema_migrations"), controls.Halted, controls.HaltReason,
            new Watchlist(db, time).List(),
            db.Query("SELECT * FROM capture_runs ORDER BY id DESC LIMIT 8", r => new RunRow(r.L("id"), r.S("started_at"),
                r.Str("finished_at"), r.S("status"), r.S("data_origin"), r.Str("detail"))),
            markets,
            new ResearchStore(db).Results(10),
            db.Query("SELECT id, packet_id, finished_at, outcome, auth_method, detail FROM research_runs ORDER BY id DESC LIMIT 8",
                r => new ResearchRunRow(r.L("id"), r.S("packet_id"), r.S("finished_at"), r.S("outcome"), r.Str("auth_method"), r.Str("detail"))),
            accounts,
            db.Query("SELECT * FROM paper_orders ORDER BY id DESC LIMIT 15", r => new OrderRow(r.S("created_at"), r.S("ticker"),
                r.S("side"), r.S("action"), r.L("quantity"), r.S("limit_price"), r.S("status"), r.L("filled_quantity"),
                r.S("data_origin"), r.Str("reasons"))));
    }

    /// <summary>Conservative mark: what the position would fetch selling into the best recorded bid (0 if none).</summary>
    public static decimal MarkPrice(MarketSnapshot? s, string side)
    {
        if (s is null) return 0m;
        if (s.Status == "finalized" && s.Result is "yes" or "no") return s.Result == side ? 1m : 0m;
        var bids = s.Book?.BidsFor(side);
        return bids is { Count: > 0 } ? bids[0].Price : (side == Side.Yes ? s.YesBid : s.NoBid) ?? 0m;
    }

    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"kalshi-bot status @ {Ts.ToText(GeneratedAt)}   mode: OBSERVE + PAPER only (no live execution)");
        sb.AppendLine($"db: {DbPath} (schema v{SchemaVersion})   kill switch: {(Halted ? "ENGAGED - " + HaltReason : "off")}");
        sb.AppendLine($"watchlist: {(Watchlist.Count == 0 ? "(empty)" : string.Join(", ", Watchlist))}");
        sb.AppendLine();
        sb.AppendLine("capture runs:");
        foreach (var r in Runs) sb.AppendLine($"  #{r.Id} {r.StartedAt} {r.Status,-9} {Origin(r.DataOrigin)}{(r.Detail is null ? "" : "  " + Trim(r.Detail, 100))}");
        sb.AppendLine();
        sb.AppendLine("markets (latest snapshot):");
        foreach (var m in Markets)
        {
            var s = m.Snapshot;
            sb.AppendLine($"  {s.Ticker} [{Origin(s.DataOrigin)}] {s.Status} yes {P(s.YesBid)}/{P(s.YesAsk)} no {P(s.NoBid)}/{P(s.NoAsk)} " +
                          $"age {Age(m.Age)} rules v{m.RulesVersions} close {(s.CloseTime is { } c ? Ts.ToText(c) : "-")}");
        }
        sb.AppendLine();
        sb.AppendLine("research results (EXPERIMENTAL, unvalidated):");
        foreach (var r in Research)
            sb.AppendLine($"  #{r.Id} {r.Ticker} p_yes={(r.ProbabilityYes is { } p ? Dec.ToText(p) : "abstained")} conf={r.Confidence} via {r.SourceKind} [{Origin(r.DataOrigin)}] packet {r.PacketId[..12]}");
        foreach (var r in ResearchRuns)
            sb.AppendLine($"  run #{r.Id} {r.FinishedAt} {r.Outcome} auth={r.AuthMethod ?? "-"} {Trim(r.Detail ?? "", 120)}");
        sb.AppendLine();
        sb.AppendLine("paper accounts (SIMULATED fills):");
        foreach (var a in Accounts)
        {
            var st = a.State;
            sb.AppendLine($"  {st.Name}: cash {Dec.Show(st.Cash)} mark {Dec.Show(a.MarkValue)} equity {Dec.Show(a.Equity)} " +
                          $"(start {Dec.Show(st.StartingCash)}, realized {Dec.Show(st.RealizedPnl)}) ledger {(a.Problems.Count == 0 ? "OK" : "PROBLEMS: " + string.Join("; ", a.Problems))}");
            foreach (var p in st.Positions) sb.AppendLine($"    {p.Ticker} {p.Side} x{p.Quantity} cost {Dec.Show(p.CostBasis)}");
        }
        sb.AppendLine();
        sb.AppendLine("recent paper orders:");
        foreach (var o in Orders)
            sb.AppendLine($"  {o.CreatedAt} {o.Action} {o.Quantity} {o.Ticker} {o.Side} @ {o.Limit} -> {o.Status} ({o.Filled} filled) [{Origin(o.DataOrigin)}] {Trim(o.Reasons ?? "", 140)}");
        return sb.ToString();
    }

    public string ToHtml()
    {
        static string H(string? s) => WebUtility.HtmlEncode(s ?? "");
        string Badge(string origin) => DataOrigin.IsSynthetic(origin)
            ? "<span class=\"b syn\">SYNTHETIC</span>" : $"<span class=\"b live\">{H(origin)}</span>";

        var sb = new StringBuilder();
        sb.Append("""
            <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Kalshi Bot Status</title><style>
            :root{--bg:#fbfbfa;--fg:#1d1d1b;--mute:#6b6b66;--line:#e3e2dd;--syn:#9a3412;--synbg:#ffedd5;--live:#14532d;--livebg:#dcfce7;--warn:#991b1b;--warnbg:#fee2e2}
            @media (prefers-color-scheme:dark){:root{--bg:#161615;--fg:#ecebe7;--mute:#a3a29c;--line:#2e2d2a;--syn:#fdba74;--synbg:#431407;--live:#86efac;--livebg:#052e16;--warn:#fca5a5;--warnbg:#450a0a}}
            body{background:var(--bg);color:var(--fg);font:14px/1.45 ui-sans-serif,system-ui,sans-serif;margin:0 auto;max-width:1100px;padding:16px}
            h1{font-size:20px;margin:0 0 4px}h2{font-size:15px;margin:22px 0 6px}p.m{color:var(--mute);margin:0}
            .wrap{overflow-x:auto}table{border-collapse:collapse;width:100%;font-variant-numeric:tabular-nums}
            th,td{border-bottom:1px solid var(--line);padding:5px 8px;text-align:left;vertical-align:top}th{color:var(--mute);font-weight:600}
            .b{border-radius:4px;padding:1px 6px;font-size:12px;font-weight:600;white-space:nowrap}
            .syn{color:var(--syn);background:var(--synbg)}.live{color:var(--live);background:var(--livebg)}.warn{color:var(--warn);background:var(--warnbg)}
            code{font-size:12px}
            </style></head><body>
            """);
        sb.Append($"<h1>Kalshi Bot</h1><p class=\"m\">Generated {H(Ts.ToText(GeneratedAt))} · mode <b>OBSERVE + PAPER</b> (no live execution) · schema v{SchemaVersion}</p>");
        if (Halted) sb.Append($"<p><span class=\"b warn\">KILL SWITCH ENGAGED</span> {H(HaltReason)}</p>");
        sb.Append($"<p class=\"m\">Watchlist: {H(Watchlist.Count == 0 ? "(empty)" : string.Join(", ", Watchlist))}</p>");

        sb.Append("<h2>Markets (latest snapshot)</h2><div class=\"wrap\"><table><tr><th>Ticker</th><th>Origin</th><th>Status</th><th>Yes bid/ask</th><th>No bid/ask</th><th>Age</th><th>Rules</th><th>Close</th></tr>");
        foreach (var m in Markets)
        {
            var s = m.Snapshot;
            sb.Append($"<tr><td><code>{H(s.Ticker)}</code><br>{H(s.Title)}</td><td>{Badge(s.DataOrigin)}</td><td>{H(s.Status)}</td><td>{P(s.YesBid)} / {P(s.YesAsk)}</td><td>{P(s.NoBid)} / {P(s.NoAsk)}</td><td>{Age(m.Age)}</td><td>v{m.RulesVersions}</td><td>{H(s.CloseTime is { } c ? Ts.ToText(c) : "-")}</td></tr>");
        }
        sb.Append("</table></div>");

        sb.Append("<h2>Paper accounts <span class=\"b warn\">SIMULATED</span></h2><div class=\"wrap\"><table><tr><th>Account</th><th>Cash</th><th>Mark value</th><th>Equity</th><th>Realized</th><th>Positions</th><th>Ledger</th></tr>");
        foreach (var a in Accounts)
        {
            var st = a.State;
            var pos = string.Join("<br>", st.Positions.Select(p => $"{H(p.Ticker)} {H(p.Side)} ×{p.Quantity} (cost {Dec.Show(p.CostBasis)})"));
            sb.Append($"<tr><td>{H(st.Name)}</td><td>{Dec.Show(st.Cash)}</td><td>{Dec.Show(a.MarkValue)}</td><td>{Dec.Show(a.Equity)}</td><td>{Dec.Show(st.RealizedPnl)}</td><td>{(pos.Length == 0 ? "-" : pos)}</td><td>{(a.Problems.Count == 0 ? "OK" : "<span class=\"b warn\">" + H(string.Join("; ", a.Problems)) + "</span>")}</td></tr>");
        }
        sb.Append("</table></div>");

        sb.Append("<h2>Recent paper orders</h2><div class=\"wrap\"><table><tr><th>Time</th><th>Order</th><th>Origin</th><th>Result</th><th>Why</th></tr>");
        foreach (var o in Orders)
            sb.Append($"<tr><td>{H(o.CreatedAt)}</td><td>{H(o.Action)} {o.Quantity} <code>{H(o.Ticker)}</code> {H(o.Side)} @ {H(o.Limit)}</td><td>{Badge(o.DataOrigin)}</td><td>{H(o.Status)} ({o.Filled} filled)</td><td>{H(o.Reasons)}</td></tr>");
        sb.Append("</table></div>");

        sb.Append("<h2>Research <span class=\"b warn\">EXPERIMENTAL · UNVALIDATED</span></h2><div class=\"wrap\"><table><tr><th>#</th><th>Ticker</th><th>P(yes)</th><th>Confidence</th><th>Source</th><th>Origin</th><th>Packet</th></tr>");
        foreach (var r in Research)
            sb.Append($"<tr><td>{r.Id}</td><td><code>{H(r.Ticker)}</code></td><td>{(r.ProbabilityYes is { } p ? Dec.ToText(p) : "abstained")}</td><td>{H(r.Confidence)}</td><td>{H(r.SourceKind)}</td><td>{Badge(r.DataOrigin)}</td><td><code>{H(r.PacketId[..12])}</code></td></tr>");
        sb.Append("</table></div>");
        if (ResearchRuns.Count > 0)
        {
            sb.Append("<div class=\"wrap\"><table><tr><th>Claude run</th><th>Finished</th><th>Outcome</th><th>Auth</th><th>Detail</th></tr>");
            foreach (var r in ResearchRuns)
                sb.Append($"<tr><td>#{r.Id}</td><td>{H(r.FinishedAt)}</td><td>{H(r.Outcome)}</td><td>{H(r.AuthMethod ?? "-")}</td><td>{H(r.Detail)}</td></tr>");
            sb.Append("</table></div>");
        }

        sb.Append("<h2>Capture runs</h2><div class=\"wrap\"><table><tr><th>#</th><th>Started</th><th>Status</th><th>Origin</th><th>Detail</th></tr>");
        foreach (var r in Runs)
            sb.Append($"<tr><td>{r.Id}</td><td>{H(r.StartedAt)}</td><td>{H(r.Status)}</td><td>{Badge(r.DataOrigin)}</td><td>{H(r.Detail)}</td></tr>");
        sb.Append("</table></div></body></html>");
        return sb.ToString();
    }

    private static string Origin(string o) => DataOrigin.IsSynthetic(o) ? "SYNTHETIC" : o;
    private static string P(decimal? d) => d is null ? "-" : Dec.Show(d.Value);
    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n] + "…";
    private static string Age(TimeSpan t) => t.TotalMinutes < 1 ? $"{(int)t.TotalSeconds}s" : t.TotalHours < 1 ? $"{(int)t.TotalMinutes}m" : t.TotalDays < 1 ? $"{t.TotalHours:0.#}h" : $"{t.TotalDays:0.#}d";
}
