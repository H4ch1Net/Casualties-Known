# CasualtiesCoop 2

Co-op multiplayer for **Casualties: Unknown** (full release, Steam app 4576490), as a BepInEx 5 plugin.

Host a run, your friends join from Steam, and you descend through the layers together. Everyone stands in the same generated world: the same terrain, crates, loot, creatures and layer modifier. Each player keeps their own body, wounds and inventory, with the game's full medical simulation.

Version 2 is a rewrite. Version 1 targeted the demo and is kept under `legacy/v1/` for reference.

## Playing

### Install

1. Install [BepInEx 5.4.23.x x64](https://github.com/BepInEx/BepInEx/releases) into the game folder (`steamapps/common/Casualties Unknown`).
2. Unzip `CasualtiesCoop-2.0.0.zip` into the game folder, which gives you `BepInEx/plugins/CasualtiesCoop/`.
3. Everyone in the session needs the same mod version and the same game build.

### Host

1. Launch the game through Steam.
2. On the main menu, click **Co-op** (top left) or press **F1**.
3. On the **Host** tab, pick who can join (**Friends**, **Public** or **Invite only**), the number of players, and optionally a password.
4. Click **Host with Steam**, then **Invite Steam friends**, or let people find you under **Find games**.
5. Start a run as usual. Anyone who joins, before or during the run, is pulled into it.

There are no ports to open. Steam relays the connection. As host you can **Kick** or **Ban** players from the player list. Bans are kept across sessions, are tied to the Steam account the connection really comes from (or the IP over LAN), and are listed on the Host tab for unbanning.

### Join

- **Find games** lists your Steam friends' sessions and public ones, with the host's layer, the player count, a password marker and Steam's estimated ping. Sessions you can't join (a different mod or game version) say what to update.
- Or accept a Steam invite, or click **Join Game** on the host in your friends list. This works even when your game isn't running yet.

A locked session asks for its password. You land in the host's run on the host's current layer, playing the character you last picked on the character screen.

### LAN / direct IP

The **LAN / IP** tab hosts on port 7777 and joins by address (with the host's password, if any), over LAN or a virtual LAN (Tailscale, ZeroTier, Radmin). The host must allow UDP 7777 through the firewall.

### Steam extras

- Friends see you **playing together** in their friends list, and teammates land in your **Recent players**.
- Avatars appear in the player list and on name tags.
- If your connection to the host gets slow or lossy, a small warning in the corner says so.
- With Steam **game recording** on, each co-op run is a timeline phase with a marker per layer, and markers for joins, leaves and deaths. Turn this off with `SteamTimeline` in the config.
- On **Steam Deck** the co-op window is drawn larger, and text fields bring up Steam's on-screen keyboard. `UiScale` in the config overrides the size anywhere.

## Playing together

| | |
|---|---|
| **Text chat** | **Enter** (or **T** if your game doesn't use T). Your character says the line out loud, slurred if they're hurt or have a broken jaw, and it shows above their head. A brain-damaged reader sees it garbled. The dead chat only with the dead. |
| **Voice** | Hold **V** (or set an open mic in the co-op window). Nearby players hear you, quieter with distance. The dead hear only each other. Needs Steam. |
| **Ping** | **Middle mouse** marks a spot everyone sees for a few seconds. |
| **Gestures** | Hold **H** for the wheel: wave, come here, wait, thumbs up, no, point (a line to the spot), help (a marker everyone sees) and **hug**, which lifts both players' mood and shares warmth. Your bark (**B**) is heard by everyone. |
| **Helping a teammate** | Right-click a teammate for their health panel: their vitals and every wound. Then:<br>• treat a limb with what's in your hand. **You play the game's own minigame**, and the effect lands on their body.<br>• set a joint, or pull shrapnel.<br>• take off a tourniquet or splint.<br>• hold CPR, or shock them with a defibrillator (it can even bring them back within 30 s of death).<br>• give them an item, or carry them while they're out.<br>• search a dead teammate's body. |
| **Moving together** | From the teammate panel: carry a downed teammate (drag, on your back or in your arms), give a leg-up, reach down and pull them up a ledge, snap them out of a deep low, or help carry a heavy item. Hold **J** under a falling teammate to catch them. |
| **Difficulty** | The host picks **Friendly**, **Standard**, **Hard** or **Brutal**. These set the co-op rules (respawn, exits, arrows to teammates, name tags, spawning apart, voice range, friendly fire) on top of the game's own run settings. The host can also scale loot and enemy strength with the number of players. |
| **Descending** | Continuing at an exit is a vote. By default the team goes down once half of the living players are at the exit; the host can change this to "first arrives" or "all are there". A drill pod needs everyone inside. Nobody is left behind. |
| **Comfort** | In the co-op window: colour-blind safe player colours, and "reduce flashes and shaking" for the mod's own screen effects. |
| **Host rules** | The host can change the rules mid-session in the co-op window, under **Rules**: who can join, max players, password, respawn rule, exit rule, speed-up and friendly fire (off by default: in the game itself a player's shot never hits a body). |
| **Saved runs** | Each player's character is saved with the run on every new layer and when leaving. The host can continue a saved run from the Host tab; returning players get their own character back. A team wipe ends the run and deletes its save, like the game. |

## What is shared

| | How |
|---|---|
| **World** | The game has no seed, so the host streams each layer to every client: terrain, water, about 2,000 objects with their health and item state, structures, the layer modifier and fog. That's ~200 KB and loads in 1–2 s. |
| **Descending** | Exits and drill pods are votes (see above). The host's descent brings everybody along. |
| **Terrain** | Mining, explosions, block placement and quakes, with crack overlays and hit sounds. Every 30 s the host checks every client's terrain chunk by chunk; a chunk that has drifted apart is repaired from the host's copy. |
| **Objects** | Damage adds up across players. The host alone decides when a crate breaks and rolls its loot, so loot never duplicates. Hit flashes, damage numbers and sounds show on every screen. |
| **Items** | Pickup is arbitrated by the host, so two players can't grab the same item. Drops and throws keep their velocity. Containers keep their contents, and loose items settle in the same place on every screen. |
| **Creatures** | Each creature is run by the player closest to it. Crawlers hunt and bite any player; other enemies (guards, drones, crystals) hunt the player whose machine runs them, which is usually the closest. A fight's threat music plays for teammates nearby. |
| **Gunfire** | Every shot (players, guards, turrets, gun mines) is seen and heard by everyone: tracer, sound, puffs. A shot that passes through you wounds you on your own machine. |
| **Traps and props** | Bear traps, spike stabbers, coils, stalactites, mines, cave-tick nests, med stations, bio terminals and scrap eaters with their doors, the drill pod repair, guard spawners and the start shuttle: set off on one machine, in the same state everywhere. Damage lands on whoever was hit. Players who join later see the world as it is now. |
| **Traders** | One shelf for the team: buying is cleared with the host, so two players can't buy the same item. Each player has their own standing with the trader. Anger a trader and it turns on everyone. |
| **Story** | The Blue Heart rises for the whole team, and everyone gets the cargo. |
| **Warmth** | Standing next to a living teammate slowly warms you up. |
| **Players** | Movement and full limb pose at 20 Hz. Your chosen character (Experiment, Milky or Dune) shows correctly, along with wounds, blood, dirt, wetness, held and worn items, swings and speech bubbles. Name tags float above other players. |
| **Clock** | Time speed is shared and decided by the host. Earthquakes happen to everyone at once. Sleep fast-forwards time once every living player is asleep or out cold, and stops when anyone wakes. |
| **Death** | When you die, you spectate. The screen stays clear, the camera follows a living teammate (**A / D** to switch), and you hear what they hear. The host's **When you die** rule decides what happens next: **Next layer** (the default: back healed when the team descends), **Permadeath** (out for the rest of the run) or **Quick** (back beside a teammate after 15 s). The host can die too: the run carries on for the others. Only when everyone is dead does the run end, with the normal death screen. |
| **Rejoining** | If you drop out and come back to the same layer, you return where you were, and still dead if you were dead. |
| **Props** | Lifepod heater and shower (only the players in the pod get washed). |

The game's own save slot is never touched during a session: co-op saves go to their own folder, and the single-player save is put back even after a crash.

## Known limits

- If the host leaves the run, everyone returns to the menu.
- If your connection drops, your character is saved, you go back to the menu, and the game reconnects on its own for up to a minute. You come back where you were.
- A dead player's body can be searched by teammates (everything but what's in their hands and mouth).
- Water is shared when a layer loads. After that, each game simulates water near its own player, so lakes far from everyone can drift apart.
- Pausing or alt-tabbing pauses only your own game. Creatures you're running stop for others until you return.
- One sleeper alone doesn't speed up time (others would be raced through their day). The sleeper's screen says time is waiting for everyone.
- Minigames (lockpicking, surgery) and crafting are per-player. Their results, such as broken containers, items and dropped loot, are shared. Crafting with an item on the ground isn't checked with the host yet.
- Item decay runs on each machine for loose items; the first machine where an item rots away removes it for everyone.
- An enemy only targets the player whose machine runs it (usually the closest). Two players next to one guard: it fights one of them.
- The surface breach, the End lab escape and merc runs are still per-machine. See [docs/game-systems.md](docs/game-systems.md) and the roadmap.

## Test status

**Verified with two instances over LAN, by automated self-test and matching world fingerprints:**

- joining a run from the menu
- layer streaming
- terrain edits in both directions
- object damage and breaking, including a client-initiated break through the host
- loot replication
- item drop and host-arbitrated pickup
- creature ownership split between both players
- remote player puppets at the correct positions
- client-requested descent to the next layer, with both peers matching there too
- the save guard
- the co-op menu rendering
- explosions from either side (one real blast, replayed body-only elsewhere)
- full item state: a water bottle's liquid and a pistol's ammo
- crystal effects on layer 8 (616 crystals)
- death and spectating:
  - a client dies and spectates (no death screen, clear screen)
  - it comes back alive on the next layer
  - the Quick rule brings it back beside the host after 15 s
  - under Permadeath it stays dead after the descent
- the host dying: it spectates while the client plays on, and creatures move to the client's machine
- everyone dead: a team wipe and the normal death screen on both
- sleeping together: 25× on both once both are asleep, back to normal when one wakes
- layer 12 (the End lab) loading on clients
- a forced Turbulent modifier (same gravity on both)
- shared ambient temperature

**Smoke-tested on Steam with one account:**

- lobby creation, the P2P listen socket and session start
- the lobby advert read back with every field, including the ping location and the Steam build id
- avatar loading
- a browser query

**Checked by screenshot:** every tab of the co-op window, the password prompt, and 1.5× scaling.

**Not yet tested by real players:**

- a two-account Steam join, including through the browser, with a password, and a ban
- the friends' games list (needs a friend hosting) and the public list (needs a public lobby)
- Steam Deck
- creature bites on non-owners
- throwing at speed
- container edits
- rejoining after a disconnect (where you were, and dead if you were dead)
- switching between several teammates while spectating (A / D)
- the host leaving mid-run
- name tags and speech bubbles on screen

Report anything odd with the `coop-*.log` from both players.

## Building

Requirements: .NET SDK 8 or newer, plus the game with BepInEx installed (for the plugin only).

```bash
cp GamePath.Local.props.example GamePath.Local.props   # then edit GamePath
dotnet test CasualtiesCoop.slnx                          # networking library, no game needed
dotnet build src/CCoop.Plugin/CCoop.Plugin.csproj        # plugin; deploys if DeployToGame=true
dotnet build src/CCoop.Plugin/CCoop.Plugin.csproj -c Release   # also writes dist/CasualtiesCoop-<version>.zip
```

`CasualtiesCoop.slnx` contains only the game-independent projects. The plugin needs the game install and is built through its own csproj.

**CI.** `.github/workflows/build.yml` builds and tests the network library on every push. The plugin job only runs on a self-hosted runner that has the game (labelled `game`, with the repository variable `CCOOP_GAME_PATH` set), because the game's assemblies are never committed.

**Update check.** Set `Update.Repository` in the config to `owner/name` of the release repository. The mod then asks GitHub once per launch whether a newer release exists and says so in the co-op window. Nothing is downloaded.

## Linux, Proton and Steam Deck

BepInEx loads through `winhttp.dll`, which Proton ignores unless told otherwise. In the game's Steam **Properties → Launch options**, set:

```
WINEDLLOVERRIDES="winhttp=n,b" %command%
```

On Steam Deck the co-op window scales up by itself, and text fields open Steam's on-screen keyboard.

**Bug reports.** The co-op window's **Bug report** button gathers your co-op logs and the session details into a folder on your desktop. Attach its files to the report.

## Testing on one machine

```powershell
.\tools\launch-instances.ps1 -Count 2 -Auto -SelfTest
```

This starts two game instances. The first hosts, the second joins `127.0.0.1`, and the host starts a run. Once both are in, each side edits terrain, damages and breaks objects, and moves items. Then the client asks to descend. Both sides log `VERIFY` lines to `%USERPROFILE%\AppData\LocalLow\Orsoniks\CasualtiesUnknown\CasualtiesCoop\coop-*-pid*.log`. Matching `terrain`, `health` and `runtime` values mean the two worlds agree.

`-Steam` makes the first instance host an invite-only Steam lobby instead, which requires the Steam client to be running.

Two instances need `boot_config_override = BepInEx\boot.multi.config` in `doorstop_config.ini`, pointing at a copy of `CasualtiesUnknown_Data\boot.config` without the `single-instance=` line. No game file is modified.

## Layout

```
src/CCoop.Net/          wire format, message registry, host/client session, LiteNetLib + loopback transports
src/CCoop.Plugin/
  Runtime/              BepInEx entry, session runtime, world channel, logging
  Net/                  Steam transport (SteamNetworkingSockets) and lobbies
  Protocol/             every gameplay message
  World/                entity ids, layer snapshot, item codec, reflection helpers
  Sync/                 one module per concern: run flow, layer, players, terrain, entities, items, creatures, clock, props
  UI/                   co-op window and notices
  Dev/                  env-var test automation and world fingerprints
tests/CCoop.Net.Tests/  session, relay, handshake, blob and codec tests
docs/architecture.md    how it works and why
docs/ROADMAP.md         what comes next, milestone by milestone
docs/game-systems.md    how the game works, system by system, and what each means for co-op
legacy/v1/              the demo-era version
```

## Notes

- Never commit game assemblies or decompiled game source (`.gitignore` enforces this).
- [Casualties Together](https://github.com/creaturefeaturelarry/casualties-together) (LGPL-2.1) is a separate, mature co-op mod for the same game. No code from it, or from any other co-op mod, is used here.
- Get the developer's permission before distributing anything publicly.
