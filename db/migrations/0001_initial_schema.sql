-- 0001: tables for feed snapshots, per-stop delay observations and cancelled trips.
--
-- The schema is SQL-first: these files are the source of truth, applied in order by
-- SeqTransit.Migrator. EF Core maps onto these tables for reads; it does not own migrations.

-- One row per distinct GTFS-RT feed version that has been loaded.
-- The primary key makes loading idempotent: a snapshot can never be counted twice.
CREATE TABLE feed_snapshot (
    feed_timestamp       timestamptz PRIMARY KEY,
    s3_key               text        NOT NULL,
    loaded_at            timestamptz NOT NULL DEFAULT now(),
    trip_update_count    integer     NOT NULL,
    stop_update_count    integer     NOT NULL,
    cancelled_trip_count integer     NOT NULL
);

-- The latest known delay for each stop of each trip on a service day.
-- Every snapshot re-predicts upcoming stops, so a row is overwritten until the vehicle
-- has passed the stop; the value left behind is the best estimate of what happened.
CREATE TABLE stop_delay_observation (
    service_date      date        NOT NULL,
    trip_id           text        NOT NULL,
    stop_sequence     integer     NOT NULL,
    route_id          text        NOT NULL,
    stop_id           text        NOT NULL,
    vehicle_id        text,
    arrival_delay_s   integer,
    departure_delay_s integer,
    event_time        timestamptz,             -- predicted (later: actual) arrival or departure time
    first_seen_at     timestamptz NOT NULL,    -- feed timestamp when this stop was first seen
    last_feed_at      timestamptz NOT NULL,    -- feed timestamp of the values stored now
    PRIMARY KEY (service_date, trip_id, stop_sequence)
);

CREATE INDEX ix_stop_delay_route_date ON stop_delay_observation (route_id, service_date);
CREATE INDEX ix_stop_delay_stop_date  ON stop_delay_observation (stop_id, service_date);
CREATE INDEX ix_stop_delay_event_time ON stop_delay_observation (event_time);

-- Trips the feed marked as CANCELED at least once on a service day.
CREATE TABLE trip_cancellation (
    service_date  date        NOT NULL,
    trip_id       text        NOT NULL,
    route_id      text        NOT NULL,
    first_seen_at timestamptz NOT NULL,
    last_feed_at  timestamptz NOT NULL,
    PRIMARY KEY (service_date, trip_id)
);

CREATE INDEX ix_trip_cancellation_route_date ON trip_cancellation (route_id, service_date);
