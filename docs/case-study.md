# SEQ Transit Reliability — Engineering Case Study

**Live dashboard:** [https://d2ckfcry48m74h.cloudfront.net](https://d2ckfcry48m74h.cloudfront.net)  
**OpenAPI 3.0 spec:** [https://d2ckfcry48m74h.cloudfront.net/api/openapi/v1.json](https://d2ckfcry48m74h.cloudfront.net/api/openapi/v1.json)

## Problem

Arrival apps help with the next service but do not readily show which South East Queensland (SEQ) routes
and stops suffer consistently poor punctuality, especially during commute peaks. I built an automated
serverless data pipeline, API, and Swiss-style wayfinding dashboard that retain Translink's real-time
GTFS-RT feed every minute and compare observed arrival delay by route, mode, day, hour, and stop.

## Production Architecture (Live in AWS `ap-southeast-2`)

- **Ingestion & Storage:** EventBridge Scheduler triggers a .NET 10 ARM64 (Graviton) `FetchFunction`
  every minute to parse and store raw GTFS-RT `TripUpdates` protobuf snapshots in S3 (14-day lifecycle
  expiry). Every 60 minutes, `LoadFunction` (guarded by a PostgreSQL session advisory lock) folds new
  snapshots in memory (`Newest Wins`) and writes them via single-round-trip `unnest` bulk upserts into
  **Aurora PostgreSQL 17.9 Serverless v2 Express** (`0–2 ACU`, auto-pause after `300 s`, no VPC).
- **Static Timetable Sync:** A weekly GitHub Actions workflow (`sync-gtfs.yml`) downloads the latest
  `SEQ_GTFS.zip` every Monday at 03:00 AEST and performs an Npgsql binary `COPY` atomic table swap into
  Aurora so route names, modes, and stop coordinates stay current when Translink rotates `route_id` suffixes.
- **API, Edge Security & Caching:** An ASP.NET Core 10 Minimal API (`ApiFunction`) sits behind API Gateway
  HTTP API (`5 req/s`, burst `10`) and CloudFront. CloudFront attaches an `X-Origin-Verify` header (the
  CloudFormation stack UUID) that the API validates in constant time (`403` otherwise), caches settled past
  service days for 24 hours (`Cache-Control`), whitelists only the 4 query parameters the API reads, and
  serves SPA client routes via a CloudFront Function without rewriting `/api/*` 404s into HTML.
- **AI Weekly Brief with Numeric Guardrail:** Every Monday at 07:00 AEST, `SummaryFunction` computes
  previous-week metrics in SQL and calls Amazon Bedrock (`amazon.nova-micro-v1:0`) to narrate a 2–3 sentence
  plain-English brief. A deterministic regex guardrail (`SummaryGuardrail`) extracts every number from the
  model output and rejects the reply before persistence if any figure is not present in the SQL whitelist.
- **Zero-Key CI/CD:** GitHub Actions runs .NET unit + Testcontainers PostgreSQL integration tests, CDK
  template assertions + offline synth, and Vite builds on every push, then deploys `main` via GitHub OIDC
  federation with no stored AWS access keys.

## Key Engineering Decisions & Real Production Trade-offs

1. **Aurora Express configuration without a VPC (no NAT Gateway).**  
   Using Aurora's internet access gateway with `SslMode=VerifyFull` and short-lived RDS IAM auth tokens
   (refreshed every 10 minutes by Npgsql) removes the need for a VPC and avoids a fixed ~$32+/month NAT
   Gateway bill. Least-privilege roles (`seq_ingest` write, `seq_api` read-only) enforce access boundaries
   with zero database passwords.
2. **Raw snapshots every minute, batched DB loads & 90-day detail retention.**  
   Decoupling 1-minute S3 writes from 60-minute database loads allows Aurora Serverless v2 to pause to
   `0 ACU` between runs and keeps raw protobuf replayable. Because a full weekday produces ~540,000
   trip-stop rows (~190 MB with indexes, ~70 GB/year), Migration `0009` + `DetailArchiver` keep per-stop
   detail for 90 days and roll older days into `stop_daily_stats` in a single transaction, unified behind
   an inlineable SQL function `stop_day_figures()`.
3. **Measured PostgreSQL planner tuning (`EXPLAIN ANALYZE` on live Aurora).**  
   On 25 November 2026, live `EXPLAIN ANALYZE` runs against Aurora (`docs/evidence/2026-11-25/query-plans.txt`)
   showed the planner underestimated full-day row counts by ~160× (`337` estimated vs `53,554` actual)
   because `reliable_stop_observation` filtered on `coalesce(arrival_delay_s, departure_delay_s)`, which
   had no column statistics. That caused `/api/stops/worst` to pick a slow nested loop (~0.8 s) and spill
   sorts to disk at `4 MB work_mem`. Migration `0008` added a `STORED GENERATED` `delay_s` column with
   statistics, raised `work_mem` to `16 MB` for app roles, and aggregated before joining static GTFS.
4. **Solving a real cold-wake Lambda throttling incident on a 10-concurrency account quota.**  
   During 48-hour live monitoring (`2026-11-25 11:06 UTC`), `ApiErrorsAlarm` fired (`6` HTTP 5xx in 5 min).
   CloudWatch Logs and Lambda metrics proved **`0` application errors** and **`6` Lambda throttles**:
   when Aurora was paused at `0 ACU`, opening the homepage fired 6 parallel API requests that waited ~23 s
   for Aurora to resume; a second reload 7.5 s later hit the new-account 10-concurrent-execution Lambda
   quota. Fix: `loadDay()` and `loadRouteDetail()` now wake Aurora with a single `GET /api/health` call
   first (`1/10` concurrency when cold; ~20 ms CloudFront edge hit when warm) before fanning out parallel
   queries. Zero `ApiErrorsAlarm` events have occurred since.

## Verified Evidence (Local Weekday + 48-Hour+ Live AWS Production)

### Local Full-Weekday Audit (Monday 12 October 2026)
- **Raw capture:** 2,866 distinct protobuf snapshots (3.41 GB), covering all 24 hours with a max gap of
  70 seconds ([validation-2026-10-13.md](validation-2026-10-13.md)).
- **Observations:** 540,225 unique trip-stop rows across 499 routes; 534,719 trusted passed stops after
  excluding 5,506 delays outside ±1 hour. Median delay: 95 s; on-time rate (−60/+300 s window): 68.44%.
- **Data-quality findings:** 15,702 of 19,696 rail observations lacked a `vehicle_id` yet carried real
  variable delays (only 3.57% exact zero), so untracked trips are retained. Reinstated cancellations are
  withdrawn cleanly when a newer snapshot shows the trip running.

### Live AWS 48-Hour+ Verification (25–27 November 2026, `ap-southeast-2`)
- **Aurora Serverless v2 Auto-Pause (`ServerlessDatabaseCapacity`):** Confirmed scaling cleanly down to
  **`0 ACU` for ~45–50 minutes every hour** between hourly loads (24h peak `1.48 ACU`; active floor `0.50 ACU`
  on occasional user visits before pausing back to `0 ACU`).
- **CloudWatch Alarms:** All **7/7 alarms `OK`** (`NoSnapshotsAlarm`, `FeedStaleAlarm`, `LoadErrorsAlarm`,
  `ApiErrorsAlarm`, `SummaryErrorsAlarm`, `SummaryRejectedAlarm`, `DatabaseNeverPausedAlarm`).
- **Measured AWS Cost Explorer Spend (25–27 Nov 2026, USD, pre-tax + GST):**
  - **Nov 25 (launch day, migrations & heavy testing):** $1.68 (`RDS` $1.37, `S3` $0.01, `Tax` $0.30)
  - **Nov 26 (first full steady-state 24h at 60-min load interval):** **$1.33 pre-tax** (`RDS` $1.32,
    `S3` $0.01; **$0.00 across all 12 other services** — Lambda, CloudFront, API Gateway, Amazon Bedrock,
    CloudWatch, EventBridge, SNS, SQS, CloudFormation, KMS, Secrets Manager, Data Transfer)
  - **Nov 27 (partial UTC day):** $0.24 (`RDS` $0.24)
  - **Cost takeaway:** 99% of spend is Aurora ACU compute during hourly wakes (~15 min awake/hr × 24 wakes/day
    ≈ $1.32/day or ~$40/month at 60-min cadence). Because load cadence is parameterized via
    `SEQ_LOAD_INTERVAL_MINUTES`, lengthening the batch interval (e.g. to 180 min) scales RDS cost down
    proportionally without code changes.
- **Frontend Performance & Accessibility:** Lazy-loaded Leaflet OpenStreetMap monochrome hotspot map and
  route detail chunk, responsive mobile card layout (`390×844` verified), `ResizeObserver`-measured SVG
  charts (`useChartWidth`), WCAG AA contrast, and Lighthouse evidence recorded in
  [docs/evidence/2026-11-25/lighthouse-scores.json](evidence/2026-11-25/lighthouse-scores.json).

## What I Would Improve Next

- Switch CloudFront's `Content-Security-Policy-Report-Only` header to enforcing `Content-Security-Policy`
  after a week of clean browser telemetry.
- Request a Lambda account concurrency quota increase (from default `10` to `100`) and enable optional
  `SEQ_RESERVE_CONCURRENCY` if public traffic grows.
- Evaluate gzip-compressing raw S3 protobuf snapshots if snapshot retention is extended beyond 14 days.
