-- 0009: keep per-stop detail for 90 days, and daily figures for every day after that.
--
-- A full weekday is about 540,000 trip-stop rows (about 190 MB with indexes), so detail kept
-- forever would grow by roughly 70 GB a year. The load function now archives each service day
-- once it is older than the retention window (DETAIL_RETENTION_DAYS, default 90): it writes the
-- day's per-route, per-stop totals into stop_daily_stats and deletes the detail rows, in one
-- transaction. See docs/decisions.md, decision 14.
--
-- The on-time count is frozen with the window in force when the day was archived; the window is
-- stored on each row so a later change of OnTime settings is visible, not silent.

CREATE TABLE stop_daily_stats (
    service_date      date    NOT NULL,
    route_id          text    NOT NULL,
    stop_id           text    NOT NULL,
    observed_stops    integer NOT NULL,
    on_time_stops     integer NOT NULL,
    total_delay_s     bigint  NOT NULL,
    early_tolerance_s integer NOT NULL,
    late_tolerance_s  integer NOT NULL,
    PRIMARY KEY (service_date, route_id, stop_id)
);

CREATE INDEX ix_stop_daily_stats_route_date ON stop_daily_stats (route_id, service_date);

GRANT SELECT, INSERT, UPDATE ON stop_daily_stats TO seq_ingest;
GRANT SELECT ON stop_daily_stats TO seq_api;
GRANT DELETE ON stop_delay_observation TO seq_ingest;

-- Per day, route and stop: totals from the detail (trusted, already passed) plus archived days.
-- Every reliability query reads this, so a day reports the same numbers before and after it is
-- archived. A plain SQL function, so the planner inlines it and still uses the indexes.
CREATE FUNCTION stop_day_figures(
    from_date date,
    to_date   date,
    as_of     timestamptz,
    min_delay integer,
    max_delay integer)
RETURNS TABLE (
    service_date   date,
    route_id       text,
    stop_id        text,
    observed_stops bigint,
    on_time_stops  bigint,
    total_delay_s  bigint)
LANGUAGE sql STABLE
AS $$
    SELECT o.service_date, o.route_id, o.stop_id,
           count(*),
           count(*) FILTER (WHERE o.delay_s >= min_delay AND o.delay_s <= max_delay),
           sum(o.delay_s)
    FROM reliable_stop_observation o
    WHERE o.service_date >= from_date
      AND o.service_date <= to_date
      AND o.event_time < as_of
    GROUP BY o.service_date, o.route_id, o.stop_id
    UNION ALL
    SELECT a.service_date, a.route_id, a.stop_id,
           a.observed_stops::bigint, a.on_time_stops::bigint, a.total_delay_s
    FROM stop_daily_stats a
    WHERE a.service_date >= from_date
      AND a.service_date <= to_date
$$;
