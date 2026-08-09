$ErrorActionPreference = "Stop"

$ApiBaseUrl = "http://localhost:5074"

# Укажите данные реального клиента и зарегистрированного устройства.
$Login = "PUT_LOGIN_HERE"
$Password = "PUT_PASSWORD_HERE"
$DeviceId = "PUT_DEVICE_ID_HERE"

if ($Login -like "PUT_*" -or $Password -like "PUT_*" -or $DeviceId -like "PUT_*") {
    throw "Сначала заполните Login, Password и DeviceId в начале файла."
}

Write-Host "1. Авторизация..." -ForegroundColor Cyan

$loginJson = @{
    login = $Login
    password = $Password
    deviceId = $DeviceId
} | ConvertTo-Json

# Явно отправляем UTF-8, чтобы русские имена клиентов не искажались
$loginResponse = Invoke-RestMethod `
    -Method Post `
    -Uri "$ApiBaseUrl/api/auth/login" `
    -ContentType "application/json; charset=utf-8" `
    -Body ([Text.Encoding]::UTF8.GetBytes($loginJson))

$loginResponse | Format-List

$headers = @{
    Authorization = "Bearer $($loginResponse.accessToken)"
}

Write-Host "2. Текущая лицензия..." -ForegroundColor Cyan

$licenseResponse = Invoke-RestMethod `
    -Method Get `
    -Uri "$ApiBaseUrl/api/license/current" `
    -Headers $headers

$licenseResponse | Format-List

Write-Host "3. Версия HonestFlow..." -ForegroundColor Cyan

$versionResponse = Invoke-RestMethod `
    -Method Get `
    -Uri "$ApiBaseUrl/api/version/current/HonestFlow"

$versionResponse | Format-List

Write-Host "Все запросы выполнены успешно." -ForegroundColor Green

# Регистрация нового устройства изменяет базу, поэтому по умолчанию выключена.
# Чтобы проверить её, сначала войдите с новым Device ID, затем раскомментируйте блок:
#
# $registerJson = @{
#     deviceId = $DeviceId
#     name = "Тестовая касса"
# } | ConvertTo-Json
#
# Invoke-RestMethod `
#     -Method Post `
#     -Uri "$ApiBaseUrl/api/device/register" `
#     -Headers $headers `
#     -ContentType "application/json; charset=utf-8" `
#     -Body ([Text.Encoding]::UTF8.GetBytes($registerJson))
