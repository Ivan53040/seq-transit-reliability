-- 0002: least-privilege database roles for the two Lambda functions.
--
-- seq_ingest: the load function (writes observations).
-- seq_api:    the web API (read-only).
--
-- On Aurora the roles sign in with IAM authentication (rds_iam), so neither has a password.
-- Locally (Docker Postgres) rds_iam does not exist and the app connects as the postgres user.

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'seq_ingest') THEN
        CREATE ROLE seq_ingest LOGIN;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'seq_api') THEN
        CREATE ROLE seq_api LOGIN;
    END IF;
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'rds_iam') THEN
        GRANT rds_iam TO seq_ingest;
        GRANT rds_iam TO seq_api;
    END IF;
END
$$;

GRANT USAGE ON SCHEMA public TO seq_ingest, seq_api;

GRANT SELECT, INSERT, UPDATE ON feed_snapshot, stop_delay_observation, trip_cancellation TO seq_ingest;
GRANT SELECT ON feed_snapshot, stop_delay_observation, trip_cancellation TO seq_api;
