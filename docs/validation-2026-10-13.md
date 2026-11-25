# Local validation — 13 October 2026

All dates below use Australia/Brisbane. This records measured local results; AWS deployment remains pending.

## Full weekday and feed facts

The entire Monday 12 October is covered by 2,866 distinct raw snapshots, from 00:00:10 to 23:59:30.
All 24 hours contain snapshots; the largest gap, including the two midnight boundaries, is 70 seconds.
Every protobuf parsed and matched its timestamp-based filename. The raw files total 3,414,522,949 bytes
(3.41 GB decimal), averaging 1,191,389 bytes each. Two collectors were running about 28 seconds apart;
they sampled roughly two distinct feed versions per minute. The extra process was stopped and the runner
now holds an exclusive lock for its snapshot directory. The intended production schedule remains once per minute.

The complete raw audit saw 3,710,228 trip sightings and 66,367,580 stop sightings. These are repeated
appearances across snapshots, not unique trips or served stops. There were no missing/invalid start dates,
no missing stop sequences, and no stops with an event time but no delay. Missing vehicle IDs occurred
153,732 times. Retaining untracked services remains appropriate.

One unique trip was marked CANCELED in two snapshots, most recently at 18:26:40, then seen running later
(the last later sighting was 18:34:00). The database's current cancellation count of zero is therefore
consistent with the reinstatement rule. There were 45,854 SKIPPED stop sightings, 3 NO_DATA sightings,
and 8 sightings of trips whose entire reported stop list was SKIPPED. This does not prove full-trip cancellation;
the semantics still need confirmation before changing the parser. Absence of a trip from the feed is also
not proof that it was cancelled.

## Database quality

Migrations 0001–0006 are applied. Static GTFS contains 1,243 routes, 13,123 stops and 136,128 trips.
Every route observed on Monday matches the static timetable. Monday has 540,225 unique stored trip-stop
rows over 499 routes. Of these, 5,506 have delays outside ±3,600 seconds and are excluded by the trusted
view, leaving 534,719 passed, trusted stops. None lack an event time or delay, and none are future events.
The project's on-time rate is 68.44%; delay percentiles p5/p50/p95 are −194/95/639 seconds.

The share of exactly-zero delays is 3.57% for untracked rail and 5.16% for tracked rail; the weekday result
does not support dropping all services without a vehicle ID. Bus shares are 0.39% untracked and 0.66% tracked.
See the evidence for tram/ferry and hourly breakdowns, including both commute peaks.

The uniform −60 to +300 second window is an explicit project convention. It is not directly comparable
with [Translink's official dashboard](https://translink.com.au/about-translink/reports-and-publications/performance),
which uses different modes, key locations, service destinations and force-majeure adjustments. Retain the
existing window and disclose the distinction instead of changing one threshold and implying comparability.

## Performance

Saved `EXPLAIN (ANALYZE, BUFFERS)` results against 844,323 total rows at measurement time:

| Query | SQL execution time |
| --- | ---: |
| Route summary, highest-volume route L1-5029, seven-day date range | 8.513 ms |
| Hourly performance, L1-5029 | 9.480 ms |
| Worst stops, Monday, minimum 20 | 191.460 ms |
| Mode performance, Monday | 121.432 ms |

These are single runs affected by cache state and concurrent ingestion. The date range is seven days,
but the collected feed only spans Sunday evening through Tuesday midday. **The one-week-volume acceptance
criterion remains pending.** Do not add a summary table or more indexes from this result alone.

Five local HTTP requests per endpoint measured route summary 10.63–19.01 ms, hourly performance
9.71–11.82 ms, worst stops 129.26–157.41 ms, and modes 86.23–98.29 ms. These include application/HTTP
overhead and use route 196-5146 for the route endpoints. They are not AWS latency or cold-start measurements.

## Verification and deployment readiness

- .NET tests: Core 42, Data 17, API 9; total 68, all passed, including PostgreSQL Testcontainers.
- CDK: 10 tests passed; actual application and GitHub entrypoints synthesize offline.
- Web: TypeScript and production Vite build passed.
- Both Lambda packages publish successfully for linux-arm64, framework-dependent .NET 10.
- Lighthouse 13.5.0 mobile accessibility: board 100, route 100 after correcting heading levels.
- Browser: real Monday data, search, keyboard route navigation, back-link filter preservation,
  390×844 phone viewport and dark appearance. Tables scroll within their containers without page overflow.
- Empty historical date shows No data; sample mode remains available. Desktop and mobile screenshots
  are in `docs/screenshots`.
- Existing remote CI at f9ffb3f passed; the new changes have not yet run in remote CI.
- No AWS CLI/profile is configured, as confirmed by Ivan. No AWS resources were created or deployed.

The deployment entrypoints were missing and ignored by the global `bin/` pattern; both are now restored
and explicitly included by `.gitignore`. CI exercises both real entrypoints with offline synthesis.
OpenAPI is also exposed at `/api/openapi/v1.json` so the CloudFront API behaviour can serve it.
The API alarm watches API Gateway HTTP 5xx, including errors returned by an otherwise successful Lambda.
Database creation now stops on waiter/auto-pause/describe failures. Publishing cleans only the two Lambda
package directories, preserving audit and collector logs, and verifies Windows deletion paths first.

## Reproduce

```powershell
docker compose up -d
./scripts/audit-local.ps1 -Date 2026-10-12
dotnet test -c Release --artifacts-path artifacts/validation
cd infra
npm run build
npm test
```

The audit only reads raw files and runs read-only SQL transactions. It writes reports under `artifacts/audit`.
Frozen [feed audit](evidence/2026-10-13/feed-audit.txt), [database audit](evidence/2026-10-13/database-audit.txt),
[query plans](evidence/2026-10-13/query-plans.txt), and [HTTP timings](evidence/2026-10-13/api-latencies.json)
are retained in the repository. Lighthouse raw JSON/HTML reports are in `artifacts/audit`; a compact score
record is retained alongside the other evidence.

Keep the single collector and Docker running until at least a full week's feed exists. The week beginning
12 October ends at 00:00 on 19 October; assess gaps before claiming seven complete days.
