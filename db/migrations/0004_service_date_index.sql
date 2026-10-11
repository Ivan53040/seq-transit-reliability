-- 0004: index for queries that filter on the service day alone.
--
-- The board (/api/routes/on-time), /api/stops/worst and /api/modes/on-time select one service
-- day across every route. The indexes from 0001 all lead with route_id, stop_id or event_time,
-- so those queries scanned the whole table. event_time is included so the "already passed"
-- condition (event_time < now) is answered from the index.

CREATE INDEX ix_stop_delay_date_event_time ON stop_delay_observation (service_date, event_time);
