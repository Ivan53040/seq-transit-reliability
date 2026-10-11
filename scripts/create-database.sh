#!/usr/bin/env bash
# Creates the Aurora PostgreSQL serverless cluster with "express configuration":
# no VPC, reachable through Aurora's internet access gateway, IAM sign-in only.
# Needs a recent AWS CLI v2 and credentials for the target account.
set -euo pipefail

CLUSTER_ID="${CLUSTER_ID:-seq-transit}"
REGION="${AWS_REGION:-ap-southeast-2}"

aws rds create-db-cluster \
  --db-cluster-identifier "$CLUSTER_ID" \
  --engine aurora-postgresql \
  --with-express-configuration \
  --region "$REGION"

echo "Waiting for the cluster to become available..."
aws rds wait db-cluster-available --db-cluster-identifier "$CLUSTER_ID" --region "$REGION"

# Let the database pause after 5 idle minutes and cap its size. If your engine version
# does not support pausing (minimum capacity 0), change MinCapacity to 0.5.
aws rds modify-db-cluster \
  --db-cluster-identifier "$CLUSTER_ID" \
  --serverless-v2-scaling-configuration MinCapacity=0,MaxCapacity=2,SecondsUntilAutoPause=300 \
  --apply-immediately \
  --region "$REGION" > /dev/null

aws rds describe-db-clusters --db-cluster-identifier "$CLUSTER_ID" --region "$REGION" \
  --query 'DBClusters[0].{dbHost: Endpoint, dbClusterResourceId: DbClusterResourceId}' --output json
echo "Copy these two values into infra/cdk.context.json."
