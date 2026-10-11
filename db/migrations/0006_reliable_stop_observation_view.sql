-- 0006: one definition of "an observation we trust" for every reliability query.
--
-- Before this view, the same filter (has a time, has a delay, delay within one hour either way)
-- was repeated in the LINQ queries and in four raw SQL queries. To change a data rule, add a new
-- migration with CREATE OR REPLACE VIEW; the API queries pick it up without code changes.
-- The "vehicle has already passed" condition (event_time < now) stays in the queries, because
-- "now" is a parameter there (tests pass a fixed clock).
--
-- See docs/decisions.md, decision 9, for why delays beyond an hour are dropped.

CREATE VIEW reliable_stop_observation AS
SELECT
    service_date,
    trip_id,
    stop_sequence,
    route_id,
    stop_id,
    vehicle_id,
    event_time,
    coalesce(arrival_delay_s, departure_delay_s) AS delay_s
FROM stop_delay_observation
WHERE event_time IS NOT NULL
  AND coalesce(arrival_delay_s, departure_delay_s) BETWEEN -3600 AND 3600;

GRANT SELECT ON reliable_stop_observation TO seq_api, seq_ingest;
