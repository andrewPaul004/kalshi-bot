# kalshi-bot

Personal Kalshi research and paper-trading system: .NET 10, SQLite, and a single CLI. It is **observe + paper only**.
Claude research runs only through the official `claude` CLI under your own subscription. No live execution exists.

- `STATE.md`: current status, blockers, next actions
- `docs/PLAN.md`: phases and the build-vs-reuse decision
- `docs/INTEGRATION_NOTES.md`: verified Kalshi and Claude integration facts
- `CLAUDE.md`: rules for Claude Code sessions working on this repo

## Quick start

Requires the .NET 10 SDK. `./kb` is a bash wrapper; on Windows use `dotnet run --project src/KalshiBot.Cli -- <args>`.

```bash
dotnet test                 # 90 tests, offline
./kb demo                   # offline end-to-end on SYNTHETIC fixtures -> data/demo.db, data/demo-status.html
./kb help
```

## Real data (observe + paper)

```bash
./kb discover --series KXHIGHNY                  # list open tickers (read-only, public endpoint)
./kb watch add <TICKER>
./kb record                                      # one capture; --repeat N --interval 60 for more
./kb markets && ./kb rules <TICKER>
./kb research export <TICKER>                    # packet + paste-ready prompt in data/research/
./kb research run <PACKET_ID>                    # subscription-only Claude round trip (guarded)
./kb research import result.json                 # or import a result you got manually
./kb paper init && ./kb paper run <TICKER>       # strategy -> risk -> SIMULATED fill
./kb paper settle && ./kb paper verify
./kb status --html data/status.html              # or: ./kb serve  (http://127.0.0.1:5080)
./kb halt "reason" / ./kb resume                 # kill switch
```

Inspect the database directly with `sqlite3 data/kalshibot.db` (tables: `capture_runs`, `raw_responses`,
`market_snapshots`, `market_rules`, `research_packets`, `research_results`, `research_runs`, `paper_orders`,
`paper_fills`, `ledger_entries`).
