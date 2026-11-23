# Integration notes

Verified 2026-11-23. "Verified" means checked against the named primary source. "Unverified" means it could not be
checked from the build environment, and the code takes the conservative side.

## Claude (subscription only)

Sources: code.claude.com/docs/en/authentication, /headless, /agent-sdk/overview; support.claude.com article 12304248;
and the installed Claude Code 2.1.330 binary (`--help` and `auth status` code path).

- **Credential precedence (verified):** cloud provider env (`CLAUDE_CODE_USE_BEDROCK/VERTEX/FOUNDRY`) →
  `ANTHROPIC_AUTH_TOKEN` → `ANTHROPIC_API_KEY` → `apiKeyHelper` → `CLAUDE_CODE_OAUTH_TOKEN` → Anthropic profile/federation →
  subscription OAuth from `/login`.
- **With `-p`, `ANTHROPIC_API_KEY` is always used when present.** Interactive mode asks first; `-p` does not. A stray key
  in your shell therefore bills the API.
- **`--bare` never reads OAuth or the keychain.** It needs an API key, so it is forbidden here.
- `claude setup-token` mints a one-year subscription OAuth token for headless use (`CLAUDE_CODE_OAUTH_TOKEN`). It requires
  Pro, Max, Ultra, Team, or Enterprise.
- `claude auth status` prints JSON. `authMethod` is one of `none | third_party | claude.ai | api_key_helper | oauth_token | api_key`.
  For a claude.ai login it also reports `subscriptionType`, and `apiKeySource` whenever a key is configured.
  `oauth_token` covers any token source, including `ANTHROPIC_AUTH_TOKEN`.
- Agent SDK docs: third-party developers may not offer claude.ai login for their products. This project only runs
  the official CLI under your own login, for personal use. Keep research runs manual and low-volume.
- `total_cost_usd` in `-p` JSON output is a client-side estimate, not a bill.

**How the guard applies this** (`BillingGuard`, `ClaudeResearchRunner`):
1. Rebuild the child environment without `ANTHROPIC_*`, `CLAUDE_CODE_USE_*`, `OPENROUTER_*`, `OPENAI_*`, and `AWS_BEARER_TOKEN_BEDROCK`.
2. Run `claude auth status` in that environment. Require `loggedIn`, `apiProvider = firstParty`, and no `apiKeySource`.
   `authMethod` must be `claude.ai` with a `subscriptionType`, or `oauth_token` with `CLAUDE_CODE_OAUTH_TOKEN` present.
   Anything else is refused and audited in `research_runs`. There is no override.
3. Run `claude -p "<prompt>" --output-format json --json-schema <schema> --no-session-persistence --strict-mcp-config
   --disable-slash-commands --permission-mode dontAsk --tools WebSearch,WebFetch --allowedTools WebSearch,WebFetch`
   in an empty temp directory, so no project CLAUDE.md, hooks, or `.mcp.json` load. The packet goes in on stdin.
4. `structured_output` goes through the same strict `ResultImporter` as a manual import.

**This cloud container:** `claude auth status` reports `oauth_token` with no `CLAUDE_CODE_OAUTH_TOKEN`, which is a
session-injected credential. The guard cannot prove that is subscription auth, so it refuses (verified live:
`research_runs` outcome `refused`). Run the round trip on your machine after `claude auth login`.

## Kalshi

Sources: Kalshi's official OpenAPI-generated SDKs `kalshi-python-sync` and `kalshi-typescript` 3.36.0 (npm maintainer
@kalshi.com, published 2026-11-11). docs.kalshi.com and the Kalshi API hosts are blocked in this environment.

- **Hosts (verified):** prod `https://external-api.kalshi.com/trade-api/v2` (also `api.elections.kalshi.com`); demo
  `https://external-api.demo.kalshi.co/trade-api/v2` (also `demo-api.kalshi.co`).
- **Public, no auth (verified):** `GET /markets/{ticker}`, `/markets/{ticker}/orderbook`, `/events/{event_ticker}`,
  `/series/{series_ticker}`, `/markets?series_ticker=&status=open&cursor=&limit=` (limit max 1000).
- **Signing for later phases (verified):** headers `KALSHI-ACCESS-KEY`, `KALSHI-ACCESS-TIMESTAMP` (ms),
  `KALSHI-ACCESS-SIGNATURE` = base64 signature of `timestamp + METHOD + path`, where the path includes `/trade-api/v2`
  and excludes the query string. RSA keys: PSS, SHA-256, MGF1-SHA-256, salt = digest length. Ed25519 keys are also accepted.
- **Fixed-point fields (verified):** prices are `*_dollars` strings with up to 6 decimals in responses. Counts are
  `*_fp` strings with 2 decimals, and fractional contracts exist at 0.01 granularity. This code parses them into
  `decimal` and trades whole contracts only.
- **Order book (verified):** `orderbook_fp.{yes_dollars,no_dollars}` = `[[price, count], ...]`, bids only.
  YES ask = 1 − best NO bid. The code sorts levels itself rather than trusting payload order. An empty side may be `null`.
- **Market `status` enum (verified):** initialized, inactive, active, closed, determined, disputed, amended, finalized.
  Paper trading requires `active`. Paper settlement requires `finalized` with `result` in {yes, no}.
  `market_type` is binary or scalar; only binary is supported.
- **Rules:** `rules_primary`, `rules_secondary`, `early_close_condition`. Hashed together into a rules version.
- **Tick size:** `price_ranges[{start,end,step}]` per market. The risk engine checks limits against it.
- **Fees (unverified):** series `fee_type` (quadratic, quadratic_with_maker_fees, quadratic_with_combo_maker_fees, flat)
  and `fee_multiplier`, with event-level overrides. The schedule PDF (kalshi.com/docs/kalshi-fee-schedule.pdf) was
  unreachable. Paper fees use `ceil_cent(0.07 × max(1, multiplier) × C × P × (1−P))` per fill level, which errs high.
  Verify before trusting paper P&L.
- **Rate limits (unverified):** client pacing is 250 ms between requests, and 429/5xx retry up to 3 times honoring `Retry-After`.

## Environment blockers (this cloud session)

The network policy denies `chatgpt.com` (spec), `docs.kalshi.com`, `external-api.kalshi.com`, `api.elections.kalshi.com`,
the demo hosts, and `builds.dotnet.microsoft.com`. The .NET SDK came from Ubuntu's archive instead. To record real data
from a cloud session, add the Kalshi API hosts under the environment's Network access → Allowed domains
(https://code.claude.com/docs/en/cloud-environments#network-access), or run the recorder locally.
