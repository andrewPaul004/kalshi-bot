# Synthetic fixtures

Hand-written JSON shaped like Kalshi Trade API v2 responses (field names checked against Kalshi's official
SDK models, v3.36.0). **None of this is observed market data.** Tickers start with `KXSYNTH` and dates are in 2099
so they cannot collide with real markets. Records captured from these files carry `data_origin = fixture:synthetic`
and are badged SYNTHETIC everywhere.

- `kalshi-open/` — two active markets with order books.
- `kalshi-settled/` — the same markets after finalization (T50 resolves yes, T60 resolves no).
