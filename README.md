# kalshi-bot

Personal Kalshi research and paper-trading system: .NET 10, SQLite, and a single CLI. It is **observe + paper only**.
Claude research runs only through the official `claude` CLI under your own subscription. No live execution exists.

Requirements: `kalshi-claude-subscription-spec.md`.

- `STATE.md`: current status, blockers, next actions
- `docs/PLAN.md`: phases and the build-vs-reuse decision
- `docs/INTEGRATION_NOTES.md`: verified Kalshi and Claude integration facts
- `CLAUDE.md`: rules for Claude Code sessions working on this repo

## Quick start

Requires the .NET 10 SDK. `./kb` is a bash wrapper; on Windows use `dotnet run --project src/KalshiBot.Cli -- <args>`.

```bash
dotnet test                 # 133 tests, offline
./kb replay                 # frozen SYNTHETIC fixture replay -> data/replay.db, data/replay-status.html
./kb help
```

## Real data (observe + paper)

Real observations have no validated forecast model yet, so paper decisions on real data abstain, by design.
Fees also stay "unknown" until Kalshi's schedule is verified.

```bash
./kb discover --series KXHIGHNY                  # list open tickers (read-only, public endpoint)
./kb watch add <TICKER>
./kb record                                      # one capture; --repeat N --interval 60 for more
./kb markets && ./kb rules <TICKER>
./kb research attest-billing --usage-credits-disabled   # after YOU confirm usage credits are off in claude.ai
./kb research export <TICKER>                    # packet + paste-ready prompt in data/research/
./kb research run <PACKET_ID>                    # guarded subscription round trip; or: kb research import FILE
./kb paper init && ./kb paper run <TICKER> --min-edge 0.05   # records the decision (abstains without a model)
./kb paper resolve && ./kb paper settle && ./kb paper verify
./kb status --html data/status.html              # or: ./kb serve  (http://127.0.0.1:5080)
./kb halt "reason" / ./kb resume                 # safety halt
```

Use one database per data origin, e.g. `--db data/fixtures.db` for fixture experiments. The default
`data/kalshibot.db` is for production observations.

Inspect the database directly with `sqlite3 data/kalshibot.db`. The main tables are `capture_runs`, `raw_responses`,
`market_snapshots`, `market_rules`, `research_packets`, `research_records`, `experimental_probabilities`,
`research_runs`, `paper_decisions`, `paper_orders`, `risk_reservations`, `paper_fills`, and `ledger_entries`.
