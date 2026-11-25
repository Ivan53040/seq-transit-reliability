# Builds everything the CDK stack packages:
#   artifacts/ingestion  fetch + load Lambda functions
#   artifacts/api        ASP.NET Core API Lambda
#   web/dist             dashboard
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')

foreach ($packageName in @('ingestion', 'api')) {
  $artifactRoot = Join-Path (Get-Location).Path 'artifacts'
  $artifactPath = Join-Path $artifactRoot $packageName
  foreach ($targetPath in @($artifactRoot, $artifactPath)) {
    if (Test-Path -LiteralPath $targetPath) {
      $artifactItem = Get-Item -LiteralPath $targetPath
      if (($artifactItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
          (Resolve-Path -LiteralPath $targetPath).Path -ne $targetPath) {
        throw 'Refusing to remove a package directory redirected outside this repository.'
      }
    }
  }
  if (Test-Path -LiteralPath $artifactPath) {
    Remove-Item -LiteralPath $artifactPath -Recurse -Force
  }
}
dotnet publish src/SeqTransit.Ingestion -c Release -r linux-arm64 --self-contained false -o artifacts/ingestion
if ($LASTEXITCODE -ne 0) { throw 'Publishing the ingestion functions failed.' }
dotnet publish src/SeqTransit.Api -c Release -r linux-arm64 --self-contained false -o artifacts/api
if ($LASTEXITCODE -ne 0) { throw 'Publishing the API failed.' }

Push-Location web
try {
  if (-not (Test-Path node_modules)) {
    npm ci
    if ($LASTEXITCODE -ne 0) { throw 'npm ci failed.' }
  }
  npm run build
  if ($LASTEXITCODE -ne 0) { throw 'Building the dashboard failed.' }
}
finally { Pop-Location }

Write-Host 'Build outputs ready in artifacts/ and web/dist/'
