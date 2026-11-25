# Creates the Aurora PostgreSQL serverless cluster with "express configuration":
# no VPC, reachable through Aurora's internet access gateway, IAM sign-in only.
# Needs a recent AWS CLI v2 and credentials for the target account.
param(
  [string]$ClusterId = 'seq-transit',
  [string]$Region = 'ap-southeast-2'
)
$ErrorActionPreference = 'Stop'

aws rds create-db-cluster `
  --db-cluster-identifier $ClusterId `
  --engine aurora-postgresql `
  --with-express-configuration `
  --region $Region
if ($LASTEXITCODE -ne 0) { throw 'Creating the cluster failed.' }

Write-Host 'Waiting for the cluster to become available...'
aws rds wait db-cluster-available --db-cluster-identifier $ClusterId --region $Region
if ($LASTEXITCODE -ne 0) { throw 'Waiting for the cluster failed. Check its status before retrying.' }

# Let the database pause after 5 idle minutes and cap its size. If your engine version
# does not support pausing (minimum capacity 0), stop and review the running cost before proceeding.
aws rds modify-db-cluster `
  --db-cluster-identifier $ClusterId `
  --serverless-v2-scaling-configuration MinCapacity=0,MaxCapacity=2,SecondsUntilAutoPause=300 `
  --apply-immediately `
  --region $Region | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Setting the auto-pause capacity failed. The cluster exists; review it in RDS before proceeding.' }

aws rds describe-db-clusters --db-cluster-identifier $ClusterId --region $Region `
  --query 'DBClusters[0].{dbHost: Endpoint, dbClusterResourceId: DbClusterResourceId}' --output json
if ($LASTEXITCODE -ne 0) { throw 'Reading the cluster endpoint failed.' }
Write-Host 'Copy these two values into infra/cdk.context.json.'
