# State

_Last updated 2026-11-23. Milestone: Phases 0–2 complete on the operator's machine; Phase 3 started (KXHIGHNY)._

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

**Verified on the operator's Windows machine (2026-11-23):**
- A production capture of KXHIGHNY-26NOV24-T56.
- A research packet exported, and a manual research record imported.
- `kb research run`: IMPORTED, with auth claude.ai on a Team plan (record `insufficient_evidence`).

**Added since:**
- A verified Kalshi fee schedule (reproduces Kalshi's fee table).
- Series watching (`watch add-series`).
- A recorder lock, so scheduled and manual runs can't collide.
- `record-scheduled.cmd` for Windows Task Scheduler.
- `research score` (Brier score against outcome and against the same-time market mid). 242 tests.

## Blockers (exact)

1. **No live Kalshi data here.** The egress proxy returns 403 for `external-api.kalshi.com`, `api.elections.kalshi.com`,
   and the demo hosts (verified: capture run `failed`, proxy tunnel 403).
2. **The Claude round trip was not run.** In this cloud container, `claude auth status` reports `oauth_token` without
   `CLAUDE_CODE_OAUTH_TOKEN`. The guard cannot prove subscription billing and refuses, by design and audited.
   No model call was made.
3. **From this cloud environment only:** Kalshi, NWS, and archive hosts are unreachable, so live data and forecast
   collection run on the operator's machine until those hosts are allowed.

## Known limitations

- There is no validated forecast model, so paper on real data observes and abstains. That is Phase 3 work, by design.
- Paper fill model: IOC only, 2 s latency, 2 min evidence gap. No passive orders, queue model, or market impact.
- Whole contracts and binary markets only. One reference strategy.
- The billing guard cannot inspect MDM or server-managed Claude settings, and cannot verify the usage-credit setting.
  The latter is operator-attested.
- Not yet done: AC-07 (cancel races), 08, and 18 (exchange) are Phase 4; AC-16 (OS isolation) is Phase 5; the
  fixed-location exchange-account lock for AC-19 is Phase 4; the drawdown high-water mark comes before any live pilot.

## Next concrete actions

1. **Operator (Windows, in the kalshi-bot folder):**
   ```powershell
   git pull
   .\kb.cmd watch add-series KXHIGHNY
   .\kb.cmd record
   schtasks /Create /TN "kalshi-bot record" /SC MINUTE /MO 15 /TR "C:\Users\AndrewPaul\source\repos\Personal\Kalshi-Trader\kalshi-bot\record-scheduled.cmd" /F
   ```
   After Nov 24's market settles (about 8 AM ET Nov 25), run `.\kb.cmd research score` to score the first two records.
2. **NWS forecast collector: built** (`kb forecast collect` / `kb forecast list`; also hourly from `record-scheduled.cmd`).
   It stores Central Park point-forecast vintages with NWS publication time and our retrieval time kept separate.
   Field names follow the NWS API docs but were unverified against live responses until the first run on the operator's machine.
3. **Next build:** follow `docs/PHASE3_PLAN.md`. V1–V8 were verified on 2026-11-23 (§2a there). Settlement is the Weather
   Company value, which matches the NWS CLI high on 99.9% of 1,861 days. The model input is IEM GFS MOS, because IEM's NBM
   feed stopped on 2026-05-04. **W1 (strike parsing, migration 6) and W2 (truth data, migration 7) are done.** `kb truth
   report` on the operator's machine: 1,863 Kalshi settled days, 1,860 of 1,861 agree with the NWS high, 72 days
   excluded, 6,096-day scoring series (data/truth.db). **W3 (migration 8, `kb archive`) is done**: GFS MOS and Kalshi candles (9,568 windows, 119,014 hourly candles) are fully collected; `.\kb.cmd archive report` shows coverage. Then W4 (point-in-time evidence interface), W5 (model fit), W6 (Stage A).
4. Optional: allow `external-api.kalshi.com`, `api.weather.gov`, and `mesonet.agron.iastate.edu` in the cloud
   environment's network settings, so collectors can be verified against live data from here.
