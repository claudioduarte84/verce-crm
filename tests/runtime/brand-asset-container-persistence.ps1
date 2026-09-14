$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$testDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '.container-cert'))
if (-not $testDirectory.StartsWith([IO.Path]::GetFullPath($PSScriptRoot), [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Refusing to create test material outside tests/runtime.'
}
if (Test-Path -LiteralPath $testDirectory) {
    throw "Temporary test directory already exists: $testDirectory"
}

New-Item -ItemType Directory -Path $testDirectory | Out-Null
$passwordText = 'temporary-container-test-password'
$password = ConvertTo-SecureString $passwordText -AsPlainText -Force
$certificate = New-SelfSignedCertificate -DnsName 'verce-container-test' -CertStoreLocation 'Cert:\CurrentUser\My' -KeyExportPolicy Exportable
$pfxPath = Join-Path $testDirectory 'verce-dp.pfx'
$passwordPath = Join-Path $testDirectory 'verce-dp-password'
Export-PfxCertificate -Cert $certificate -FilePath $pfxPath -Password $password | Out-Null
[IO.File]::WriteAllText($passwordPath, $passwordText)
Remove-Item -LiteralPath ('Cert:\CurrentUser\My\' + $certificate.Thumbprint)
$env:VERCE_DP_CERT_PATH = $pfxPath
$env:VERCE_DP_PASSWORD_PATH = $passwordPath

function Wait-Liveness {
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    do {
        Start-Sleep -Seconds 2
        try { $health = Invoke-WebRequest -Uri 'http://localhost:8080/health/live' -UseBasicParsing -TimeoutSec 3 } catch { $health = $null }
    } while (($null -eq $health -or $health.StatusCode -ne 200) -and [DateTime]::UtcNow -lt $deadline)
    if ($null -eq $health -or $health.StatusCode -ne 200) {
        docker compose --profile application logs api
        throw 'API container did not become live.'
    }
}

try {
    Push-Location $repositoryRoot
    docker compose --profile application up -d api
    Wait-Liveness
    $storageKey = (docker exec verce-postgres psql -U verce -d verce -tAc 'SELECT file_path FROM settings.brand_asset_version ORDER BY uploaded_at LIMIT 1;').Trim()
    if ([string]::IsNullOrWhiteSpace($storageKey)) { throw 'Seed did not create a synthetic brand asset.' }
    $containerIdBefore = (docker compose --profile application ps -q api).Trim()
    $hashBefore = (docker exec $containerIdBefore sha256sum ('/var/lib/verce/brand-assets/' + $storageKey)).Split(' ')[0]

    docker compose --profile application rm -s -f api | Out-Null
    docker compose --profile application up -d api
    Wait-Liveness
    $containerIdAfter = (docker compose --profile application ps -q api).Trim()
    $hashAfter = (docker exec $containerIdAfter sha256sum ('/var/lib/verce/brand-assets/' + $storageKey)).Split(' ')[0]

    if ($containerIdBefore -eq $containerIdAfter) { throw 'The application container was not recreated.' }
    if ($hashBefore -ne $hashAfter) { throw 'The synthetic asset changed or disappeared across recreation.' }
    [pscustomobject]@{
        StorageKey = $storageKey
        ContainerBefore = $containerIdBefore
        ContainerAfter = $containerIdAfter
        HashBefore = $hashBefore
        HashAfter = $hashAfter
        SameHash = $true
    } | Format-List
}
finally {
    docker compose --profile application rm -s -f api | Out-Null
    Pop-Location -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $pfxPath) { Remove-Item -LiteralPath $pfxPath }
    if (Test-Path -LiteralPath $passwordPath) { Remove-Item -LiteralPath $passwordPath }
    if (Test-Path -LiteralPath $testDirectory) { Remove-Item -LiteralPath $testDirectory }
}
