param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$env:DOTNET_CLI_HOME = Join-Path $root ".dotnet-home"

dotnet build (Join-Path $root "OutlandersMultiplayer.Mod\OutlandersMultiplayer.Mod.csproj") -c $Configuration
dotnet build (Join-Path $root "OutlandersMultiplayer.RelayServer\OutlandersMultiplayer.RelayServer.csproj") -c $Configuration

$output = Join-Path $root ".msbuild-bin\OutlandersMultiplayer.Mod\$Configuration\net6.0"
$packageMods = Join-Path $root "artifacts\OutlandersMultiplayer\Mods"
$packageRelay = Join-Path $root "artifacts\OutlandersMultiplayer\RelayServer"
New-Item -ItemType Directory -Force -Path $packageMods | Out-Null
New-Item -ItemType Directory -Force -Path $packageRelay | Out-Null

$files = @(
    "OutlandersMultiplayer.Mod.dll",
    "OutlandersMultiplayer.Core.dll",
    "LiteNetLib.dll"
)

foreach ($file in $files) {
    Copy-Item -LiteralPath (Join-Path $output $file) -Destination (Join-Path $packageMods $file) -Force
}

$relayOutput = Join-Path $root ".msbuild-bin\OutlandersMultiplayer.RelayServer\$Configuration\net8.0"
Copy-Item -Path (Join-Path $relayOutput "*") -Destination $packageRelay -Recurse -Force

Write-Host "Package written to $packageMods"
Write-Host "Relay server written to $packageRelay"
