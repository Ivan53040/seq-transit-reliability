-- 0005: let the load function withdraw a cancellation when Translink reinstates the trip.
--
-- The loader deletes a trip_cancellation row when a newer snapshot shows the same trip running.

GRANT DELETE ON trip_cancellation TO seq_ingest;
