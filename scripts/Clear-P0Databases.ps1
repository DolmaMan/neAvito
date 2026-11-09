#Requires -Version 7.0
param([Parameter(Mandatory)][string]$ReportPath)
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
$report = Get-Content -LiteralPath $ReportPath -Raw | ConvertFrom-Json
if ($report.runId -notmatch '^[0-9a-f]{32}$') { throw 'Invalid run ID.' }
foreach ($kind in @('fresh','upgrade','restore')) {
    $property = @{ fresh='freshDatabase'; upgrade='upgradeDatabase'; restore='restoredDatabase' }[$kind]
    if ($report.$property -cne "neavito_p0_${kind}_$($report.runId)") { throw 'Unsafe database name.' }
}
foreach ($database in @($report.freshDatabase,$report.upgradeDatabase,$report.restoredDatabase)) {
    docker compose exec -T postgres dropdb -U neavito --if-exists $database
    if ($LASTEXITCODE -ne 0) { throw 'Could not remove temporary database; stop the P0 server first.' }
}
$expectedDump = "/tmp/neavito_p0_$($report.runId).dump"
if ($report.containerDump -cne $expectedDump) { throw 'Unsafe temporary dump path.' }
docker compose exec -T postgres rm -f -- $expectedDump
if ($LASTEXITCODE -ne 0) { throw 'Could not remove temporary container dump.' }
Write-Output 'P0 temporary databases removed. Local backup and evidence retained.'
