# Regression tests use a dedicated PostgreSQL container and mocked Stripe HTTP.
# No production databases, emails, payments, or Stripe subscriptions are touched.
$ErrorActionPreference = 'Stop'
$repoPath = Split-Path $PSScriptRoot -Parent
$stamp = [DateTime]::UtcNow.ToString('yyyyMMddHHmmss')
$container = 'distancetracker-billing-tests-' + $stamp
$database = 'billing_tests_' + $stamp
$password = [Guid]::NewGuid().ToString('N')
$previousConnection = $env:BILLING_TEST_CONNECTION
try {
    & docker run -d --name $container -e "POSTGRES_PASSWORD=$password" -e "POSTGRES_DB=$database" -p '127.0.0.1::5432' --mount "type=volume,source=${container}_data,target=/var/lib/postgresql/data" postgres:16-alpine | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not start disposable PostgreSQL.' }
    $ready = $false
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        & docker exec $container pg_isready -U postgres -d $database *> $null
        if ($LASTEXITCODE -eq 0) { $ready = $true; break }
        Start-Sleep -Seconds 1
    }
    if (!$ready) { throw 'Test database did not become ready.' }
    $inspect = & docker inspect $container | ConvertFrom-Json
    $port = $inspect.NetworkSettings.Ports.'5432/tcp'[0].HostPort
    $env:BILLING_TEST_CONNECTION = "Host=127.0.0.1;Port=$port;Database=$database;Username=postgres;Password=$password;GSS Encryption Mode=Disable"
    & dotnet run --project (Join-Path $repoPath 'tests/DistanceTracker.Billing.Tests/DistanceTracker.Billing.Tests.csproj')
    if ($LASTEXITCODE -ne 0) { throw 'Billing regression tests failed.' }
} finally {
    $env:BILLING_TEST_CONNECTION = $previousConnection
    & docker stop $container | Out-Null
    Write-Output "Test container stopped; volume retained: ${container}_data"
}

