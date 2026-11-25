# AWS deployment checklist

Local packages and both CDK stacks have been validated offline. Live deployment is pending AWS account
setup, SSO sign-in, an alert address and an agreed monthly budget. The optional Bedrock phase waits until
the required phases pass. This document uses PowerShell from the repository root.

## 1. Account and sign-in

Enable root MFA and set up an IAM Identity Center user for administration. Install AWS CLI v2 using the
[official Windows instructions](https://docs.aws.amazon.com/cli/latest/userguide/getting-started-install.html).
Then configure [SSO authentication](https://docs.aws.amazon.com/cli/latest/userguide/cli-configure-sso.html):

```powershell
aws --version
aws configure sso --profile seq-transit
aws sso login --profile seq-transit
$env:AWS_PROFILE = 'seq-transit'
$env:AWS_REGION = 'ap-southeast-2'
aws sts get-caller-identity
```

Confirm that the returned account is the intended account. Set a monthly cost budget in Billing before
creating the database. The example stack budget is USD 10; choose the actual amount and alert email.
Budget notifications do not impose a spending cap. Do not send access keys or SSO tokens in chat.

## 2. Create the database and load the timetable

[Aurora express configuration](https://docs.aws.amazon.com/AmazonRDS/latest/AuroraUserGuide/CHAP_GettingStartedAurora.AuroraPostgreSQL.ExpressConfig.html)
provides the internet gateway and IAM authentication used here. Review the account, region, capacity limit
(0–2 ACUs) and idle pause (300 seconds) before running the creation command.

```powershell
./scripts/create-database.ps1
Copy-Item infra/cdk.context.example.json infra/cdk.context.json
# Fill in dbHost, dbClusterResourceId, alertEmail, monthlyBudgetUsd,
# and githubRepository = Ivan53040/seq-transit-reliability.
$env:DB_CONNECTION_STRING = $null
$env:DB_HOST = '<dbHost from the script>'
$env:DB_USER = 'postgres'
$env:DB_NAME = 'postgres'
dotnet run --project src/SeqTransit.Migrator -c Release
dotnet run --project src/SeqTransit.Local -c Release -- load-gtfs .gtfs-cache/SEQ_GTFS.zip
```

The static timetable must be loaded into Aurora too: loading it into Docker does not give the live site
route names. Never rerun create-db-cluster blindly if creation succeeded but a later step failed; inspect
the existing cluster and resume from the failed operation. Keep local collector environment variables in
its separate process so these admin settings cannot redirect local ingestion to Aurora.

## 3. Build, inspect and deploy

```powershell
./scripts/publish.ps1
Push-Location infra
try {
  npm ci
  npm run build
  npm test
  npx cdk bootstrap
  npx cdk diff SeqTransitStack
  npm run deploy
}
finally { Pop-Location }
```

Review the diff and CDK IAM approval prompt before deployment. The diff should include only the intended
application resources. Record SiteUrl, ApiUrl and SnapshotBucketName. Confirm the SNS subscription email.

## 4. Verify live data and costs

At the public SiteUrl verify `/api/health`, `/api/openapi/v1.json`, the route board and a route page.
Check that an unknown `/api/routes/...` returns HTTP 404 with no HTML rewrite, while a direct
`/routes/196-5146` navigation loads the SPA. Fetch logs should show SNAPSHOT_STORED each minute; load logs
should show BATCH_LOADED after the default 30-minute interval. Check all four alarms after an hour.

After 48 hours, record Cost Explorer amounts grouped by service and cluster ServerlessDatabaseCapacity.
Confirm actual capacity reaches 0 between loads; do not assume configuration alone proves that it pauses.
Record the measurement period, USD amounts and API response times in the case study. AWS cost data is delayed;
do not describe the configured budget as measured spend.

## 5. GitHub deployment

Set githubRepository in context and deploy the OIDC stack with `cd infra; npm run deploy:github`.
If the account has an existing GitHub OIDC provider, pass `-c existingOidcProviderArn=<its ARN>`.
Set repository Actions variables AWS_DEPLOY_ROLE_ARN, SEQ_DB_HOST, SEQ_DB_CLUSTER_RESOURCE_ID and
SEQ_ALERT_EMAIL. Also set SEQ_MONTHLY_BUDGET_USD if the selected budget differs from the default.
Configure branch protection to require .NET build and tests, CDK type-check and tests, and Dashboard build.
Use a small PR, merge it, then verify the deploy job and live site. Keep the repository private until the
portfolio and credential checks are ready; changing visibility is a separate publishing action.

## 6. Portfolio finish

Update the README with the verified live URL and cost results. Replace pending fields in the case study,
record the demo video, then publish the case study/LinkedIn post and update the CV with measured numbers.
The repository can then be made public, tagged and pinned. None of those publication steps have occurred yet.
