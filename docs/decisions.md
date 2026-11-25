# Design decisions

Each entry records what was chosen, why, and what it costs. Add new entries as the project changes.

## 1. Aurora PostgreSQL with express configuration, and no VPC

**Choice.** The database is an Aurora PostgreSQL serverless cluster created with express configuration. It sits
outside any VPC and is reached through Aurora's internet access gateway, which only accepts IAM authentication.

**Why.** A Lambda function inside a VPC needs a NAT gateway to reach the internet (here: the Translink feed),
and a NAT gateway costs tens of dollars a month before any traffic. With the database outside a VPC, none of
the functions need one.

**Trade-offs.** Express clusters can't be joined to a VPC later, don't support RDS Proxy, and only allow IAM
sign-in. The cluster is created by a script rather than the CDK stack, because express configuration is newer
than the CDK constructs; the stack takes the endpoint and resource ID as settings.

## 2. Raw snapshots in S3, loaded into the database in batches

**Choice.** The fetch function stores every snapshot in S3 every minute. A separate load function writes to the
database every 30 minutes (`loadIntervalMinutes`).

**Why.** Aurora Serverless can pause when idle, and is billed by the second while awake. Writing every minute
would keep it awake all day. Keeping raw snapshots also means the data can be reprocessed if the parsing or
punctuality rules change.

**Trade-off.** The dashboard can be up to one load interval behind. For a reliability report (not live arrival
times) that is acceptable. The cost model is roughly:

```
awake fraction ≈ (load duration + auto-pause delay) / load interval   (+ time woken by API requests)
```

With a 5-minute auto-pause and a 1–2 minute load, a 30-minute interval keeps the database awake about a
quarter of the time; 15 minutes keeps it awake about half the time. API requests also wake it, which is why
CloudFront caches `/api/*` for 60 seconds.

## 3. Idempotent, order-independent loading

**Choice.** Snapshot keys are derived from the feed's own timestamp; the loader's watermark is
`max(feed_timestamp)` in the database; each batch is saved in one transaction; upserts only apply when
`EXCLUDED.last_feed_at > existing.last_feed_at`.

**Why.** Scheduled jobs get retried, overlap, and fail halfway. With these rules, running a load twice, or
loading snapshots out of order, gives the same database state. Tests cover each case.

**Known gap.** A snapshot written to S3 *after* the watermark has moved past its timestamp (for example, a very
late retry) is skipped. Its data is almost always superseded by newer snapshots anyway.

## 4. Fold in memory, then one bulk upsert

**Choice.** `ObservationFold` merges a batch of snapshots into one row per (service date, trip, stop sequence),
then `ObservationWriter` sends each column as an array and expands it with `unnest`.

**Why.** One statement per batch instead of one per row or per snapshot. Folding also guarantees unique keys,
which PostgreSQL requires for `INSERT ... ON CONFLICT DO UPDATE`. A 50,000-row batch upserts in under a second
on a small local PostgreSQL.

## 5. SQL-first schema; EF Core for reads

**Choice.** `db/migrations/*.sql` define the schema and are applied by a small migrator that records checksums.
EF Core maps onto those tables for the API's queries.

**Why.** The bulk upsert relies on PostgreSQL features (`unnest`, `ON CONFLICT ... WHERE`) that are clearer in
SQL, and plain SQL migrations are easy to review. EF Core still gives typed, testable queries for the API.

## 6. Least-privilege database roles with IAM sign-in

**Choice.** Two roles: `seq_ingest` (insert/update) for the load function and `seq_api` (select) for the API.
Each Lambda's IAM policy allows `rds-db:connect` for its own role only. Only migrations use the admin user.

**Why.** No password exists to leak, and a bug in the API can't modify data.

## 7. Brisbane time as a fixed UTC+10 offset

**Choice.** Service dates fall back to the Brisbane date of the feed timestamp using a fixed +10:00 offset.

**Why.** Queensland has no daylight saving, so the offset is exact, and it avoids depending on time zone data
in the Lambda image. The feed's own `start_date` is used whenever it is present, which also handles trips that
run past midnight.

## 8. CDK in TypeScript; functions on .NET 10 and Graviton

**Choice.** Infrastructure is a TypeScript CDK app with assertion tests. Functions use the .NET 10 managed
runtime on arm64.

**Why.** TypeScript is the CDK's most widely used language and the dashboard already uses it. arm64 Lambda is
cheaper per GB-second than x86.

## 9. Data quality rules: drop absurd delays beyond 1 hour; do not filter untracked trips

**Choice.**
1. Observations where `|coalesce(arrival_delay_s, departure_delay_s)| > 3600` (over 1 hour early or late) are excluded from punctuality calculations. The rule lives in the `reliable_stop_observation` view (migration 0006), which every reliability query reads; change it with a new migration that replaces the view.
2. Trips without a `vehicle_id` are retained, rather than dropped.

**Why (discovered in real Translink SEQ data):**
- **Train compatibility:** Queensland Rail suburban and interurban train routes (`CLSH`, `SPRP`, `CAIP`, `IPNA`, `BRBD`, etc., operator 5167) mostly do not report a `vehicle_id` in Translink's GTFS-RT feed (in a sample of 10 snapshots on a Sunday evening, about 89% of their stop updates had none), but they DO report authentic, variable arrival and departure delays. Dropping trips without `vehicle_id` would eliminate most train services across South East Queensland.
- **Absurd delay rejection:** Isolated observations report astronomical delays (such as bus route 100 reporting 4,137s / 68-minute delay on ghost runs or stale tracking hardware). Excluding `|delay| > 3600s` prevents outliers from skewing average route delay figures.


## 10. Feed facts (preliminary: about 1.8 hours of data on a Sunday evening, 11 Oct 2026)

Re-check these against a full weekday before relying on them.

- **Snapshot size:** about 450 KB each. At one per minute that is roughly 650 MB a day, so 14 days of
  retention is about 9 GB in S3. Worth checking whether gzip is worth adding.
- **Update rate:** 93 snapshots over about 107 minutes, so a new feed version roughly every 70 seconds rather
  than every minute. The fetch function rewrites the same key when the timestamp hasn't moved.
- **Identifiers:** every trip in the sample had `start_date`, and every stop update had `stop_sequence`, so the
  service-date fallback and the "no stop_sequence" skip rarely apply.
- **Trip relationships:** of 6,097 sampled trips, 5,215 were `SCHEDULED`, 813 `REPLACEMENT` and 69 `ADDED`; none
  were `CANCELED` or `DELETED`. If that holds on weekdays, Translink signals cancellations some other way (for
  example, every stop `SKIPPED`), and the cancellation counts will stay at zero until that is handled.
- **Exactly-zero delays:** about 5% of bus stop updates, 15% of untracked rail stop updates, and 46% of tracked
  rail/tram stop updates reported a delay of exactly 0. Re-run the Phase 2 "share_exactly_zero" query grouped by
  `route_type` on a full day before deciding whether any group needs filtering.

## 11. Honest numbers on the dashboard

**Choice.**
1. A rate with no observations is `null` ("No data"), never 0%.
2. Routes with fewer than 20 observed stops stay on the board but are listed after the others and marked
   "Low sample", in grey.
3. Routes missing from the static timetable are reported as route type `-1` ("Unknown"), not as buses.
4. Charts keep a slot for every day and every hour, so a gap in the data shows as a gap.

**Why.** Sorting worst-first puts tiny samples at the top, a 0% for "nothing observed" reads as a total failure,
and closing up empty hours or days hides exactly the periods a reader should ask about.

## 12. Reinstated trips withdraw their cancellation

**Choice.** The parser records every trip it sees running. When a snapshot shows a trip running that is newer
than its latest cancellation, the fold drops the cancellation from the batch and the writer deletes any stored
cancellation older than that sighting (`seq_ingest` has DELETE on `trip_cancellation`, migration 0005).

**Why.** Translink can cancel a trip and later reinstate it. Without this, the trip counted as cancelled for
the whole day.

**Known gap.** Like decision 3, this assumes snapshots are loaded roughly in order: a cancellation loaded in a
later batch than a newer "running" sighting would be stored again. The watermark makes that rare.

## 13. Full-weekday validation supersedes the preliminary feed sample

**Evidence.** The complete Monday 12 October 2026 contains 2,866 distinct raw feed versions, 3.41 GB of
protobuf and a maximum gap of 70 seconds. All 24 hours are covered. Two collectors ran about 28 seconds
apart, so the observed mean version interval is 30.1 seconds; the intended schedule is still one fetch
per minute. The local runner now takes an exclusive lock to prevent duplicate collectors for one directory.

There are 540,225 distinct stored trip-stop rows for Monday over 499 routes; 5,506 delays outside ±1 hour
are excluded, leaving 534,719 trusted passed stops. All Monday route IDs match static GTFS. No raw trip
sighting lacked start_date, no stop lacked stop_sequence, and no stop reported time without delay.
Most rail rows remain untracked (15,702 versus 3,994 tracked); their exactly-zero-delay shares are 3.57%
and 5.16%. Keep untracked services and the existing ±1-hour quality rule, with regression tests for both.

**Cancellations.** One trip was explicitly CANCELED in two raw snapshots and later reported running. A
current database count of zero reflects its reinstatement. Eight sightings had every reported stop SKIPPED;
do not infer entire-trip cancellations from these incomplete stop lists. UI labels now say feed-reported
cancellations and explain that zero does not establish complete service delivery.

**Punctuality window.** Keep the project −60/+300 second convention and disclose that it differs from
[Translink's official definitions](https://translink.com.au/about-translink/reports-and-publications/performance).
Official train results use destination/key-station measures, different late thresholds and force-majeure
adjustments; changing a single threshold would not make these stop-level rates comparable.

**Performance.** Current representative SQL plans run in about 9–191 ms. Do not add a daily aggregate
table yet. Repeat with a complete week before accepting Phase 4's workload target. Frozen data, raw-feed
counts, plans and HTTP timings are linked from [the validation report](validation-2026-10-13.md).

## 14. Measured query plans; 90 days of detail, daily totals after that

**Evidence.** On 25 November 2026 the representative queries ran against Aurora (PostgreSQL 17.9,
`work_mem` 4 MB) with about 177,000 rows from 24–25 November; output in
[docs/evidence/2026-11-25/query-plans.txt](evidence/2026-11-25/query-plans.txt). The planner
estimated 337 rows where 53,554 matched on every full-day scan, because the view filtered on the
expression `coalesce(arrival_delay_s, departure_delay_s)`, which has no statistics. With that
estimate `/api/stops/worst` used a nested loop over `gtfs_stop` and took about 0.8 s, and the
full-day GROUP BY sorts spilled to disk.

**Decision.** Migration 0008 stores the delay as a generated column (statistics, correct
estimates), points the view at it, and gives the app roles 16 MB of `work_mem`; worst stops and
modes aggregate before joining static GTFS. A full weekday is about 540,000 rows (about 190 MB
with indexes), so detail kept forever would cost tens of GB a year on a 10-dollar budget.
Migration 0009 keeps per-stop detail for 90 days (`DETAIL_RETENTION_DAYS`) and per-day,
per-route, per-stop totals (`stop_daily_stats`) after that. The load function archives at most
one day per run, after it has caught up.

**Consequences.** Every count, rate and average is read through `stop_day_figures`, so a day
reports the same numbers before and after archiving (tested). Hourly figures and median/P90
delays need individual delays: for archived days the hourly chart is empty and the percentiles
are null. Archived on-time counts keep the OnTime window in force when they were archived; the
window is stored on each row.
