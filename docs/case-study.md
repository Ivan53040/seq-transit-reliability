# SEQ Transit Reliability

## Problem

Arrival apps help with the next service but do not readily show which SEQ routes have consistently poor
punctuality, especially during commute peaks. I built a pipeline and dashboard that retain Translink's
real-time feed and let a reader compare observed delay by route, day, hour and stop.

## What works locally

C#/.NET 10 parses GTFS-RT protobuf, preserves the raw snapshots and folds repeated predictions into one
observation per trip stop on a service day. PostgreSQL stores the observations and a static GTFS timetable;
an ASP.NET Core API calculates rates and percentiles, and React exposes a searchable board with route charts.
The current AWS design uses Lambda, S3, Aurora Serverless, API Gateway, CloudFront and CDK. Live AWS
deployment and GitHub deployment are still pending.

## Three decisions and trade-offs

1. **Aurora express configuration without a VPC.** IAM sign-in through Aurora's internet access gateway avoids
   a Lambda VPC/NAT setup. It also ties the design to the supported express configuration and its limitations.
   This is a validated infrastructure design, not evidence of measured cloud savings yet.
2. **Raw storage every minute, database loads in batches.** Durable protobuf makes parser changes replayable;
   a 30-minute production load interval gives the database time to pause. The trade-off is delayed analytics,
   and API requests can still keep the database awake. Actual auto-pause and cost must be measured after deployment.
3. **Idempotent loading.** Feed timestamps key snapshots, a database watermark marks progress and a transaction
   contains bulk upserts. Retries preserve the newest known values. Very late snapshots older than the watermark
   remain a documented gap, and absence from the feed is not interpreted as cancellation.

## Evidence from a complete weekday

On Monday 12 October 2026, 2,866 distinct snapshots cover every hour with at most 70 seconds between versions.
Their total size is 3.41 GB. Two local collectors were running; that is why the observed capture cadence is
roughly twice the intended production cadence. The runner now rejects duplicate instances for the same directory.

The database holds 540,225 unique trip-stop observations for that service day across 499 routes. Excluding
5,506 delays outside ±1 hour leaves 534,719 trusted passed stops. Their median delay is 95 seconds and their
on-time rate under the project's −60/+300 second window is 68.44%. These describe observed feed estimates,
not the punctuality of every scheduled service or Translink's official performance measure.

A surprise was how many rail observations lacked a vehicle ID: 15,702 untracked versus 3,994 tracked.
Untracked rail still had variable delays; just 3.57% were exactly zero. A blanket vehicle-ID filter would
have removed most rail data. Another finding was one explicitly cancelled trip later appearing as running;
withdrawing the cancellation explains the final count of zero without claiming there were no disruptions.

Four representative SQL plans execute in about 9–191 ms on the current local data. Five HTTP samples per
analysis endpoint measured 10–157 ms. The one-week-volume and AWS cold-start checks remain pending.
Lighthouse mobile accessibility scores are 100 for both board and route page. The .NET, PostgreSQL integration
and CDK tests pass. Detailed counts and measurement limits are in [the validation report](validation-2026-10-13.md).

## Before publication

- Add the live URL, measured monthly spend by service and evidence of Aurora pausing after 48 hours.
- Repeat query measurements with a complete week of data and record any resulting schema decision.
- Confirm the meaning of entirely SKIPPED stop lists; do not derive full-trip cancellations from them yet.
- Record a 2–3 minute demo: board and peak-hour chart (60s), architecture (30s), batching/retry trade-off (30s),
  then the verified AWS alarms, cost graph and pause metric (30s).
- Write the LinkedIn post and CV bullets using only validated local facts until the cloud claims are measured.

## What I would change next

Use the complete-week workload to decide whether daily aggregates are necessary. Evaluate compressing raw
snapshots using measured S3 request/storage cost and Lambda CPU trade-offs. Distinguish estimates observed
before/after a stop event, and investigate cancellation semantics against timetable and service-alert evidence
before making stronger service-delivery claims.
