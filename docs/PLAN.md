# Plan

Requirements come from `kalshi-claude-subscription-spec.md`, v2.0 of 2026-11-22. This file records decisions and how
the build maps to the spec.

## Build vs. reuse (spec §10)

| Concern | Decision | Evidence / reason |
|---|---|---|
| Overall | **Build** a small C# core (the spec's default recommendation). | .NET 10 LTS is the operator's stack. None of the screened projects is .NET. |
| Kalshi market data | **Build** a thin GET-only client and store raw bodies verbatim. | Kalshi's official SDKs (`kalshi-python-sync` and `kalshi-typescript` 3.36.0) are Python/TypeScript and OpenAPI-generated. Field names and hosts were checked against them. Market endpoints need no auth. |
| Kalshi execution (Phase 4) | **Build** against the then-current contract. | SDK 3.36.0 confirms `/portfolio/events/orders` and the signing scheme (see INTEGRATION_NOTES). A pinned contract snapshot comes before demo work. |
| Claude access | **Reuse** the official `claude` CLI only. | The only subscription-backed programmatic path (spec §2.1, S1–S3). |
| Storage | **Reuse** `Microsoft.Data.Sqlite` 10.0.15 with explicit SQL. | Append-only triggers, CHECKs, and `BEGIN IMMEDIATE` reservations live in the schema. |
| Research schema validation | **Build** a small JSON-Schema-subset validator. | Validates against the exact `schemas/` file and fails closed on unknown keywords. Avoids a dependency whose licensing was not verified. |
| `ryanfrigo/kalshi-ai-trading-bot` | Ideas only; no code. | Default path is OpenRouter (BILL-02 conflict). Its owner-published track record shows no measured edge (S17). |
| `clayve-one/kalshi` | Architectural reference only. | The backtest/paper/demo/live interface idea matches our mode boundary. Python, license unreviewed. |
| `apeabody007/Quant-toolkit` | Possible research reference for Phase 3 scoring. | Not a bot or a model. |
| `Skylcodes/krypt-trader`, `cryptuon/polybot` | Not used. | Out of scope; trade-capable tooling is not attached to research. |

The repository screening is the spec's (§10). It was not re-fetched here, because this session's GitHub access is
scoped to this repository. No third-party code was copied, so the §10 per-copy documentation duty has not arisen.

## Phases (spec §11) and status

| Phase | Status |
|---|---|
| 0 Project and integration facts | Done: docs, verified facts, reuse decision. |
| 1 Recorder and offline paper slice | Done. Replay: `kb replay`. Live capture is blocked here by network policy. |
| 2 Claude research workflow | Done. Manual round trip, plus an automated `kb research run` IMPORTED on the operator's Windows machine (2026-11-23). |
| 3 One candidate strategy and prospective evaluation | Started. Category: NYC daily high temperature (KXHIGHNY). Done so far: series watching, scheduled recording, research scoring. |
| 4 Demo execution and recovery | Not started. Separately authorized: demo credentials, the exchange lifecycle, AC-07/08/18/19. |
| 5 Live pilot | Not authorized. |
| 6 Optional improvements | Not started. |

## Acceptance-test traceability (spec §9)

| AC | Status | Where |
|---|---|---|
| 01 No Claude or LLM credentials needed | ✅ | The replay and every test run with no `claude` and no keys |
| 02 Metered credential or unsupported auth refused | ✅ | `BillingGuardTests`, `ReviewRegressionTests`, `ClaudeRunnerTests` |
| 03 Quota, expiry, or changed CLI output suspends without fallback | ✅ | `Quota_exhaustion_or_errors_suspend_without_retry_or_fallback`; unknown version or auth output is refused |
| 04 Unsafe research fields rejected | ✅ | `ResearchImportTests` |
| 05 Research probability not authoritative | ✅ | `ResearchBoundaryTests` (source scan plus a behavioral test) |
| 06 Shared cap under concurrency | ✅ | `Concurrent_signals_cannot_both_spend_the_same_capacity` |
| 07 Partial fill and cancel race | ◐ | Paper partial fills reconcile with reservations. The cancel race needs the exchange (Phase 4). |
| 08 Lost submission response | ⏳ | Phase 4 |
| 09 Restart mid-reservation or mid-fill | ✅ (paper) | `RecoveryTests` crash points; exchange reconciliation is Phase 4 |
| 10 Invalid inputs give specific rejections | ✅ | `Invalid_required_inputs_are_rejected_with_specific_reasons` |
| 11 Halt persists across restart and day change | ✅ | `Daily_loss_trigger_persists_a_halt_that_survives_restart_and_midnight` |
| 12 YES/NO grid and complement economics | ✅ | `Side_complement_grid_and_maximum_loss_are_economically_correct` |
| 13 Settlement, fees, duplicates | ✅ | `LedgerTests`. Paper has a single deposit; withdrawals are not modeled. |
| 14 Deterministic replay | ✅ | `Frozen_fixture_replay_is_exactly_reproducible` |
| 15 No write path in paper | ✅ | `NoExchangeWritePathTests` (no write code exists) |
| 16 OS-enforced research/executor separation | ⏳ | Phase 5 deployment |
| 17 Fresh inputs required | ✅ | Forecast-age and snapshot-age checks; the strategy does not depend on Claude |
| 18 External account activity | ⏳ | Phase 4 |
| 19 One executor per account | ◐ | `ProcessLock` per paper account and for research. A fixed-location exchange-account lock is Phase 4. |
| 20 No re-entry of the same signal | ✅ | `The_same_signal_is_not_entered_again_across_scans_and_restarts` |
| 21 Halt between reservation and dispatch | ✅ (paper) | `A_halt_set_after_one_decision_blocks_the_next_dispatch_but_not_resolution` |
| 22 Vanished quote before arrival | ✅ | `A_quote_that_disappears_before_arrival_does_not_fill` |

## Decisions log

- **2026-11-23 Research probabilities.** Claude's probabilities are not strategy input, even in paper (spec §4.5, AC-05).
  Real data has no validated model, so paper on real data abstains. A labeled synthetic fixture model drives fixture runs.
- **2026-11-23 Paper fills.** Fills come only from the first observation at or after simulated arrival: 2 s latency,
  2 min maximum evidence gap, IOC, and liquidity already used is subtracted (spec §8.4).
- **2026-11-23 Fees.** Unknown fees block risk. Only a labeled synthetic fixture schedule exists until the operator
  verifies Kalshi's schedule (DATA-01).
- **2026-11-23 Migration 3.** It renames the pre-spec paper and research tables to `legacy_*_v1`, keeping their data,
  rather than editing shipped migrations.
- **2026-11-23 Research bounds.** Claude Code 2.1.330 has no max-turns flag, so research runs are bounded by a 5-minute
  timeout and web-only tools. Revisit after CLI upgrades.
- **2026-11-23 Phase 3 category: NYC daily high temperature (KXHIGHNY).** Operator deferred the choice. Reasons:
  - Daily events with one clear settlement source (the Weather Company value for CLINYC).
  - Public forecast data with issue times (NWS) and historical forecast archives for measuring error.
  - Caveat: the strikes within one day are correlated, so each day is roughly one independent observation.
  - Not a claim of edge.
- **2026-11-23 Fees verified** from the operator-provided Kalshi Fee Schedule PDF, effective 2026-07-07 (INTEGRATION_NOTES).
  Real-data candidates are no longer blocked for fees. With no validated model, they still abstain.
