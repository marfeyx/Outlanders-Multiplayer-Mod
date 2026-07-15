$ErrorActionPreference = "Stop"
$bootstrap = Join-Path $PSScriptRoot "Bootstrap-MelonLoader.ps1"
$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) "OutlandersMultiplayer.BootstrapTests.$([guid]::NewGuid().ToString('N'))"

function Assert-True {
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

try {
    $payload = Join-Path $testRoot "payload"
    $managed = Join-Path $payload "MelonLoader\net6"
    New-Item -ItemType Directory -Force -Path $managed | Out-Null
    Set-Content -LiteralPath (Join-Path $payload "version.dll") -Value "fixture bootstrap" -NoNewline
    Set-Content -LiteralPath (Join-Path $managed "MelonLoader.dll") -Value "fixture managed assembly" -NoNewline

    $validArchive = Join-Path $testRoot "valid.zip"
    Compress-Archive -Path (Join-Path $payload "*") -DestinationPath $validArchive
    $validHash = (Get-FileHash -LiteralPath $validArchive -Algorithm SHA256).Hash
    $destination = Join-Path $testRoot "installed"

    & $bootstrap -DestinationPath $destination -ArchivePath $validArchive -ExpectedSha256 $validHash | Out-Null
    Assert-True (Test-Path -LiteralPath (Join-Path $destination "version.dll") -PathType Leaf) "Bootstrap did not install version.dll."
    Assert-True (Test-Path -LiteralPath (Join-Path $destination "MelonLoader\net6\MelonLoader.dll") -PathType Leaf) "Bootstrap did not install MelonLoader.dll."

    Set-Content -LiteralPath (Join-Path $destination "bootstrap-marker.txt") -Value "keep" -NoNewline
    & $bootstrap -DestinationPath $destination -ArchivePath $validArchive -ExpectedSha256 $validHash | Out-Null
    Assert-True (Test-Path -LiteralPath (Join-Path $destination "bootstrap-marker.txt") -PathType Leaf) "Bootstrap replaced an already valid installation."

    $checksumRejected = $false
    try {
        & $bootstrap -DestinationPath (Join-Path $testRoot "bad-checksum") -ArchivePath $validArchive -ExpectedSha256 ("0" * 64) | Out-Null
    }
    catch {
        $checksumRejected = $_.Exception.Message -like "*checksum mismatch*"
    }
    Assert-True $checksumRejected "Bootstrap did not reject an archive with the wrong checksum."

    $invalidPayload = Join-Path $testRoot "invalid-payload"
    New-Item -ItemType Directory -Force -Path $invalidPayload | Out-Null
    Set-Content -LiteralPath (Join-Path $invalidPayload "readme.txt") -Value "not MelonLoader" -NoNewline
    $invalidArchive = Join-Path $testRoot "invalid.zip"
    Compress-Archive -Path (Join-Path $invalidPayload "*") -DestinationPath $invalidArchive
    $invalidHash = (Get-FileHash -LiteralPath $invalidArchive -Algorithm SHA256).Hash

    $layoutRejected = $false
    try {
        & $bootstrap -DestinationPath (Join-Path $testRoot "bad-layout") -ArchivePath $invalidArchive -ExpectedSha256 $invalidHash | Out-Null
    }
    catch {
        $layoutRejected = $_.Exception.Message -like "*does not contain version.dll*"
    }
    Assert-True $layoutRejected "Bootstrap did not reject an archive with an invalid layout."

    Write-Host "PASS MelonLoader bootstrap installs, is idempotent, and rejects invalid archives."
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
