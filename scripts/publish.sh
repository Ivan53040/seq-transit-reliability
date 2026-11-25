#!/usr/bin/env bash
# Builds everything the CDK stack packages:
#   artifacts/ingestion  fetch + load Lambda functions
#   artifacts/api        ASP.NET Core API Lambda
#   web/dist             dashboard
set -euo pipefail
cd "$(dirname "$0")/.."

rm -rf artifacts/ingestion artifacts/api
dotnet publish src/SeqTransit.Ingestion -c Release -r linux-arm64 --self-contained false -o artifacts/ingestion
dotnet publish src/SeqTransit.Api -c Release -r linux-arm64 --self-contained false -o artifacts/api

(cd web && npm ci && npm run build)

echo "Build outputs ready in artifacts/ and web/dist/"
