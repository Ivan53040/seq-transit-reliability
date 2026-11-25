# Reproducible read-only feed/data audit against this project's Docker database.
param(
  [Parameter(Mandatory)][ValidatePattern('^\d{4}-\d{2}-\d{2}$')][string]$Date,
  [string]$OutputDirectory = 'artifacts/audit'
)
$ErrorActionPreference = 'Stop'
[datetime]::ParseExact($Date, 'yyyy-MM-dd', [cultureinfo]::InvariantCulture) | Out-Null
$repoPath = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $repoPath
try {
  New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
  dotnet run --project src/SeqTransit.Local -c Release --artifacts-path artifacts/audit-build -- audit-feed $Date |
    Tee-Object -FilePath (Join-Path $OutputDirectory "$Date-feed.txt")
  if ($LASTEXITCODE -ne 0) { throw 'Feed audit failed; check the captured report.' }
  Get-Content -LiteralPath (Join-Path $PSScriptRoot 'sql/data-quality.sql') -Raw |
    docker compose exec -T db psql -X -v ON_ERROR_STOP=1 -v "audit_date=$Date" -U postgres -d seq_transit |
    Tee-Object -FilePath (Join-Path $OutputDirectory "$Date-database.txt")
  if ($LASTEXITCODE -ne 0) { throw 'Database audit failed.' }
  Get-Content -LiteralPath (Join-Path $PSScriptRoot 'sql/query-plans.sql') -Raw |
    docker compose exec -T db psql -X -v ON_ERROR_STOP=1 -v "audit_date=$Date" -U postgres -d seq_transit |
    Tee-Object -FilePath (Join-Path $OutputDirectory "$Date-query-plans.txt")
  if ($LASTEXITCODE -ne 0) { throw 'Query-plan audit failed.' }
}
finally { Pop-Location }
