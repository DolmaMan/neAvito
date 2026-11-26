#Requires -Version 7.0
param([switch]$IncludeModerator)
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
$taskLine = Get-Content NeAvito/App_Data/demo/demo.env | Where-Object { $_ -like 'DEMO_ACCOUNT_PASSWORD=*' }
$taskPassword = $taskLine.Split('=', 2)[1]
$taskAccounts = @(
    @{ email = 'seller@demo.test'; password = $taskPassword; moderator = $false },
    @{ email = 'buyer@demo.test'; password = $taskPassword; moderator = $false }
)
if ($IncludeModerator) {
    $taskConfig = Get-Content NeAvito/App_Data/demo/demo.settings.json -Raw | ConvertFrom-Json
    $taskAccounts += @{ email = $taskConfig.Demo.ModeratorEmail; password = $taskConfig.Demo.ModeratorPassword; moderator = $true }
}
foreach ($taskAccount in $taskAccounts) {
    $taskSession = [Microsoft.PowerShell.Commands.WebRequestSession]::new()
    $taskLogin = Invoke-WebRequest https://localhost:7353/api/auth/login -Method Post -SkipCertificateCheck -SkipHttpErrorCheck -WebSession $taskSession -Headers @{ 'X-NeAvito-Request' = '1' } -ContentType application/json -Body (@{ email = $taskAccount.email; password = $taskAccount.password } | ConvertTo-Json)
    if ($taskLogin.StatusCode -ne 200) { throw 'Login failed: confirm email first; this check uses the initial configured password.' }
    $taskProfile = Invoke-RestMethod https://localhost:7353/api/profile -SkipCertificateCheck -WebSession $taskSession
    if ($taskProfile.canModerate -ne $taskAccount.moderator) { throw 'Unexpected permissions.' }
    $taskQueue = Invoke-WebRequest https://localhost:7353/api/moderation/reports -SkipCertificateCheck -SkipHttpErrorCheck -WebSession $taskSession
    $taskExpected = if ($taskAccount.moderator) { 200 } else { 403 }
    if ($taskQueue.StatusCode -ne $taskExpected) { throw 'Unexpected moderation access.' }
}
$taskCatalog = Invoke-WebRequest https://localhost:7353/api/listings -SkipCertificateCheck
if ($taskCatalog.StatusCode -ne 200) { throw 'Catalog unavailable.' }
Write-Output 'Account permissions and catalog verified. No emails sent. Verify real delivery manually with your own mailbox.'
