using System.Net;
using System.Text;
using KalshiBot.Core.Common;
using KalshiBot.Core.Recording;
using KalshiBot.Core.Research;
using KalshiBot.Core.Storage;
using KalshiBot.Core.Trading;

namespace KalshiBot.Core.Status;

public sealed record RunRow(long Id, string StartedAt, string Status, string DataOrigin, string? Detail);
public sealed record MarketRow(MarketSnapshot Snapshot, int RulesVersions, TimeSpan Age);
public sealed record AccountRow(AccountState State, Valuation Valuation, decimal? OpeningEquityToday, IReadOnlyList<string> Problems);
public sealed record OrderRow(string CreatedAt, string Ticker, string Side, string Action, long Quantity, string Limit, string Status,
    long Filled, string ArrivalAt, string DataOrigin, string? Reasons);
public sealed record DecisionRow(string CreatedAt, string Ticker, string Outcome, string? ModelId, string Rationale);
public sealed record ResearchRunRow(long Id, string FinishedAt, string Outcome, string? AuthMethod, string? CliVersion, string? Detail);

/// <param name="BillingVerified">What the software checked itself (last research auth check).</param>
/// <param name="BillingAttested">What the owner stated and when (usage credits disabled).</param>
/// <param name="BillingUnknown">What nobody has verified (spec 4.2: never claim a guaranteed zero-dollar bill).</param>
public sealed record StatusReport(DateTimeOffset GeneratedAt, string DbPath, long SchemaVersion, string? DbOrigin, bool Halted,
    string? HaltReason, IReadOnlyList<string> Watchlist, IReadOnlyList<RunRow> Runs, IReadOnlyList<MarketRow> Markets,
    string BillingVerified, string BillingAttested, IReadOnlyList<string> BillingUnknown,
    IReadOnlyList<StoredRecord> Research, IReadOnlyList<ResearchRunRow> ResearchRuns,
    IReadOnlyList<AccountRow> Accounts, IReadOnlyList<OrderRow> Orders, IReadOnlyList<DecisionRow> Decisions,
    int Abstentions, int DecisionsWithCandidates)
{
    public static StatusReport Build(Db db, TimeProvider time)
    {
        var now = time.GetUtcNow();
        var controls = new Controls(db, time);
        var store = new MarketStore(db);
        var ledger = new PaperLedger(db, time);
        var engine = new PaperEngine(db, time, new RiskEngine(RiskLimits.PaperFixture));
        var research = new ResearchStore(db);
        var day = ReportingDay.Text(ReportingDay.For(now));

        var markets = store.LatestPerTicker().Select(s => new MarketRow(s,
            (int)db.Scalar<long>("SELECT COUNT(*) FROM market_rules WHERE ticker = $t", ("$t", s.Ticker)), now - s.ObservedAt)).ToList();

        var accounts = ledger.Accounts().Select(a =>
        {
            var st = ledger.State(a.Id, controls.Halted);
            var opening = db.Scalar<string>("SELECT opening_equity FROM paper_daily_equity WHERE account_id = $a AND reporting_day = $d",
                ("$a", a.Id), ("$d", day));
            return new AccountRow(st, engine.Value(st, now), opening is null ? null : Dec.Parse(opening), ledger.Verify(a.Id));
        }).ToList();

        var lastAuth = db.QuerySingle("SELECT finished_at, outcome, auth_method, subscription_type, cli_version FROM research_runs WHERE auth_method IS NOT NULL ORDER BY id DESC LIMIT 1",
            r => $"last auth check {r.S("finished_at")}: {r.S("auth_method")}{(r.Str("subscription_type") is { } t ? " (" + t + ")" : "")}, CLI {r.Str("cli_version") ?? "?"}, run {r.S("outcome")}");
        var attestation = research.LatestAttestation();

        return new StatusReport(now, db.Path, db.Scalar<long>("SELECT MAX(version) FROM schema_migrations"), DatabaseOrigin.Get(db),
            controls.Halted, controls.HaltReason, new Watchlist(db, time).List(),
            db.Query("SELECT * FROM capture_runs ORDER BY id DESC LIMIT 8",
                r => new RunRow(r.L("id"), r.S("started_at"), r.S("status"), r.S("data_origin"), r.Str("detail"))),
            markets,
            lastAuth ?? "no research auth check has run yet",
            attestation is null ? "none: usage credits/extra usage not attested (research runs refuse)" : $"owner attested on {Ts.ToText(attestation.AttestedAt)}: {attestation.Statement}",
            ["usage-credit setting is not machine-verifiable; relies on the owner attestation",
             "MDM and server-managed Claude settings are not inspected (check /status → Setting sources)",
             "no guarantee of a zero-dollar bill is implied"],
            research.Records(10),
            db.Query("SELECT id, finished_at, outcome, auth_method, cli_version, detail FROM research_runs ORDER BY id DESC LIMIT 8",
                r => new ResearchRunRow(r.L("id"), r.S("finished_at"), r.S("outcome"), r.Str("auth_method"), r.Str("cli_version"), r.Str("detail"))),
            accounts,
            db.Query("SELECT * FROM paper_orders ORDER BY id DESC LIMIT 15", r => new OrderRow(r.S("created_at"), r.S("ticker"), r.S("side"),
                r.S("action"), r.L("quantity"), r.S("limit_price"), r.S("status"), r.L("filled_quantity"), r.S("arrival_at"),
                r.S("data_origin"), r.Str("reasons"))),
            db.Query("""
                SELECT d.created_at, s.ticker, d.outcome, d.model_id, d.rationale FROM paper_decisions d
                JOIN market_snapshots s ON s.id = d.snapshot_id ORDER BY d.id DESC LIMIT 10
                """, r => new DecisionRow(r.S("created_at"), r.S("ticker"), r.S("outcome"), r.Str("model_id"), r.S("rationale"))),
            (int)db.Scalar<long>("SELECT COUNT(*) FROM paper_decisions WHERE outcome = 'abstain'"),
            (int)db.Scalar<long>("SELECT COUNT(*) FROM paper_decisions WHERE outcome = 'candidates'"));
    }

    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"kalshi-bot status @ {Ts.ToText(GeneratedAt)}   mode: OBSERVE + PAPER only (no live execution exists)");
        sb.AppendLine($"db: {DbPath} (schema v{SchemaVersion}, data origin {Origin(DbOrigin)})   safety halt: {(Halted ? "ENGAGED - " + HaltReason : "off")}");
        sb.AppendLine($"watchlist: {(Watchlist.Count == 0 ? "(empty)" : string.Join(", ", Watchlist))}");
        sb.AppendLine();
        sb.AppendLine("claude billing boundary:");
        sb.AppendLine($"  verified: {BillingVerified}");
        sb.AppendLine($"  attested: {BillingAttested}");
        foreach (var u in BillingUnknown) sb.AppendLine($"  unknown:  {u}");
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
        sb.AppendLine("paper accounts (SIMULATED fills; fixture results are meaningless as performance):");
        foreach (var a in Accounts)
        {
            var st = a.State;
            sb.AppendLine($"  {st.Name} [{Origin(st.DataOrigin)}]: cash {Dec.Show(st.Cash)} reserved {Dec.Show(st.HeldReservations)} " +
                          $"capital at risk {Dec.Show(st.CapitalAtRisk)} equity {Dec.Show(a.Valuation.Equity)}{(a.Valuation.Certain ? "" : " (UNCERTAIN: " + string.Join("; ", a.Valuation.Problems) + ")")} " +
                          $"realized {Dec.Show(st.RealizedPnl)} | today opened {(a.OpeningEquityToday is { } o ? Dec.Show(o) : "-")} | ledger {(a.Problems.Count == 0 ? "OK" : "PROBLEMS: " + string.Join("; ", a.Problems))}");
            foreach (var p in st.Positions) sb.AppendLine($"    {p.Ticker} {p.Side} x{p.Quantity} cost {Dec.Show(p.CostBasis)}");
        }
        sb.AppendLine();
        sb.AppendLine($"decisions: {DecisionsWithCandidates} with candidates, {Abstentions} abstentions (recent):");
        foreach (var d in Decisions) sb.AppendLine($"  {d.CreatedAt} {d.Ticker} {d.Outcome} [{d.ModelId ?? "no model"}] {Trim(d.Rationale, 140)}");
        sb.AppendLine();
        sb.AppendLine("recent paper orders:");
        foreach (var o in Orders)
            sb.AppendLine($"  {o.CreatedAt} {o.Action} {o.Quantity} {o.Ticker} {o.Side} @ {Dec.Show(Dec.Parse(o.Limit))} IOC -> {o.Status} ({o.Filled} filled, arrival {o.ArrivalAt}) [{Origin(o.DataOrigin)}] {Trim(o.Reasons ?? "", 120)}");
        sb.AppendLine();
        sb.AppendLine("research records (EXPERIMENTAL; probabilities are for scoring only and never drive trades):");
        foreach (var r in Research)
            sb.AppendLine($"  #{r.Id} {r.ResearchId} {r.MarketTicker} {r.Disposition} p_yes={(r.ExperimentalProbability is { } p ? Dec.Show(p) : "-")} via {r.SourceKind} [{Origin(r.DataOrigin)}] valid until {Ts.ToText(r.ValidUntilUtc)}");
        foreach (var r in ResearchRuns)
            sb.AppendLine($"  run #{r.Id} {r.FinishedAt} {r.Outcome} auth={r.AuthMethod ?? "-"} cli={r.CliVersion ?? "-"} {Trim(r.Detail ?? "", 120)}");
        return sb.ToString();
    }

    public string ToHtml()
    {
        static string H(string? s) => WebUtility.HtmlEncode(s ?? "");
        string Badge(string? origin) => origin is not null && DataOrigin.IsSynthetic(origin)
            ? "<span class=\"b syn\">SYNTHETIC</span>" : $"<span class=\"b live\">{H(origin ?? "none")}</span>";

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
            code{font-size:12px}ul{margin:4px 0;padding-left:20px}
            </style></head><body>
            """);
        sb.Append($"<h1>Kalshi Bot</h1><p class=\"m\">Generated {H(Ts.ToText(GeneratedAt))} · mode <b>OBSERVE + PAPER</b> (no live execution exists) · schema v{SchemaVersion} · data {Badge(DbOrigin)}</p>");
        if (Halted) sb.Append($"<p><span class=\"b warn\">SAFETY HALT ENGAGED</span> {H(HaltReason)}</p>");
        sb.Append($"<p class=\"m\">Watchlist: {H(Watchlist.Count == 0 ? "(empty)" : string.Join(", ", Watchlist))}</p>");

        sb.Append("<h2>Claude billing boundary</h2><ul>");
        sb.Append($"<li><b>Verified:</b> {H(BillingVerified)}</li><li><b>Attested:</b> {H(BillingAttested)}</li>");
        foreach (var u in BillingUnknown) sb.Append($"<li><b>Unknown:</b> {H(u)}</li>");
        sb.Append("</ul>");

        sb.Append("<h2>Markets (latest snapshot)</h2><div class=\"wrap\"><table><tr><th>Ticker</th><th>Origin</th><th>Status</th><th>Yes bid/ask</th><th>No bid/ask</th><th>Age</th><th>Rules</th><th>Close</th></tr>");
        foreach (var m in Markets)
        {
            var s = m.Snapshot;
            sb.Append($"<tr><td><code>{H(s.Ticker)}</code><br>{H(s.Title)}</td><td>{Badge(s.DataOrigin)}</td><td>{H(s.Status)}</td><td>{P(s.YesBid)} / {P(s.YesAsk)}</td><td>{P(s.NoBid)} / {P(s.NoAsk)}</td><td>{Age(m.Age)}</td><td>v{m.RulesVersions}</td><td>{H(s.CloseTime is { } c ? Ts.ToText(c) : "-")}</td></tr>");
        }
        sb.Append("</table></div>");

        sb.Append("<h2>Paper accounts <span class=\"b warn\">SIMULATED</span></h2><div class=\"wrap\"><table><tr><th>Account</th><th>Cash</th><th>Reserved</th><th>Capital at risk</th><th>Equity</th><th>Realized</th><th>Opened today</th><th>Positions</th><th>Ledger</th></tr>");
        foreach (var a in Accounts)
        {
            var st = a.State;
            var pos = string.Join("<br>", st.Positions.Select(p => $"{H(p.Ticker)} {H(p.Side)} ×{p.Quantity} (cost {Dec.Show(p.CostBasis)})"));
            var eq = Dec.Show(a.Valuation.Equity) + (a.Valuation.Certain ? "" : $" <span class=\"b warn\">UNCERTAIN</span> {H(string.Join("; ", a.Valuation.Problems))}");
            sb.Append($"<tr><td>{H(st.Name)} {Badge(st.DataOrigin)}</td><td>{Dec.Show(st.Cash)}</td><td>{Dec.Show(st.HeldReservations)}</td><td>{Dec.Show(st.CapitalAtRisk)}</td><td>{eq}</td><td>{Dec.Show(st.RealizedPnl)}</td><td>{(a.OpeningEquityToday is { } o ? Dec.Show(o) : "-")}</td><td>{(pos.Length == 0 ? "-" : pos)}</td><td>{(a.Problems.Count == 0 ? "OK" : "<span class=\"b warn\">" + H(string.Join("; ", a.Problems)) + "</span>")}</td></tr>");
        }
        sb.Append("</table></div>");

        sb.Append($"<h2>Decisions</h2><p class=\"m\">{DecisionsWithCandidates} with candidates · {Abstentions} abstentions</p><div class=\"wrap\"><table><tr><th>Time</th><th>Market</th><th>Outcome</th><th>Model</th><th>Rationale</th></tr>");
        foreach (var d in Decisions)
            sb.Append($"<tr><td>{H(d.CreatedAt)}</td><td><code>{H(d.Ticker)}</code></td><td>{H(d.Outcome)}</td><td>{H(d.ModelId ?? "none")}</td><td>{H(d.Rationale)}</td></tr>");
        sb.Append("</table></div>");

        sb.Append("<h2>Paper orders</h2><div class=\"wrap\"><table><tr><th>Time</th><th>Order</th><th>Origin</th><th>Result</th><th>Why</th></tr>");
        foreach (var o in Orders)
            sb.Append($"<tr><td>{H(o.CreatedAt)}</td><td>{H(o.Action)} {o.Quantity} <code>{H(o.Ticker)}</code> {H(o.Side)} @ {H(Dec.Show(Dec.Parse(o.Limit)))} IOC</td><td>{Badge(o.DataOrigin)}</td><td>{H(o.Status)} ({o.Filled} filled)<br><span class=\"m\">arrival {H(o.ArrivalAt)}</span></td><td>{H(o.Reasons)}</td></tr>");
        sb.Append("</table></div>");

        sb.Append("<h2>Research <span class=\"b warn\">EXPERIMENTAL · SCORING ONLY</span></h2><div class=\"wrap\"><table><tr><th>#</th><th>Research id</th><th>Market</th><th>Disposition</th><th>Experimental P(yes)</th><th>Source</th><th>Origin</th><th>Valid until</th></tr>");
        foreach (var r in Research)
            sb.Append($"<tr><td>{r.Id}</td><td><code>{H(r.ResearchId)}</code></td><td><code>{H(r.MarketTicker)}</code></td><td>{H(r.Disposition)}</td><td>{(r.ExperimentalProbability is { } p ? Dec.Show(p) : "-")}</td><td>{H(r.SourceKind)}</td><td>{Badge(r.DataOrigin)}</td><td>{H(Ts.ToText(r.ValidUntilUtc))}</td></tr>");
        sb.Append("</table></div>");
        if (ResearchRuns.Count > 0)
        {
            sb.Append("<div class=\"wrap\"><table><tr><th>Claude run</th><th>Finished</th><th>Outcome</th><th>Auth</th><th>CLI</th><th>Detail</th></tr>");
            foreach (var r in ResearchRuns)
                sb.Append($"<tr><td>#{r.Id}</td><td>{H(r.FinishedAt)}</td><td>{H(r.Outcome)}</td><td>{H(r.AuthMethod ?? "-")}</td><td>{H(r.CliVersion ?? "-")}</td><td>{H(r.Detail)}</td></tr>");
            sb.Append("</table></div>");
        }

        sb.Append("<h2>Capture runs</h2><div class=\"wrap\"><table><tr><th>#</th><th>Started</th><th>Status</th><th>Origin</th><th>Detail</th></tr>");
        foreach (var r in Runs)
            sb.Append($"<tr><td>{r.Id}</td><td>{H(r.StartedAt)}</td><td>{H(r.Status)}</td><td>{Badge(r.DataOrigin)}</td><td>{H(r.Detail)}</td></tr>");
        sb.Append("</table></div></body></html>");
        return sb.ToString();
    }

    private static string Origin(string? o) => o is null ? "none yet" : DataOrigin.IsSynthetic(o) ? "SYNTHETIC" : o;
    private static string P(decimal? d) => d is null ? "-" : Dec.Show(d.Value);
    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n] + "…";
    private static string Age(TimeSpan t) => t.TotalMinutes < 1 ? $"{(int)t.TotalSeconds}s" : t.TotalHours < 1 ? $"{(int)t.TotalMinutes}m" : t.TotalDays < 1 ? $"{t.TotalHours:0.#}h" : $"{t.TotalDays:0.#}d";
}
