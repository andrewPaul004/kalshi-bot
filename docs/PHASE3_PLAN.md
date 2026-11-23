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

### 2a. Verification results (2026-11-23, operator's Windows machine)

Raw probes were read-only GETs, kept outside the repo. Settled-market counts below come from Kalshi's live and
`/historical` partitions (cutoff 2026-09-24).

| # | Result | Consequence |
|---|---|---|
| V1 | **Settles on The Weather Company value** for "New York City (CLINYC)", per `rules_primary`, `rules_secondary` and the series' `settlement_sources` (weather.com/kalshi). **Not** the NWS CLI report. Kalshi publishes the settled value as `expiration_value` on every finalized market. | The authoritative truth is Kalshi's `expiration_value`. W2 changes (§6). |
| V1/V2 cross-check | Across **1,861 days** with both values, Kalshi's settled value equals the NWS CLI high for Central Park on **1,860 (99.9%)**. One day differs: 2025-06-18, Kalshi 84 vs CLI 83. | CLI is a validated truth proxy for **pre-Kalshi years** (IEM CLI from 2010 or earlier), with a known ~0.1% disagreement. The CLI day convention matches Kalshi's in practice, so V2 is settled empirically. |
| V3 | Whole °F. Six markets per event. `between`: `floor_strike`..`cap_strike` **inclusive**, 2°F wide (`B49.5` = 49–50). `less`: `cap_strike=49` means **≤ 48** (yes_sub_title "48° or below"). `greater`: `floor_strike=56` means **≥ 57** ("57° or above"). Pre-2022 markets have no `strike_type`. | W1 must parse `strike_type`/`floor_strike`/`cap_strike`, and fall back to ticker and rules text for pre-2022 markets. |
| V4 | A day's markets open at **15:00Z on D-1** (10:00 ET in winter). Spreads at the recorded mid-buckets are about 1¢, with tight books. D-1 15:00 ET is about 5 hours after listing. | D-1 15:00 ET stays the decision time. Depth on thin tail buckets is still to be checked from the recorder history. |
| V5 | IEM GFS MOS for KNYC is available from **2010 to the present** (cycles 00/06/12/18Z, `n_x` = daily max). IEM NBM text is **not usable**: NBS covers about 2020 to **2026-05-04**, NBE about 2021 onward, and both are stale since May 2026. | NBM cannot be the live input. **M1 uses GFS MOS** (12Z cycle) as its forecast input, historical and live. M2's NBM features are dropped. See §3 and §4. |
| V6 | IEM CLI history available (366 rows/yr, fetched for 2021–2026; `high`, `valid`, product id `CLINYC`). | Truth for the pre-Kalshi training years. |
| V7 | `/historical/markets/{ticker}/candlesticks` (and `/series/.../candlesticks` for the live partition) return **hourly `yes_bid`/`yes_ask` OHLC** for 2021–2026. **Kalshi history: 1,863 settled event-days, 2021-08-06 to 2026-11-22, 72 missing days** (including 2025-10-31 to at least 2025-11-05). Older tickers use the `HIGHNY-` prefix, newer ones `KXHIGHNY-`. | Kill gate A's market comparison is possible. Quotes are hourly closes, not depth. Probability comparison only, never P&L. |
| V8 | `kb forecast collect` works against the live NWS API. 14 daytime-high vintages stored for Nov 23–29 (e.g. Nov 24 = 49°F), with `updateTime` and our retrieval time kept apart. | Collector field names are confirmed. Remaining caveat: NWS `updateTime` stayed `06:19Z` across two retrievals 1 hour apart, as expected for a 12-hour product. |

**Settlement-source history (found while building W1).** The rules text named the NWS Daily Climate Report from
2022-03 to 2025-03, the NWS "Climatological Report (Daily)" until 2026-08-13, and The Weather Company only for events
from 2026-08-14 on. The 99.9% agreement above therefore mostly measures NWS-vs-NWS; only about 100 days are under the
Weather Company regime, and they agree with the NWS high too. W2 must store the regime per day, and Stage A must report
results for the Weather Company period separately. Stage B (prospective) runs entirely under the Weather Company rule.

**Not yet verified:** the publication lag of each GFS MOS cycle (the plan assumes the 12Z product is out before
19:00Z on D-1, 3+ hours before the decision time); whether the `HIGHNY-` and `KXHIGHNY-` series are the same contract
economically (same station and source); and bid/ask depth on the thin tail buckets.

## 3. Data

Each source gets its own database file (spec §8.1, one data origin per database). Archive data is not production data.

| Source | Use | Origin label | Times stored |
|---|---|---|---|
| IEM GFS MOS for KNYC (V5), 12Z cycle | Model input, historical (2010+) and live | `archive:iem` (historical) / `production` (live) | model runtime, valid time, retrieval time |
| NWS point forecast (`kb forecast collect`) | Secondary feature and baseline; prospective only | `production` | `update_time`, `generated_at`, `received_at` (exists) |
| Kalshi `expiration_value` on settled markets (V1) | Authoritative truth, 2021-08 onward and live | `production` / `archive:kalshi` | settlement time, retrieval time |
| IEM CLI daily max (V6) | Truth for pre-Kalshi training years, and a cross-check against Kalshi's value | `archive:iem` | report product id, climate date, retrieval time |
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
  - Input: the GFS MOS 12Z `n_x` for the target date (V5). Train on 2010 to the start of the test year, truth from the
    CLI for years before 2021-08 and from Kalshi's `expiration_value` afterwards.
  - `T_max ~ forecast + bias(season, cycle) + ε`, with ε drawn from the empirical residuals for the same season
    window and issue cycle, kernel-smoothed.
  - Bucket probability = P(round(T) ∈ [floor, cap]) under the bucket edges from V3.
  - `Uncertainty` = the 90th-percentile absolute calibration error of the bucket probability, measured on the
    training walk-forward. It is reported, not hand-picked (spec §5.4).
  - About 3–6 parameters, fit on roughly 1,500+ daily observations. A model this small cannot overfit them.
- **M2 (later, only if M1 passes):** add features such as GFS MOS spread across cycles, NWS-vs-MOS disagreement, or
  wind/cloud, using a tree model. NBM features are out unless a live NBM source is found (V5). M2 is a new version with its own frozen protocol. M1 results are not reused as M2's out-of-sample
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
| W1 | **Done 2026-11-23.** Parse `strike_type` / `floor_strike` / `cap_strike` into the market store (migration 6, backfilled from stored raw bodies), with `OutcomeBucket` (`FromStrikes`, `FromRules`, `Resolve`) and `MarketSnapshot.Bucket` | V3. 34 tests, five mutations caught. Edge semantics and rules-text fallback checked against 8,606 real markets (INTEGRATION_NOTES). |
| W2 | **Done 2026-11-23.** `kb truth collect-kalshi`, `collect-cli`, `report` into its own `data/truth.db` (origin `archive:truth`, migration 7, append-only). Kalshi `expiration_value` per settled event (live and `/historical`), the IEM NWS climate-report high beside it as a cross-check, and the settlement regime per day. A differing day is flagged and reported, never patched. `TruthStore.Resolve` gives the scoring series. | V1/V2 (2a). 25 tests, 11 mutations caught. Real run below. |
| W3 | Archive importers for IEM **GFS MOS** (KNYC, 12Z cycle, 2010+) and Kalshi hourly candlesticks, into archive databases separate from `truth.db` | V5/V7. Raw bodies plus hashes, like the recorder. NBM is not imported (stale on IEM). IEM climate-report history is already collected by W2. |
| W4 | Evidence-aware forecast interface: change `IForecastModel.Forecast(MarketSnapshot)` to also take a point-in-time evidence view (forecast vintages as of decision time) | Test: a vintage received after the decision time must not change the forecast. |
| W5 | `kb model fit nyc-high-residual` writes the frozen parameter JSON and prints its hash; `ResidualForecastModel` loads it | Deterministic: the same archive gives byte-identical parameters. |
| W6 | `kb eval retro` runs Stage A: walk-forward, baselines, bootstrap, kill gate A verdict | Seed and inputs recorded, so the report reproduces. |
| W7 | Scheduled decision scan at the fixed time, using `kb paper run` with the frozen model and config | Extends `record-scheduled.cmd`. |
| W8 | `kb eval prospective` runs Stage B: the metrics in §5.4 and the pass/fail/insufficient verdict at N | Refuses a verdict before N. |
| W9 | `docs/PHASE3_PROTOCOL.md`: the frozen copy of §5, with its hash in `docs/PLAN.md` | Done before the first Stage B decision. |

V5 resolved to GFS MOS (2a), so W3, W5 and W6 are unblocked.

**W2 result on real data (2026-11-23, `kb truth report`):**

| Measure | Value |
|---|---|
| Kalshi settled event-days | 1,863 (2021-08-06 to 2026-11-22): 1,863 ok, 0 conflict, 0 inconsistent, from 9,976 markets |
| Internal consistency | Every settled yes/no result agrees with its event's value under the strike edges |
| NWS climate-report highs (KNYC, 2010 to 2026) | 6,166 days |
| Kalshi vs NWS high | 1,860 of 1,861 agree. Only 2025-06-18 differs (Kalshi 84, NWS 83) |
| By regime | Weather Company 101/101, NWS Climatological Report 456/457, NWS Daily Climate Report 1,096/1,096, unnamed 207/207 |
| Days in the Kalshi range with no settled value | 72, excluded from scoring (68 events settled with no stated value; the rest never listed) |
| Scoring truth series | 6,096 days = 1,863 Kalshi + 4,233 NWS-high proxy (2010-01-01 to 2021-08-05 only) |

The 101 Weather Company days are too few to say the two sources never diverge; they say the Weather Company value is not
visibly different from the NWS high so far. Stage A will report that period on its own.

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
