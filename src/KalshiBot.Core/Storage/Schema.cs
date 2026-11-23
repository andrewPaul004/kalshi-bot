namespace KalshiBot.Core.Storage;

public static class Schema
{
    /// <summary>Forward-only migrations. Never edit a shipped migration; append a new one.</summary>
    private static readonly (int Version, string Sql)[] Migrations =
    [
        (1, """
        CREATE TABLE capture_runs (
            id            INTEGER PRIMARY KEY,
            started_at    TEXT NOT NULL,
            finished_at   TEXT,
            status        TEXT NOT NULL CHECK (status IN ('running','completed','partial','failed','abandoned')),
            data_origin   TEXT NOT NULL,
            base_url      TEXT NOT NULL,
            app_version   TEXT NOT NULL,
            detail        TEXT
        );

        -- Verbatim HTTP bodies. Every derived row points back here (source reference).
        CREATE TABLE raw_responses (
            id              INTEGER PRIMARY KEY,
            capture_run_id  INTEGER NOT NULL REFERENCES capture_runs(id),
            method          TEXT NOT NULL,
            url             TEXT NOT NULL,
            http_status     INTEGER NOT NULL,
            requested_at    TEXT NOT NULL,
            received_at     TEXT NOT NULL,
            server_date     TEXT,
            body_sha256     TEXT NOT NULL,
            body            TEXT NOT NULL
        );
        CREATE TRIGGER raw_responses_no_update BEFORE UPDATE ON raw_responses BEGIN SELECT RAISE(ABORT, 'raw_responses is append-only'); END;
        CREATE TRIGGER raw_responses_no_delete BEFORE DELETE ON raw_responses BEGIN SELECT RAISE(ABORT, 'raw_responses is append-only'); END;

        -- One row per distinct rules text per market. A new row means the rules changed.
        CREATE TABLE market_rules (
            id                    INTEGER PRIMARY KEY,
            ticker                TEXT NOT NULL,
            rules_sha256          TEXT NOT NULL,
            rules_primary         TEXT NOT NULL,
            rules_secondary       TEXT NOT NULL,
            early_close_condition TEXT,
            first_seen_at         TEXT NOT NULL,
            last_seen_at          TEXT NOT NULL,
            first_raw_id          INTEGER NOT NULL REFERENCES raw_responses(id),
            last_raw_id           INTEGER NOT NULL REFERENCES raw_responses(id),
            UNIQUE (ticker, rules_sha256)
        );

        CREATE TABLE market_snapshots (
            id                INTEGER PRIMARY KEY,
            capture_run_id    INTEGER NOT NULL REFERENCES capture_runs(id),
            raw_market_id     INTEGER NOT NULL REFERENCES raw_responses(id),
            raw_orderbook_id  INTEGER REFERENCES raw_responses(id),
            rules_id          INTEGER NOT NULL REFERENCES market_rules(id),
            data_origin       TEXT NOT NULL,
            observed_at       TEXT NOT NULL,
            ticker            TEXT NOT NULL,
            event_ticker      TEXT NOT NULL,
            market_type       TEXT NOT NULL,
            title             TEXT,
            yes_sub_title     TEXT,
            status            TEXT NOT NULL,
            open_time         TEXT,
            close_time        TEXT,
            expected_expiration_time TEXT,
            yes_bid           TEXT,
            yes_ask           TEXT,
            no_bid            TEXT,
            no_ask            TEXT,
            last_price        TEXT,
            volume            TEXT,
            open_interest     TEXT,
            result            TEXT,
            price_ranges_json TEXT,
            orderbook_json    TEXT
        );
        CREATE INDEX ix_market_snapshots_ticker ON market_snapshots (ticker, observed_at);

        CREATE TABLE event_snapshots (
            id                      INTEGER PRIMARY KEY,
            capture_run_id          INTEGER NOT NULL REFERENCES capture_runs(id),
            raw_id                  INTEGER NOT NULL REFERENCES raw_responses(id),
            data_origin             TEXT NOT NULL,
            observed_at             TEXT NOT NULL,
            event_ticker            TEXT NOT NULL,
            series_ticker           TEXT NOT NULL,
            title                   TEXT,
            mutually_exclusive      INTEGER,
            settlement_sources_json TEXT,
            fee_type_override       TEXT,
            fee_multiplier_override TEXT
        );
        CREATE INDEX ix_event_snapshots_ticker ON event_snapshots (event_ticker, observed_at);

        CREATE TABLE series_snapshots (
            id                      INTEGER PRIMARY KEY,
            capture_run_id          INTEGER NOT NULL REFERENCES capture_runs(id),
            raw_id                  INTEGER NOT NULL REFERENCES raw_responses(id),
            data_origin             TEXT NOT NULL,
            observed_at             TEXT NOT NULL,
            series_ticker           TEXT NOT NULL,
            title                   TEXT,
            category                TEXT,
            fee_type                TEXT,
            fee_multiplier          TEXT,
            settlement_sources_json TEXT,
            contract_terms_url      TEXT
        );
        CREATE INDEX ix_series_snapshots_ticker ON series_snapshots (series_ticker, observed_at);

        CREATE TABLE watchlist (
            ticker    TEXT PRIMARY KEY,
            added_at  TEXT NOT NULL
        );

        CREATE TABLE controls (
            key         TEXT PRIMARY KEY,
            value       TEXT NOT NULL,
            updated_at  TEXT NOT NULL
        );

        CREATE TABLE research_packets (
            packet_id    TEXT PRIMARY KEY,
            ticker       TEXT NOT NULL,
            snapshot_id  INTEGER NOT NULL REFERENCES market_snapshots(id),
            rules_id     INTEGER NOT NULL REFERENCES market_rules(id),
            data_origin  TEXT NOT NULL,
            created_at   TEXT NOT NULL,
            packet_json  TEXT NOT NULL
        );

        -- Research estimates are experimental by construction in this phase: the CHECK makes
        -- "validated" unrepresentable until a calibration process exists and a migration relaxes it.
        CREATE TABLE research_results (
            id                 INTEGER PRIMARY KEY,
            packet_id          TEXT NOT NULL REFERENCES research_packets(packet_id),
            result_sha256      TEXT NOT NULL UNIQUE,
            ticker             TEXT NOT NULL,
            rules_sha256       TEXT NOT NULL,
            probability_yes    TEXT,
            abstain_reason     TEXT,
            confidence         TEXT NOT NULL,
            source_kind        TEXT NOT NULL CHECK (source_kind IN ('claude-subscription-cli','manual','synthetic-fixture')),
            validation_status  TEXT NOT NULL CHECK (validation_status = 'unvalidated'),
            imported_at        TEXT NOT NULL,
            result_json        TEXT NOT NULL,
            CHECK ((probability_yes IS NULL) <> (abstain_reason IS NULL))
        );

        -- Audit trail for every attempted Claude CLI research call, including refusals by the billing guard.
        CREATE TABLE research_runs (
            id                 INTEGER PRIMARY KEY,
            packet_id          TEXT NOT NULL REFERENCES research_packets(packet_id),
            started_at         TEXT NOT NULL,
            finished_at        TEXT NOT NULL,
            outcome            TEXT NOT NULL CHECK (outcome IN ('refused','failed','imported')),
            auth_method        TEXT,
            subscription_type  TEXT,
            detail             TEXT,
            stdout_sha256      TEXT,
            stdout             TEXT,
            research_result_id INTEGER REFERENCES research_results(id)
        );

        CREATE TABLE paper_accounts (
            id             INTEGER PRIMARY KEY,
            name           TEXT NOT NULL UNIQUE,
            created_at     TEXT NOT NULL,
            starting_cash  TEXT NOT NULL
        );

        CREATE TABLE paper_orders (
            id                 INTEGER PRIMARY KEY,
            account_id         INTEGER NOT NULL REFERENCES paper_accounts(id),
            client_order_id    TEXT NOT NULL UNIQUE,
            decision_id        TEXT NOT NULL,
            strategy           TEXT NOT NULL,
            ticker             TEXT NOT NULL,
            side               TEXT NOT NULL CHECK (side IN ('yes','no')),
            action             TEXT NOT NULL CHECK (action IN ('buy','sell')),
            quantity           INTEGER NOT NULL CHECK (quantity > 0),
            limit_price        TEXT NOT NULL,
            snapshot_id        INTEGER NOT NULL REFERENCES market_snapshots(id),
            research_result_id INTEGER REFERENCES research_results(id),
            data_origin        TEXT NOT NULL,
            created_at         TEXT NOT NULL,
            status             TEXT NOT NULL CHECK (status IN ('filled','partially_filled','unfilled','rejected')),
            filled_quantity    INTEGER NOT NULL DEFAULT 0,
            reasons            TEXT
        );

        CREATE TABLE paper_fills (
            id         INTEGER PRIMARY KEY,
            order_id   INTEGER NOT NULL REFERENCES paper_orders(id),
            ticker     TEXT NOT NULL,
            side       TEXT NOT NULL,
            action     TEXT NOT NULL,
            quantity   INTEGER NOT NULL CHECK (quantity > 0),
            price      TEXT NOT NULL,
            fee        TEXT NOT NULL,
            filled_at  TEXT NOT NULL,
            simulated  INTEGER NOT NULL CHECK (simulated = 1)
        );
        CREATE TRIGGER paper_fills_no_update BEFORE UPDATE ON paper_fills BEGIN SELECT RAISE(ABORT, 'paper_fills is append-only'); END;
        CREATE TRIGGER paper_fills_no_delete BEFORE DELETE ON paper_fills BEGIN SELECT RAISE(ABORT, 'paper_fills is append-only'); END;

        -- Append-only journal. Cash = sum(cash_delta); position(ticker, side) = sum(quantity_delta).
        CREATE TABLE ledger_entries (
            id              INTEGER PRIMARY KEY,
            account_id      INTEGER NOT NULL REFERENCES paper_accounts(id),
            ts              TEXT NOT NULL,
            kind            TEXT NOT NULL CHECK (kind IN ('deposit','trade','fee','settlement')),
            ticker          TEXT,
            side            TEXT CHECK (side IS NULL OR side IN ('yes','no')),
            quantity_delta  INTEGER NOT NULL DEFAULT 0,
            cash_delta      TEXT NOT NULL,
            cost_delta      TEXT NOT NULL DEFAULT '0',
            ref_kind        TEXT,
            ref_id          INTEGER,
            memo            TEXT
        );
        CREATE INDEX ix_ledger_account ON ledger_entries (account_id, id);
        CREATE TRIGGER ledger_no_update BEFORE UPDATE ON ledger_entries BEGIN SELECT RAISE(ABORT, 'ledger_entries is append-only'); END;
        CREATE TRIGGER ledger_no_delete BEFORE DELETE ON ledger_entries BEGIN SELECT RAISE(ABORT, 'ledger_entries is append-only'); END;

        CREATE TABLE paper_settlements (
            account_id   INTEGER NOT NULL REFERENCES paper_accounts(id),
            ticker       TEXT NOT NULL,
            result       TEXT NOT NULL CHECK (result IN ('yes','no')),
            settled_at   TEXT NOT NULL,
            snapshot_id  INTEGER NOT NULL REFERENCES market_snapshots(id),
            PRIMARY KEY (account_id, ticker)
        );
        """),
        (2, """
        -- Estimate freshness is measured from when the evidence was current (as_of), not from import time.
        ALTER TABLE research_results ADD COLUMN as_of TEXT;
        UPDATE research_results SET as_of = json_extract(result_json, '$.as_of') WHERE as_of IS NULL;
        """),
    ];

    public static int LatestVersion => Migrations[^1].Version;

    public static void Migrate(Db db)
    {
        db.Exec("CREATE TABLE IF NOT EXISTS schema_migrations (version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL);");
        var current = db.Scalar<long?>("SELECT MAX(version) FROM schema_migrations") ?? 0;
        foreach (var (version, sql) in Migrations.Where(m => m.Version > current))
        {
            db.InTransaction(() =>
            {
                db.Exec(sql);
                db.Exec("INSERT INTO schema_migrations (version, applied_at) VALUES ($v, $t)",
                    ("$v", version), ("$t", Common.Ts.ToText(DateTimeOffset.UtcNow)));
            });
        }
    }
}
