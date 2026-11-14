# CasualtiesCoop

An independent, host-authoritative co-op multiplayer mod for **Casualties: Unknown** (Steam demo), built from scratch on BepInEx 5 + HarmonyX.

Written without any dependency on *Casualties: Together*, ScavMulti, or CUCoreLib's networking bridge. See [docs/game-api-map.md](docs/game-api-map.md) for the reverse-engineered integration surface.

## Status

Milestone **M0 (toolchain + probe)** — complete and verified in-game. `CCoop.Probe` loads under BepInEx, confirms the pinned assembly hash, traces lifecycle events, and measures the world snapshot.

Two findings already settled by M0:

- **No world seed exists.** Two runs on identical settings produced different terrain hashes (`35f98730…` vs `07975911…`). Clients cannot regenerate the host's world; the host must stream it.
- **World streaming is cheap.** 1024×1024 blocks, 17 distinct ids, 2 MB raw → **~145 KiB deflated per layer** (~725 KiB per 5-layer run), against 7–8 s the host already spends generating.
- **BepInEx plugin components do not survive scene loads.** Session state must live on our own `DontDestroyOnLoad` object.

Milestone **M1 (feasibility gates)** — **closed. Answer: visual proxies, not multi-`Body`.**

M1 asked whether two `Body` instances could coexist. An early gate run reported 34/34 pass and "multi-Body viable"; **that verdict was retracted** when, in play, the full-`Body` clone froze the local player's animation, broke terrain collision and killed dialogue. The gates had measured state independence and never functional integrity of the original player.

The project went with sprite-only visual proxies instead, and those now work in-game with full limb pose sync — so the question is answered even though the original hypothesis failed. What still holds from the investigation:

- Medical state is genuinely per-instance (`skinHealth`, `bleedAmount`, `broken`, `bloodVolume`, `consciousness` …).
- `Utils.GetBody` is body-agnostic, so damage/interaction sources need no patching. The only local-player hardcoding is `PlayerCamera`'s range checks.
- `Body.OnCollisionEnter2D` reaches into `PlayerCamera.main.shaker` unconditionally, and `Body.Awake` ignores collisions only among its *own* limbs — two known sources of cross-talk.

A full-`Body` clone may still be wanted host-side one day (for authoritative simulation of remote players), but nothing depends on it now.

Milestone **M2 (protocol + transport)** — **complete. 195 tests passing across the suite.**

- Binary protocol: 24-byte header, 38 message types, strict length-checked codec. The reader **never throws** and rejects non-canonical values (bad booleans, NaN/Infinity floats, invalid UTF-8, trailing bytes).
- Compatibility handshake gating on protocol version, mod version, App ID, `Assembly-CSharp.dll` SHA-256 and plugin manifest, with player-facing rejection text.
- LiteNetLib transport behind `INetworkTransport`, six delivery channels so a stalled bulk transfer can't head-of-line block movement or handshakes.
- End-to-end loopback UDP harness: handshake accept/reject, 4 concurrent clients, peer caps, clean disconnect, 25 connect/disconnect cycles, 200 reliable messages under 10% loss + 30–80 ms latency, `MaxBlobBytes` fragment/reassemble, and 300 garbage packets that must not disturb a live session.

```bash
dotnet test CasualtiesCoop.slnx
```

Everything in `src/` is `netstandard2.0` and builds with **no game install**, so CI can verify it. (The .NET 10 SDK emits a `.slnx` solution, not `.sln`.)

Milestone **M3a (networked players)** — **working in-game.** Two instances connect, hand shake, and render each other with full pose sync.

- `CCoop.Core` session layer (`HostSession` / `ClientSession`), Unity-free so it is tested headlessly over real UDP.
- `CCoop.Plugin` BepInEx plugin: session lifecycle on a `DontDestroyOnLoad` object, sprite-only remote proxies, buffered interpolation, IMGUI overlay, per-process logging.
- **SaveGuard**: vanilla saves are blocked while a session is live. The game has one global save slot and Unity derives `persistentDataPath` from company/product name, so two instances share it — saving during co-op would corrupt the single-player save.
- Verified live: handshake in ~50 ms; remote limb poses match the source body to 3 decimals.

Milestone **M3b (world streaming)** — **terrain works.** Host and client hold byte-identical terrain, verified by SHA-256 on both sides in-game.

| Measure | Value |
|---|---|
| Raw grid | 2,097,152 B (1024×1024 × `ushort`) |
| Deflated | ~155 KiB in 23 chunks |
| Transfer (loopback) | ~280 ms |
| Client apply | ~2 s |

Clients suppress local generation by patching `WorldGeneration.InstantiateWorld(generate: false)` — which still builds all the scaffolding — then write the received grid into `worldBlocks` and rebuild the chunk tilemaps. This is required rather than an optimisation: with no world seed, a client that generates locally produces a *different* world.

**World contents sync too.** The host captures every generated object (~2,000 across ~90 prefabs) into a string-table manifest that travels with the terrain in one verified transfer. Clients spawn 2,002 of 2,003 live; the single failure (`ShuttleStarter`) is instantiated from a script-held prefab reference rather than `Resources`.

| Measure | Value |
|---|---|
| Entity manifest | ~29 KB |
| Full layer payload | ~185 KB compressed, 27 chunks |
| Spawned on client | 2,002 / 2,003 |

Entities mutate the blocks they sit on, so the host's terrain is re-asserted after spawning — otherwise those edits apply twice and the worlds diverge.

Milestone **M3c (world mutation)** — **the layer stays shared after the transfer.** M3b hands over a world once; M3c keeps the two copies the same while people play in it.

**Terrain edits sync.** Streaming the world once is not enough: the first block either player mines exists only for them, and two players digging the same wall diverge permanently. `WorldGeneration.SetBlock` turns out to be the single funnel for every runtime terrain change — mining reaches it through `DamageBlock`, explosions through `GenerateBlockCircle`, and block placement directly, while generation uses `SetBlockNoUpdate` and bypasses it entirely. One hook therefore captures every gameplay edit and no generation traffic.

Edits are batched per frame and published peer-to-peer rather than arbitrated by the host. An edit names the block a cell now *holds* instead of requesting a change, so it is idempotent and two players editing the same cell converge on the same answer — nothing to duplicate, so nothing to arbitrate, and mining does not wait a round trip per swing. Receivers write `worldBlocks` directly and call `UpdateChunk` once per affected chunk; going through `SetBlock` per cell would redraw a 64×64 tilemap every time.

Verified across two real processes: identical live terrain hash before the edit, 25 cells written on the host, `applied 25 remote edits across 1 chunk` on the client, identical hash after.

**Breaking things syncs too, loot included.** The earlier claim that containers fill from `ItemLootPool` on opening was wrong: world crates are `BuildingEntity` objects that hold nothing until destroyed, and `BuildingEntity.Update` rolls their drops when `health` falls below 0.5 — random chance, condition, rotation and velocity, created through `Object.Instantiate`. Nothing about that is reproducible from the same inputs, so the outcome has to travel rather than be recomputed.

Ownership is first-to-die rather than host-arbitrated. Entity health is local, so the peer whose copy dies is the one that actually broke it; host arbitration would mean a client chopping a crate got nothing, because the host's untouched copy would never die. That peer rolls the drops itself through the game's own item factory so they can be captured, blanks the entity's drop fields so the game cannot roll them again, and sends a despawn followed by a spawn per drop. Receivers blank their copy and zero its health, so it breaks apart with the right particles and sound but no loot of its own.

Verified across two processes: the host destroyed a crate and published two drops; the client destroyed its copy and spawned exactly those two under the host's ids, with runtime-entity counts equal on both sides.

**Damage stacks, too.** `health` is a public float written directly from a dozen unrelated call sites — melee, bullets, explosions, a cactus, a burrowing creature — with no method to patch and no event to subscribe to. So it is observed rather than intercepted: the per-entity `Update` hook already runs every frame, and a drop since the last look is damage somebody just dealt here. The *amount* is broadcast, never the resulting health, so two players chopping the same crate add up instead of overwriting each other; without it, splitting the work between them could leave a crate that neither could fell.

Verified by summing health across every networked object on both peers: `1807:897300` before, `1806:894775` after — identical on each side, reflecting one destroyed crate plus exactly 25 damage.

**Layers keep their character.** The game rolls one modifier per layer — Foggy, Freezing, Flooded, Infested — at the very end of `GenerateWorld`, the coroutine clients suppress. So a client stood in the host's terrain, at the host's coordinates, in a different climate: no fog, no cold, none of the hazards the layer is named after. The chosen index now travels with the layer and the client applies it once the world lands; it can't be re-rolled, because the roll consumes the unseeded global RNG and would land somewhere else.

Two of the eleven get special handling. `Infested` and `Ionized` scatter world objects, and they do it *before* the host captures the world — so those shadecrawlers and rad barrels already arrive in the entity manifest, and re-running them client-side would scatter a second, different set on top.

**Water comes too.** `PlaceLiquids` runs during the suppressed generation, so a client's fluid grid was empty while the host had lakes — one player wading where the other saw dry rock. The grid now ships as a third section of the layer payload. It's a megabyte raw and nearly free compressed, being one byte per cell and mostly zeroes: the whole layer goes from ~184 KB to 186–196 KB. `InstantiateWorld` already allocates the client's array at the right size, so it's a straight copy into something already waiting. Verified byte-exact by hashing at send and at write.

Only the *starting* grid is shared. `FluidManager` simulates within ±64 blocks of each peer's own camera, so water drifts apart where the players separate — and stays exact everywhere neither of them is standing.

**Everyone runs at the same speed.** `Time.timeScale` is global to a process and drives physics, animation, AI and fluid together, so a peer that fast-forwards doesn't merely see things sooner — its whole simulation runs away from the others. Of everything left unsynchronised this was the cheapest to trigger and the widest in effect: one keypress put the two worlds 20× out of step.

`SetTimeScale` is the single funnel, so one patch sorts every change three ways. The deliberate speeds are the session's and the host arbitrates them. Pause, unpause and start-up stay local — one player opening a menu must not freeze another's game, and they're identifiable because only those paths pass `force: true`. The automatic speeds are refused outright: `UnconsciousFast` is 25×, driven by *one body's* consciousness, so sharing it would race everybody's world because a single player passed out, and not sharing it desyncs that peer by 25×. An unconscious player now waits in real time while their partner plays.

**Earthquakes are shared.** The game rolled both the interval and the duration per peer from the unseeded RNG, so each player had their own. Terrain sync made that worse before it made it better: the blocks a quake tears out do converge, but it tears them out *around the shaking player* — so the other one watched holes open around themselves during a quake they weren't in. Only the schedule travels; each peer already produces the shake, the stumbling and the damage correctly for its own player.

Milestone **M4 (transactions)** — **partial.** Item pickup is host-arbitrated and confirmed by a real player. Drop is built but untested. Container looting works, though by way of destruction sync rather than as a transaction. Container transfers and crafting are untouched.

**Still not playable.** Creature AI runs independently on each peer, remote players' held items are invisible, and there's no medical, death, layer-transition or persistence handling yet (M5/M6).

### Two-instance testing

```powershell
.\tools\launch-instances.ps1 -Count 2
```

Then `F6` to host in one window, `F7` to join in the other. `F9` logs a hash of the **live** world on each peer — terrain, entity registry, and blocks sent/applied — so the two logs can be diffed to answer "are these still the same world?" long after the transfer succeeded.

For unattended runs, per-process env vars (config files are shared between instances, so a config setting could not distinguish them):

| Variable | Effect |
|---|---|
| `CCOOP_AUTOSTART=host\|join` | Host or join on startup |
| `CCOOP_AUTORUN=1` | Enter a run without menu input |
| `CCOOP_POSEDIAG=1` | Log local and proxy limb poses for diffing |
| `CCOOP_BLOCKTEST=1` | Host mutates terrain once the world settles, then both peers hash it — terrain sync proved end to end with no human input |

## Supported build

Pinned to one exact build; anything else is unverified.

| | |
|---|---|
| Steam App ID | `4576510` (Casualties: Unknown Demo) |
| Build ID | `23560597` |
| Game version | 7.0.1 |
| Unity | 2022.3.62f3 (Mono, x64) |
| `Assembly-CSharp.dll` SHA-256 | `fd0ad2851f6bae4ab2589634643b92e16696c0e36f090bdd3f4c41e8f1c97095` |

## Prerequisites

- .NET SDK 8+ (builds `net472` targets)
- BepInEx **5.4.23.5** x64 installed into the game directory
- The game installed locally

## Building

```bash
cp GamePath.Local.props.example GamePath.Local.props
```

Edit `GamePath.Local.props` to point `GamePath` at your install, then:

```bash
dotnet build tools/CCoop.Probe/CCoop.Probe.csproj
```

With `DeployToGame=true` the output is copied to `BepInEx\plugins\CCoop` automatically.

## Running

Launch the .exe directly — Steam is not required:

```powershell
.\tools\launch-instances.ps1 -Count 1
```

For co-op testing, two instances on one machine work:

```powershell
.\tools\launch-instances.ps1 -Count 2
```

This needs one piece of setup, already applied here: `UnityPlayer.dll` enforces single-instance from the `single-instance=` key in `CasualtiesUnknown_Data\boot.config`, so `doorstop_config.ini` sets `boot_config_override = BepInEx\boot.multi.config` — a copy of that file without the key. **No game file is modified.**

Logs: per-process at `%LOCALAPPDATA%Low\Orsoniks\CasualtiesUnknown\CCoop\probe-*-pid*.log`. Prefer these over `BepInEx\LogOutput.log`, which two instances contend for. See [docs/testing.md](docs/testing.md) for what else is shared between instances — notably the game's single save slot.

## Repository layout

```
tools/CCoop.Probe/   M0 read-only reconnaissance plugin (net472)
docs/                game API map, protocol, authority matrix, testing
```

Further projects (`src/CCoop.Protocol`, `CCoop.Core`, `CCoop.Transport*`, `CCoop.GameAdapter`, `CCoop.Plugin`) are added as their milestones begin.

## What must never be committed

`Assembly-CSharp.dll`, Unity DLLs, game assets, **decompiled game source**, or save files. Decompile to a scratch directory outside the repo; `.gitignore` enforces this.

## Licensing notes

- LiteNetLib (planned transport) is MIT — vendor its source under `third_party/` with attribution intact.
- **ScavMulti has no license**, so normal copyright applies: do not copy or incorporate any of it.
- CUCoreLib is LGPL and its multiplayer bridge routes through the KrokMP runtime, so it is not an independent foundation. Any future adapter may use only its public non-networking APIs.
- The game ships **no Steam API**, so *the game* does not use Steamworks. That does **not** rule out a Steam transport: a mod can ship its own `steam_api64.dll` + Steamworks.NET and initialise against the game's App ID, which is how other mods provide lobbies and invites. Currently direct IP / LAN, with internet play via user-provided tunnels (Tailscale, Radmin, Playit); a `SteamTransport` behind `INetworkTransport` is being evaluated.
- Do not redistribute game files. Seek the developer's written permission before any public release.
