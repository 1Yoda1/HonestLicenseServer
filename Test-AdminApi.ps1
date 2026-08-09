$ErrorActionPreference = "Stop"

$ApiBaseUrl = "http://localhost:5074"
$AdminKey = "local-dev-admin-key-change-me"
$headers = @{ "X-Admin-Key" = $AdminKey }

Write-Host "Клиенты" -ForegroundColor Cyan
$clients = Invoke-RestMethod -Uri "$ApiBaseUrl/api/admin/clients" -Headers $headers
$clients | Format-Table clientId, name, isActive, deviceCount, licenseCount

Write-Host "Устройства" -ForegroundColor Cyan
$devices = Invoke-RestMethod -Uri "$ApiBaseUrl/api/admin/devices" -Headers $headers
$devices | Format-Table clientName, deviceId, name, isActive

Write-Host "Лицензии" -ForegroundColor Cyan
$licenses = Invoke-RestMethod -Uri "$ApiBaseUrl/api/admin/licenses" -Headers $headers
$licenses | Format-Table id, clientName, deviceId, revision, isActive, hasSignature

Write-Host "Версии" -ForegroundColor Cyan
$versions = Invoke-RestMethod -Uri "$ApiBaseUrl/api/admin/versions" -Headers $headers
$versions | Format-Table application, currentVersion, updatedAtUtc

Write-Host "Административные GET-запросы работают." -ForegroundColor Green
