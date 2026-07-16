param(
    [string]$DestinationPath = (Join-Path (Join-Path $PSScriptRoot "..") "tools\MelonLoader.x64.v0.7.3"),
    [string]$ArchivePath,
    [string]$DownloadUrl = "https://github.com/LavaGang/MelonLoader/releases/download/v0.7.3/MelonLoader.x64.zip",
    [string]$ExpectedSha256 = "5B2B2F3D1CD42B59EC886C5BDC2663EDAE87A0097A4F4A8F58C0965A99DDA416"
)

$ErrorActionPreference = "Stop"
$version = "0.7.3"
$destination = [System.IO.Path]::GetFullPath($DestinationPath)
$destinationParent = Split-Path -Parent $destination
$downloadedArchive = $null
$stagingPath = $null

function Test-MelonLoaderPayload {
    param([string]$Path)

    return (Test-Path -LiteralPath (Join-Path $Path "version.dll") -PathType Leaf) -and
        (Test-Path -LiteralPath (Join-Path $Path "MelonLoader\net6\MelonLoader.dll") -PathType Leaf)
}

if (Test-MelonLoaderPayload -Path $destination) {
    Write-Host "MelonLoader v$version is ready at $destination"
    return
}

try {
    New-Item -ItemType Directory -Force -Path $destinationParent | Out-Null

    if ([string]::IsNullOrWhiteSpace($ArchivePath)) {
        $downloadedArchive = Join-Path ([System.IO.Path]::GetTempPath()) "MelonLoader.x64.v$version.$([guid]::NewGuid().ToString('N')).zip"
        Write-Host "Downloading MelonLoader v$version from $DownloadUrl"

        try {
            Invoke-WebRequest -Uri $DownloadUrl -OutFile $downloadedArchive -UseBasicParsing
        }
        catch {
            throw "Could not download MelonLoader v$version from '$DownloadUrl'. Download that archive manually and extract it to '$destination'. $($_.Exception.Message)"
        }

        $archive = $downloadedArchive
    }
    else {
        $archive = (Resolve-Path -LiteralPath $ArchivePath).Path
    }

    $actualSha256 = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
    if ($actualSha256 -ne $ExpectedSha256) {
        throw "MelonLoader v$version archive checksum mismatch for '$archive'. Expected $ExpectedSha256 but received $actualSha256. Download the official archive from '$DownloadUrl' and extract it to '$destination'."
    }

    $stagingPath = Join-Path $destinationParent ".melonloader-v$version-$([guid]::NewGuid().ToString('N'))"
    Expand-Archive -LiteralPath $archive -DestinationPath $stagingPath -Force

    if (-not (Test-MelonLoaderPayload -Path $stagingPath)) {
        throw "The MelonLoader v$version archive does not contain version.dll and MelonLoader\net6\MelonLoader.dll. Download the official archive from '$DownloadUrl' and extract it to '$destination'."
    }

    if (Test-Path -LiteralPath $destination) {
        Remove-Item -LiteralPath $destination -Recurse -Force
    }

    Move-Item -LiteralPath $stagingPath -Destination $destination
    $stagingPath = $null
    Write-Host "MelonLoader v$version is ready at $destination"
}
finally {
    if ($stagingPath -and (Test-Path -LiteralPath $stagingPath)) {
        Remove-Item -LiteralPath $stagingPath -Recurse -Force
    }

    if ($downloadedArchive -and (Test-Path -LiteralPath $downloadedArchive)) {
        Remove-Item -LiteralPath $downloadedArchive -Force
    }
}
