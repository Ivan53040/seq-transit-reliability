-- 0008: accurate row estimates and in-memory sorts for the full-day queries.
--
-- Measured on Aurora on 2026-11-25 (docs/evidence/2026-11-25/query-plans.txt): the planner
-- estimated 337 rows where 53,554 matched (about 160x too few), because the view filtered on the
-- expression coalesce(arrival_delay_s, departure_delay_s), which has no statistics. With that
-- estimate /api/stops/worst chose a nested loop over gtfs_stop and took about 0.8 s, and every
-- full-day GROUP BY spilled its sort to disk under the default 4 MB work_mem.
--
-- 1. Store the delay as a generated column, so ANALYZE keeps statistics on it and the view's
--    filter is estimated correctly. The table is rewritten once (about 60 MB at this point).
-- 2. Point the view at that column. Same columns, same names, so nothing reading it changes.
-- 3. Give the two app roles 16 MB of work_mem (per sort, per connection; the API pool is 2).

ALTER TABLE stop_delay_observation
    ADD COLUMN delay_s integer GENERATED ALWAYS AS (coalesce(arrival_delay_s, departure_delay_s)) STORED;

CREATE OR REPLACE VIEW reliable_stop_observation AS
SELECT
    service_date,
    trip_id,
    stop_sequence,
    route_id,
    stop_id,
    vehicle_id,
    event_time,
    delay_s
FROM stop_delay_observation
WHERE event_time IS NOT NULL
  AND delay_s BETWEEN -3600 AND 3600;

ALTER ROLE seq_api SET work_mem = '16MB';
ALTER ROLE seq_ingest SET work_mem = '16MB';

ANALYZE stop_delay_observation;
