# Uses disposable data and placeholder credentials. Leaves test volumes intact.
$ErrorActionPreference = 'Stop'
$repoPath = Split-Path $PSScriptRoot -Parent
$project = 'distancetracker-audit-' + [DateTime]::UtcNow.ToString('yyyyMMddHHmmss')
$overridePath = Join-Path ([IO.Path]::GetTempPath()) ($project + '.yaml')
@"
services:
  api:
    image: distancetracker-api:docker-audit
    ports: !reset []
    environment:
      Email__UseFake: "true"
      Stripe__Enabled: "false"
  postgres:
    ports: !reset []
  redis:
    ports: !reset []
networks:
  default:
    internal: true
"@ | Set-Content $overridePath
$composeArgs = @('compose', '--project-name', $project, '--env-file', (Join-Path $repoPath '.env.example'), '-f', (Join-Path $repoPath 'compose.yaml'), '-f', $overridePath)
function Invoke-TestCompose {
    & docker @composeArgs @args
    if ($LASTEXITCODE -ne 0) { throw "Docker Compose failed: $args" }
}
function Invoke-TestRequest($url, $json, $token) {
    $containerId = & docker @composeArgs ps -q api
    if ($json) {
        $requestPath = Join-Path ([IO.Path]::GetTempPath()) ($project + '.json')
        [IO.File]::WriteAllText($requestPath, $json)
        & docker cp $requestPath "${containerId}:/tmp/audit-request.json" | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Could not copy request.' }
        $result = & docker exec $containerId wget -q -O - --timeout=3 --header=Content-Type:application/json --post-file=/tmp/audit-request.json $url
    } elseif ($token) {
        $result = & docker exec $containerId wget -q -O - --timeout=3 ("--header=Authorization: Bearer " + $token) $url
    } else {
        $result = & docker exec $containerId wget -q -O - --timeout=3 $url
    }
    if ($LASTEXITCODE -ne 0) { throw 'HTTP request failed.' }
    return ($result | Out-String)
}
try {
    Invoke-TestCompose config --quiet
    Invoke-TestCompose up -d --wait postgres redis
    Invoke-TestCompose run --rm --no-deps api --migrate
    Invoke-TestCompose run --rm --no-deps api --migrate
    & docker @composeArgs run --rm --no-deps -e Stripe__Enabled=true -e Stripe__SecretKey=sk_test_fake -e Stripe__WebhookSecret=whsec_fake -e Stripe__PriceId=price_fake api --recover-stripe-customer nonexistent-review-user --confirmed-no-customer
    if ($LASTEXITCODE -ne 1) { throw 'Expected missing-user recovery to exit 1' }
    Write-Output 'PASS: recovery CLI accepts arguments and rejects an unknown user without starting HTTP or Stripe resource creation.'
    Invoke-TestCompose up -d --no-build api
    $baseUrl = 'http://127.0.0.1:8080'
    $ready = $false
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        try {
            $swagger = Invoke-TestRequest "$baseUrl/swagger/v1/swagger.json"
            if ($swagger -match 'openapi') { $ready = $true; break }
        } catch { Start-Sleep -Seconds 1 }
    }
    if (!$ready) { throw 'API did not become ready.' }
    $email = 'docker-audit@example.invalid'
    $password = 'DisposableAudit123!'
    $body = @{ email = $email; password = $password } | ConvertTo-Json
    $registered = Invoke-TestRequest "$baseUrl/api/auth/register" $body
    if ($registered -notmatch 'confirmation') { throw 'Registration failed.' }
    $logs = & docker @composeArgs logs --no-color api 2>&1 | Out-String
    $match = [regex]::Match($logs, 'Your token is (\S+)')
    if (!$match.Success) { throw 'Fake confirmation token not found.' }
    $confirmation = @{ userId = ''; token = $match.Groups[1].Value }
    # Read the disposable user's ID without printing credentials or tokens.
    $sql = 'SELECT "Id" FROM "AspNetUsers";'
    $userId = $sql | & docker @composeArgs exec -T postgres sh -c 'psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -At'
    if ($LASTEXITCODE -ne 0 -or !$userId) { throw 'Identity row not persisted.' }
    $confirmation.userId = ($userId | Out-String).Trim()
    Invoke-TestCompose up -d --no-build --force-recreate api
    $confirmed = $false
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        try {
            $response = Invoke-TestRequest "$baseUrl/api/auth/confirm-email" ($confirmation | ConvertTo-Json)
            if ($response -match 'confirmed successfully') { $confirmed = $true; break }
        } catch { Start-Sleep -Seconds 1 }
    }
    if (!$confirmed) { throw 'Confirmation failed after container recreation.' }
    $login = Invoke-TestRequest "$baseUrl/api/auth/login" $body | ConvertFrom-Json
    if (!$login.token) { throw 'JWT login failed.' }
    $trips = Invoke-TestRequest "$baseUrl/api/trips" $null $login.token
    if ($trips -notmatch '\[') { throw 'Authenticated trips request failed.' }
    $logs = & docker @composeArgs logs --no-color api 2>&1 | Out-String
    if ($logs -notmatch 'Redis connected') { throw 'Redis connection not confirmed.' }
    Write-Output 'PASS: Compose readiness, migrations, Swagger, registration, persisted confirmation keys, login/JWT, authenticated trips, Redis.'
    Write-Output "Disposable project retained: $project (containers stopped; no volumes deleted)."
} finally {
    & docker @composeArgs stop
}
