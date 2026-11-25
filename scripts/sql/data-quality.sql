-- psql -v audit_date=YYYY-MM-DD; all results share a consistent, read-only database snapshot.
\pset pager off
BEGIN ISOLATION LEVEL REPEATABLE READ READ ONLY;
SET LOCAL statement_timeout = '60s';
SELECT now() AS measured_at, :'audit_date'::date AS service_date;
SELECT version, applied_at FROM schema_migration ORDER BY version;
WITH snapshots AS (
  SELECT feed_timestamp, lag(feed_timestamp) OVER (ORDER BY feed_timestamp) AS previous
  FROM feed_snapshot
)
SELECT (feed_timestamp AT TIME ZONE 'Australia/Brisbane')::date AS day, count(*) AS snapshots,
       min(feed_timestamp AT TIME ZONE 'Australia/Brisbane') AS first_local,
       max(feed_timestamp AT TIME ZONE 'Australia/Brisbane') AS last_local,
       count(DISTINCT date_trunc('hour', feed_timestamp AT TIME ZONE 'Australia/Brisbane')) AS hours,
       max(extract(epoch FROM feed_timestamp - previous)) AS max_gap_seconds
FROM snapshots GROUP BY 1 ORDER BY 1;
SELECT service_date, count(*) AS raw_rows, count(DISTINCT route_id) AS routes
FROM stop_delay_observation GROUP BY 1 ORDER BY 1;
SELECT count(*) AS raw_rows,
       count(*) FILTER (WHERE event_time IS NULL) AS no_event_time,
       count(*) FILTER (WHERE coalesce(arrival_delay_s, departure_delay_s) IS NULL) AS no_delay,
       count(*) FILTER (WHERE abs(coalesce(arrival_delay_s, departure_delay_s)::bigint) > 3600) AS absurd_delay,
       count(*) FILTER (WHERE event_time >= now()) AS future_events
FROM stop_delay_observation WHERE service_date = :'audit_date'::date;
SELECT count(*) AS trusted_passed_stops, count(DISTINCT route_id) AS routes,
       round(avg((delay_s BETWEEN -60 AND 300)::int),4) AS on_time_rate,
       percentile_cont(ARRAY[0.05,0.5,0.95]) WITHIN GROUP (ORDER BY delay_s) AS p5_p50_p95_seconds
FROM reliable_stop_observation WHERE service_date = :'audit_date'::date AND event_time < now();
SELECT coalesce(r.route_type,-1) AS route_type, o.vehicle_id IS NOT NULL AS tracked, count(*) AS stops,
       round(avg((coalesce(o.arrival_delay_s,o.departure_delay_s)=0)::int),4) AS share_exactly_zero
FROM stop_delay_observation o LEFT JOIN gtfs_route r USING(route_id)
WHERE o.service_date = :'audit_date'::date AND o.event_time < now() GROUP BY 1,2 ORDER BY 1,2;
SELECT extract(hour FROM event_time AT TIME ZONE 'Australia/Brisbane') AS hour, count(*) AS stops,
       round(avg((delay_s BETWEEN -60 AND 300)::int),4) AS on_time_rate
FROM reliable_stop_observation WHERE service_date = :'audit_date'::date AND event_time < now()
GROUP BY 1 ORDER BY 1;
SELECT (SELECT count(*) FROM gtfs_route) AS static_routes, (SELECT count(*) FROM gtfs_stop) AS static_stops,
       (SELECT count(*) FROM gtfs_trip) AS static_trips;
SELECT o.route_id, count(*) AS observations_without_static_route
FROM stop_delay_observation o LEFT JOIN gtfs_route r USING(route_id)
WHERE o.service_date = :'audit_date'::date AND r.route_id IS NULL GROUP BY 1 ORDER BY 2 DESC;
SELECT service_date, count(*) AS feed_reported_cancelled_trips FROM trip_cancellation GROUP BY 1 ORDER BY 1;
COMMIT;
