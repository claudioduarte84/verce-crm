$ErrorActionPreference = 'Stop'

function Start-CandidateHost([string] $connection, [int] $port, [string] $logPath) {
    $environment = @{
        'ASPNETCORE_ENVIRONMENT' = 'Development'
        'ASPNETCORE_URLS' = "http://127.0.0.1:$port"
        'ConnectionStrings__Verce' = $connection
        'Settings__SeedOnStartup' = 'true'
        'Outbox__SchedulingEnabled' = 'false'
    }
    Start-Process dotnet -ArgumentList @('run', '--no-build', '--project', 'src/Verce.Api', '--no-launch-profile') `
        -WorkingDirectory $repositoryRoot -Environment $environment -RedirectStandardOutput $logPath `
        -RedirectStandardError ($logPath + '.err') -WindowStyle Hidden -PassThru
}

function Wait-Readiness([int] $port, [string] $logPath) {
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    do {
        Start-Sleep -Seconds 2
        try { $health = Invoke-WebRequest -Uri "http://127.0.0.1:$port/health/ready" -UseBasicParsing -TimeoutSec 3 } catch { $health = $null }
    } while (($null -eq $health -or $health.StatusCode -ne 200) -and [DateTime]::UtcNow -lt $deadline)
    if ($null -eq $health -or $health.StatusCode -ne 200) {
        Get-Content $logPath, ($logPath + '.err') -ErrorAction SilentlyContinue
        throw "Host on port $port did not become ready."
    }
    $health.StatusCode
}

function Stop-CandidateHost($process, [string] $logPath) {
    if ($null -ne $process -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force }
    Remove-Item -LiteralPath $logPath -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath ($logPath + '.err') -ErrorAction SilentlyContinue
}

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$suffix = [Guid]::NewGuid().ToString('N')
$freshDatabase = 'verce_wave1_fresh_' + $suffix
$upgradeDatabase = 'verce_wave1_upgrade_' + $suffix
$freshConnection = "Host=localhost;Port=5432;Database=$freshDatabase;Username=verce;Password=verce_dev_only"
$upgradeConnection = "Host=localhost;Port=5432;Database=$upgradeDatabase;Username=verce;Password=verce_dev_only"
$freshLog = Join-Path ([IO.Path]::GetTempPath()) ($freshDatabase + '.log')
$upgradeLog = Join-Path ([IO.Path]::GetTempPath()) ($upgradeDatabase + '.log')
$freshProcess = $null
$upgradeProcess = $null

try {
    Push-Location $repositoryRoot

    docker exec verce-postgres createdb -U verce $freshDatabase
    dotnet ef database update --project src/Verce.Platform --startup-project src/Verce.Api --connection $freshConnection --no-build
    $freshProcess = Start-CandidateHost $freshConnection 7311 $freshLog
    $freshHealth = Wait-Readiness 7311 $freshLog
    $freshCounts = (docker exec verce-postgres psql -U verce -d $freshDatabase -tAc "SELECT (SELECT count(*) FROM information_schema.tables WHERE table_schema IN ('customers','settings')) || ',' || (SELECT count(*) FROM settings.app_setting) || ',' || (SELECT count(*) FROM information_schema.tables WHERE table_schema='platform' AND table_name LIKE 'qrtz_%');").Trim()
    Stop-CandidateHost $freshProcess $freshLog
    $freshProcess = $null

    docker exec verce-postgres createdb -U verce $upgradeDatabase
    dotnet ef database update 20260908004426_AddQuartzSchema --project src/Verce.Platform --startup-project src/Verce.Api --connection $upgradeConnection --no-build
    $previousConnection = $env:ConnectionStrings__Verce
    $previousEnvironment = $env:ASPNETCORE_ENVIRONMENT
    $previousScheduling = $env:Outbox__SchedulingEnabled
    try {
        $env:ConnectionStrings__Verce = $upgradeConnection
        $env:ASPNETCORE_ENVIRONMENT = 'Development'
        $env:Outbox__SchedulingEnabled = 'false'
        $bootstrapOutput = dotnet run --no-build --project src/Verce.Api -- bootstrap-owner --email s1-survivor@example.test --name 'S1 Survivor'
        if ($LASTEXITCODE -ne 0 -or ($bootstrapOutput -join [Environment]::NewLine) -notmatch 'token=') {
            throw "S1 representative Owner bootstrap failed (exit $LASTEXITCODE): $($bootstrapOutput -join [Environment]::NewLine)"
        }
    }
    finally {
        $env:ConnectionStrings__Verce = $previousConnection
        $env:ASPNETCORE_ENVIRONMENT = $previousEnvironment
        $env:Outbox__SchedulingEnabled = $previousScheduling
    }
    dotnet ef database update --project src/Verce.Platform --startup-project src/Verce.Api --connection $upgradeConnection --no-build
    $upgradeProcess = Start-CandidateHost $upgradeConnection 7312 $upgradeLog
    $upgradeHealth = Wait-Readiness 7312 $upgradeLog
    $upgradeCounts = (docker exec verce-postgres psql -U verce -d $upgradeDatabase -tAc "SELECT (SELECT count(*) FROM platform.user_role ur JOIN platform.role r ON r.id=ur.role_id WHERE r.name='Owner') || ',' || (SELECT count(*) FROM settings.app_setting) || ',' || (SELECT count(*) FROM information_schema.tables WHERE table_schema='platform' AND table_name LIKE 'qrtz_%');").Trim()

    [pscustomobject]@{
        FreshHealth = $freshHealth
        FreshCountsS2SettingsQuartz = $freshCounts
        UpgradeHealth = $upgradeHealth
        UpgradeCountsOwnerSettingsQuartz = $upgradeCounts
        S1Terminal = '20260908004426_AddQuartzSchema'
    } | Format-List
}
finally {
    Stop-CandidateHost $freshProcess $freshLog
    Stop-CandidateHost $upgradeProcess $upgradeLog
    Pop-Location -ErrorAction SilentlyContinue
    docker exec verce-postgres dropdb -U verce --if-exists $freshDatabase
    docker exec verce-postgres dropdb -U verce --if-exists $upgradeDatabase
}
