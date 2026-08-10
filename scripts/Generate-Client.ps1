param(
    [string]$OpenApiUrl
)

$ErrorActionPreference = "Stop"
$repository = Split-Path -Parent $PSScriptRoot
$specification = Join-Path $repository "openapi\honest-license-v1.json"
$output = Join-Path $repository "clients\HonestLicense.Client\HonestLicenseApiClient.g.cs"

if ($OpenApiUrl) {
    $directory = Split-Path -Parent $specification
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    Invoke-WebRequest -UseBasicParsing -Uri $OpenApiUrl -OutFile $specification
}

if (-not (Test-Path -LiteralPath $specification)) {
    throw "OpenAPI specification was not found: $specification"
}

Push-Location $repository
try {
    dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw "dotnet tool restore failed" }

    dotnet tool run nswag openapi2csclient `
        /Input:$specification `
        /Output:$output `
        /ClassName:HonestLicenseApiClient `
        /Namespace:HonestLicense.Client `
        /OperationGenerationMode:SingleClientFromOperationId `
        /GenerateClientInterfaces:true `
        /InjectHttpClient:true `
        /UseBaseUrl:true `
        /JsonLibrary:SystemTextJson `
        /JsonLibraryVersion:10.0 `
        /GenerateNullableReferenceTypes:true `
        /UseRequiredKeyword:true
    if ($LASTEXITCODE -ne 0) { throw "NSwag client generation failed" }
}
finally {
    Pop-Location
}
