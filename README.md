# Fracture Point Multiplayer

An experimental LAN co-op mod for **Fracture Point**, built for Unity IL2CPP with **BepInEx 6**, **Harmony**, and C#/.NET 6.

Play with up to four players over LAN or a VPN such as Radmin VPN. The mod adds a party menu, synchronized players, combat, enemies, loot, world interactions, elevator travel, and spectator controls. Personal saves and progression remain local.

## Download

[**Download the ready-to-play RAR**](https://github.com/x1pee/Fracture-Point-Multiplayer/releases/latest/download/FracturePointMultiplayer-0.3.0-ExtractToGame.rar)

1. Close the game.
2. Extract the archive into the game folder, beside **Fracture Point Release.exe**.
3. Everyone must install the same mod and supported game build.
4. Launch the game, press **F11**, then create or join a lobby while at home.

This is an experimental release. Some animations, audio, physics, and multiplayer interactions still need further testing.

## Build

Install the .NET 6 SDK and run:

```powershell
./build.ps1 -GameDirectory "D:/Games/Fracture Point"
```

BepInEx must already have generated the game's interop assemblies. Game binaries and generated assemblies are not included in this source repository.