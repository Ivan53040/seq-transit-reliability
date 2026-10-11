# SEQ Transit Reliability: Implementation Plan

Oct 11, 2026 · @Ivan

## How to use this plan

Work through nine phases over about four weeks to turn the starter in `Sideproject\seq-transit-reliability` into a live, tested project you can show and explain. Tick each box as you finish it.

**Where the project stands today**

- Done in the starter: feed parsing, the fetch and load workflows, the database schema, the API, a basic dashboard, the AWS stack, CI, and tests for all of them.
- Not done yet: the first real build on your PC, real data, route and stop names, deeper analysis, charts, the AWS deployment, and the portfolio write-up.

**How each phase is laid out**

- A first line with the goal and a rough time.
- Tasks as checkboxes, in order. Commands are written for PowerShell on Windows.
- **Done when**: the check that tells you the phase is finished. Don't move on until it passes.
- **Be able to explain**: what an interviewer could ask about that phase. If you can't answer it without notes, you don't own that part yet.

Phase 8 (Amazon Bedrock) is optional. Do it only if the rest is finished and you're aiming at AI roles.

&#91;embedded content: roadmap: 4 weeks, 3 checkpoints\]

Week 1 decides the rest: if the real data can't be trusted, the dashboard and the deployment don't matter yet.

## Phase 0: Set up your machine

Goal: every tool installed and the project on GitHub, with CI running. About half a day.

- [ ] Install the .NET 10 SDK. Check: `dotnet --list-sdks` shows a 10.0 version.
- [ ] Install Node.js 22 LTS or later. Check: `node -v`.
- [ ] Install Docker Desktop (WSL 2 backend) and start it. Check: `docker run --rm hello-world`.
- [ ] Install Git and the latest AWS CLI v2. Check: `aws --version`.
- [ ] Install an editor: Visual Studio, Rider, or VS Code with the C# Dev Kit extension.
- [ ] Move `docs\github-actions-ci.yml` to `.github\workflows\ci.yml` (the copy tool couldn't write there).
- [ ] Create the repository and push it:

```powershell
cd C:\Users\ivank\Desktop\Sideproject\seq-transit-reliability
git init -b main
git add .
git commit -m "Starter: ingestion, API, dashboard and AWS stack"
# Create an empty private repo named seq-transit-reliability on github.com, then:
git remote add origin https://github.com/Ivan53040/seq-transit-reliability.git
git push -u origin main
```

**Done when:** the CI run on GitHub shows three jobs (.NET, CDK, dashboard). The deploy job is skipped until Phase 7. If a job fails, that's expected at this stage; Phase 1 fixes it.

**Be able to explain:** why the repo pins the SDK in `global.json` and keeps package versions in `Directory.Packages.props`.

## Phase 1: Build, test and own the starter

Goal: everything builds and passes on your PC, and you can explain every file. About 2 to 3 days. This phase matters most: an interviewer will ask about code you put on your CV.

**Build and test**

- [ ] `dotnet build` from the repo root. This is the first build with the real NuGet packages; if anything fails in Data, Ingestion or Api, paste the errors to Claude.
- [ ] `dotnet test --filter "Category!=Integration"` (no Docker needed).
- [ ] With Docker running: `dotnet test` (adds the PostgreSQL integration tests).
- [ ] `cd infra; npm ci; npm run build; npm test`
- [ ] `cd web; npm install; npm run dev`, then open http://localhost:5173/?sample
- [ ] Push the fixes. CI should now go green.

**Read the code in this order**

| # | File | Question to answer in your own words |
| --- | --- | --- |
| 1 | `proto/gtfs-realtime.proto` (TripUpdate, StopTimeUpdate, TripDescriptor) | What is a stop\_sequence, and what do SKIPPED and NO\_DATA mean? |
| 2 | `Core/Gtfs/TripUpdateFeedParser.cs` | Why are some stop updates skipped, and when is the service date a guess? |
| 3 | `Core/Ingestion/SnapshotKeys.cs`, `SnapshotFetcher.cs` | Why is the S3 key built from the feed's timestamp? Which errors are retried? |
| 4 | `Core/Ingestion/ObservationFold.cs` | Why does the newest snapshot win, and why fold before writing? |
| 5 | `Core/Ingestion/SnapshotLoader.cs` | What happens if the load crashes halfway through? |
| 6 | `db/migrations/*.sql`, `Data/ObservationWriter.cs` | What do `unnest` and `ON CONFLICT ... WHERE` each do here? |
| 7 | `Data/TransitDataSource.cs` | How does the app sign in to Aurora without a password? |
| 8 | `Data/ReliabilityQueries.cs`, `Api/Endpoints/ReliabilityEndpoints.cs` | Why count only stops the vehicle has already passed? |
| 9 | `Ingestion/Functions.cs` | Which code runs once per Lambda container, and which runs on every call? |
| 10 | `infra/lib/seq-transit-stack.ts` | Why does the stack have no VPC, and what does each alarm watch? |
| 11 | `web/src/App.tsx` | How does the page load data, and what does it show while loading or on error? |

**Make it yours**

- [ ] Make one change in each layer yourself. For example: an `/api/routes/{routeId}/stops` endpoint, a new parser test for a feed edge case, a new column in the dashboard table.
- [ ] Break a test on purpose, watch it fail, then fix it.
- [ ] Rewrite `docs/decisions.md` in your own words. Delete anything you can't defend.

**Done when:** CI is green, and you can answer all 11 questions above without looking.

**Be able to explain:** the whole data flow from Translink's feed to one number on the dashboard, in under two minutes.

## Phase 2: Collect real data and check its quality

Goal: at least one full weekday of real Translink data on your PC, and written rules for which data you trust. About 2 days, mostly leaving it running.

**Collect**

- [ ] Start the database and create the tables:

```powershell
docker compose up -d
$env:DB_CONNECTION_STRING = "Host=localhost;Database=seq_transit;Username=postgres;Password=postgres"
dotnet run --project src/SeqTransit.Migrator
```

- [ ] Run `dotnet run --project src/SeqTransit.Local -- run` for 24 hours on a weekday, so you capture both peaks (about 7 to 9 am and 3 to 6 pm). Keep the PC awake.
- [ ] Open a SQL shell with `docker compose exec db psql -U postgres -d seq_transit` for the checks below.

**Learn the feed's facts** (write each answer in `docs/decisions.md`)

- [ ] Size of one snapshot: `Get-ChildItem .local-snapshots -Recurse -File | Measure-Object Length -Average -Sum`. This decides S3 cost and whether to compress snapshots.
- [ ] How often the feed changes: count snapshot files per hour. 60 means a new version every minute; fewer means the feed updates more slowly.
- [ ] Whether trips include `start_date`, `stop_sequence` and a vehicle id. The skipped-update counts in the runner's output hint at this.

**Check the data before trusting any number**

```sql
-- How much data is there?
SELECT count(*) AS snapshots, min(feed_timestamp) AS first, max(feed_timestamp) AS last FROM feed_snapshot;
SELECT count(*) AS stop_rows, count(DISTINCT route_id) AS routes FROM stop_delay_observation;

-- Do trips without a tracked vehicle report exactly zero delay? (That would inflate on-time rates.)
SELECT vehicle_id IS NOT NULL AS tracked,
       count(*) AS stops,
       round(avg((coalesce(arrival_delay_s, departure_delay_s) = 0)::int), 3) AS share_exactly_zero
FROM stop_delay_observation
WHERE event_time < now()
GROUP BY 1;

-- What does a normal delay look like?
SELECT percentile_cont(ARRAY[0.05, 0.5, 0.95])
         WITHIN GROUP (ORDER BY coalesce(arrival_delay_s, departure_delay_s)) AS p5_p50_p95_seconds
FROM stop_delay_observation
WHERE event_time < now();

-- Anything absurd? (more than an hour early or late)
SELECT route_id, trip_id, stop_sequence, coalesce(arrival_delay_s, departure_delay_s) AS delay_s
FROM stop_delay_observation
WHERE abs(coalesce(arrival_delay_s, departure_delay_s)) > 3600
ORDER BY delay_s DESC
LIMIT 20;

-- On-time rate by hour of day, Brisbane time
SELECT extract(hour FROM event_time AT TIME ZONE 'Australia/Brisbane') AS hour,
       count(*) AS stops,
       round(avg((coalesce(arrival_delay_s, departure_delay_s) BETWEEN -60 AND 300)::int), 3) AS on_time_rate
FROM stop_delay_observation
WHERE event_time < now()
GROUP BY 1
ORDER BY 1;
```

- [ ] Decide your data rules from the results (for example: exclude untracked trips, drop delays beyond an hour) and add them to `docs/decisions.md` as decision 9.
- [ ] Apply the rules in `ReliabilityQueries.PassedStops` and add an integration test for each one.
- [ ] Look up how Translink defines "on time" in its published performance reports, and set the `OnTime` window in `appsettings.json` to match so your numbers are comparable.

**Done when:** you have 24 hours of data, the feed facts are written down, and every data rule has a test.

**Be able to explain:** one surprise you found in the real data and what you changed because of it. This becomes the best story in your case study.

## Phase 3: Add route and stop names from static GTFS

Goal: the dashboard shows real route numbers, names and modes (bus, train, ferry, tram) instead of raw ids. About 3 days. The real-time feed only carries ids; the names live in Translink's static timetable ([SEQ GTFS on the Queensland Open Data Portal](https://www.data.qld.gov.au/dataset/general-transit-feed-specification-gtfs-translink/resource/e43b6b9f-fc2b-4630-a7c9-86dd5483552b)).

- [ ] Download the SEQ GTFS zip and look inside. Note the row counts of `routes.txt`, `stops.txt`, `trips.txt` and `stop_times.txt` (the last one is large).
- [ ] Check that route ids in your real-time data match `routes.txt`. Write down any that don't.
- [ ] Add `db/migrations/0003_static_gtfs.sql` with tables for routes (id, short name, long name, route type), stops (id, name, latitude, longitude) and trips (id, route id, headsign, direction). Grant SELECT to `seq_api`. Never edit 0001 or 0002: the migrator refuses changed files.
- [ ] In Core, write a reader that opens the zip with `System.IO.Compression.ZipArchive` and parses the CSV files. Add `CsvHelper` to `Directory.Packages.props` rather than splitting lines yourself: GTFS fields can contain quoted commas.
- [ ] In Data, write a loader that bulk-copies into staging tables with Npgsql's binary COPY (`BeginBinaryImportAsync`), then replaces the live tables in one transaction, so the API never sees a half-loaded timetable.
- [ ] Add a command: `dotnet run --project src/SeqTransit.Local -- load-gtfs <path to zip>`.
- [ ] Join names into the API results with a LEFT JOIN (a route missing from the timetable should still appear, with its id). Add a `/api/routes` endpoint listing every route for search.
- [ ] Tests: a tiny fixture zip (2 routes, 3 stops) for the reader; an integration test that loading twice gives the same result.
- [ ] Dashboard: route number in the badge, long name beside it, and a mode filter.
- [ ] Leave `stop_times.txt` (scheduled times) for later. You only need it if Phase 2 showed many stops with a time but no delay.

**Done when:** the dashboard shows real route numbers and names, and reloading the timetable is safe to repeat.

**Be able to explain:** the difference between static and real-time GTFS, and why the timetable is swapped in one transaction.

## Phase 4: Add analysis endpoints to the API

Goal: the API answers the questions a commuter or a reviewer would actually ask, with honest numbers. About 3 to 4 days.

**New endpoints**

| Endpoint | Answers | Notes |
| --- | --- | --- |
| `GET /api/routes/{routeId}?days=7` | How did this route do this week? | On-time rate, median and 90th-percentile delay, cancellations |
| `GET /api/routes/{routeId}/hours?date=` | Is it worse at peak times? | On-time rate per hour, Brisbane time |
| `GET /api/stops/worst?date=&limit=20` | Where do delays build up? | Highest average delay, minimum 20 observations per stop |
| `GET /api/modes/on-time?date=` | Bus, train, ferry or tram: which is most reliable? | Needs Phase 3 |

**Tasks**

- [ ] Write each query as SQL in `psql` first and check the numbers by hand on a small case.
- [ ] Move it into `ReliabilityQueries`. Use LINQ where it stays readable; for percentiles and time-zone hours, use raw SQL through EF Core (`Database.SqlQuery<T>`) with parameters, never string concatenation.
- [ ] Return the sample size with every rate, and hide rates below a minimum count. A route with 3 observations and a 33% on-time rate says nothing.
- [ ] Add an integration test per query with seeded rows whose answer you worked out by hand.
- [ ] Add API tests with `Microsoft.AspNetCore.Mvc.Testing` (`WebApplicationFactory<Program>`; the `Program` class is already public for this). Test status codes and the JSON shape.
- [ ] Add an OpenAPI document (`Microsoft.AspNetCore.OpenApi`, `AddOpenApi()` and `MapOpenApi()`), so reviewers can see the API.
- [ ] Measure: run `EXPLAIN ANALYZE` on each query with a week of data. Add an index if a query takes over about 200 ms.
- [ ] If the queries get slow, add a daily summary table that the load function updates, and have the API read that instead. Record the decision either way.

**Done when:** four new endpoints, each with a query test and an API test, all under about 200 ms on a week of data.

**Be able to explain:** why rates come with sample sizes, and why parameterised SQL matters.

## Phase 5: Build out the dashboard

Goal: someone new can find out which routes are worst at peak time in two clicks. About 3 days.

- [ ] Add a route page (`/routes/:routeId`) with React Router. Clicking a row on the board opens it.
- [ ] On the route page: a line chart of the daily on-time rate (Phase 4 `?days=`), a bar chart by hour of day, and a table of its worst stops.
- [ ] Pick one chart library and stick to it (Recharts is the common choice), or draw simple SVG yourself. Every chart also needs its numbers available as text for screen readers.
- [ ] Add the mode filter and name search from Phase 3 to the main board.
- [ ] Put the date in the URL (`?date=2026-10-12`) so a link shows the same view.
- [ ] Show the sample size next to every rate, matching the API.
- [ ] Check on a phone-sized window, in dark mode, and using only the keyboard. Run Lighthouse in Chrome DevTools and fix anything under 90 for accessibility.
- [ ] Keep `?sample` working with sample data for the new views, so you can work on layout without the API.
- [ ] Take two screenshots (board and route page) for the README.

**Done when:** board → route → peak-hour chart works with real data, on a phone and with the keyboard.

**Be able to explain:** how the page handles loading, errors and empty days, and why the dev server proxies `/api` while CloudFront does the same in production.

## Phase 6: Deploy to AWS

Goal: a public link that shows today's data, with costs you've checked. About 1 to 2 days, plus 48 hours of watching costs.

**Prepare the account**

- [ ] Turn on MFA for the root user. Create an admin user in IAM Identity Center and stop using root.
- [ ] Sign the CLI in as that user (`aws configure sso`) with region `ap-southeast-2`. Check: `aws sts get-caller-identity`.
- [ ] In the Billing console, create a monthly cost budget with an email alert now. Don't wait for the stack's own budget.
- [ ] Bootstrap the CDK once: `cd infra; npm ci; npx cdk bootstrap`.

**Create the database**

- [ ] Run `.\scripts\create-database.ps1`. It prints `dbHost` and `dbClusterResourceId`.
- [ ] Copy `infra\cdk.context.example.json` to `infra\cdk.context.json` and fill in those two values and `alertEmail`. The file is git-ignored.
- [ ] Create the tables and roles, signing in as the admin user with IAM:

```powershell
$env:DB_CONNECTION_STRING = $null
$env:DB_HOST = "<dbHost>"; $env:DB_USER = "postgres"; $env:AWS_REGION = "ap-southeast-2"
dotnet run --project src/SeqTransit.Migrator
```

- [ ] Open the cluster in the RDS console and connect through CloudShell. Check the tables with `\dt` and the roles with `\du`.

**Deploy**

- [ ] `.\scripts\publish.ps1`, then `cd infra; npm run deploy`. Read the IAM changes it lists before you approve them.
- [ ] Confirm the subscription email from AWS, or alarms won't reach you.

**Check it works, in this order**

- [ ] CloudWatch Logs, fetch function: a `SNAPSHOT_STORED` line every minute. The snapshot bucket fills up.
- [ ] After 30 minutes, load function logs: `BATCH_LOADED` with rows written.
- [ ] `<SiteUrl>/api/health` returns `"status":"ok"`, and the site shows today's routes.
- [ ] The no-snapshots alarm starts in ALARM and should turn OK within about 5 minutes. All four alarms OK after an hour.

**Check costs after 48 hours**

- [ ] Cost Explorer, grouped by service. Note each line; these numbers go in your case study.
- [ ] CloudWatch metric `ServerlessDatabaseCapacity` for the cluster: it should drop to 0 between loads. If it never does, find what keeps it awake (API traffic, long loads) and raise `loadIntervalMinutes` or lower the auto-pause delay.

**Done when:** the public link shows today's data, all alarms are OK, and two days of costs match what you expected.

**Be able to explain:** why there's no VPC or NAT gateway, how IAM database sign-in works, what each alarm catches, and the monthly cost broken down by service.

## Phase 7: Deploy automatically with GitHub Actions

Goal: every pull request is tested, and merging to `main` deploys, with no AWS keys stored anywhere. About half a day.

- [ ] Set `githubRepository` to `Ivan53040/seq-transit-reliability` in `infra\cdk.context.json`.
- [ ] Run `cd infra; npm run deploy:github`. Copy the `DeployRoleArn` it prints. If the account already has a GitHub OIDC provider, run `npm run deploy:github -- -c existingOidcProviderArn=<its ARN>` instead.
- [ ] In GitHub: Settings → Secrets and variables → Actions → Variables. Add `AWS_DEPLOY_ROLE_ARN`, `SEQ_DB_HOST`, `SEQ_DB_CLUSTER_RESOURCE_ID` and `SEQ_ALERT_EMAIL`.
- [ ] Push a small change to `main` and watch the deploy job: it builds, runs migrations, then runs `cdk deploy`.
- [ ] Add a branch protection rule on `main` that requires the three CI jobs to pass.
- [ ] From now on, work in branches and merge through pull requests. Reviewers do look at commit and PR history.
- [ ] Add the CI status badge to the top of the README.

**Done when:** a pull request shows green checks, and merging it updates the live site without you touching AWS.

**Be able to explain:** how GitHub's OIDC token becomes temporary AWS credentials, and why the role only trusts the `main` branch of one repository.

## Phase 8 (optional): Weekly summary with Amazon Bedrock

Goal: every Monday morning, a short plain-English summary of last week's reliability, written by a model from numbers the database computed. About 2 days. It uses your AI Practitioner knowledge, and the guardrail is the part worth showing.

- [ ] In the Bedrock console, pick a text model available in `ap-southeast-2` and enable access if asked.
- [ ] Add `db/migrations/0004_weekly_summary.sql`: week start, summary text, the input figures as JSON, model id, created time.
- [ ] Write a new Lambda function that runs Mondays at 7 am Brisbane time (EventBridge Scheduler cron with time zone `Australia/Brisbane`).
- [ ] Compute every figure in SQL first: best and worst routes, change from the previous week, worst hour. The model only writes sentences about figures it's given; it never calculates.
- [ ] Call the Bedrock Converse API (`AWSSDK.BedrockRuntime`, `ConverseAsync`) with the figures and a fixed instruction to use only those numbers.
- [ ] Guardrail: check that every number in the reply appears in the input figures. If one doesn't, keep the previous summary and log a warning. Unit-test this check with replies that pass and fail.
- [ ] Grant the function `bedrock:InvokeModel` on that one model only.
- [ ] Add `GET /api/summaries/latest` and show it on the dashboard, labelled as written by AI from the figures shown.

**Done when:** a summary appears each Monday, and a test proves a reply with an invented number is rejected.

**Be able to explain:** why the model never does the arithmetic, and how you catch a made-up number.

## Phase 9: Make it portfolio-ready

Goal: a reviewer understands the project in two minutes and can see it running. About 2 days.

**README and repository**

- [ ] Update the README status line, add the live link, the two screenshots and the CI badge at the top.
- [ ] Replace the roadmap with what you actually built, and keep a short "What I'd do next".
- [ ] Make the repository public, add topics (dotnet, aws, aws-cdk, postgresql, gtfs) and pin it on your GitHub profile.

**Case study** (`docs/case-study.md`, then a LinkedIn post)

- [ ] The problem in two sentences.
- [ ] Three decisions with their trade-offs: no VPC, batched loads, idempotent writes.
- [ ] Real numbers: snapshots a day, rows a day, monthly cost by service, API response time.
- [ ] What surprised you in the real data (Phase 2) and what you changed.
- [ ] What you'd do differently.

**Demo video** (2 to 3 minutes, OBS or Loom)

- [ ] The dashboard: the worst route at peak.
- [ ] The architecture diagram, in 30 seconds.
- [ ] One decision explained (batched loads and cost is a good one).
- [ ] The AWS console: alarms OK, the cost graph, the database pausing.

**CV bullets** (fill in your real numbers)

- Built a serverless C#/.NET 10 pipeline on AWS (Lambda, S3, Aurora PostgreSQL, CDK) that records Translink's real-time feed every minute and reports on-time performance for \[N\] South East Queensland routes.
- Designed idempotent batch loading with database watermarks and single-transaction bulk upserts (\[N\] rows a day), letting Aurora Serverless pause between loads and keeping running costs to about \[$N\] a month.
- Set up CI/CD with GitHub Actions and OIDC (no stored keys), integration tests on real PostgreSQL with Testcontainers, and tests for the infrastructure code.

**Interview practice:** answer each out loud in under a minute.

- [ ] Why no VPC? What would make you add one?
- [ ] The load function crashes halfway through. What happens next?
- [ ] How do you know your on-time numbers are right?
- [ ] How would this scale to every transit agency in Australia?
- [ ] What does it cost, and what would you change to halve that?

**Done when:** the repository is public and pinned, the case study and video are linked from the README, and the project is on your CV and LinkedIn.

## When you get stuck

Most problems in this project fall into one of these. If it's not listed, paste the full error to Claude with what you ran.

| Problem | Likely cause | Fix |
| --- | --- | --- |
| `dotnet build` fails in Data, Ingestion or Api | A library's API differs slightly from what the starter assumed | Paste the errors to Claude; these are usually one-line fixes |
| Integration tests fail to start | Docker Desktop isn't running | Start it, check `docker ps`, run the tests again |
| Migrator or local runner can't connect | Database container not running, or port 5432 used by another PostgreSQL | `docker compose ps`; change the port in `docker-compose.yml` and the connection string |
| Migrator says a migration "changed after it was applied" | You edited an applied `.sql` file | Undo the edit and add a new numbered migration instead |
| Local runner shows HTTP errors from the feed | Translink outage, or your network blocking the request | Open the feed URL in a browser (it downloads a file); wait and retry |
| Migrator on Aurora fails to sign in | Wrong `DB_USER`, region, or the CLI isn't signed in | `aws sts get-caller-identity`; use `DB_USER=postgres` and `AWS_REGION=ap-southeast-2` |
| `cdk synth` or deploy says build outputs not found | The publish step hasn't run | `.\scripts\publish.ps1`, then deploy again |
| First API call after a quiet period is slow | Aurora is resuming from pause (normal) | Expect a few seconds; the connect timeout already allows 30 |
| No-snapshots alarm in ALARM after deploy | It needs 5 minutes of data first | Wait 5 minutes; if it stays red, read the fetch function's logs |
| Costs higher than expected | Database rarely pausing, or large snapshots in S3 | Check `ServerlessDatabaseCapacity` and the bucket size; raise `loadIntervalMinutes` or shorten retention |
| GitHub deploy: not authorized to assume the role | Repository name or branch doesn't match the role's trust rule | Check `githubRepository` and that the run is on `main` |
