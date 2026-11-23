# CLAUDE.md

Personal Kalshi research and paper-trading system. .NET 10 LTS, SQLite, one CLI. Read `STATE.md` first for
where things stand, `docs/PLAN.md` for scope, `docs/INTEGRATION_NOTES.md` before touching Kalshi or Claude code.

## Hard rules

- **Claude usage is subscription-only.** Never add the Anthropic API, an Anthropic/OpenAI/OpenRouter SDK, `ANTHROPIC_API_KEY`
  handling, `--bare`, `--settings`, or any other paid model path. The only model path is `ClaudeResearchRunner` → official
  `claude` CLI, gated by `BillingGuard`. `NoDirectModelApiTests` enforces part of this; do not weaken it.
- **No live execution.** There is no order-placing Kalshi code. `RiskEngine` refuses `ExecutionMode.Live`. Adding
  authenticated Kalshi calls, order placement, or live limits requires a separately authorized phase.
- **Do not raise risk.** `RiskLimits.PaperDefaults` are paper-only. There is no live preset; do not add one.
- **Labels are structural.** Synthetic data carries `data_origin = fixture:synthetic`; fills are `simulated = 1`;
  research results are `validation_status = 'unvalidated'` (a schema CHECK). Never present any of these as real,
  observed, or validated. Never invent a probability to make a trade happen.
- **History is append-only.** `raw_responses`, `ledger_entries`, `paper_fills` have no-update/no-delete triggers.
  Schema changes are new migrations appended in `Schema.cs`; never edit a shipped migration.
- No secrets in the repo, chat, logs, or the database. Phase 1–2 needs no Kalshi credentials.

## Commands

```bash
dotnet build                       # warnings are errors
dotnet test                        # xUnit v3 on Microsoft.Testing.Platform (see global.json)
./kb help                          # CLI (bash wrapper); or: dotnet run --project src/KalshiBot.Cli -- help
./kb demo                          # offline end-to-end on synthetic fixtures (writes data/demo.db)
```

## Layout

```
src/KalshiBot.Core/
  Common/      Ts (UTC text timestamps), Dec (decimal money), Hash, CanonicalJson, DataOrigin
  Storage/     Db (thin SQLite wrapper, InTransaction), Schema (migrations)
  Kalshi/      KalshiPublicClient (GET-only), FixtureHandler, KalshiParse (payload -> records)
  Recording/   Recorder (capture runs), MarketStore (read side), Watchlist
  Trading/     IStrategy + EdgeThresholdStrategy, RiskEngine, FeeModel, PaperBroker, PaperLedger, PaperEngine, Controls
  Research/    PacketExporter, ResultImporter (strict), BillingGuard, ClaudeResearchRunner, ProcessRunner
  Status/      StatusReport (text + HTML)
src/KalshiBot.Cli/   Commands, Demo, StatusServer (loopback, read-only)
tests/KalshiBot.Tests/
fixtures/synthetic/  hand-written Kalshi-shaped JSON, clearly synthetic
```

## Conventions

- Money, prices, and counts are `decimal`, stored as invariant TEXT. Never `double`.
- Time comes from an injected `TimeProvider`; persisted as `Ts.ToText` (UTC, 7 fractional digits, `Z`).
- Strategies are pure: no clock, I/O, or randomness. Decision ids hash strategy id + parameters + snapshot + estimate.
- Prefer explicit SQL over abstractions. Keep the architecture small; no new services or frameworks without a reason
  recorded in `docs/PLAN.md`.
- Every behavior change touching billing, research import, risk, accounting, or recovery needs a test that fails without it.
- Update `STATE.md` at each milestone: what works, how verified, limitations, next action.
