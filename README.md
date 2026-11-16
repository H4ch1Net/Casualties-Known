# CasualtiesCoop 2

Co-op multiplayer for **Casualties: Unknown** (full release, Steam app 4576490), as a BepInEx 5 plugin.

Host a run, your friends join from Steam, and you descend through the layers together. Everyone stands in the same generated world: the same terrain, crates, loot, creatures and layer modifier. Each player keeps their own body, wounds and inventory, with the game's full medical simulation.

Version 2 is a rewrite. Version 1 targeted the demo and is kept under `legacy/v1/` for reference.

## Playing

### Install

**The easy way (Windows):** unzip `CasualtiesCoop-2.1.10.zip` anywhere, then right-click `install.ps1` and choose **Run with PowerShell** (or run `powershell -ExecutionPolicy Bypass -File install.ps1`). It finds the game, installs BepInEx 5 if you don't have it yet, and copies the mod in.

**Updating:** run the installer from the new download the same way. It replaces an older CasualtiesCoop (your settings are kept) and removes any other copy of a co-op mod it finds under `BepInEx/plugins` (the first co-op mod in `plugins/CCoop`, a second copy, loose files); other mods are left alone. It won't replace a newer version unless you add `-Force` (`--force` on Linux). If a second copy is still there when the game starts, the co-op lobby says so in red.

**The easy way (Linux and Steam Deck):** unzip it anywhere, open a terminal there (on Steam Deck: Desktop Mode, then Konsole) and run `bash install.sh`. It does the same, then shows the one launch option to set in Steam (see [Linux, Proton and Steam Deck](#linux-proton-and-steam-deck)).

**By hand:**

1. Install [BepInEx 5.4.23.x x64](https://github.com/BepInEx/BepInEx/releases) into the game folder (`steamapps/common/Casualties Unknown`).
2. Unzip `CasualtiesCoop-2.1.10.zip` into the game folder, which gives you `BepInEx/plugins/CasualtiesCoop/`.

Everyone in the session needs the same mod version and the same game build (a player with another version is told which version the host has when joining).

### Host

1. Launch the game through Steam.
2. On the main menu, click the walkie-talkie by the sleeping bag, or press **F1**.
3. On the **Host** tab, pick who can join (**Friends**, **Public** or **Invite only**) and optionally a password. The player places along the top set how many can join: **+** adds one, **Close** on an open place removes one.
4. Click **Host with Steam**, then **Invite Steam friends**, or let people find you under **Find games**.
5. Everyone picks their character with the arrows on their own portrait. The host picks the run's settings under **Run settings** (the game's presets and every custom setting; the others see them as they change) and presses **Start the run**. Anyone who joins later is pulled into the run.

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
| **Text chat** | **Enter** (or **T** if your game doesn't use T). Your character says the line out loud, slurred if their jaw is broken, and it shows above their head. A voice is clear up close and fades with distance: within 40% of the voice range every word comes through, then more and more letters are lost ("H-lp -e"), and past the range nothing arrives. A wall muffles it (it costs a few metres plus some of its thickness) rather than cutting it off. Knocked out or asleep, you can still type, but it comes out as a mumble heard only close by. Your hearing loss (gunfire, blasts) and a damaged brain garble what you hear. The dead chat only with the dead, anywhere. |
| **Radio** | Like Barotrauma's headsets: with the host's **Radios** rule (on by default) everyone has a radio. In the chat box, **Tab** switches between saying a line and radioing it. A radioed line reaches every teammate who's awake, anywhere on the layer (in green, with a blip), but it always crackles: the static grows with the distance and with the rock in between, never quite to silence. So talking is clean when you're close, and the radio is how you reach someone far off. You need to be awake to use it. The game has no radio item outside its tutorial, so there's nothing to carry. |
| **Voice** | Hold **V** (or set an open mic in the co-op window, with a threshold so it only sends when you speak). Nearby players hear you, quieter with distance and muffled through rock. A broken jaw, being under water or brain damage changes how you sound. Each teammate's volume can be set (or muted) in the player list. The dead hear only each other. Needs Steam. |
| **Ping** | **Middle mouse** marks a spot everyone sees for a few seconds. |
| **Keys** | The co-op window's **Options** tab lists every co-op key. Click one and press the new key to change it (Esc keeps the old one); it tells you when the game or another co-op key already uses that key. The first run after you start the game shows the main keys on the game's alert line, which you can turn off in the same tab. |
| **Look** | The co-op window, prompts and notices use the game's own look: its Retro Gaming pixel font, black panels and dark buttons with a one-pixel white frame, the chosen option inverted. Teammates' names, their words above their heads and the chat log are in the game's own amber speech text, typed out at the game's pace with its letter sounds. Text the pixel font can't write (Chinese, Japanese, Korean, Cyrillic, unusual Steam names) uses Unifont, the game's other pixel font. All of it is borrowed from the game when it runs; the mod ships none of its files. On the main menu, co-op is a walkie-talkie by the sleeping bag, painted for the mod in the menu's style (`art/walkie.png`, made by `art/walkie-paint.py`). It opens the co-op lobby: a full-screen menu laid out like the game's own, with the players as their characters' portraits, open places as the grey outlines of locked characters, the session in the game's bracketed terminal style, and the settings beside it. |
| **Gestures** | Hold **H**, move the mouse onto a gesture and let go. Or tap **H** and click one (or press its key, **1** to **0**). The wheel looks like the game's pause menu: round buttons with pixel icons around a circle that names the one under the mouse. Your character does them: waves, beckons, waves you along to follow, calls for help with both arms up (and a marker everyone sees), **hugs** (which lifts both players' mood and shares warmth), gives a thumbs up, nods yes, shakes its head, shrugs, and holds up a palm to wait. If you can't move your arms just then (climbing, lying down), the gesture shows above your head as words instead. To point at something, middle-click it (a ping). Your bark (**B**) is heard by everyone. |
| **Helping a teammate** | Hovering a teammate shows the game's own label (their name, and the right-click icon). Right-click them for a small menu beside them: **Health**, give or ask for an item (their items as pictures), carry, wake, leg-up and the rest (only what fits the moment). **Health** opens their panel: the game's own body figure (a copy of the real one, coloured by the game's own rules), every wound marked with the game's icons, limbs named as the game names them, but none of the chip's read-outs (nobody can read another's chip). Click a limb, then from the panel:<br>• treat a limb with what's in your hand. **You play the game's own minigame**, and the effect lands on their body.<br>• set a joint, or pull shrapnel.<br>• take off a tourniquet or splint.<br>• hold CPR, or shock them with a defibrillator (it can even bring them back within 30 s of death).<br>Esc closes the menu, then the panel. From the menu you can also give them an item (with full hands it lands at their feet), ask them for something they carry, wake them up if they're asleep or shake them if they're out cold, and search a dead teammate's body. |
| **Moving together** | From a teammate's right-click menu: carry them (drag, on your back or in your arms). Someone who is down is picked up. Someone awake (an amputee, say) is asked first, and gets down with the jump key. Carrying holds them in a real pose, tires you, and slowly builds strength, and blows never hurt the one you carry. You can also give a leg-up, reach down and pull them up a ledge, snap them out of a deep low, or help carry a heavy item. Hold **J** under a falling teammate to catch them. To help carry something heavy, right-click the teammate holding it (not the item) and pick **Help carry**; you get a notice when a teammate nearby picks one up. Reach is measured between the nearest parts of your two bodies, so a teammate lying on the ground is in reach when you stand over them. |
| **Difficulty** | The host picks **Friendly**, **Standard**, **Hard** or **Brutal**. These set the co-op rules (respawn, exits, arrows to teammates, name tags, spawning apart, voice range, friendly fire) on top of the game's own run settings. The host can also scale loot and enemy strength with the number of players. |
| **Run presets** | On the main menu, the co-op window has a few co-op run presets (Cold snap, Glass bones, Field hospital, Lean pickings, Long haul) that fill in the game's own run settings. Save your own, or share one with friends as a line of text. |
| **Descending** | Continuing at an exit is a vote. By default the team goes down once half of the living players are at the exit; the host can change this to "first arrives" or "all are there". A drill pod needs everyone inside. Nobody is left behind. |
| **Milestones** | Small co-op milestones (carry a teammate 100 m, catch a fall, bring someone back...) are counted on your own machine and listed in the co-op window. They're the mod's own, not Steam achievements. |
| **Comfort** | In the co-op window: colour-blind safe player colours, and "reduce flashes and shaking" for the mod's own screen effects. |
| **Host rules** | The host can change the rules mid-session in the co-op lobby, under **Co-op rules**, laid out like the game's own custom settings (hover a line for what it does): who can join, password, respawn rule, exit rule, arrows, name tags, spawning apart, voice range, speed-up, loot, enemies and friendly fire (off by default; on in Hard and Brutal). With friendly fire on, your shots and your blows hurt teammates: a swing that reaches a teammate lands on their body like a hard knock (bruising, a dislocation, rarely a broken bone, or a knock on the head), cutting weapons also cut, and a heavy one can knock them over. Thrown things don't, as in the game itself. With **Sleep** off, nobody gets tired and nobody can nap (sleeping pills still work). |
| **Saved runs** | Each player's character is saved with the run on every new layer and when leaving. The host can continue a saved run from the Host tab; returning players get their own character back. A team wipe ends the run and deletes its save, like the game. |

## What is shared

| | How |
|---|---|
| **World** | The game has no seed, so the host streams each layer to every client: terrain, water, about 2,000 objects with their health and item state, structures, the layer modifier and fog. That's ~200 KB and loads in 1–2 s. |
| **Descending** | Exits and drill pods are votes (see above). The host's descent brings everybody along. |
| **Terrain** | Mining, explosions, block placement and quakes, with crack overlays and hit sounds. Every 30 s the host checks every client's terrain chunk by chunk; a chunk that has drifted apart is repaired from the host's copy. Blocks someone else breaks crumble for you too, with the game's debris, dust and sound. The back-wall decorations (the holes in the rock on the first layer) are the host's, so everyone sees the same walls. Each machine used to roll its own sky colour, rain and rock-tile pattern; in a session they're the host's (sky and rain) or worked out the same way everywhere (tiles), so everyone sees the same layer. Teammates' splashes into water make the game's splash sound and spray for everyone. Ropes and vines hang and climb the same for everyone, including ropes a player puts up. |
| **Water** | Lakes, lava and other liquids near each player are simulated by that player's game and shared with everyone, so a flood, a drained pool or a geyser's eruption looks the same on every screen. |
| **Hazards** | Fans, pistons, geysers and grabber plants are the same hazard on every machine: same speed, rhythm, liquid and reach. |
| **Objects** | Damage adds up across players. The host alone decides when a crate breaks and rolls its loot, so loot never duplicates. Hit flashes, damage numbers and sounds show on every screen. |
| **Items** | Pickup is arbitrated by the host, so two players can't grab the same item. Drops and throws keep their velocity. Containers keep their contents, and loose items settle in the same place on every screen. The host decides when loose items rot or rust away, including near teammates far from the host. Crafting with items on the ground is cleared with the host, so two players can't use the same one. An item put straight into a bag or a crate leaves the floor for everyone. A teammate who knows an item tells you what it is: you see its name, how full or worn it is and what's in it if any teammate on the layer would recognize it. |
| **Creatures** | Each creature is run by the player closest to it. Crawlers hunt and bite any player; other enemies (guards, drones, crystals) hunt the player whose machine runs them, which is usually the closest. A fight's threat music plays for teammates nearby. |
| **Gunfire** | Every shot (players, guards, turrets, gun mines) is seen and heard by everyone: tracer, sound, puffs, and the muzzle flash on a teammate's gun. A shot that passes through you wounds you on your own machine (a teammate's only under the friendly-fire rule). |
| **Traps and props** | Bear traps, spike stabbers, coils, stalactites, mines, cave-tick nests, med stations, bio terminals and scrap eaters with their doors, the drill pod repair, guard spawners and the start shuttle: set off on one machine, in the same state everywhere. Damage lands on whoever was hit. Players who join later see the world as it is now. |
| **Traders** | One shelf for the team: buying is cleared with the host, so two players can't buy the same item. Each player has their own standing with the trader. Anger a trader and it turns on everyone. |
| **Story** | The Blue Heart rises for the whole team, and everyone gets the cargo. |
| **Warmth** | Standing next to a living teammate slowly warms you up. |
| **Players** | Movement and full limb pose at 20 Hz. Your chosen character (Experiment, Milky or Dune) shows correctly, along with wounds, blood, dirt, wetness, held and worn items, swings, speech bubbles and the dust they kick up (steps, jumps, landings). Name tags float above other players. |
| **Arriving** | On a new layer, whoever has arrived waits at the arrival point until the whole team has loaded, then everyone sets off together (75 s at most; the host can go ahead with the jump key). |
| **Clock** | Time speed is shared and decided by the host. Earthquakes happen to everyone at once. Sleep fast-forwards time once every living player is asleep or out cold, and stops when anyone wakes. While a teammate sleeps you can nap beside them however rested you are, and you sleep on with them unless you're cold, hurt, hungry, thirsty or in water. |
| **Death** | When you die, you spectate. The screen stays clear, the camera follows a living teammate (**A / D** to switch), and you hear what they hear. The host's **When you die** rule decides what happens next: **Next layer** (the default: back healed when the team descends), **Permadeath** (out for the rest of the run) or **Quick** (back beside a teammate after 15 s). The host can die too: the run carries on for the others. Only when everyone is dead does the run end, with the normal death screen. |
| **Rejoining** | If you drop out and come back to the same layer, you return where you were, and still dead if you were dead. |
| **Props** | Lifepod heater and shower. The shower works once for each player (only the players in the pod get washed). |

The game's own save slot is never touched during a session: co-op saves go to their own folder, and the single-player save is put back even after a crash.

## Known limits

- To leave a run, use the pause menu's middle button (in co-op it's the game's Exit, not Give up): press it, then press it again. A player leaves the session and goes to the menu with their character saved; the host takes everyone back to the menu and the run can be continued.
- If the host leaves the run, everyone returns to the menu. Everyone's character is saved, so the run isn't lost: if the host ended the session, any player can host from the co-op window (the run is already picked) and the others join with their own characters, on the layer they were on.
- If your connection drops, your character is saved, you go back to the menu, and the game reconnects on its own for up to a minute. You come back where you were.
- A dead player's body can be searched by teammates (everything but what's in their hands and mouth).
- Water is shared when a layer loads, and after that the water near each player is shared from that player's game. Liquid far from everyone (a geyser nobody is near) can differ until someone comes close.
- Pausing or alt-tabbing pauses only your own game. Creatures you're running stop for others until you return.
- One sleeper alone doesn't speed up time (others would be raced through their day). The sleeper's screen says time is waiting for everyone.
- Minigames (lockpicking, surgery) and crafting are per-player. Their results, such as broken containers, items and dropped loot, are shared.
- Batteries lying on the ground run down on each machine separately.
- An enemy only targets the player whose machine runs it (usually the closest). Two players next to one guard: it fights one of them.
- The endings are played by each player for themselves: everyone does their own cargo surgery and gets their own win. The escape starts for everyone when the scientist is shot, and the end guards come for the team when the first escape timer runs out. A host who finishes first is warned before leaving, since leaving ends the run for everyone. The endings haven't had a real playthrough yet; merc runs and the surface breach are untested.

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
- care and movement: treatment minigames, CPR, the defibrillator, giving and asking for items, leg-ups, pulling up, snapping out of a low, lifting together, catching a fall, carrying
- a dropped client reconnecting on its own and coming back where it was, with its saved character
- desync repair: a deliberately corrupted terrain chunk found and repaired
- traps, props and shots: world events in both directions, shot replay, a teammate's muzzle flash
- rot decided by the host (items crumbling for both), crafting from ground items cleared with the host
- water near both players identical, and fans and geysers rolled the same on four layers
- a co-op run preset applied on the pre-run screen, seen by both
- a message registered through the mod API, sent and answered
- three and four players, two of them joining mid-run

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
- voice chat, including the open-mic threshold and voice effects
- the endings
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

The game has only a Windows build, so on Linux and Steam Deck it runs through Proton, and the mod runs inside it like on Windows. Linux and Windows players can play together.

1. Run `bash install.sh` from the unzipped download (Steam Deck: in Desktop Mode, with Konsole). It finds the game in your Steam libraries (including Flatpak and Snap Steam), installs BepInEx 5 (the Windows x64 build, which is what Proton needs) if it's missing, and copies the mod in. `bash install.sh --uninstall` removes the mod.
2. BepInEx loads through `winhttp.dll`, which Proton ignores unless told otherwise. In Steam, right-click the game → **Properties** → **General** → **Launch options**, and set:

```
WINEDLLOVERRIDES="winhttp=n,b" %command%
```

That's all. On Steam Deck the co-op window scales up by itself, and text fields open Steam's on-screen keyboard. The game has no controller support of its own, so use a Steam Input layout with keyboard and mouse; every co-op action has a key (F1 window, Enter or T chat, H gestures, J catch, V voice, B bark, middle mouse ping) or a click (right-click a teammate). Under Proton the mod notes it in its log, and the **Bug report** button saves to your Linux desktop (or home folder), not inside the Proton prefix. The co-op logs themselves are inside the prefix: `~/.steam/steam/steamapps/compatdata/4576490/pfx/drive_c/users/steamuser/AppData/LocalLow/Orsoniks/CasualtiesUnknown/CasualtiesCoop/`.

**Bug reports.** The co-op window's **Bug report** button gathers your co-op logs and the session details into a folder on your desktop. Attach its files to the report.

## Testing on one machine

```powershell
.\tools\launch-instances.ps1 -Count 2 -Auto -SelfTest
```

This starts two game instances. The first hosts, the second joins `127.0.0.1`, and the host starts a run. Once both are in, each side edits terrain, damages and breaks objects, and moves items. Then the client asks to descend. Both sides log `VERIFY` lines to `%USERPROFILE%\AppData\LocalLow\Orsoniks\CasualtiesUnknown\CasualtiesCoop\coop-*-pid*.log`. Matching `terrain`, `health` and `runtime` values mean the two worlds agree.

`-Steam` makes the first instance host an invite-only Steam lobby instead, which requires the Steam client to be running. `-Minimized` starts the copies minimised, out of the way. `-Offline` keeps them on the menu, out of any session.

Screens for review, without touching the mouse: `CCOOP_SHOTS=<folder>` with `-Count 1 -Offline` saves every page of the co-op lobby (the key settings too), and `CCOOP_RUNSHOTS=<folder>` with `-Count 2 -Auto` saves the host's in-run screens (the right-click menu, the health panel, the pause menu, radio chat, an alert, the chat log, carry and item prompts, stacked captions, the gesture wheel, pings and arrows, the team summary, name tags beside the game's own speech, the lobby in a run).

The launcher lets several copies run by handing Doorstop a copy of `CasualtiesUnknown_Data\boot.config` without the `single-instance=` line (in `%TEMP%`). No game file is modified.

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

## Translations

The mod's text follows the game's language. A translation is a plain text file, `lang/<code>.txt`, where `<code>` is the game's language code (`zh-CN` for Simplified Chinese, or the name of a community language file in the game's `Lang` folder). Start from `lang/template.txt`: under each English line, write the translation after `= `. Keep `{0}`, `{1}`… (names and numbers) and the `*asterisks*` of emotes. Lines left empty show in English.

Put the file in `BepInEx/plugins/CasualtiesCoop/lang/`, or in the game's data folder under `CasualtiesCoop/lang/` (`%USERPROFILE%\AppData\LocalLow\Orsoniks\CasualtiesUnknown\CasualtiesCoop\lang\`) to keep it across mod updates. `Language` in the config picks a language other than the game's. After a mod update, `python tools/extract-strings.py lang/<code>.txt` adds the new lines to a translation and keeps the existing ones.

## For mod authors

Other BepInEx mods can share their own state through a CasualtiesCoop session. Reference `CCoop.Plugin.dll` and `CCoop.Net.dll`, add `[BepInDependency("ccoop.casualtiescoop", BepInDependency.DependencyFlags.SoftDependency)]`, and register each message type once from your plugin's `Awake`:

```csharp
CCoop.CCoopApi.Register<MyMessage>("my.mod.id", CCoop.Net.Delivery.Reliable, (from, m) => { /* on the main thread */ });
CCoop.CCoopApi.Broadcast(new MyMessage { ... });   // or SendTo(player, ...) / SendToHost(...)
```

A message implements `CCoop.Net.IMessage` (`Write` and `Read` must mirror each other). `CCoopApi` also tells you whether you're in a session or a run, whether you're the host, and who is playing. Every player needs the same mods: registered messages are part of the protocol fingerprint, so a player with a different set is turned away when joining instead of misreading packets.

## Notes

- Never commit game assemblies or decompiled game source (`.gitignore` enforces this).
- [Casualties Together](https://github.com/creaturefeaturelarry/casualties-together) (LGPL-2.1) is a separate, mature co-op mod for the same game. No code from it, or from any other co-op mod, is used here.
- The game's developer, Orsoniks, has allowed this mod on the condition that it is never sold. It is free and stays free: no paid builds, paywalled features or donation-locked releases.
