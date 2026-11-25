-- Representative SQL shapes from ReliabilityQueries; not a replacement for API latency measurements.
\pset pager off
BEGIN ISOLATION LEVEL REPEATABLE READ READ ONLY;
SET LOCAL statement_timeout = '60s';
SELECT min(service_date) AS first_service_date, max(service_date) AS last_service_date,
       count(*) AS total_rows FROM stop_delay_observation;
SELECT route_id AS audit_route FROM reliable_stop_observation
WHERE service_date = :'audit_date'::date AND event_time < now()
GROUP BY 1 ORDER BY count(*) DESC LIMIT 1 \gset
\echo Route summary (seven-day date range; actual data volume shown above)
EXPLAIN (ANALYZE, BUFFERS)
SELECT count(*)::int, count(*) FILTER (WHERE delay_s BETWEEN -60 AND 300)::int, avg(delay_s),
       percentile_cont(0.5) WITHIN GROUP (ORDER BY delay_s),
       percentile_cont(0.9) WITHIN GROUP (ORDER BY delay_s)
FROM reliable_stop_observation WHERE route_id = :'audit_route'
AND service_date BETWEEN :'audit_date'::date - 6 AND :'audit_date'::date AND event_time < now();
\echo Hourly route performance
EXPLAIN (ANALYZE, BUFFERS)
SELECT extract(hour FROM event_time AT TIME ZONE 'Australia/Brisbane')::int, count(*)::int,
       count(*) FILTER (WHERE delay_s BETWEEN -60 AND 300)::int, avg(delay_s)
FROM reliable_stop_observation WHERE route_id = :'audit_route'
AND service_date = :'audit_date'::date AND event_time < now() GROUP BY 1 ORDER BY 1;
\echo Worst stops (minimum 20)
EXPLAIN (ANALYZE, BUFFERS)
SELECT o.stop_id, coalesce(s.stop_name,o.stop_id), s.stop_lat, s.stop_lon, count(*)::int,
       count(*) FILTER (WHERE o.delay_s BETWEEN -60 AND 300)::int,
       round(avg(o.delay_s)::numeric,1)::float8 AS average_delay
FROM reliable_stop_observation o LEFT JOIN gtfs_stop s USING(stop_id)
WHERE o.service_date = :'audit_date'::date AND o.event_time < now()
GROUP BY o.stop_id,s.stop_name,s.stop_lat,s.stop_lon HAVING count(*) >= 20 ORDER BY average_delay DESC LIMIT 20;
\echo Mode performance
EXPLAIN (ANALYZE, BUFFERS)
SELECT coalesce(r.route_type,-1)::int, count(*)::int,
       count(*) FILTER (WHERE o.delay_s BETWEEN -60 AND 300)::int, round(avg(o.delay_s)::numeric,1)::float8
FROM reliable_stop_observation o LEFT JOIN gtfs_route r USING(route_id)
WHERE o.service_date = :'audit_date'::date AND o.event_time < now() GROUP BY 1 ORDER BY 1;
COMMIT;
