# Phase 3 plan: NYC daily high temperature (KXHIGHNY)

_Draft 2026-11-23. Not frozen. The protocol in §5 is frozen, and its hash recorded in `docs/PLAN.md`, before the
first prospective decision is scored._

Spec references: §5.1–5.4 (scope, strategy definition, EV), §8.2–8.5 (evidence, metrics, paper fills, promotion),
§11 Phase 3.

## 1. Hypothesis

**H1.** At a fixed decision time on the day before the event, a calibrated error distribution around a public
day-ahead forecast of the Central Park high gives bucket probabilities that:

- (a) score better than the same-time Kalshi mid, and
- (b) after fees and conservative fills, produce positive simulated P&L.

Prior: probably false. These markets are liquid enough to attract weather bots that use the same NWS and NBM data. A
`fail` or `insufficient_evidence` result is an acceptable outcome (spec §8.5). The plan is built to find that out
cheaply before months of prospective collection.

**Not in scope:** sentiment, news, LLM-adjusted probabilities (rule 4), gradient-boosted models before a simple model
has passed (§4, M2), multiple series, same-day trading, and passive orders.

## 2. Facts to verify before freezing

None of these could be checked from the cloud container: Kalshi, NWS, and IEM hosts are blocked there.

| # | Fact | How to verify | Why it matters |
|---|---|---|---|
| V1 | Settlement source and value. `docs/PLAN.md` says "Weather Company value for CLINYC". Is that the NWS Daily Climate Report (CLI) for Central Park, or something else? | Read `rules_primary` / `rules_secondary` from the recorded `market_rules` for KXHIGHNY-26NOV24-*. | The truth series must be the one Kalshi settles on. |
| V2 | Climate-day boundary. CLI uses local standard time, so during DST the day runs 01:00–00:59 EDT. | NWS CLI documentation plus V1 rule text. | Hourly-observation maxima disagree with CLI on some days. |
| V3 | Bucket encoding: `strike_type`, `floor_strike`, `cap_strike`, inclusive or exclusive edges, whole °F. | Recorded raw market bodies (not parsed yet; see W1). | Bucket probabilities need exact edges. |
| V4 | Market listing time and when liquidity starts on day D-1. | `open_time` in recorded markets, plus book depth across the recorder's 15-minute history. | Determines whether a D-1 decision time has any executable depth. |
| V5 | Historical forecast archive: does IEM serve NBM text (NBS/NBE) and/or GFS MOS for KNYC with issuance times, and from when? | `mesonet.agron.iastate.edu` MOS archive pages and API. | The model's input must exist both historically (to fit it) and live (to run it) at the same issue cycle. |
| V6 | Historical CLI maxima for Central Park. | IEM CLI archive or NOAA ACIS. | Truth series for fitting. |
| V7 | Historical Kalshi KXHIGHNY prices: candlesticks or trades for settled markets, and whether bid/ask is included. | Kalshi public market-data API (GET only). | Enables the retrospective market comparison (§5.2) and the cheap kill gate. |
| V8 | NWS live collector field names, from the first real run of `kb forecast collect`. | `kb forecast list` on the operator's machine. | Already flagged as unverified in `INTEGRATION_NOTES.md`. |

If V5 fails (no archived forecast with issue times), the model input changes to the NWS point forecast collected
prospectively. The retrospective stage (§5.2) then shrinks to the truth-only climatology baseline, and the timeline
grows by the months needed to collect training data. Record that decision; do not reconstruct forecasts.

## 3. Data

Each source gets its own database file (spec §8.1, one data origin per database). Archive data is not production data.

| Source | Use | Origin label | Times stored |
|---|---|---|---|
| IEM NBM/MOS for KNYC (V5) | Model input, historical and live | `archive:iem` (historical) / `production` (live) | issue time, valid date, retrieval time |
| NWS point forecast (`kb forecast collect`) | Secondary feature and baseline; prospective only | `production` | `update_time`, `generated_at`, `received_at` (exists) |
| CLI daily max (V1, V6) | Truth | `archive:iem` / `production` | report issue time, climate date, retrieval time |
| Kalshi market and book (`kb record`) | Market baseline, paper fills, settlement | `production` (exists) | observed time (exists) |
| Kalshi historical candles/trades (V7) | Retrospective market baseline only; never fills | `archive:kalshi` | period end, retrieval time |

Point-in-time rule: a decision at time *t* sees only rows with `received_at ≤ t` (prospective) or `issue_time ≤ t`
(retrospective, plus a stated publication lag). No revised or later vintages.

## 4. Models

All models are pure `IForecastModel` implementations with a frozen, hashed parameter file and a version id. Fitting is
an offline command that writes the parameter file. Floating point is allowed inside the fit; the model boundary
converts to `decimal` and validates the result (spec §5.4).

- **B0 Climatology.** Empirical distribution of the CLI max for ±15 days around the calendar date, 30 years. Baseline only.
- **B1 Naive forecast.** Normal(forecast, σ) with one global σ fitted on training years. Baseline only.
- **M1 `nyc-high-residual/1` (candidate).**
  - `T_max ~ forecast + bias(season, cycle) + ε`, with ε drawn from the empirical residuals for the same season
    window and issue cycle, kernel-smoothed.
  - Bucket probability = P(round(T) ∈ [floor, cap]) under the bucket edges from V3.
  - `Uncertainty` = the 90th-percentile absolute calibration error of the bucket probability, measured on the
    training walk-forward. It is reported, not hand-picked (spec §5.4).
  - About 3–6 parameters, fit on roughly 1,500+ daily observations. A model this small cannot overfit them.
- **M2 (later, only if M1 passes):** add features such as NBM spread, NWS-vs-NBM disagreement, or wind/cloud, using
  a tree model. M2 is a new version with its own frozen protocol. M1 results are not reused as M2's out-of-sample
  evidence (spec §8.2).

Market-implied baseline: for each event, the yes-mid of every bucket at decision time. Report both raw mids and mids
normalized to sum to 1, with the overround disclosed.

## 5. Evaluation protocol (to be frozen)

### 5.1 Fixed parameters

- **Unit of evidence:** the event-day. All buckets for one date make up one observation (spec §8.2, correlated strikes).
- **Primary decision time:** D-1 at 15:00 America/New_York, after the morning model cycle and the NWS afternoon
  update. Adjust once, using V4, if depth at that time is consistently empty; record the change. Other times are
  logged but not used for promotion.
- **Primary score:** Ranked Probability Score (RPS) across the ordered buckets, paired per day against the market
  baseline. Per-contract Brier is a secondary score, kept comparable with `research score`.
- **Uncertainty:** day-level block bootstrap with 10,000 resamples, a 95% interval on the mean paired RPS
  difference, and a fixed seed recorded in the report.
- **Strategy:** `EdgeThresholdStrategy` with `min_edge` chosen once from the retrospective stage and then frozen. One
  contract, IOC, held to settlement, **at most one position per event-day.** That keeps observations independent and
  stands in for an event-level correlation bucket.
- **Fill model:** the existing one (first observation at or after arrival, 2 s latency, 2 min evidence gap, used
  liquidity subtracted).
  - **Pessimistic sensitivity:** fill 1 tick worse and add 30 s latency.
  - **Optimistic sensitivity:** fill at the decision-time ask. Disclosed only, never used for promotion.

### 5.2 Stage A: retrospective (historical, walk-forward)

1. Fit on years < Y, test on year Y, rolling forward. Test years must not overlap any data used to pick features or
   the decision time.
2. Score M1 against B0 and B1 (RPS, calibration curve, sharpness).
3. If V7 holds, score M1 against the historical market mid at the matching time, on probability only. This is not
   P&L evidence: historical candles aren't executable depth.
4. **Kill gate A:** stop Phase 3 (outcome: `fail`, hypothesis rejected) if either
   - M1 does not beat B1 with the 95% CI of the paired RPS difference excluding zero, or
   - (when V7 is available) M1's mean RPS is worse than the historical market mid.
5. Choose `min_edge` here, with the procedure written down before looking: the smallest threshold whose historical
   trade count is at least 30 event-days. Freeze it.

### 5.3 Stage B: prospective paper (production observations)

1. Freeze M1's parameter hash, the strategy config hash, and this protocol's hash. Record all three in
   `docs/PLAN.md`.
2. Run the decision scan at the fixed time every day. Every abstention gets its reason recorded.
3. **Minimum sample:** 90 event-days with a market-baseline score (about 3–4 months). Interim looks check only data
   integrity and process health. They cannot produce a pass.
4. **At N = 90:**
   - **pass:**
     - the paired RPS improvement over the market has a 95% CI excluding zero, and
     - net simulated P&L after fees is above zero under the base fill model and at least zero under the
       pessimistic one, and
     - no single event-day contributes more than 25% of gross P&L.
   - **fail:** paired RPS is not better than the market (point estimate ≥ 0), or net simulated P&L is ≤ 0 under the
     base fill model.
   - **insufficient_evidence:** anything else. Either extend to N = 180 once, under the same frozen protocol, or
     stop.
5. A pass makes Phase 3 *eligible for consideration*. It authorizes nothing (rule 10). Phase 4 and any live pilot
   still need separate operator decisions.

### 5.4 Reported every time (spec §8.3)

- Coverage, abstentions by reason, and sample size in contracts and event-days.
- Calibration and RPS/Brier against B0, B1, and the market.
- Net simulated P&L and fill rate under all three fill scenarios.
- Spread and depth at decision time, drawdown, and P&L concentration.
- Incremental cost, expected $0 (rule 2), plus a fully allocated subscription share if one is chosen.

## 6. Build items (ordered)

Each item follows the engineering invariants: append-only history, `decimal` storage, injected `TimeProvider`, pure
models, and a test that fails without the change.

| # | Item | Notes |
|---|---|---|
| W1 | Parse `strike_type` / `floor_strike` / `cap_strike` into the market store via a new migration, read from stored raw bodies | V3. Test against a recorded KXHIGHNY body. |
| W2 | Truth collector: CLI daily max for Central Park, with report issue time and climate date. Reconcile against Kalshi `result` at settlement. | V1/V2. A mismatch blocks scoring for that day and is reported, never silently patched. |
| W3 | Archive importers for IEM NBM/MOS and CLI history, into a separate `archive:iem` database | V5/V6. Raw bodies plus hashes, like the recorder. |
| W4 | Evidence-aware forecast interface: change `IForecastModel.Forecast(MarketSnapshot)` to also take a point-in-time evidence view (forecast vintages as of decision time) | Test: a vintage received after the decision time must not change the forecast. |
| W5 | `kb model fit nyc-high-residual` writes the frozen parameter JSON and prints its hash; `ResidualForecastModel` loads it | Deterministic: the same archive gives byte-identical parameters. |
| W6 | `kb eval retro` runs Stage A: walk-forward, baselines, bootstrap, kill gate A verdict | Seed and inputs recorded, so the report reproduces. |
| W7 | Scheduled decision scan at the fixed time, using `kb paper run` with the frozen model and config | Extends `record-scheduled.cmd`. |
| W8 | `kb eval prospective` runs Stage B: the metrics in §5.4 and the pass/fail/insufficient verdict at N | Refuses a verdict before N. |
| W9 | `docs/PHASE3_PROTOCOL.md`: the frozen copy of §5, with its hash in `docs/PLAN.md` | Done before the first Stage B decision. |

W1, W2 and W4 are useful whatever V5 shows. W3, W5 and W6 depend on V5.

## 7. Timeline

| Step | Gate | Calendar time |
|---|---|---|
| Verify V1–V8 on the operator's machine | All answered or explicitly replaced | Days |
| W1–W6 and Stage A | Kill gate A | 1–2 weeks of build. The historical data already exists. |
| Freeze, then Stage B | N = 90 event-days | About 3–4 months of observation. This cannot be compressed (spec §11). |
| Verdict | pass / fail / insufficient_evidence | — |

Stage A is the cheap test. If M1 can't beat the historical market mid on probability alone, the prospective months
are not worth spending.

## 8. Open decisions (operator)

- ~~Whether a 3–4 month observation period is acceptable.~~ **Decided 2026-11-23: yes.** Stage B runs if kill gate A passes.
- Optional: allow `mesonet.agron.iastate.edu`, `api.weather.gov`, and the Kalshi market-data host in the cloud
  environment, so V5–V8 can be checked and W3 built against live responses from there instead of hand-written
  fixtures.
