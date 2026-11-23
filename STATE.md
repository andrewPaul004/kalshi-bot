# State

_Last updated 2026-11-23. Milestone: Phases 0–2 aligned with spec v2.0 (`kalshi-claude-subscription-spec.md`)._

## Mode

OBSERVE + PAPER only.
- No Kalshi credentials, no order-placing code, no live limits.
- Claude runs only through the guarded subscription CLI path.
- Live bankroll, limits, market category, and hosting: **not selected**.

## What works

| Capability | Command | Verified by |
|---|---|---|
| Recorder: market, book, event, series, and rules, with raw bodies, hashes, and timestamps. One data origin per database. | `kb record` | `RecorderTests`; CLI smoke |
| Frozen replay: candidate, decision, reservation, post-arrival fill, settlement. Deterministic fingerprint. | `kb replay` | `Frozen_fixture_replay_is_exactly_reproducible` |
| Deterministic forecast model interface. Real data abstains; a labeled SYNTHETIC fixture model is used for fixtures. | `kb paper run T` | `ResearchBoundaryTests`, `StrategyTests` |
| Risk: spec §6.2 paper fixture limits, worst-case reservations under `BEGIN IMMEDIATE`, specific rejections | `kb paper run T` | `RiskEngineTests`; concurrency test (AC-06) |
| Paper broker: IOC fills only from observations at or after arrival; used liquidity is not reused; missing evidence means no fill | `kb paper resolve` | AC-22 and expiry tests |
| No re-entry of the same economic signal across scans and restarts | `kb paper run T` | AC-20 test (plus a unique-index backstop) |
| Daily loss trigger (America/Chicago day). The halt persists across restart and midnight. | `kb halt` / `kb resume` | Daily-loss test; DST test |
| Append-only ledger and settlement, plus `verify`, which rebuilds the ledger from fills, orders, reservations, and settlements | `kb paper settle` / `kb paper verify` | `LedgerTests` and tamper tests |
| Research packets v2 (`packetId` + `packetHash`, no prices) and strict import of research-record v2 against `schemas/research-record.schema.json` | `kb research export` / `kb research import` | `ResearchExportTests`, `ResearchImportTests` |
| Experimental probabilities kept in a separate table that trading code cannot reference | — | Source-scan and behavioral tests (AC-05) |
| Claude runner: attestation, lock, allowlisted env, settings inspection, CLI version, auth status, 5-minute bound, skip unchanged packets, packet linkage | `kb research attest-billing` / `kb research run` | `ClaudeRunnerTests`; the real guard refused correctly here |
| Status: billing verified vs. attested vs. unknown, reservations, valuation certainty, abstentions | `kb status`, `kb serve` | CLI smoke |

133 tests pass (`dotnet test`, about 4 s). Breaking any of the new guards turns the suite red. Each was mutated and
caught: the arrival rule, signal dedupe, reservations in capacity, liquidity reuse, unverified fees, attestation,
one origin per database, the daily-loss trigger, and the fixture model on live data.

Spec traceability, AC-01 to AC-22: `docs/PLAN.md`.

**Spec alignment changes in this milestone** (behavior that differed from the spec before):
- Claude's probabilities no longer feed the strategy, even in paper.
- Fills no longer come from the decision snapshot.
- Real-data fees no longer use an unverified formula.
- Re-scans can no longer re-enter the same signal.
- Risk limits now follow §6.2.
- The research contract now follows §4.5.
- The pre-spec tables were retired by migration 3 (`legacy_*_v1`, data kept).

## Blockers (exact)

1. **No live Kalshi data here.** The egress proxy returns 403 for `external-api.kalshi.com`, `api.elections.kalshi.com`,
   and the demo hosts (verified: capture run `failed`, proxy tunnel 403).
2. **The Claude round trip was not run.** In this cloud container, `claude auth status` reports `oauth_token` without
   `CLAUDE_CODE_OAUTH_TOKEN`. The guard cannot prove subscription billing and refuses, by design and audited.
   No model call was made.
3. **Kalshi fee schedule not verified** (kalshi.com unreachable). Real-data candidates are rejected with "fees unknown".

## Known limitations

- There is no validated forecast model, so paper on real data observes and abstains. That is Phase 3 work, by design.
- Paper fill model: IOC only, 2 s latency, 2 min evidence gap. No passive orders, queue model, or market impact.
- Whole contracts and binary markets only. One reference strategy.
- The billing guard cannot inspect MDM or server-managed Claude settings, and cannot verify the usage-credit setting.
  The latter is operator-attested.
- Not yet done: AC-07 (cancel races), 08, and 18 (exchange) are Phase 4; AC-16 (OS isolation) is Phase 5; the
  fixed-location exchange-account lock for AC-19 is Phase 4; the drawdown high-water mark comes before any live pilot.

## Next concrete actions

1. **On your machine:** a manual subscription round trip, run in a separate database for production observations.
   ```bash
   claude auth status                       # expect "authMethod": "claude.ai" and a subscriptionType
   # In claude.ai settings, confirm usage credits / extra usage are OFF, then:
   ./kb research attest-billing --usage-credits-disabled
   ./kb discover --series <SERIES>          # pick 1–3 markets in one candidate category
   ./kb watch add <TICKER> && ./kb record
   ./kb research export <TICKER>
   ./kb research run <PACKET_ID>            # or paste data/research/<id>.prompt.md into Claude, then: kb research import FILE
   ./kb status --html data/status.html
   ```
2. **Verify Kalshi's fee schedule** and add it to `FeeSchedules.Verified`, with source, verification date, and effective date.
3. **Phase 3:** choose one category (the spec suggests weather as a candidate; the data checks come first), freeze an
   evaluation protocol, and start scheduled recording.
