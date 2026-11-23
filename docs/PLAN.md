# Plan

## Source of requirements

The specification was provided as a ChatGPT share link. It could not be read: `chatgpt.com` is outside this cloud
environment's network policy, and the link is account-scoped. Phases 1–2 below are derived from the deliverables in
the kickoff request. **Reconcile against the spec** (drop it in `docs/SPEC.md`) before starting Phase 3.

## Build vs. reuse (decided 2026-11-23)

| Concern | Decision | Evidence |
|---|---|---|
| Kalshi client | **Build** a thin GET-only `HttpClient` for 5 public endpoints. Store raw bodies verbatim. | Kalshi publishes official SDKs only for Python (`kalshi-python-sync`) and TypeScript (`kalshi-typescript`), both v3.36.0 and OpenAPI-generated. There is no official .NET SDK. Market data endpoints need no auth (`_auth_settings = []` in the SDK). Field names were checked against the SDK models. |
| Kalshi signing (later) | **Build** when needed. About 20 lines of `System.Security.Cryptography` code. | SDK `auth.py`/`auth.ts`: RSA-PSS SHA-256 (salt = digest) or Ed25519 over `timestamp_ms + METHOD + path` (no query string). |
| Claude access | **Reuse** the official `claude` CLI (`claude -p --output-format json --json-schema`). | Only subscription-backed programmatic path. Agent SDK docs bar third-party products from offering claude.ai login; this is personal use of your own login through the official CLI. |
| Storage | **Reuse** `Microsoft.Data.Sqlite` 10.0.15 with hand-written SQL. No ORM. | Small schema; explicit SQL is easier to review; append-only triggers and CHECKs live in the schema. |
| CLI parsing / web | Hand-rolled argument parser; ASP.NET Core minimal API from the shared framework for the status page. | Avoids dependencies for about 20 commands. |
| Tests | xUnit v3 (4.0.3) on Microsoft.Testing.Platform. | The .NET 10 SDK no longer runs VSTest with xUnit v3 4.x. |
| Community Kalshi bots | Not adopted. | The spec's repository review was unavailable, and community repos were not re-reviewed here. None is .NET, and none would save meaningful work at this scope. |

Runtime: .NET 10 LTS (SDK 10.0.115, runtime 10.0.15, from Ubuntu's archive). .NET 11 is out but is STS, so 10 is
the supported LTS choice.

## Phases

**Phase 1 — Observe (done).** Record the selected markets: market, order book, event, and series data, plus rules text,
into SQLite. Raw bodies are stored with SHA-256, URL, request and response timestamps, and the server `Date` header.
A rules change creates a new rules version. Capture runs are labeled by data origin, and crashed runs are marked
abandoned.

**Phase 2 — Paper + research round trip (done, except the real Claude round trip, which is blocked here).**
- Deterministic `IStrategy` (one reference strategy) and a fail-closed `RiskEngine`.
- IOC paper broker against recorded books, plus an append-only ledger with settlement.
- Research packet export (deterministic, quote-free) and strict result import.
- `research run`, which calls the Claude CLI only after `BillingGuard` verifies subscription auth.

**Phase 3 — Real data + calibration (next; reconcile with the spec first).**
1. Run the recorder against production for a small watchlist on a schedule (the user's machine or an allowed environment).
2. Run real research round trips. Accumulate estimates and settled outcomes.
3. Score the estimates (Brier score, log loss, calibration buckets) against outcomes and against the market price at
   packet time. Paper P&L by strategy.
4. Promote an estimate source from `unvalidated` only through a recorded calibration result and a new migration.
   Until then, research probabilities stay out of any non-paper decision.

**Phase 4 — Demo-environment execution (separately authorized).** Signed requests against `external-api.demo.kalshi.co`,
order state reconciliation, and a real kill switch. Credentials come from a local secret store, never the repo.

**Phase 5 — Production execution (separately authorized).** Requires user-chosen bankroll and limits, a live `RiskLimits`
preset reviewed by the user, and explicit go-ahead. Out of scope until then.

## Non-goals now

Multiple strategies, a large dashboard, a Python service, multi-agent orchestration, websockets, and any live order path.
