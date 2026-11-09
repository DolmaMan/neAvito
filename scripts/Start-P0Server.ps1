param([Parameter(Mandatory)][string]$ReportPath)
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
$report = Get-Content -LiteralPath $ReportPath -Raw | ConvertFrom-Json
if ($report.freshDatabase -notmatch '^neavito_p0_fresh_[0-9a-f]{32}$') { throw 'Not a P0 temporary database.' }
$secretId = ([xml](Get-Content NeAvito/NeAvito.csproj)).Project.PropertyGroup.UserSecretsId
$secrets = Get-Content (Join-Path $env:APPDATA "Microsoft/UserSecrets/$secretId/secrets.json") -Raw | ConvertFrom-Json
$connection = [System.Data.Common.DbConnectionStringBuilder]::new()
$connection.set_ConnectionString([string]$secrets.'ConnectionStrings:Postgres')
$connection.set_Item('database', $report.freshDatabase)
$env:ConnectionStrings__Postgres = $connection.get_ConnectionString()
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:AccountEmail__PublicOrigin = 'https://localhost:7253'
$env:AccountEmail__PickupDirectory = Join-Path $report.artifactPath 'mail'
$env:Images__StoragePath = Join-Path $report.artifactPath 'browser-images'
$env:DataProtection__KeysPath = Join-Path $report.artifactPath 'keys'
dotnet run --project NeAvito --no-build --no-launch-profile --urls https://localhost:7253
exit $LASTEXITCODE
