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
        (3, """
        -- Spec v2.0 alignment. The pre-spec research and paper tables are retired, not deleted: renamed to legacy_*_v1
        -- (their append-only triggers and foreign keys follow the rename) and no longer read or written.
        ALTER TABLE research_results  RENAME TO legacy_research_results_v1;
        ALTER TABLE research_runs     RENAME TO legacy_research_runs_v1;
        ALTER TABLE research_packets  RENAME TO legacy_research_packets_v1;
        ALTER TABLE paper_settlements RENAME TO legacy_paper_settlements_v1;
        ALTER TABLE ledger_entries    RENAME TO legacy_ledger_entries_v1;
        ALTER TABLE paper_fills       RENAME TO legacy_paper_fills_v1;
        ALTER TABLE paper_orders      RENAME TO legacy_paper_orders_v1;
        ALTER TABLE paper_accounts    RENAME TO legacy_paper_accounts_v1;

        -- Research (spec 4.5). Records are evidence and suggestions, never orders.
        CREATE TABLE research_packets (
            packet_id      TEXT PRIMARY KEY,
            packet_hash    TEXT NOT NULL UNIQUE,
            market_ticker  TEXT NOT NULL,
            event_ticker   TEXT NOT NULL,
            series_ticker  TEXT,
            snapshot_id    INTEGER NOT NULL REFERENCES market_snapshots(id),
            rules_id       INTEGER NOT NULL REFERENCES market_rules(id),
            rules_sha256   TEXT NOT NULL,
            data_origin    TEXT NOT NULL,
            prompt_version TEXT NOT NULL,
            created_at     TEXT NOT NULL,
            packet_json    TEXT NOT NULL
        );

        CREATE TABLE research_records (
            id                   INTEGER PRIMARY KEY,
            research_id          TEXT NOT NULL UNIQUE,
            packet_id            TEXT NOT NULL REFERENCES research_packets(packet_id),
            record_sha256        TEXT NOT NULL,
            market_ticker        TEXT NOT NULL,
            disposition          TEXT NOT NULL CHECK (disposition IN ('needs_review','insufficient_evidence','no_issue_found')),
            created_at_utc       TEXT NOT NULL,
            valid_until_utc      TEXT NOT NULL,
            source_kind          TEXT NOT NULL CHECK (source_kind IN ('claude-subscription-cli','manual','synthetic-fixture')),
            prompt_version       TEXT NOT NULL,
            reported_cli         TEXT,
            reported_model       TEXT,
            observed_cli_version TEXT,
            observed_model       TEXT,
            imported_at          TEXT NOT NULL,
            record_json          TEXT NOT NULL
        );

        -- Separate table on purpose (spec 4.5, AC-05): the strategy evaluator has no code path that reads it.
        -- 'validated' is unrepresentable until a calibration process exists and a migration relaxes the CHECK.
        CREATE TABLE experimental_probabilities (
            research_record_id INTEGER PRIMARY KEY REFERENCES research_records(id),
            probability_yes    TEXT NOT NULL,
            method             TEXT NOT NULL,
            uncertainty        TEXT NOT NULL,
            validation_status  TEXT NOT NULL CHECK (validation_status = 'unvalidated')
        );

        -- Audit of every Claude CLI research attempt, including refusals and skips.
        CREATE TABLE research_runs (
            id                 INTEGER PRIMARY KEY,
            packet_id          TEXT NOT NULL REFERENCES research_packets(packet_id),
            started_at         TEXT NOT NULL,
            finished_at        TEXT NOT NULL,
            outcome            TEXT NOT NULL CHECK (outcome IN ('refused','failed','imported','skipped')),
            auth_method        TEXT,
            subscription_type  TEXT,
            cli_version        TEXT,
            detail             TEXT,
            stdout_sha256      TEXT,
            stdout             TEXT,
            research_record_id INTEGER REFERENCES research_records(id)
        );

        -- Operator statements the software cannot verify (spec 4.2 step 4), e.g. "usage credits disabled".
        CREATE TABLE billing_attestations (
            id          INTEGER PRIMARY KEY,
            attested_at TEXT NOT NULL,
            statement   TEXT NOT NULL
        );
        CREATE TRIGGER billing_attestations_append_only_u BEFORE UPDATE ON billing_attestations BEGIN SELECT RAISE(ABORT, 'billing_attestations is append-only'); END;
        CREATE TRIGGER billing_attestations_append_only_d BEFORE DELETE ON billing_attestations BEGIN SELECT RAISE(ABORT, 'billing_attestations is append-only'); END;

        -- Paper trading. One data origin per account so fixture and live P&L never blend (spec 8.1).
        CREATE TABLE paper_accounts (
            id             INTEGER PRIMARY KEY,
            name           TEXT NOT NULL UNIQUE,
            data_origin    TEXT NOT NULL,
            created_at     TEXT NOT NULL,
            starting_cash  TEXT NOT NULL
        );

        -- Every evaluation, including abstentions, so coverage and rejection reasons are reportable (spec 8.3).
        CREATE TABLE paper_decisions (
            id                   INTEGER PRIMARY KEY,
            account_id           INTEGER NOT NULL REFERENCES paper_accounts(id),
            decision_id          TEXT NOT NULL,
            strategy             TEXT NOT NULL,
            config_hash          TEXT NOT NULL,
            snapshot_id          INTEGER NOT NULL REFERENCES market_snapshots(id),
            data_origin          TEXT NOT NULL,
            model_id             TEXT,
            forecast_probability TEXT,
            forecast_uncertainty TEXT,
            fee_schedule_id      TEXT,
            outcome              TEXT NOT NULL CHECK (outcome IN ('abstain','candidates')),
            rationale            TEXT NOT NULL,
            created_at           TEXT NOT NULL,
            UNIQUE (account_id, decision_id)
        );

        CREATE TABLE paper_orders (
            id                   INTEGER PRIMARY KEY,
            account_id           INTEGER NOT NULL REFERENCES paper_accounts(id),
            decision_row_id      INTEGER NOT NULL REFERENCES paper_decisions(id),
            client_order_id      TEXT NOT NULL UNIQUE,
            signal_key           TEXT NOT NULL,
            strategy             TEXT NOT NULL,
            ticker               TEXT NOT NULL,
            event_ticker         TEXT NOT NULL,
            side                 TEXT NOT NULL CHECK (side IN ('yes','no')),
            action               TEXT NOT NULL CHECK (action IN ('buy','sell')),
            quantity             INTEGER NOT NULL CHECK (quantity > 0),
            limit_price          TEXT NOT NULL,
            time_in_force        TEXT NOT NULL CHECK (time_in_force = 'ioc'),
            fee_schedule_id      TEXT,
            fee_coefficient      TEXT,
            fee_multiplier       TEXT,
            decision_snapshot_id INTEGER NOT NULL REFERENCES market_snapshots(id),
            data_origin          TEXT NOT NULL,
            created_at           TEXT NOT NULL,
            arrival_at           TEXT NOT NULL,
            status               TEXT NOT NULL CHECK (status IN ('rejected','pending','filled','partially_filled','unfilled','expired_no_evidence')),
            filled_quantity      INTEGER NOT NULL DEFAULT 0,
            fill_snapshot_id     INTEGER REFERENCES market_snapshots(id),
            resolved_at          TEXT,
            reasons              TEXT
        );
        -- One entry per economic signal (spec 7.2, AC-20). Rejected or unfilled attempts do not consume the signal.
        CREATE UNIQUE INDEX ux_paper_orders_signal_entry ON paper_orders (account_id, signal_key)
            WHERE status IN ('pending','filled','partially_filled');

        -- Worst-case cost held from intent until the simulated outcome is known (spec 6.1, AC-06).
        CREATE TABLE risk_reservations (
            id           INTEGER PRIMARY KEY,
            account_id   INTEGER NOT NULL REFERENCES paper_accounts(id),
            order_id     INTEGER NOT NULL UNIQUE REFERENCES paper_orders(id),
            amount       TEXT NOT NULL,
            status       TEXT NOT NULL CHECK (status IN ('held','resolved')),
            consumed     TEXT NOT NULL DEFAULT '0',
            created_at   TEXT NOT NULL,
            resolved_at  TEXT
        );

        CREATE TABLE paper_fills (
            id          INTEGER PRIMARY KEY,
            order_id    INTEGER NOT NULL REFERENCES paper_orders(id),
            snapshot_id INTEGER NOT NULL REFERENCES market_snapshots(id),
            ticker      TEXT NOT NULL,
            side        TEXT NOT NULL,
            action      TEXT NOT NULL,
            quantity    INTEGER NOT NULL CHECK (quantity > 0),
            price       TEXT NOT NULL,
            fee         TEXT NOT NULL,
            filled_at   TEXT NOT NULL,
            simulated   INTEGER NOT NULL CHECK (simulated = 1)
        );
        CREATE TRIGGER paper_fills_append_only_u BEFORE UPDATE ON paper_fills BEGIN SELECT RAISE(ABORT, 'paper_fills is append-only'); END;
        CREATE TRIGGER paper_fills_append_only_d BEFORE DELETE ON paper_fills BEGIN SELECT RAISE(ABORT, 'paper_fills is append-only'); END;

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
        CREATE INDEX ix_ledger_entries_account ON ledger_entries (account_id, id);
        CREATE TRIGGER ledger_entries_append_only_u BEFORE UPDATE ON ledger_entries BEGIN SELECT RAISE(ABORT, 'ledger_entries is append-only'); END;
        CREATE TRIGGER ledger_entries_append_only_d BEFORE DELETE ON ledger_entries BEGIN SELECT RAISE(ABORT, 'ledger_entries is append-only'); END;

        CREATE TABLE paper_settlements (
            account_id   INTEGER NOT NULL REFERENCES paper_accounts(id),
            ticker       TEXT NOT NULL,
            result       TEXT NOT NULL CHECK (result IN ('yes','no')),
            settled_at   TEXT NOT NULL,
            snapshot_id  INTEGER NOT NULL REFERENCES market_snapshots(id),
            PRIMARY KEY (account_id, ticker)
        );

        -- Opening marked equity per America/Chicago reporting day (spec 6.3).
        CREATE TABLE paper_daily_equity (
            account_id     INTEGER NOT NULL REFERENCES paper_accounts(id),
            reporting_day  TEXT NOT NULL,
            opening_equity TEXT NOT NULL,
            recorded_at    TEXT NOT NULL,
            PRIMARY KEY (account_id, reporting_day)
        );
        """),
        (4, """
        -- Series watched as a whole: each capture records every open market in the series (daily markets roll over).
        CREATE TABLE watchlist_series (
            series_ticker TEXT PRIMARY KEY,
            added_at      TEXT NOT NULL
        );
        """),
        (5, """
        -- External evidence (spec 3.1 evidence collector): NWS point forecasts with publication and retrieval times kept
        -- separate (spec 8.2), so later evaluation uses only what was available at decision time.
        CREATE TABLE forecast_runs (
            id           INTEGER PRIMARY KEY,
            started_at   TEXT NOT NULL,
            finished_at  TEXT,
            status       TEXT NOT NULL CHECK (status IN ('running','completed','failed')),
            source       TEXT NOT NULL,
            data_origin  TEXT NOT NULL,
            location     TEXT NOT NULL,
            detail       TEXT
        );

        CREATE TABLE forecast_raw (
            id            INTEGER PRIMARY KEY,
            run_id        INTEGER NOT NULL REFERENCES forecast_runs(id),
            url           TEXT NOT NULL,
            http_status   INTEGER NOT NULL,
            requested_at  TEXT NOT NULL,
            received_at   TEXT NOT NULL,
            server_date   TEXT,
            body_sha256   TEXT NOT NULL,
            body          TEXT NOT NULL
        );
        CREATE TRIGGER forecast_raw_append_only_u BEFORE UPDATE ON forecast_raw BEGIN SELECT RAISE(ABORT, 'forecast_raw is append-only'); END;
        CREATE TRIGGER forecast_raw_append_only_d BEFORE DELETE ON forecast_raw BEGIN SELECT RAISE(ABORT, 'forecast_raw is append-only'); END;

        -- One row per forecast period per retrieved vintage. update_time = NWS publication; received_at = our retrieval.
        CREATE TABLE nws_forecast_periods (
            id               INTEGER PRIMARY KEY,
            run_id           INTEGER NOT NULL REFERENCES forecast_runs(id),
            raw_id           INTEGER NOT NULL REFERENCES forecast_raw(id),
            data_origin      TEXT NOT NULL,
            location         TEXT NOT NULL,
            grid             TEXT NOT NULL,
            generated_at     TEXT,
            update_time      TEXT,
            received_at      TEXT NOT NULL,
            period_number    INTEGER NOT NULL,
            name             TEXT NOT NULL,
            start_time       TEXT NOT NULL,
            end_time         TEXT NOT NULL,
            local_date       TEXT NOT NULL,
            is_daytime       INTEGER NOT NULL,
            temperature      INTEGER NOT NULL,
            temperature_unit TEXT NOT NULL,
            short_forecast   TEXT,
            UNIQUE (raw_id, period_number)
        );
        CREATE INDEX ix_nws_periods_date ON nws_forecast_periods (location, local_date, is_daytime, received_at);
        """),
        (6, """
        -- Strike fields from the market payload (Phase 3 bucket edges, docs/PHASE3_PLAN.md W1). Parsed values are stored
        -- beside the raw body, which stays the record. Existing snapshots are backfilled from their stored raw market body.
        ALTER TABLE market_snapshots ADD COLUMN strike_type  TEXT;
        ALTER TABLE market_snapshots ADD COLUMN floor_strike TEXT;
        ALTER TABLE market_snapshots ADD COLUMN cap_strike   TEXT;

        UPDATE market_snapshots SET
            strike_type  = (SELECT json_extract(r.body, '$.market.strike_type')                  FROM raw_responses r WHERE r.id = market_snapshots.raw_market_id AND json_valid(r.body)),
            floor_strike = (SELECT CAST(json_extract(r.body, '$.market.floor_strike') AS TEXT)   FROM raw_responses r WHERE r.id = market_snapshots.raw_market_id AND json_valid(r.body)),
            cap_strike   = (SELECT CAST(json_extract(r.body, '$.market.cap_strike')   AS TEXT)   FROM raw_responses r WHERE r.id = market_snapshots.raw_market_id AND json_valid(r.body));
        """),
        (7, """
        -- Truth data for Phase 3 (docs/PHASE3_PLAN.md W2): what each market day actually settled on, and the NWS climate
        -- report high used to cross-check it. Lives in its own database (origin 'archive:truth'), never in a recording DB.
        CREATE TABLE truth_runs (
            id          INTEGER PRIMARY KEY,
            started_at  TEXT NOT NULL,
            finished_at TEXT,
            status      TEXT NOT NULL CHECK (status IN ('running','completed','partial','failed','abandoned')),
            source      TEXT NOT NULL CHECK (source IN ('kalshi','iem-cli')),
            data_origin TEXT NOT NULL,
            scope       TEXT NOT NULL,
            detail      TEXT
        );

        CREATE TABLE truth_raw (
            id            INTEGER PRIMARY KEY,
            run_id        INTEGER NOT NULL REFERENCES truth_runs(id),
            url           TEXT NOT NULL,
            http_status   INTEGER NOT NULL,
            requested_at  TEXT NOT NULL,
            received_at   TEXT NOT NULL,
            server_date   TEXT,
            body_sha256   TEXT NOT NULL,
            body          TEXT NOT NULL
        );
        CREATE TRIGGER truth_raw_no_update BEFORE UPDATE ON truth_raw BEGIN SELECT RAISE(ABORT, 'truth_raw is append-only'); END;
        CREATE TRIGGER truth_raw_no_delete BEFORE DELETE ON truth_raw BEGIN SELECT RAISE(ABORT, 'truth_raw is append-only'); END;

        -- One row per event per distinct vintage. A revised value or regime appends a new row; the latest id is current.
        -- raw_id is the first fetched page that held a market of the event. expiration_value is NULL only for 'conflict'.
        CREATE TABLE settled_values (
            id                   INTEGER PRIMARY KEY,
            run_id               INTEGER NOT NULL REFERENCES truth_runs(id),
            raw_id               INTEGER NOT NULL REFERENCES truth_raw(id),
            data_origin          TEXT NOT NULL,
            series_ticker        TEXT NOT NULL,
            event_ticker         TEXT NOT NULL,
            climate_date         TEXT NOT NULL,
            expiration_value     TEXT,
            regime               TEXT NOT NULL,
            markets_seen         INTEGER NOT NULL,
            markets_checked      INTEGER NOT NULL,
            markets_inconsistent INTEGER NOT NULL,
            status               TEXT NOT NULL CHECK (status IN ('ok','conflict','inconsistent')),
            received_at          TEXT NOT NULL,
            CHECK ((status = 'conflict') = (expiration_value IS NULL)),
            UNIQUE (run_id, event_ticker)
        );
        CREATE INDEX ix_settled_values_event ON settled_values (event_ticker, id);
        CREATE INDEX ix_settled_values_date  ON settled_values (climate_date);
        CREATE TRIGGER settled_values_no_update BEFORE UPDATE ON settled_values BEGIN SELECT RAISE(ABORT, 'settled_values is append-only'); END;
        CREATE TRIGGER settled_values_no_delete BEFORE DELETE ON settled_values BEGIN SELECT RAISE(ABORT, 'settled_values is append-only'); END;

        -- NWS climate report daily highs (IEM archive of the CLI products). Appended per vintage like settled_values.
        CREATE TABLE cli_highs (
            id           INTEGER PRIMARY KEY,
            run_id       INTEGER NOT NULL REFERENCES truth_runs(id),
            raw_id       INTEGER NOT NULL REFERENCES truth_raw(id),
            data_origin  TEXT NOT NULL,
            station      TEXT NOT NULL,
            climate_date TEXT NOT NULL,
            high         TEXT NOT NULL,
            product      TEXT,
            received_at  TEXT NOT NULL,
            UNIQUE (run_id, station, climate_date)
        );
        CREATE INDEX ix_cli_highs_day ON cli_highs (station, climate_date, id);
        CREATE TRIGGER cli_highs_no_update BEFORE UPDATE ON cli_highs BEGIN SELECT RAISE(ABORT, 'cli_highs is append-only'); END;
        CREATE TRIGGER cli_highs_no_delete BEFORE DELETE ON cli_highs BEGIN SELECT RAISE(ABORT, 'cli_highs is append-only'); END;
        """),
        (8, """
        -- Historical inputs for Phase 3 Stage A (docs/PHASE3_PLAN.md W3): GFS MOS forecast runs from IEM and Kalshi hourly
        -- candlesticks. Lives in its own database (origin 'archive:history'), apart from truth.db and any recording DB.
        CREATE TABLE archive_runs (
            id          INTEGER PRIMARY KEY,
            started_at  TEXT NOT NULL,
            finished_at TEXT,
            status      TEXT NOT NULL CHECK (status IN ('running','completed','partial','failed','abandoned')),
            source      TEXT NOT NULL CHECK (source IN ('iem-mos','kalshi-candles')),
            data_origin TEXT NOT NULL,
            scope       TEXT NOT NULL,
            detail      TEXT
        );

        CREATE TABLE archive_raw (
            id            INTEGER PRIMARY KEY,
            run_id        INTEGER NOT NULL REFERENCES archive_runs(id),
            url           TEXT NOT NULL,
            http_status   INTEGER NOT NULL,
            requested_at  TEXT NOT NULL,
            received_at   TEXT NOT NULL,
            server_date   TEXT,
            body_sha256   TEXT NOT NULL,
            body          TEXT NOT NULL
        );
        CREATE TRIGGER archive_raw_no_update BEFORE UPDATE ON archive_raw BEGIN SELECT RAISE(ABORT, 'archive_raw is append-only'); END;
        CREATE TRIGGER archive_raw_no_delete BEFORE DELETE ON archive_raw BEGIN SELECT RAISE(ABORT, 'archive_raw is append-only'); END;

        -- One row per MOS run and forecast time. runtime is the model cycle (UTC), NOT when the bulletin was published:
        -- publication time is not in the archive (see the plan), so consumers must apply an explicit availability lag.
        CREATE TABLE mos_rows (
            id          INTEGER PRIMARY KEY,
            run_id      INTEGER NOT NULL REFERENCES archive_runs(id),
            raw_id      INTEGER NOT NULL REFERENCES archive_raw(id),
            data_origin TEXT NOT NULL,
            station     TEXT NOT NULL,
            model       TEXT NOT NULL,
            runtime     TEXT NOT NULL,
            ftime       TEXT NOT NULL,
            n_x         TEXT,
            tmp         TEXT,
            UNIQUE (station, model, runtime, ftime)
        );
        CREATE INDEX ix_mos_rows_run ON mos_rows (station, model, runtime);
        CREATE TRIGGER mos_rows_no_update BEFORE UPDATE ON mos_rows BEGIN SELECT RAISE(ABORT, 'mos_rows is append-only'); END;
        CREATE TRIGGER mos_rows_no_delete BEFORE DELETE ON mos_rows BEGIN SELECT RAISE(ABORT, 'mos_rows is append-only'); END;

        -- One row per market and fetched window: the completion marker that makes a bulk fetch resumable. A window with
        -- zero candles is still recorded (the market had no trades or quotes in it).
        CREATE TABLE candle_fetches (
            id             INTEGER PRIMARY KEY,
            run_id         INTEGER NOT NULL REFERENCES archive_runs(id),
            raw_id         INTEGER NOT NULL REFERENCES archive_raw(id),
            data_origin    TEXT NOT NULL,
            market_ticker  TEXT NOT NULL,
            event_ticker   TEXT NOT NULL,
            window_start   TEXT NOT NULL,
            window_end     TEXT NOT NULL,
            period_minutes INTEGER NOT NULL,
            endpoint       TEXT NOT NULL CHECK (endpoint IN ('series','historical')),
            candle_count   INTEGER NOT NULL,
            received_at    TEXT NOT NULL,
            UNIQUE (market_ticker, window_start, window_end, period_minutes)
        );
        CREATE TRIGGER candle_fetches_no_update BEFORE UPDATE ON candle_fetches BEGIN SELECT RAISE(ABORT, 'candle_fetches is append-only'); END;
        CREATE TRIGGER candle_fetches_no_delete BEFORE DELETE ON candle_fetches BEGIN SELECT RAISE(ABORT, 'candle_fetches is append-only'); END;

        -- Hourly bid/ask candles. end_period_ts is the end of the hour the candle covers (UTC). These are quote closes,
        -- not executable depth: usable for a probability baseline, never for fills.
        CREATE TABLE candles (
            id             INTEGER PRIMARY KEY,
            fetch_id       INTEGER NOT NULL REFERENCES candle_fetches(id),
            data_origin    TEXT NOT NULL,
            market_ticker  TEXT NOT NULL,
            end_period_ts  TEXT NOT NULL,
            yes_bid_close  TEXT,
            yes_ask_close  TEXT,
            price_close    TEXT,
            volume         TEXT,
            open_interest  TEXT,
            UNIQUE (market_ticker, end_period_ts)
        );
        CREATE TRIGGER candles_no_update BEFORE UPDATE ON candles BEGIN SELECT RAISE(ABORT, 'candles is append-only'); END;
        CREATE TRIGGER candles_no_delete BEFORE DELETE ON candles BEGIN SELECT RAISE(ABORT, 'candles is append-only'); END;
        """),
    ];

    public static int LatestVersion => Migrations[^1].Version;

    public static void Migrate(Db db, int maxVersion = int.MaxValue)
    {
        db.Exec("CREATE TABLE IF NOT EXISTS schema_migrations (version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL);");
        var current = db.Scalar<long?>("SELECT MAX(version) FROM schema_migrations") ?? 0;
        foreach (var (version, sql) in Migrations.Where(m => m.Version > current && m.Version <= maxVersion))
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
