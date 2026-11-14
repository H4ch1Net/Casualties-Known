# Third-party dependencies

## LiteNetLib 1.3.5

- Upstream: https://github.com/RevenantX/LiteNetLib
- Package: https://www.nuget.org/packages/LiteNetLib/1.3.5
- Source commit for this release (from the package nuspec): `cf54c7d7a15a45bd2460cd4a460807329ee7026a`
- License: **MIT** (`licenseExpression` in the package metadata; full text at
  https://github.com/RevenantX/LiteNetLib/blob/master/LICENSE)

Consumed as a NuGet `PackageReference`, not vendored, so the package's own license
metadata travels with the restored assembly.

### Why 1.3.5 and not 2.x

`LiteNetLib` 2.x (2.1.4 at time of writing) publishes only `net8.0` and
`netstandard2.1` assemblies. Our game-facing plugin targets `net472` on Unity's Mono
runtime, and .NET Framework never supported `netstandard2.1`, so 2.x cannot be
referenced from the plugin at all.

1.3.5 is the newest release that still ships a **`netstandard2.0`** target, which
`net472` can consume. Versions 1.1.0–1.3.1 are deprecated upstream.

Verified by inspecting the packages:

| Version | Targets |
|---|---|
| 1.3.5 | net6.0, net7.0, net8.0, **netstandard2.0**, netstandard2.1 |
| 2.1.4 | net8.0, netstandard2.1 only |

Revisit if upstream restores a `netstandard2.0` target, or if the plugin ever moves
off BepInEx 5 / `net472`.

## Excluded on licensing grounds

- **ScavMulti** (https://github.com/Dathuss/ScavMulti) — no license file, so default
  copyright applies and none of it may be copied or adapted.
- **Casualties: Together** — no source available; its binaries, protocol and API are
  not used or inspected.
- **CUCoreLib** — LGPL. Its multiplayer bridge routes through the KrokMP runtime, so it
  is not an independent networking foundation. Any future adapter must use only its
  public non-networking APIs.

## Steamworks (under evaluation, not yet a dependency)

An earlier note here claimed Steam networking was impossible for this mod because the
game ships no `steam_api64.dll`. That was wrong: it shows only that *the game* does not
use Steamworks. A mod may ship its own `steam_api64.dll` plus
[Steamworks.NET](https://github.com/rlabrecque/Steamworks.NET) (MIT) and initialise
against the game's App ID, which is how other mods provide lobbies and invites.

If adopted, `steam_api64.dll` is Valve's redistributable and is covered by the
Steamworks SDK terms, not by this project's licence — check those terms before
redistributing it. Steamworks.NET itself is MIT.

## Game files

No game assemblies, assets, or decompiled game source are present in this repository,
and none may be added. See `.gitignore`.
