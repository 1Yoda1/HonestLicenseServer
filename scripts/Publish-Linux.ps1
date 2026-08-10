param(
    [string]$Runtime = "linux-x64"
)

$ErrorActionPreference = "Stop"
$repository = Split-Path -Parent $PSScriptRoot
$output = Join-Path $repository "artifacts\$Runtime"

Push-Location $repository
try {
    dotnet publish HonestLicenseServer.csproj `
        --configuration Release `
        --runtime $Runtime `
        --self-contained true `
        --output $output `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true
    if ($LASTEXITCODE -ne 0) { throw "Linux publish failed" }

    $databaseFiles = Get-ChildItem -LiteralPath $output -File |
        Where-Object { $_.Extension -in ".db", ".db-shm", ".db-wal" }
    if ($databaseFiles) {
        throw "Publish output unexpectedly contains a database file."
    }

    Write-Host "Linux artifact: $output\HonestLicenseServer"
}
finally {
    Pop-Location
}
