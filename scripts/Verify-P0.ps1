#Requires -Version 7.0
param([switch]$SkipRestore)
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
function Run([string]$Program, [string[]]$Arguments) {
    & $Program @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Program failed ($LASTEXITCODE)" }
}
$taskId = [guid]::NewGuid().ToString('N')
$artifactPath = Join-Path (Get-Location) "NeAvito/App_Data/p0/$taskId"
New-Item -ItemType Directory -Path $artifactPath -Force | Out-Null
$secretId = ([xml](Get-Content NeAvito/NeAvito.csproj)).Project.PropertyGroup.UserSecretsId
$secretPath = Join-Path $env:APPDATA "Microsoft/UserSecrets/$secretId/secrets.json"
$secrets = Get-Content -LiteralPath $secretPath -Raw | ConvertFrom-Json
$connection = [System.Data.Common.DbConnectionStringBuilder]::new()
$connection.set_ConnectionString([string]$secrets.'ConnectionStrings:Postgres')
if ($connection.get_Item('database') -ne 'neavito' -or $connection.get_Item('port') -ne '15432') {
    throw 'This verification script targets the local compose database neavito:15432 only.'
}
$oldConnection = $env:ConnectionStrings__Postgres
$oldPgTests = $env:NEAVITO_TEST_POSTGRES
$freshDb = "neavito_p0_fresh_$taskId"
$upgradeDb = "neavito_p0_upgrade_$taskId"
$restoredDb = "neavito_p0_restore_$taskId"
$containerDump = "/tmp/neavito_p0_$taskId.dump"
try {
    if (!$SkipRestore) { Run dotnet @('restore', 'NeAvito.slnx', '--force', '--no-cache') }
    Run dotnet @('build', 'NeAvito.slnx', '--no-restore', '-m:1')
    $env:NEAVITO_TEST_POSTGRES = '1'
    Run dotnet @('test', 'NeAvito.slnx', '--no-build', '--no-restore', '-m:1', '--logger', 'trx;LogFileName=p0.trx')
    foreach ($db in @($freshDb, $upgradeDb, $restoredDb)) {
        Run docker @('compose', 'exec', '-T', 'postgres', 'createdb', '-U', 'neavito', $db)
    }
    $connection.set_Item('database', $freshDb)
    $env:ConnectionStrings__Postgres = $connection.get_ConnectionString()
    Run dotnet @('ef', 'database', 'update', '--project', 'NeAvito', '--no-build')
    Run dotnet @('ef', 'database', 'update', '--project', 'NeAvito', '--no-build')
    $connection.set_Item('database', $upgradeDb)
    $env:ConnectionStrings__Postgres = $connection.get_ConnectionString()
    Run dotnet @('ef', 'database', 'update', '20261007054650_ProfileDeliveryAddress', '--project', 'NeAvito', '--no-build')
    $fixtureSql = @'
INSERT INTO "AspNetUsers" ("Id","UserName","NormalizedUserName","Email","NormalizedEmail","EmailConfirmed","PasswordHash","SecurityStamp","ConcurrencyStamp","PhoneNumberConfirmed","TwoFactorEnabled","LockoutEnabled","AccessFailedCount","DeliveryAddress","DisplayName")
VALUES ('p0-legacy','legacy@example.invalid','LEGACY@EXAMPLE.INVALID','legacy@example.invalid','LEGACY@EXAMPLE.INVALID',false,'fixture-hash','fixture-stamp','fixture-version',false,false,true,0,'','Legacy');
'@
    Run docker @('compose','exec','-T','postgres','psql','-U','neavito','-d',$upgradeDb,'-v','ON_ERROR_STOP=1','-c',$fixtureSql)
    Run dotnet @('ef', 'database', 'update', '--project', 'NeAvito', '--no-build')
    $verifySql = @'
SELECT "Id" || ':' || "EmailConfirmed"::text || ':' || "PasswordHash" || ':' || "SecurityStamp" FROM "AspNetUsers" WHERE "Id"='p0-legacy';
'@
    $legacy = & docker compose exec -T postgres psql -U neavito -d $upgradeDb -tAc $verifySql
    if ($LASTEXITCODE -ne 0 -or $legacy.Trim() -ne 'p0-legacy:false:fixture-hash:fixture-stamp') { throw 'Legacy identity changed unexpectedly.' }
    Run docker @('compose','exec','-T','postgres','pg_dump','-U','neavito','-d','neavito','-Fc','-f',$containerDump)
    $containerId = (& docker compose ps -q postgres).Trim()
    if ($LASTEXITCODE -ne 0 -or !$containerId) { throw 'Container not found.' }
    Run docker @('cp', "${containerId}:$containerDump", (Join-Path $artifactPath 'neavito.dump'))
    Run docker @('compose','exec','-T','postgres','pg_restore','-U','neavito','--exit-on-error','--no-owner','-d',$restoredDb,$containerDump)
    $fingerprintSql = @'
SELECT table_name || ':' || (xpath('/row/c/text()', query_to_xml(format('SELECT count(*)::text || '':'' || coalesce(md5(string_agg(md5(row_to_json(t)::text), '''' ORDER BY md5(row_to_json(t)::text))), ''empty'') AS c FROM %I.%I t', table_schema, table_name), false, true, '')))[1]::text
FROM information_schema.tables WHERE table_schema='public' AND table_type='BASE TABLE' ORDER BY table_name;
'@
    $original = & docker compose exec -T postgres psql -U neavito -d neavito -tAc $fingerprintSql
    if ($LASTEXITCODE -ne 0) { throw 'Source verification failed.' }
    $restored = & docker compose exec -T postgres psql -U neavito -d $restoredDb -tAc $fingerprintSql
    if ($LASTEXITCODE -ne 0 -or (Compare-Object $original $restored)) { throw 'Backup verification differs (stop writes and retry if source changed).' }
    $imagesPath = Join-Path (Get-Location) 'NeAvito/App_Data/product-images'
    if ($secrets.'Images:StoragePath') { $imagesPath = $secrets.'Images:StoragePath' }
    $imageCount = 0
    if (Test-Path -LiteralPath $imagesPath) {
        $backupImages = Join-Path $artifactPath 'product-images'
        Copy-Item -LiteralPath $imagesPath -Destination $backupImages -Recurse
        foreach ($file in Get-ChildItem -LiteralPath $imagesPath -File -Recurse) {
            $relative = [IO.Path]::GetRelativePath($imagesPath, $file.FullName)
            if ((Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath (Join-Path $backupImages $relative)).Hash) { throw 'Image backup mismatch.' }
            $imageCount++
        }
    }
    [ordered]@{ runId=$taskId; freshDatabase=$freshDb; upgradeDatabase=$upgradeDb; restoredDatabase=$restoredDb;
        artifactPath=$artifactPath; migratedLegacyUserPreserved=$true; restoredTables=$original.Count; imagesVerified=$imageCount;
        containerDump=$containerDump; completedAtUtc=[DateTime]::UtcNow.ToString('O') } |
        ConvertTo-Json | Set-Content (Join-Path $artifactPath 'result.json')
    Write-Output "P0 database checks passed. Report: $artifactPath/result.json"
    Write-Output "Temporary databases retained for browser checks; clean up only the three exact database names in result.json."
} finally {
    $env:ConnectionStrings__Postgres = $oldConnection
    $env:NEAVITO_TEST_POSTGRES = $oldPgTests
}
