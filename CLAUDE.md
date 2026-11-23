# CLAUDE.md

Personal Kalshi research and paper-trading system: .NET 10 LTS, SQLite, one CLI.
Requirements: `kalshi-claude-subscription-spec.md` (the spec). Read it and `STATE.md` before continuing work.
Record important decisions in `docs/PLAN.md` and the next concrete action in `STATE.md`.
Read `docs/INTEGRATION_NOTES.md` before touching any Kalshi or Claude integration code.

## Rules (spec §12)

1. Read the spec and `STATE.md` before continuing work. Record important decisions and the next concrete action.
2. **No separately billed model APIs, usage credits, or alternate paid inference providers.** Kalshi's APIs and approved
   public data APIs are permitted.
   - The only model path is `ClaudeResearchRunner` → the official `claude` CLI, gated by `BillingGuard` and the owner's
     usage-credit attestation.
   - Never add `--bare`, `--settings`, API-key handling, or an Anthropic/OpenAI/OpenRouter SDK.
3. **Default to paper.** Production order writes and funding are not authorized. No order-placing or authenticated Kalshi
   code exists, and `RiskEngine` refuses `ExecutionMode.Live`.
4. **Claude research is untrusted input.**
   - Research records are evidence. Their experimental probabilities are stored for scoring only.
   - Trading code never references research storage; `ResearchBoundaryTests` enforces this.
   - Decisions come from deterministic, versioned forecast models and strategies.
5. Use current official API contracts and keep source references. Do not blindly run third-party setup scripts,
   plugins, or live tests.
6. Never read, request in chat, commit, print, or pass production secrets to model tools. Keep research and execution
   privileges separate.
7. Continue authorized local research, implementation, fixes, and meaningful tests without asking at every small step.
8. When access, billing, or live authority is missing, stop only the blocked part. Keep doing mocks, fixtures, docs, and
   paper work.
9. Show actual results and limitations.
   - Label synthetic data (`data_origin = fixture:synthetic`), simulated fills (`simulated = 1`), placeholders, and
     unvalidated probabilities (`validation_status = 'unvalidated'`, enforced by a schema CHECK).
   - Never claim an edge because the software is complete.
10. Do not auto-promote strategies, expand risk, deploy to a paid service, or enable live trading.
    `RiskLimits.PaperFixture` holds the spec §6.2 paper values. There is no live preset.

## Engineering invariants

- **Append-only history:** `raw_responses`, `ledger_entries`, `paper_fills`, and `billing_attestations` have
  no-update and no-delete triggers, with `recursive_triggers` ON. Schema changes are new migrations appended in
  `Schema.cs`; never edit a shipped migration.
- **One data origin per database** (spec §8.1). Fixture, demo, and production data never share a file.
- Money, prices, and counts are `decimal`, stored as invariant TEXT.
- Time comes from an injected `TimeProvider` and is persisted as `Ts.ToText` (UTC). Reporting days use America/Chicago.
- Strategies and forecast models are pure: no clock, I/O, or randomness.
- Paper fills come only from observations at or after simulated arrival, never from the decision snapshot.
- Write transactions are `BEGIN IMMEDIATE`; risk checks and reservations commit together.
- Any change to billing, research import, risk, accounting, or recovery needs a test that fails without it.

## Commands

```bash
dotnet build && dotnet test        # warnings are errors; xUnit v3 on Microsoft.Testing.Platform
./kb help                          # CLI: bash wrapper on macOS/Linux; on Windows use .\kb.cmd help
./kb replay                        # frozen SYNTHETIC fixture replay (deterministic)
```

## Layout

```
src/KalshiBot.Core/
  Common/     Ts, Dec, Hash, CanonicalJson, DataOrigin, ReportingDay, ProcessLock, ManualClock
  Storage/    Db (thin SQLite wrapper), Schema (append-only migrations)
  Kalshi/     KalshiPublicClient (GET-only), FixtureHandler, KalshiParse
  Recording/  Recorder (+ DatabaseOrigin), MarketStore, Watchlist
  Trading/    IForecastModel, IStrategy/EdgeThresholdStrategy, RiskEngine, FeeSchedules, PaperBroker, PaperLedger, PaperEngine
  Research/   PacketExporter, ResearchImporter (schema-validated), BillingGuard, ClaudeResearchRunner, JsonSchemaSubset
  Truth/      SettlementRegime, TruthParse, KalshiTruthCollector, IemCliCollector, TruthStore (own database, data/truth.db)
  Archive/    MosCsv, CandleParse, DecisionClock, IemMosCollector, KalshiCandleCollector, ArchiveStore (own database, data/archive.db)
  Status/     StatusReport, FixtureReplay
schemas/research-record.schema.json   research contract (spec §4.5), embedded in Core
fixtures/synthetic/                   hand-written, clearly synthetic Kalshi-shaped JSON and fixture model
```
