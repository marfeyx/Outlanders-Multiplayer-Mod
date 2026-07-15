param(
    [string]$GamePath = "C:\Program Files (x86)\Steam\steamapps\common\Outlanders",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$melonSource = Join-Path $root "tools\MelonLoader.x64.v0.7.3"

if (-not (Test-Path (Join-Path $GamePath "Outlanders.exe"))) {
    throw "Outlanders.exe was not found in '$GamePath'."
}

& (Join-Path $PSScriptRoot "Build-Package.ps1") -Configuration $Configuration

if (-not (Test-Path (Join-Path $GamePath "MelonLoader"))) {
    Copy-Item -LiteralPath (Join-Path $melonSource "MelonLoader") -Destination (Join-Path $GamePath "MelonLoader") -Recurse -Force
}

if (-not (Test-Path (Join-Path $GamePath "version.dll"))) {
    Copy-Item -LiteralPath (Join-Path $melonSource "version.dll") -Destination (Join-Path $GamePath "version.dll") -Force
}

$modsPath = Join-Path $GamePath "Mods"
New-Item -ItemType Directory -Force -Path $modsPath | Out-Null

$packageMods = Join-Path $root "artifacts\OutlandersMultiplayer\Mods"
Copy-Item -LiteralPath (Join-Path $packageMods "OutlandersMultiplayer.Mod.dll") -Destination (Join-Path $modsPath "OutlandersMultiplayer.Mod.dll") -Force
Copy-Item -LiteralPath (Join-Path $packageMods "OutlandersMultiplayer.Core.dll") -Destination (Join-Path $modsPath "OutlandersMultiplayer.Core.dll") -Force
Copy-Item -LiteralPath (Join-Path $packageMods "LiteNetLib.dll") -Destination (Join-Path $modsPath "LiteNetLib.dll") -Force

Write-Host "Installed Outlanders Multiplayer Mod to $modsPath"
