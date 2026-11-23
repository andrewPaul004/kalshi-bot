# Integration notes

Verified 2026-11-23 (the spec's sources were checked 2026-11-22; source ids S1–S20 refer to spec §14).
- **Verified** means checked against the named primary source.
- **Unverified** means it could not be checked from the build environment, so the code takes the conservative side.

Installed at verification:
- Claude Code CLI 2.1.330.
- .NET SDK 10.0.115 with runtime 10.0.15 (Ubuntu archive). .NET 10 is the current LTS; 8 and 9 are out of support (S10).
- Kalshi SDK contract 3.36.0.

## Claude (subscription only)

Sources:
- code.claude.com/docs/en/authentication (S3), /headless (S2), /settings, /managed-settings, and /agent-sdk/overview.
- support.claude.com article 12304248.
- The installed Claude Code 2.1.330 binary: its `--help` output and the `auth status` code path.

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
- `total_cost_usd` in `-p` JSON output is a client-side estimate, not a bill. Token estimates are telemetry, not a cap (spec §4.3).
- **Usage credits** (S4) can bill beyond included usage. No documented programmatic check of that setting exists. The
  runner therefore requires an owner attestation, made with `kb research attest-billing --usage-credits-disabled`, that
  is at most 30 days old. The status page reports it as *attested*, never as *verified*.
- **No `--max-turns` flag in 2.1.330** (absent from `--help`). Research is bounded by a 5-minute process timeout, a
  web-only tool set, `dontAsk` permissions, and one process at a time (an OS file lock). Re-check after CLI upgrades.
- **AUP (S6), design interpretation:** Claude research stays in research and scoring. Trades come only from deterministic,
  versioned models and strategies. Before any feature turns Claude's subjective view into an order, review it against
  the policy.

**How the guard applies this** (`BillingGuard`, `ClaudeResearchRunner`). Every step fails closed, and every attempt,
including refusals, is audited in `research_runs`:
1. **Allowlisted child environment.** Only system basics, proxy/CA variables, `LC_*`, `CLAUDE_CONFIG_DIR`, and
   `CLAUDE_CODE_OAUTH_TOKEN` pass through. Everything else is dropped, including `ANTHROPIC_*`, `CLAUDE_CODE_USE_*`,
   session-injected token files, and unknown variables.
2. **Settings files.** User `settings.json` (`$CLAUDE_CONFIG_DIR` or `~/.claude`) and managed settings must not set
   `apiKeyHelper` or a billing-relevant `env` key (`ANTHROPIC_*` except `*_MODEL`, `CLAUDE_CODE_USE_*`, OpenAI/OpenRouter,
   `AWS_BEARER_TOKEN_BEDROCK`). Managed settings are `managed-settings.json` plus `managed-settings.d/*.json` in
   `/etc/claude-code`, `/Library/Application Support/ClaudeCode`, or `C:\Program Files\ClaudeCode`.
   An unreadable or invalid file refuses the run. This layer exists because `claude auth status` does not reveal a
   base-URL gateway.
3. **Version and auth.** `claude --version` must parse, and is recorded in every run. Then `claude auth status`, run in
   that environment and working directory, must report `loggedIn`, `apiProvider = firstParty`, and no `apiKeySource`.
   `authMethod` must be `claude.ai` with a `subscriptionType`, or `oauth_token` with a **non-empty** `CLAUDE_CODE_OAUTH_TOKEN`.
4. **Model run.** `claude -p "<prompt>" --output-format json --json-schema <schema> --no-session-persistence --strict-mcp-config
   --disable-slash-commands --permission-mode dontAsk --tools WebSearch,WebFetch --allowedTools WebSearch,WebFetch`
   runs in an empty temp directory, which is refused if it is inside a git repo, so no project settings, hooks,
   CLAUDE.md, or `.mcp.json` load. The packet goes in on stdin.
5. **Import.** `structured_output` must echo the `packetId`, `packetHash`, and `marketTicker` that were run. It then passes
   through the same strict `ResearchImporter` as a manual import, which validates it against
   `schemas/research-record.schema.json` plus linkage, date, and evidence checks.
   The observed CLI version and model (from `modelUsage`) are stored alongside the record's self-reported ones.
6. **Before steps 1–5:** an unchanged packet that already has a record for this prompt version is skipped (spec §4.3), and
   a missing or stale owner attestation refuses the run.

**Windows launch:**
- An npm install (e.g. under nvm4w) gives only `claude.cmd`, a cmd.exe shim.
- Starting a batch file makes cmd.exe re-parse every argument, which can corrupt the JSON schema argument (the
  "BatBadBut" class). So `ClaudeLaunch` reads the shim and starts `node.exe` plus Claude Code's JS entry point
  directly. A native `claude.exe` is used as is.
- A shim it cannot unwrap is refused, with a pointer to `--claude <path to claude.exe>`.
- `CLAUDE_CODE_GIT_BASH_PATH` passes the environment filter.

**Residual risk the guard cannot see:** MDM profiles (macOS `com.anthropic.claudecode`, Windows HKLM policy) and
server-managed settings from a Team/Enterprise admin console. On a personal Pro/Max login neither normally exists.
Check `/status` → `Setting sources` once on your machine.

**Operator machine (Windows, verified 2026-11-23):** `kb research run` returned `IMPORTED (auth claude.ai, team)`.
Claude Code is installed via npm under nvm4w and was launched through the unwrapped shim. The subscription is a Team plan.

**This cloud container:** `claude auth status` reports `oauth_token` with no `CLAUDE_CODE_OAUTH_TOKEN`, which is a
session-injected credential. The guard cannot prove that is subscription auth, so it refuses (verified live:
`research_runs` outcome `refused`). Run the round trip on your machine after `claude auth login`.

## Kalshi

Sources: Kalshi's official OpenAPI-generated SDKs `kalshi-python-sync` and `kalshi-typescript` 3.36.0 (npm maintainer
@kalshi.com, published 2026-11-11). docs.kalshi.com and the Kalshi API hosts are blocked in this environment, so the
spec's S7–S9 and S16 pages could not be re-read here.

- **Hosts (verified):** prod `https://external-api.kalshi.com/trade-api/v2` (also `api.elections.kalshi.com`); demo
  `https://external-api.demo.kalshi.co/trade-api/v2` (also `demo-api.kalshi.co`).
- **Public, no auth (verified):** `GET /markets/{ticker}`, `/markets/{ticker}/orderbook`, `/events/{event_ticker}`,
  `/series/{series_ticker}`, `/markets?series_ticker=&status=open&cursor=&limit=` (limit max 1000).
- **Order writes (verified in SDK; Phase 4):** `/portfolio/events/orders` (create), `/portfolio/events/orders/batched`,
  `/{order_id}`, and `/{order_id}/amend`. The create request has `client_order_id`, `side`, `action`, `count`/`count_fp`,
  `yes_price_dollars`/`no_price_dollars`, `time_in_force`, `buy_max_cost`, and `post_only`.
  Per the spec (S9), client-order-id deduplication covers open orders only, so a durable local intent is mandatory.
- **Kalshi MCP (S7):** read-only research tooling. It is not used, and nothing trade-capable is attached to research.
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
- **Strike fields (verified 2026-11-23, KXHIGHNY):** `strike_type` is `between`, `less`, or `greater`, with numeric
  `floor_strike` / `cap_strike`. Checked against 8,606 markets with these fields (live and `/historical`, 2022-2026) and
  4,280 of them with a settled result and `expiration_value`: zero mismatches, and no market had an unexpected extra field.
  - `between`: floor..cap inclusive. `less`: value < cap. `greater`: value > floor. Whole °F. Parsed in `OutcomeBucket`.
  - Markets from 2021 to early 2022 and a few from 2025 carry no strike fields; `OutcomeBucket.FromRules` reads their
    rules text (1,414 of 1,418 parse; 4 odd phrasings fail closed). Where both exist, the two agree on all 8,606.
  - Settlement source in the rules text changed over time: unnamed (2021-08 to 2022-03), NWS "Daily Climate Report"
    (2022-03 to 2025-03), NWS "Climatological Report (Daily)" (2025-03 to 2026-08-13), then The Weather Company (events
    from 2026-08-14). Older markets live under the `HIGHNY-` series prefix; newer ones `KXHIGHNY-`.
  - `expiration_value` is the settled value as a decimal string (e.g. "61.00"). It is empty on many 2022-2024 markets,
    and within one finalized event some markets carry it while others leave it empty. 2021 values arrive padded
    (" 88") or without decimals ("87"). No event has two different non-empty values.
- **Settled markets (verified 2026-11-23):** `GET /markets?series_ticker=S&status=settled&limit=1000` returns recent
  settled markets (about 414, since 2026-09-24). Older ones are on `GET /historical/markets?series_ticker=S&limit=1000`
  (cursor-paged; `GET /historical/cutoff` gives the boundary). Both work against `external-api.kalshi.com`. Querying
  `KXHIGHNY` also returns the older `HIGHNY-` events. Hourly bid/ask candlesticks:
  `/historical/markets/{ticker}/candlesticks` (older) and `/series/{series}/markets/{ticker}/candlesticks` (recent).
- **IEM NWS climate reports (verified 2026-11-23):** `GET https://mesonet.agron.iastate.edu/json/cli.py?station=KNYC&year=Y`
  returns one row per climate date (`valid`, `high`, `product`, ...); about 366 rows a year, from at least 2010.
  IEM GFS MOS: `/api/1/mos.json?station=KNYC&model=GFS&runtime=...` (2010 to now). NBS/NBE stopped on 2026-05-04.
- **IEM bulk GFS MOS (verified 2026-11-23):** `GET /cgi-bin/request/mos.py?station=KNYC&model=GFS&sts=2024-01-01T00:00Z&ets=2025-01-01T00:00Z&format=csv`
  returns every run (00/06/12/18Z) in the range, about 31,000 rows and 3 MB a year. The CSV has 23 columns including
  `runtime`, `ftime` (both UTC), `n_x`, `tmp`. `format=json` and rapid repeat requests drew HTTP 429, so `kb archive
  collect-mos` waits 5 s between requests and retries with back-off. A year's request also returns the next year's
  00Z Jan 1 run, so adjacent years overlap by one run (de-duplicated).
- **GFS MOS timing (verified against 5,429 settled highs, 2012-2026):** the max temperature for local day D is `n_x` at
  forecast time **00Z on D+1** of the 12Z run on D-1: MAE 2.65 F, bias -0.45 F, SD 3.59 F. The 12Z and 12Z-next-day
  columns carry the overnight min (about 13 F off), and 00Z on D+2 is the following day's max (MAE 5.75). 12Z runs are
  stored for about 365 days a year; 9 settled days have no 12Z run.
- **GFS MOS publication time: NOT verified.** The archive holds the cycle time only. IEM's AFOS archive has no MAV products,
  `nomads.ncep.noaa.gov` returned 403 to this client, and the NWS pages read gave no figure. `DecisionClock.AssumedMosLag`
  is 6 h (generous; the product is normally out in about 4), which keeps the 12Z run available an hour before the earliest
  decision time (19:00Z in summer). The live collector must record when each run is first seen so this can be measured.
- **Kalshi candlesticks (verified 2026-11-23):** per market, `start_ts`, `end_ts` (Unix seconds), `period_interval=60`.
  `/series/{series}/markets/{ticker}/candlesticks` uses field names `close_dollars` / `volume_fp` / `open_interest_fp`;
  `/historical/markets/{ticker}/candlesticks` uses `close` / `volume` / `open_interest`. For markets before
  `GET /historical/cutoff` (`market_settled_ts`, 2026-09-24) use the historical path; the series path also answered for
  Sep 2026 markets, and a 404 on one path is worth one try on the other. The event-level
  `/series/{series}/events/{event}/candlesticks` exists only for recent events and returns an array of arrays; the
  `/historical/events/...` variant 404s, so this project fetches per market. Candles are quote closes: a market with no
  quote shows bid 0.00 and ask 1.00.
- **Tick size:** `price_ranges[{start,end,step}]` per market. The risk engine checks limits against it.
- **Fees (verified 2026-11-23):** source is the Kalshi Fee Schedule PDF, "Last updated and effective: July 7, 2026"
  (sha256 `c326a69f596a11e8f8be2620402d39a8d4823920c21cc97c93a114d862699601`), a copy provided by the operator.
  - Taker fee = `round up(M × 0.07 × C × P × (1−P))`. M is a per-series multiplier, default 1. The non-standard list has
    M values of 0, 1, or 2, for example; KXHIGHNY is not listed.
  - Maker fee = `0.0175 × …` with default M = 0. It applies only to resting orders, and paper never rests orders.
  - Discrepancy: the text says the fee rounds "such that fee + positionCost is rounded to a centicent", but the PDF's own
    General Trading Fees Table matches rounding up to a whole **cent**. The code uses cent rounding; it reproduces all
    21 table rows (test) and errs high.
  - Paper uses `max(1, fee_multiplier)`. The API exposes one multiplier, and its mapping to the PDF's separate maker and
    taker M is undocumented, so paper never charges less than the standard fee.
  - Supported fee types: `quadratic` and `quadratic_with_maker_fees`. `flat` and combo fee types are unknown, so they block risk.
  - Upcoming changes are announced at https://kalshi.com/fee-schedule. Add a new `FeeSchedule` with its effective date;
    never edit an existing one.
- **Rate limits (unverified):** client pacing is 250 ms between requests, and 429/5xx retry up to 3 times honoring `Retry-After`.

## NWS (api.weather.gov) — evidence for KXHIGHNY

- Flow: `GET /points/40.7789,-73.9692` (Central Park, the CLINYC station), then `properties.forecast`, a
  `/gridpoints/{office}/{x},{y}/forecast` URL. Only `https://api.weather.gov` URLs are followed.
- Stored per period: `name`, `startTime`/`endTime` (local offset), `isDaytime`, `temperature`/`temperatureUnit`, and
  `shortForecast`. The response's `updateTime` (publication) and `generatedAt` are stored separately from our retrieval
  time (spec 8.2).
- A User-Agent header is required, so a generic `kalshi-bot/0.4 (personal research; read-only)` is sent. No API key.
- **Unverified here:** api.weather.gov is unreachable from the build environment. Field names follow the NWS API
  documentation, the fixtures are hand-written to that shape, and raw bodies are always stored first. Confirm on the
  first live run.

## Environment blockers (this cloud session)

The network policy denies `chatgpt.com` (spec), `docs.kalshi.com`, `external-api.kalshi.com`, `api.elections.kalshi.com`,
the demo hosts, and `builds.dotnet.microsoft.com`. The .NET SDK came from Ubuntu's archive instead. To record real data
from a cloud session, add the Kalshi API hosts under the environment's Network access → Allowed domains
(https://code.claude.com/docs/en/cloud-environments#network-access), or run the recorder locally.
