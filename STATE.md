# State

_Last updated 2026-11-23, Phases 1–2 milestone._

## Mode

OBSERVE + PAPER only. No Kalshi credentials, no order-placing code, no live limits. Claude access goes only through the
subscription-guarded CLI path. Live bankroll and limits: **not selected**.

## What works

| Capability | Command | Verified by |
|---|---|---|
| Record markets, order books, events, series, and rules with raw bodies, hashes, URLs, and timestamps | `kb record` | `RecorderTests` (8), CLI smoke on fixtures |
| Rules versioning (a new row only when the text changes) | `kb rules T` | `Rules_get_a_new_version_only_when_text_changes` |
| Crash-safe capture (abandoned runs marked; partial runs keep the error body) | `kb record` | `RecoveryTests`, `RecorderTests` |
| Deterministic strategy + fail-closed risk engine | `kb paper run T` | `RiskEngineTests`, `StrategyTests` (13) |
| IOC paper broker + append-only ledger + settlement + ledger verify | `kb paper run/settle/verify` | `FeeAndFillTests`, `LedgerTests` (10) |
| Idempotent, crash-safe decision cycle | `kb paper run T` (rerun = no-op) | `Crash_inside_an_order_rolls_back_completely_and_rerun_fills_exactly_once` |
| Research packet export (deterministic, quote-free, source refs) | `kb research export T` | `ResearchExportTests` |
| Strict result import | `kb research import F` | `ResearchImportTests` (11) |
| Subscription-only Claude runner with audit trail | `kb research run PACKET` | `BillingGuardTests`, `ClaudeRunnerTests`; real guard refused correctly in this container |
| Kill switch | `kb halt REASON` / `kb resume` | `Kill_switch_rejects_orders_and_leaves_the_ledger_untouched` |
| Status text, HTML file, and loopback server | `kb status [--html F]`, `kb serve` | CLI smoke (HTTP 200, labels present) |
| Offline end-to-end demo | `kb demo` | Run: buy 1 YES @ 0.45 (fee 0.02), settles yes, cash 100.53, ledger OK |

90 tests pass (`dotnet test`, about 2 s). A mutation check confirmed six guard edits each turn the suite red: env scrub,
api_key auth accepted, live mode allowed, idempotency removed, unknown keys allowed, ledger oversell.

## Blockers (exact)

1. **Spec unreadable.** The ChatGPT share link needs `chatgpt.com`, which this environment's network policy denies (DNS
   ENOTFOUND), and the link is account-scoped. Phases are derived from the kickoff request. Action: put the spec at `docs/SPEC.md`.
2. **No live Kalshi data here.** The egress proxy returns 403 for `external-api.kalshi.com`, `api.elections.kalshi.com`, and
   the demo hosts. Verified: `kb record` gives capture run `failed`, proxy tunnel 403. Action: run locally, or allow these
   hosts in the environment's network settings.
3. **Claude round trip not run.** In this cloud container `claude auth status` reports `oauth_token` without
   `CLAUDE_CODE_OAUTH_TOKEN`, so the guard cannot prove subscription billing and refuses (by design; audited as `refused`).
   No model call was made. Action: run on your machine (below).

## Known limitations

- Fee model unverified (fee schedule PDF unreachable). It errs high. Rate limits unverified (conservative pacing).
- Paper fills are IOC against one recorded snapshot: no resting orders, queue position, latency, or market impact.
- Whole contracts only; binary markets only; one reference strategy, which is plumbing, not an edge.
- A rejected decision (for example, under the kill switch) is final for that snapshot. Record again to re-decide.
- Single-writer assumption: don't run two recorders against one database at the same time.
- The research JSON schema passed to `--json-schema` avoids range and length keywords whose CLI support was unverifiable.
  The importer enforces them instead.

## Next concrete actions

1. On your machine (needs .NET 10 SDK and the `claude` CLI logged in with your subscription):
   ```bash
   unset ANTHROPIC_API_KEY ANTHROPIC_AUTH_TOKEN ANTHROPIC_BASE_URL   # the guard strips these anyway
   claude auth status                                                # expect "authMethod": "claude.ai" and a subscriptionType
   ./kb discover --series <SERIES>          # pick 1–3 real markets
   ./kb watch add <TICKER> && ./kb record
   ./kb research export <TICKER>
   ./kb research run <PACKET_ID>            # manual subscription round trip; or paste data/research/<id>.prompt.md into Claude and `kb research import`
   ./kb paper init && ./kb record && ./kb paper run <TICKER>
   ./kb status --html data/status.html
   ```
2. Provide the spec as `docs/SPEC.md`, so Phase 3 (scheduled recording + calibration scoring) can be reconciled against it.
3. Verify Kalshi's current fee schedule and replace the conservative `FeeModel` constant.
