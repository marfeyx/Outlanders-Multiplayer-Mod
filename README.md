# Outlanders Multiplayer Mod

Prototype MelonLoader mod for the Steam version of Outlanders.

This project is building toward friend-hosted multiplayer for Sandbox/Endless saves. The current build already installs as a real MelonLoader mod, shows an in-game menu, supports LAN/direct networking, supports online relay join codes, and can transfer a host save snapshot into a new client Sandbox slot. Full live gameplay synchronization is still in progress.

## Current Status

- Works with Windows Steam Outlanders.
- Uses MelonLoader `v0.7.3` for Unity IL2CPP mod loading.
- Adds an in-game `Outlanders Multiplayer` menu.
- Supports `Host Online` and `Join Code` through a relay server.
- Supports `Host Direct` and `Join Direct` for LAN, VPN, or port-forwarded direct IP play.
- Sends the host's explicitly selected `Endless_*.dat` save as a compressed snapshot.
- Registers client snapshots as new `Endless_N.dat` slots in the active Outlanders save game.
- Does not overwrite normal Outlanders save slots.
- Build placement is captured from `SitePlacementSystem.PlaceOneSite`, validated by the host, and applied on every peer through the same game method.
- Direct and relay sessions carry validated player intents, host-assigned commands, and per-tick state hashes.
- Build cancellation, villagers, resources, decrees, and time sync are not complete yet.

## Fast Install

1. Close Outlanders.
2. Open PowerShell in this project folder:

```powershell
cd "C:\Outlanders Multiplayer Mod"
```

3. Run the installer:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Install-OutlandersMultiplayer.ps1
```

4. Start Outlanders from Steam.
5. Check the MelonLoader console. You should see:

```text
Outlanders Multiplayer v0.1.0
```

The installer copies MelonLoader into the Outlanders game folder if it is missing, then copies the mod files into the game's `Mods` folder.

On the first run, the build automatically downloads the official [MelonLoader v0.7.3 x64 archive](https://github.com/LavaGang/MelonLoader/releases/download/v0.7.3/MelonLoader.x64.zip), verifies its SHA-256 checksum, and extracts it to:

```text
tools\MelonLoader.x64.v0.7.3\
```

The fast-install path therefore requires an internet connection and the .NET 8 SDK on the first run. To bootstrap the dependency separately, run `powershell -ExecutionPolicy Bypass -File .\scripts\Bootstrap-MelonLoader.ps1`.

## Manual Install

Use this only if the install script does not work.

1. Close Outlanders.
2. Install MelonLoader `v0.7.3` x64 into the Outlanders game folder.
3. Create this folder if it does not exist:

```text
C:\Program Files (x86)\Steam\steamapps\common\Outlanders\Mods
```

4. Copy these files into that `Mods` folder:

```text
artifacts\OutlandersMultiplayer\Mods\OutlandersMultiplayer.Mod.dll
artifacts\OutlandersMultiplayer\Mods\OutlandersMultiplayer.Core.dll
artifacts\OutlandersMultiplayer\Mods\LiteNetLib.dll
```

5. Start Outlanders.

## Online Play With A Join Code

This is the flow for playing with a friend over the internet:

1. Start a relay server on a public machine.
2. Host opens Outlanders and loads a Sandbox/Endless save.
3. Host opens the multiplayer menu.
4. Host opens `Advanced` once and sets:

```text
Relay Host = public IP or domain of the relay server
Relay Port = 17668
```

5. Host clicks `Host Online`.
6. Host clicks `Copy Code`.
7. Host sends that one code to the friend.
8. Friend pastes the code into `Join Code`.
9. Friend clicks `Join Code`.

Your friend should not need to type relay host, relay port, session key, or room details. Those are packed inside the join code.

## Running The Relay Server

Relay mode is what makes worldwide play possible without port forwarding on the host's home router.

The relay assigns each joined client a connection ID within its room. Handshake responses and snapshot frames use that ID to reach only the joining client, while host gameplay frames sent without a target are broadcast to every client in the room.

Run this on a VPS, cloud VM, rented server, or any computer that has a public TCP port:

```powershell
.\artifacts\OutlandersMultiplayer\RelayServer\OutlandersMultiplayer.RelayServer.exe 17668
```

If you prefer running the DLL:

```powershell
dotnet .\artifacts\OutlandersMultiplayer\RelayServer\OutlandersMultiplayer.RelayServer.dll 17668
```

The optional second and third arguments set the initial join-handshake timeout and the later client-read timeout in seconds. Defaults are 10 seconds and 120 seconds:

```powershell
.\artifacts\OutlandersMultiplayer\RelayServer\OutlandersMultiplayer.RelayServer.exe 17668 10 120
```

Connections that do not send a complete join frame or stop sending frames after joining are closed without stopping the relay or disconnecting other rooms.

Firewall requirement:

- Relay server must allow inbound TCP `17668`.
- Host and friend only need outbound TCP access to the relay.

If the game says `Set a public relay host in Advanced before hosting online`, the relay host is still set to `127.0.0.1`, `localhost`, or another local-only address. That cannot create a join code for a friend across the world. Set it to the public IP or domain of the relay server.

## LAN, VPN, Or Port Forwarding

Direct mode is separate from join-code relay mode.

Use direct mode only when your friend can directly reach the host computer:

- same LAN,
- same VPN,
- or host has UDP port forwarding configured.

Default direct port:

```text
17667 UDP
```

Host:

1. Load a Sandbox/Endless save.
2. Open `Advanced`.
3. Click `Host Direct`.

Friend:

1. Open `Advanced`.
2. Enter the host IP in `Direct IP`.
3. Enter `17667` in `Direct Port`.
4. Click `Join Direct`.

## Save Safety

Normal Outlanders saves are not overwritten by client snapshot joining.

The multiplayer overlay shows the exact `user-*\Endless_*.dat` save selected for hosting. Only top-level saves in `user-*` folders are eligible; backup subfolders and `OutlandersMultiplayerTemp` are never hosting candidates. If more than one normal save exists, use the previous/next controls to choose one before starting direct or relay hosting. Hosting refuses to start until that choice is valid.

Client multiplayer snapshots are registered under the active save game's `Endless` folder with the first unused slot number:

```text
%USERPROFILE%\AppData\LocalLow\Pomelo Games\Outlanders\user-<steamid>\<save-id>\Endless\Endless_N.dat
```

After the transfer finishes, follow the persistent overlay instruction: return to `Main Menu`, open `Sandbox`, choose `Load`, and select the displayed `Endless_N.dat` slot. Automatic IL2CPP world loading is intentionally deferred until the exact game load API can be invoked safely.

The host still owns the real save. Back up important saves before testing because this is still a prototype mod.

## Troubleshooting

`Outlanders Multiplayer` does not show in MelonLoader:

- Make sure the three mod DLLs are in the game's `Mods` folder.
- Make sure MelonLoader is installed into the Outlanders folder, not this project folder.

`Host Online` will not create a code:

- Open `Advanced`.
- Set `Relay Host` to a public IP or domain.
- Keep `Relay Port` at `17668` unless your relay uses a different port.

Friend cannot join with the code:

- Make sure the relay server is still running.
- Make sure TCP `17668` is open on the relay server firewall.
- Make sure both players use the same mod build.
- Create a new code and try again.
- Unreachable relay attempts remain non-blocking and show an overlay error after the 10-second connection timeout.

MelonLoader shows remote API or 502/526 warnings:

- Those are usually MelonLoader lookup warnings during startup.
- If the mod still loads and the menu appears, they are not the multiplayer mod failing.

## Build From Source

Package everything:

```powershell
$env:DOTNET_CLI_HOME=(Join-Path (Get-Location) '.dotnet-home')
.\scripts\Build-Package.ps1
```

`Build-Package.ps1` runs the pinned MelonLoader bootstrap automatically. No manual `tools` directory setup is required in a fresh checkout.

Validate the bootstrap without downloading or modifying the real dependency folder:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Test-Bootstrap-MelonLoader.ps1
```

Run protocol tests:

```powershell
$env:DOTNET_CLI_HOME=(Join-Path (Get-Location) '.dotnet-home')
dotnet run --project .\OutlandersMultiplayer.Tests\OutlandersMultiplayer.Tests.csproj --configuration Release
```

The same command runs automatically in GitHub Actions for every pull request and push to `main`. The command exits with a nonzero status if any protocol, snapshot, relay-frame, or join-code test fails.

Build output:

```text
artifacts\OutlandersMultiplayer\Mods\
artifacts\OutlandersMultiplayer\RelayServer\
```

## Next Development Work

- Extend the concrete IL2CPP hooks from build placement to demolish, cancel, priority, work-slot, decree, and time-speed actions.
- Route the remaining action categories through `SendPlayerIntent` and `AcceptedCommandReceived`.
- Publish deterministic hashes for resources, buildings, day/time, construction progress, and villagers through `PublishStateHash`.
- Add corrective resync when clients diverge from the host.
