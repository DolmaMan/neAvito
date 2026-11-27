#Requires -Version 7.0
param([Parameter(Mandatory)][string]$ReportPath, [Parameter(Mandatory)][string]$Email)
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
$report = Get-Content -LiteralPath $ReportPath -Raw | ConvertFrom-Json
if ($report.freshDatabase -notmatch '^neavito_p0_fresh_[0-9a-f]{32}$' -or $Email -notmatch '^p3-moderator-[0-9]+@example\.invalid$') {
    throw 'This helper grants a role only to a P3 fixture in an isolated verification database.'
}
$taskId = ([xml](Get-Content NeAvito/NeAvito.csproj)).Project.PropertyGroup.UserSecretsId
$taskSecrets = Get-Content (Join-Path $env:APPDATA "Microsoft/UserSecrets/$taskId/secrets.json") -Raw | ConvertFrom-Json
$taskConnection = [System.Data.Common.DbConnectionStringBuilder]::new()
$taskConnection.set_ConnectionString([string]$taskSecrets.'ConnectionStrings:Postgres')
$taskConnection.set_Item('database', $report.freshDatabase)
$taskPrevious = $env:ConnectionStrings__Postgres
try {
    $env:ConnectionStrings__Postgres = $taskConnection.get_ConnectionString()
    dotnet run --project NeAvito --no-build --launch-profile https -- --grant-moderator $Email
    if ($LASTEXITCODE -ne 0) {
        throw 'Test moderator setup failed.'
    }
}
finally {
    $env:ConnectionStrings__Postgres = $taskPrevious
}
