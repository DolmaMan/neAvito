#Requires -Version 7.0
param([ValidateSet('Start', 'Stop', 'Status')][string]$Action = 'Start', [switch]$NoBuild)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
Set-Location $taskRoot
function Run([string]$Program, [string[]]$Arguments) {
    & $Program @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Program failed ($LASTEXITCODE). See output above."
    }
}
$taskDirectory = Join-Path $taskRoot 'NeAvito/App_Data/demo'
$taskConfigPath = Join-Path $taskDirectory 'demo.settings.json'
if ($Action -eq 'Start') {
    New-Item -ItemType Directory -Path $taskDirectory -Force | Out-Null
    if (-not (Test-Path -LiteralPath $taskConfigPath)) {
        Copy-Item -LiteralPath (Join-Path $taskRoot 'demo.settings.example.json') -Destination $taskConfigPath
        throw "Fill SMTP and moderator settings in $taskConfigPath, then run this script again."
    }
    $taskConfig = Get-Content -LiteralPath $taskConfigPath -Raw | ConvertFrom-Json
    foreach ($taskField in @('From', 'Host', 'UserName', 'Password')) {
        if ([string]::IsNullOrWhiteSpace($taskConfig.AccountEmail.$taskField)) {
            throw "Fill AccountEmail.$taskField in demo.settings.json."
        }
    }
    if ([Uri]::CheckHostName($taskConfig.AccountEmail.Host) -eq [UriHostNameType]::Unknown) {
        throw 'AccountEmail.Host must be a hostname only, for example smtp.gmail.com, without https:// or a port.'
    }
    foreach ($taskField in @('ModeratorEmail', 'ModeratorPassword', 'ModeratorDisplayName')) {
        if ([string]::IsNullOrWhiteSpace($taskConfig.Demo.$taskField)) {
            throw "Fill Demo.$taskField in demo.settings.json."
        }
    }
    if ($taskConfig.AccountEmail.PublicOrigin -ne 'https://localhost:7353' -or
        $taskConfig.AccountEmail.EnableSsl -ne $true -or $taskConfig.AccountEmail.PickupDirectory -or
        $taskConfig.AccountEmail.Port -lt 1 -or $taskConfig.AccountEmail.Port -gt 65535 -or $taskConfig.AccountEmail.Port -eq 465) {
        throw 'Use https://localhost:7353, EnableSsl=true, empty PickupDirectory and a STARTTLS SMTP port (usually 587, not implicit TLS 465).'
    }
}
foreach ($taskCommand in @('docker', 'dotnet')) {
    if (-not (Get-Command $taskCommand -ErrorAction SilentlyContinue)) {
        throw "Install $taskCommand first."
    }
}
$taskDockerReady = & docker info --format '{{.OSType}}' 2>$null
if ($LASTEXITCODE -ne 0 -and $IsWindows -and $Action -eq 'Start') {
    $taskDesktop = Join-Path $env:ProgramFiles 'Docker/Docker/Docker Desktop.exe'
    if (Test-Path -LiteralPath $taskDesktop) {
        Start-Process -FilePath $taskDesktop -WindowStyle Hidden
        for ($taskWait = 0; $taskWait -lt 60; $taskWait++) {
            Start-Sleep -Seconds 2
            $taskDockerReady = & docker info --format '{{.OSType}}' 2>$null
            if ($LASTEXITCODE -eq 0) {
                break
            }
        }
    }
}
if ($LASTEXITCODE -ne 0 -or $taskDockerReady -ne 'linux') {
    throw 'Start Docker Desktop with Linux containers, then retry.'
}
$taskDirectory = Join-Path $taskRoot 'NeAvito/App_Data/demo'
$taskEnv = Join-Path $taskDirectory 'demo.env'
$taskCompose = @('compose', '--env-file', $taskEnv, '-f', 'compose.demo.yaml')
if ($Action -ne 'Start') {
    if (-not (Test-Path -LiteralPath $taskEnv)) {
        throw 'Start the demo first.'
    }
    if ($Action -eq 'Stop') {
        Run docker ($taskCompose + @('stop'))
    }
    else {
        Run docker ($taskCompose + @('ps'))
    }
    exit
}
New-Item -ItemType Directory -Path (Join-Path $taskDirectory 'https') -Force | Out-Null
if (-not (Test-Path -LiteralPath $taskEnv)) {
    $taskExistingVolume = & docker volume ls --filter 'label=com.docker.compose.project=neavito-demo' --format '{{.Name}}'
    if ($LASTEXITCODE -ne 0) {
        throw 'Cannot inspect demo volumes.'
    }
    if ($taskExistingVolume) {
        throw 'Demo volumes exist but demo.env is missing. Restore demo.env; credentials will not be replaced.'
    }
    $taskDbPassword = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(24))
    $taskCertPassword = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(24))
    $taskAccountPassword = 'Demo!' + [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(12)) + 'a1'
    @("DEMO_DB_PASSWORD=$taskDbPassword", "DEMO_CERT_PASSWORD=$taskCertPassword", "DEMO_ACCOUNT_PASSWORD=$taskAccountPassword") | Set-Content -LiteralPath $taskEnv -Encoding utf8
}
$taskSettings = @{}
foreach ($taskLine in Get-Content -LiteralPath $taskEnv) {
    if ($taskLine -match '^(DEMO_[A-Z_]+)=(.+)$') {
        $taskSettings[$Matches[1]] = $Matches[2]
    }
}
foreach ($taskKey in @('DEMO_DB_PASSWORD', 'DEMO_CERT_PASSWORD', 'DEMO_ACCOUNT_PASSWORD')) {
    if (-not $taskSettings[$taskKey]) {
        throw "Missing $taskKey in demo.env."
    }
    if (Test-Path "Env:$taskKey") {
        throw "Remove environment override $taskKey so Compose uses saved demo credentials."
    }
}
Run dotnet @('dev-certs', 'https', '--trust')
Run dotnet @('dev-certs', 'https', '--export-path', (Join-Path $taskDirectory 'https/demo.pfx'), '--password', $taskSettings.DEMO_CERT_PASSWORD)
Run docker ($taskCompose + @('up', '-d', '--wait', '--wait-timeout', '120', 'postgres'))
if (-not $NoBuild) {
    Run docker ($taskCompose + @('build', 'app'))
}
# Stop only this demo application before applying migrations; other projects are untouched.
Run docker ($taskCompose + @('stop', 'app'))
Run docker ($taskCompose + @('run', '--rm', '--no-deps', 'app', '--setup-demo'))
Run docker ($taskCompose + @('up', '-d', '--no-deps', 'app'))
$taskReady = $false
for ($taskAttempt = 0; $taskAttempt -lt 60; $taskAttempt++) {
    try {
        $taskResponse = Invoke-WebRequest 'https://localhost:7353/api/listings' -SkipCertificateCheck -TimeoutSec 3
        if ($taskResponse.StatusCode -eq 200) {
            $taskReady = $true; break
        }
    }
    catch {
        Start-Sleep -Seconds 1
    }
}
if (-not $taskReady) {
    throw 'Demo did not start. Inspect docker compose logs using the saved env file.'
}
@"
NeAvito local demo
Website: https://localhost:7353
Mail: real SMTP configured in demo.settings.json
Interface demo accounts: seller@demo.test, buyer@demo.test
Moderator: $($taskConfig.Demo.ModeratorEmail) (password set separately in demo.settings.json; confirm real email)
Initial password for the two interface accounts: $($taskSettings.DEMO_ACCOUNT_PASSWORD)
Existing passwords, roles and blocks are never reset by setup.
Database: localhost:25432 / neavito_demo / user neavito_demo
Database password: see DEMO_DB_PASSWORD in demo.env next to this file.
"@ | Set-Content -LiteralPath (Join-Path $taskDirectory 'ACCOUNTS.txt') -Encoding utf8
Write-Output 'Ready: https://localhost:7353 | Mail: real SMTP configured in demo.settings.json'
Write-Output "Accounts and initial password: $taskDirectory/ACCOUNTS.txt"

Write-Output 'Скрипт завершён. Сайт продолжает работать в Docker в фоновом режиме.'
Write-Output 'Откройте https://localhost:7353 — окно PowerShell можно закрыть.'
Write-Output 'Проверка состояния: pwsh -File scripts/Start-Demo.ps1 -Action Status'
Write-Output 'Остановка сайта: pwsh -File scripts/Start-Demo.ps1 -Action Stop'
