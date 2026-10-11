-- 0003: static GTFS tables for routes, stops, and trips.

CREATE TABLE gtfs_route (
    route_id         text    PRIMARY KEY,
    route_short_name text    NOT NULL,
    route_long_name  text    NOT NULL,
    route_type       integer NOT NULL
);

CREATE TABLE gtfs_stop (
    stop_id   text             PRIMARY KEY,
    stop_name text             NOT NULL,
    stop_lat  double precision,
    stop_lon  double precision
);

CREATE TABLE gtfs_trip (
    trip_id       text    PRIMARY KEY,
    route_id      text    NOT NULL,
    trip_headsign text,
    direction_id  integer
);

CREATE INDEX ix_gtfs_trip_route_id ON gtfs_trip (route_id);

GRANT SELECT, INSERT, UPDATE, DELETE, TRUNCATE ON gtfs_route, gtfs_stop, gtfs_trip TO seq_ingest;
GRANT SELECT ON gtfs_route, gtfs_stop, gtfs_trip TO seq_api;
