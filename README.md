# Atomic

Networking framework for **Classic Us** (Among Us) BepInEx IL2CPP mods.

## Features
- **Automatic handshake** — broadcasts your mod list to all players when joining a lobby
- **Lobby tracker** — knows which players have Atomic and which mods they're running
- **Version check** — detects version mismatches across players
- **Unmodded lobby detection** — fires an event if the host doesn't have Atomic
- **Optional kick enforcement** — off by default, so host-only mods can let vanilla clients join

## Usage

```csharp
// In your BepInEx plugin Load():
AtomicAPI.Register("YourModName", "1.0.0");

// Events
AtomicAPI.OnPlayerModded += (playerId, mods) => { };
AtomicAPI.OnLobbyFullyModded += () => { };
AtomicAPI.OnJoiningUnmoddedLobby += () => { };
AtomicAPI.OnModVersionMismatch += (playerId, mod, localVer, remoteVer) => { };

// Queries
bool ok = AtomicAPI.IsCompatibleToPlay();
List<byte> unmodded = AtomicAPI.GetUnmoddedPlayers();
```

## Config (`BepInEx/config/atomic.cfg`)

- `Handshake.EnforceCompatibility` (`false` by default) — when `true`, the host
  kicks players who never send an Atomic handshake (vanilla clients) or whose
  mod set mismatches, after the handshake grace period. Leave `false` for
  **host-only** mods so unmodded/vanilla clients can join a modded lobby.

## Requirements
- BepInEx IL2CPP for Classic Us
- `Atomic.dll` installed in `BepInEx/plugins/`
