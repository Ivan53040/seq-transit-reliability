# Builds everything the CDK stack packages:
#   artifacts/ingestion  fetch + load Lambda functions
#   artifacts/api        ASP.NET Core API Lambda
#   web/dist             dashboard
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')

if (Test-Path artifacts) { Remove-Item artifacts -Recurse -Force }
dotnet publish src/SeqTransit.Ingestion -c Release -r linux-arm64 --self-contained false -o artifacts/ingestion
if ($LASTEXITCODE -ne 0) { throw 'Publishing the ingestion functions failed.' }
dotnet publish src/SeqTransit.Api -c Release -r linux-arm64 --self-contained false -o artifacts/api
if ($LASTEXITCODE -ne 0) { throw 'Publishing the API failed.' }

Push-Location web
try {
  npm ci
  if ($LASTEXITCODE -ne 0) { throw 'npm ci failed.' }
  npm run build
  if ($LASTEXITCODE -ne 0) { throw 'Building the dashboard failed.' }
}
finally { Pop-Location }

Write-Host 'Build outputs ready in artifacts/ and web/dist/'
