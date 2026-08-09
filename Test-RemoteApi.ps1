param(
    [string]$BaseUrl = "http://192.168.0.103:5498",
    [string]$Application = "HonestFlow",
    [string]$Login,
    [string]$Password,
    [string]$DeviceId
)

$ErrorActionPreference = "Stop"
$BaseUrl = $BaseUrl.TrimEnd("/")
$uri = [Uri]$BaseUrl

Write-Host "Testing Honest License API: $BaseUrl" -ForegroundColor Cyan

if (-not (Test-NetConnection -ComputerName $uri.Host -Port $uri.Port -InformationLevel Quiet)) {
    throw "TCP endpoint $($uri.Host):$($uri.Port) is unavailable"
}
Write-Host "[OK] TCP endpoint is available" -ForegroundColor Green

try {
    $version = Invoke-RestMethod -Method Get -Uri "$BaseUrl/api/version/current/$([Uri]::EscapeDataString($Application))" -TimeoutSec 10
    Write-Host "[OK] Version API responded" -ForegroundColor Green
    $version | Format-List
}
catch {
    $statusCode = [int]$_.Exception.Response.StatusCode
    if ($statusCode -eq 404) {
        Write-Host "[OK] HTTP API responded; application '$Application' is absent from the database (404)" -ForegroundColor Yellow
    }
    else {
        throw
    }
}

$credentialsSpecified = $Login -or $Password -or $DeviceId
if ($credentialsSpecified -and (-not $Login -or -not $Password -or -not $DeviceId)) {
    throw "Login, Password and DeviceId must all be specified for the authentication test"
}

if ($Login) {
    $body = @{
        login = $Login
        password = $Password
        deviceId = $DeviceId
        deviceName = "PowerShell API test"
    } | ConvertTo-Json

    $session = Invoke-RestMethod `
        -Method Post `
        -Uri "$BaseUrl/api/auth/login" `
        -ContentType "application/json; charset=utf-8" `
        -Body ([Text.Encoding]::UTF8.GetBytes($body)) `
        -TimeoutSec 10

    Write-Host "[OK] Authentication succeeded" -ForegroundColor Green
    Write-Host "Device registration required: $($session.deviceRegistrationRequired)"

    $headers = @{ Authorization = "Bearer $($session.accessToken)" }
    try {
        $license = Invoke-RestMethod -Method Get -Uri "$BaseUrl/api/license/current" -Headers $headers -TimeoutSec 10
        Write-Host "[OK] Active license received" -ForegroundColor Green
        $license | Format-List
    }
    catch {
        $statusCode = [int]$_.Exception.Response.StatusCode
        Write-Host "[WARN] License request returned HTTP $statusCode" -ForegroundColor Yellow
    }
}
else {
    Write-Host "Authentication skipped. Supply -Login, -Password and -DeviceId for the full test." -ForegroundColor Yellow
}

Write-Host "Test completed." -ForegroundColor Green
