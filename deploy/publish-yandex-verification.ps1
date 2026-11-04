#requires -Version 5.1

<#
.SYNOPSIS
Publishes a Yandex Webmaster ownership-verification file to the HonestFlow site.

.DESCRIPTION
Uploads one downloaded yandex_<token>.html file to a temporary server path,
installs it into /opt/honestserver/site with sudo, then verifies the public URL.
It does not alter Caddy, honestserver, or the production database.
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = "High")]
param(
    [string]$SshTarget = "pavel-shadrov@192.168.0.103",
    [string]$VerificationFile = "C:\Users\admin\Downloads\yandex_022195397d748703.html",
    [string]$WebRoot = "/opt/honestserver/site"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Assert-Command {
    param([Parameter(Mandatory = $true)][string]$Name)

    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "Required command was not found: $Name"
    }
}

function Invoke-Native {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath failed with exit code $LASTEXITCODE."
    }
}

Assert-Command "ssh"
Assert-Command "scp"

$localPath = (Resolve-Path -LiteralPath $VerificationFile -ErrorAction Stop).Path
$fileName = Split-Path -Leaf $localPath

if ($fileName -notmatch '^yandex_([a-z0-9]+)\.html$') {
    throw "Verification file name must be yandex_<token>.html. Actual: $fileName"
}

$token = $Matches[1]
$expectedContent = [IO.File]::ReadAllText($localPath)
if ($expectedContent -notmatch [Regex]::Escape("Verification: $token")) {
    throw "The downloaded file does not contain the verification token from its file name."
}

if (-not $PSCmdlet.ShouldProcess(
    "${SshTarget}:$WebRoot/$fileName",
    "Publish Yandex Webmaster verification file")) {
    Write-Host "Cancelled."
    return
}

$remoteTemporaryPath = "/tmp/$fileName.$([Guid]::NewGuid().ToString('N'))"
$remoteDestination = "$WebRoot/$fileName"

try {
    Write-Host "[1/3] Uploading verification file..."
    Invoke-Native "scp" @($localPath, "${SshTarget}:$remoteTemporaryPath")

    Write-Host "[2/3] Installing it into the website root..."
    Invoke-Native "ssh" @(
        "-tt",
        $SshTarget,
        "sudo install -d -m 755 '$WebRoot' && sudo install -o root -g root -m 644 '$remoteTemporaryPath' '$remoteDestination' && rm -f -- '$remoteTemporaryPath'"
    )

    Write-Host "[3/3] Checking the public verification URL..."
    $response = Invoke-WebRequest -UseBasicParsing -Uri "https://honestflow.ru/$fileName" -TimeoutSec 30
    if ($response.StatusCode -ne 200 -or $response.Content.Trim() -ne $expectedContent.Trim()) {
        throw "The public URL did not return the exact verification file content."
    }

    Write-Host "Ready: https://honestflow.ru/$fileName" -ForegroundColor Green
}
catch {
    throw
}
