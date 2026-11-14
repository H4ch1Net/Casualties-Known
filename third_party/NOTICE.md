# Third-party components

## LiteNetLib 1.3.5 (shipped)

- https://github.com/RevenantX/LiteNetLib, MIT licence.
- Consumed as a NuGet package; `LiteNetLib.dll` is copied next to the plugin.
- Pinned to 1.3.5 because it is the newest release with a `netstandard2.0` target. 2.x ships only
  `net8.0`/`netstandard2.1`, which a `net472` BepInEx 5 plugin cannot load.

## Steamworks.NET (not shipped)

- https://github.com/rlabrecque/Steamworks.NET, MIT licence.
- The full game ships `com.rlabrecque.steamworks.net.dll` 2025.164 and `steam_api64.dll` and
  initialises Steam itself. The plugin references the game's copy in place and redistributes
  neither file.

## Not used

No code from other Casualties: Unknown co-op mods (Casualties Together, CUCoreLib/KrokMP,
Casualties-Unknown-Online, ScavMulti) is used or adapted here.

## Game files

No game assemblies, assets or decompiled game source are in this repository. See `.gitignore`.
