# Outlanders Multiplayer Mod

Prototype MelonLoader IL2CPP multiplayer mod for the Steam version of Outlanders.

Current status:

- Builds a real MelonLoader `net6.0` mod DLL against MelonLoader `v0.7.3`.
- Uses LiteNetLib direct-IP UDP networking.
- Adds an in-game IMGUI overlay through reflection.
- Adds Internet relay mode for players who cannot port-forward or are not on the same LAN.
- Host can serve the latest `Endless_*.dat` sandbox save as a compressed, chunked snapshot.
- Client can join, validate the snapshot hash, and write it only into `OutlandersMultiplayerTemp`.
- Live gameplay command hooks are scaffolded but not complete; they require the MelonLoader IL2CPP generated assemblies and runtime instrumentation pass.

## Build

```powershell
$env:DOTNET_CLI_HOME=(Join-Path (Get-Location) '.dotnet-home')
dotnet build .\OutlandersMultiplayer.Mod\OutlandersMultiplayer.Mod.csproj -c Release
dotnet run --project .\OutlandersMultiplayer.Tests\OutlandersMultiplayer.Tests.csproj
```

The mod build expects MelonLoader `v0.7.3` extracted at:

```text
tools\MelonLoader.x64.v0.7.3\MelonLoader\net6\MelonLoader.dll
```

## Package

```powershell
.\scripts\Build-Package.ps1
```

This creates:

```text
artifacts\OutlandersMultiplayer\Mods\OutlandersMultiplayer.Mod.dll
artifacts\OutlandersMultiplayer\Mods\OutlandersMultiplayer.Core.dll
artifacts\OutlandersMultiplayer\Mods\LiteNetLib.dll
artifacts\OutlandersMultiplayer\RelayServer\OutlandersMultiplayer.RelayServer.dll
```

## Install Locally

Close Outlanders, then run:

```powershell
.\scripts\Install-OutlandersMultiplayer.ps1
```

The script copies MelonLoader `version.dll`/`MelonLoader\` into the Outlanders folder if missing, then copies the mod DLLs into `Mods\`.

## In Game

1. Start Outlanders.
2. Load or create an Endless/Sandbox save on the host.
3. Use the `Outlanders Multiplayer` overlay.
4. Direct/LAN: host clicks `Host Direct`; client enters the host IP/port/session key and clicks `Join Direct`.
5. Internet relay: run the relay server on any public machine, then host and client enter the relay host/port, same room code, same session key, and click `Host via Relay` / `Join Relay`.

Default direct UDP port: `17667`.
Default relay TCP port: `17668`.

## Internet Relay Mode

Direct UDP only works worldwide when the host can receive inbound UDP traffic, usually by port forwarding or using a VPN. Relay mode avoids that: both players make outbound TCP connections to a public relay server.

Run the relay on a VPS, cloud VM, or any machine with a public TCP port:

```powershell
dotnet .\artifacts\OutlandersMultiplayer\RelayServer\OutlandersMultiplayer.RelayServer.dll 17668
```

Firewall/NAT requirement:

- Relay machine: allow inbound TCP `17668`.
- Host/player machines: only need outbound TCP to the relay.

Relay behavior:

- The first host in a room owns the room.
- Clients must use the same room code and session key.
- The relay forwards encrypted-at-transport? No. Current v1 relay is plaintext TCP, so use an unguessable session key and run it only for trusted friends. TLS can be added later.

## Safety

Client snapshots are written to:

```text
%USERPROFILE%\AppData\LocalLow\Pomelo Games\Outlanders\user-<steamid>\OutlandersMultiplayerTemp\
```

The mod does not overwrite normal Outlanders save slots.

## Remaining Work

The next milestone is the IL2CPP hook pass:

- Launch with MelonLoader once to generate interop assemblies.
- Log scene names, ECS worlds/systems, sandbox controllers, save/load managers, and input/build/decree methods.
- Patch the exact methods that create build orders, demolish/cancel, priority changes, work slot changes, decrees, and time-speed changes.
- Convert client-side local actions into network intents and apply host-accepted commands back through those same game methods.
- Add deterministic world hashes over resources, buildings, construction state, day/time, and villagers.
