# Architecture

## Data flow

| Step | Component | Trigger | Reads | Writes |
| --- | --- | --- | --- | --- |
| Fetch | `FetchFunction` (Lambda, 512 MB) | EventBridge Scheduler, every minute | Translink TripUpdates feed | `s3://<snapshots>/trip-updates/v1/<unix-seconds>.pb` |
| Load | `LoadFunction` (Lambda, 1 GB) | EventBridge Scheduler, every 30 minutes | S3 snapshots after the watermark | `stop_delay_observation`, `trip_cancellation`, `feed_snapshot` |
| Serve | `ApiFunction` (ASP.NET Core on Lambda) | API Gateway HTTP API via CloudFront `/api/*` | Aurora (read-only role) | — |
| Show | React dashboard | CloudFront `/` from a private S3 bucket | `/api/*` | — |

## Tables

- `feed_snapshot`: one row per loaded feed version. Its maximum timestamp is the load watermark.
- `stop_delay_observation`: the latest delay for each stop of each trip on a service day, keyed by
  `(service_date, trip_id, stop_sequence)`. Rows are overwritten until the vehicle passes the stop.
- `trip_cancellation`: trips the feed marked `CANCELED` or `DELETED`.
- `schema_migration`: which migration files have been applied, with checksums.

## Failure handling

| Failure | What happens | How you find out |
| --- | --- | --- |
| Feed unreachable or 5xx | Fetch retries twice (2 s, 5 s), then fails; the next minute tries again | `NoSnapshotsAlarm` if fewer than 3 snapshots are stored in 5 minutes |
| Feed returns something that isn't protobuf | Nothing is stored | Lambda error, then `NoSnapshotsAlarm` |
| Feed stops updating its timestamp | The same key is rewritten each minute | `FeedStaleAlarm` |
| Load fails halfway | The transaction rolls back; the next run reloads the same snapshots | `LoadErrorsAlarm` after two failed runs |
| Load falls behind | Each run loops through batches until caught up or near its time limit | `LOAD_BACKLOG` log line |
| Database paused | First connection waits while Aurora resumes (connect timeout is 30 s) | Slower first request |

## Configuration

| Setting | Where | Default |
| --- | --- | --- |
| On-time window | API `OnTime:EarlyToleranceSeconds` / `LateToleranceSeconds` | 60 / 300 |
| Load interval | CDK `loadIntervalMinutes` | 30 |
| Raw snapshot retention | CDK `rawSnapshotRetentionDays` | 14 |
| Stale-data threshold | API `Health:StaleAfterMinutes` (set by CDK) | 2 × load interval + 15 |
| Monthly budget alert | CDK `monthlyBudgetUsd` + `alertEmail` | 10 USD |
