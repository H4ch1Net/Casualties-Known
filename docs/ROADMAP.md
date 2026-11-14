# Roadmap

Where CasualtiesCoop goes after v2.0. Milestones are ordered by what players feel most, and each ends in something testable.

Sizes are rough: **S** ≈ an evening, **M** ≈ a few sessions, **L** ≈ a week or more.

The aim is not to out-feature Casualties Together. It is to be the co-op mod that doesn't desync, is easy to start with friends on Steam, and can be trusted (small, readable, CI-built).

How the game works, system by system, and why each item below exists: [game-systems.md](game-systems.md).

---

## M0 · Playtest gate (now)

Nothing below matters until two real players on two Steam accounts can finish a few layers.

- [ ] **S** Run the [playtest checklist](#playtest-checklist): host, then join by invite, by "Join Game", and from a cold start through an invite.
- [x] **S** Add a **Report a problem** button. (The **Bug report** button: a desktop folder with the logs and session info.) It opens the log folder and copies a short session summary (versions, transport, ping, epoch, last errors) to the clipboard for a bug report.
- [x] **S** Add a **desync alarm**. Every 30 s the host sends a terrain and entity fingerprint, and clients compare theirs and warn in the log and on screen. (Built as `WorldCheck`: one fingerprint per 64×64 terrain chunk. A chunk that differs on two checks in a row is logged and repaired, see M5. Entities aren't fingerprinted yet.)

**Done when:** a 30-minute, three-layer session over Steam has no fingerprint mismatches, and both players say it felt right.

---

## M0.5 · Correctness fixes from the game audit ✅

Real defects in v2, found by reading the game rather than by playing it. Details are in [game-systems.md §4](game-systems.md#4-bugs-this-audit-found-in-v2).

✔ means checked by the two-instance self-test with matching fingerprints on both machines. ◻ means code is in and the hooks install, but no test drives it yet.

- [x] ✔ **Layer 12 on clients.** Scaffolds skip `Body.PlaceBody`. Structure placements now record and send *every* part they create (the End lab's interior lives in one), and clients rebuild each part from the same child of the same structure prefab. This also replaced the approximate standalone prefabs for ladders and med crates inside structures (105 exact parts on layer 1, 322 on layer 2).
- [x] ✔ **One author per explosion.** The host's blast is real: objects, items and the crater sync as usual, and damaged items re-announce their state. Clients replay it body-only (their own wounds, sound, shake, no object damage or crater). A client's own explosion becomes a request, and repeats within 1.5 s and 2 units are dropped.
- [x] ◻ **No duplicate death loot.** On clients, `GuardScript.Die`, `TraderScript.DropInventory` and `FanTrap.BreakFan` create no items, and the host makes the loot. A trader or fan event a client sees is forwarded to the host. Guard corpses stay on every machine.
- [x] ✔ **Crystals.** Each crystal's effects travel with the layer, including every rolled field and its heater. Layer 8: 616 crystals with an identical effect fingerprint on both machines, none without effects.
- [x] ◻ **Laser drone.** Health changes from `Open`/`Close` are not reported as damage, and non-owners don't run the drone at all.
- [x] ✔ **Content modifiers.** The host's water is rewritten after the modifier initialises, and gravity comes from the host (Turbulent forced: −37.9 on both). The Shiny light is re-applied.
- [x] ✔ **Full item state.** Every `[Saveable]` component is serialised the way the save system does it, plus `CustomItemBehaviour.state/data` and a gun's last-racked flag. A spawned water bottle and pistol fingerprint identically on both machines.
- [x] ◻ **Creature puppets stay puppets.** Guards, crystal enemies, laser drones and techballs don't run their own movement on non-owners (a puppet guard can still die). Every puppet is forced Kinematic each frame.
- [x] ◻ **Bottom exit panel.** "Save & exit" does nothing in a session except explain, and "Continue" asks the host.
- [x] ✔ / ◻ **Shared clock values.** The host sends ambient temperature (✔: 17.0, 19.4 and 30.9 on both machines across three runs) and the radiation-line state (◻: not driven by a test) once a second.

**Also found and fixed while doing this:**
- Objects destroyed locally by the game (eaten, crafted away, exploded, decayed) now vanish for everyone: clients report them and the host removes its copy.
- A descent stops the old layer from being shared the moment it starts, so `RegenerateWorld`'s mass destruction is never published.

**Self-test (`tools/launch-instances.ps1 -Auto -SelfTest`)** now has both sides:
- edit terrain
- damage and break objects
- trigger an explosion (the client's goes through the host)
- spawn stateful items and move items
- descend

`-StartLayer n` and `-Modifier i` start on any layer or force a modifier.

---

## M1 · Steam-first ✅ (built; two-account test pending)

The game already initialises Steamworks (2025.164, SDK 1.64), so all of this works under the game's App ID. Everything below is built. ✔ means verified (LAN self-test, one-account Steam smoke test, or screenshots). ◻ needs a second Steam account or a Deck.

| | Feature | How |
|---|---|---|
| ✔ | Steam names and avatars in the player list and name tags | `GetMediumFriendAvatar` → `SteamUtils.GetImageRGBA` → `Texture2D` |
| ◻ | Friends list shows who you're playing with | Rich presence `steam_player_group` (lobby id) and `steam_player_group_size` (these work without developer-defined tokens), plus the existing `connect` key. Also `SetPlayedWith` so teammates appear under *Recent players*. |
| ◻ | **Lobby browser** with Public / Friends' games tabs | `RequestLobbyList` + `AddRequestLobbyListStringFilter("ccoop", version)`, a filter for the game build, and `FilterSlotsAvailable` and `DistanceFilter`. Each row shows host, layer, players/max, a padlock and **estimated ping** (the host publishes `ConvertPingLocationToString` in lobby data; clients run `EstimatePingTimeFromLocalHost`). Friends' games come from `GetFriendGamePlayed`, keeping those in a CasualtiesCoop lobby. |
| ◻ | **Password-protected lobbies** | Lobby data `locked=1`. The password goes in our handshake, never in lobby data. |
| ✔ | Version check before connecting | Lobby data carries `SteamApps.GetAppBuildId()` and the mod version, so a mismatch tells you exactly what to update. |
| ◻ | Trusted identity | Take the joiner's Steam ID from the authenticated connection (`m_identityRemote`), not from what the client claims. Use it for persistent bans and for co-op saves (M6). |
| ✔ | Connection quality | `GetConnectionRealTimeStatus`: ping, quality, loss. Warn on bad links. |
| ◻ | Steam **game recording** markers | `SteamTimeline`: a game phase per run with a "Layer N" attribute, plus instant events for descents, deaths, revives and endings, using Steam's built-in `steam_*` icons. |
| ◻ | Steam Deck | `IsSteamRunningOnSteamDeck` → larger UI scale. `ShowFloatingGamepadTextInput` for chat. |
| — | **Proximity voice chat** | Covered in M3. |

**As built:**

- `Net/SteamLobby.cs` publishes the advert (`ccoop_game`, mod and game build, Steam build id, host, players/max, layer, run state, lock, ping location) and refreshes it every 2 s. It closes joining when the session is full.
- The browser filters on `ccoop_game`. Friends' sessions come from `GetFriendGamePlayed`, plus `RequestLobbyData` for lobbies whose data hasn't arrived yet.
- Passwords are checked in the session handshake. The lobby only says `ccoop_locked=1`.
- A wrong password, or an invite to a locked lobby, brings up the prompt.
- Identity: the host takes each Steam client's id from `GetConnectionInfo().m_identityRemote` and kicks a client whose profile claims another one.
- Bans live in `persistentDataPath/CasualtiesCoop/bans.txt` and are checked before admission.
- The ping table carries link quality.
- `UI/ConnectionBadge.cs` warns a client when the round trip is 180 ms or more, or more than 15% of packets are lost.
- The Deck keyboard uses `ShowFloatingGamepadTextInput` when one of our text fields gains focus (on Deck or in Big Picture).
- Chat on Deck waits for M3's chat rework.

**Not possible from a mod:**

- **Workshop** and custom rich-presence text need the developer to enable them for the app. Worth asking Orsoniks.
- **Steam Cloud** would spend the game's own quota. Don't.
- Achievements and stats belong to the game.

**Done when:** a friend can find your game in the browser or friends list, see your ping before joining, and join with one click. A version mismatch tells them what to update.

---

## M2 · Death, spectate, respawn ✅

Built in `Sync/LifeSync.cs` (death, spectating, respawn, wipe, rejoin) and `Sync/ClockSync.cs` (shared sleep). Verified by the two-instance self-test: client death, host death, descent respawn, the Quick and Permadeath rules, team wipe and shared sleep. Rejoining after a disconnect is built but not yet exercised.

Today, dying sends a client to the menu, and the host dying ends the run for everyone. The game already models revival: `alive` is just `brainHealth > 0`, and the death screen closes by itself if the body lives again.

- [x] **M** **Spectate instead of the death screen.** In a session, `EndSequence(0)` is replaced: the corpse stays in the world, and the camera follows a living teammate (cycle with A/D) with an overlay ("Waiting for the next layer · Q to cycle"). Puppets show "down" or "dead" in their name tags.
- [x] **M** **Respawn on the next layer.** When the team descends, dead players come back healed (`Body.Heal`, brain reset) at the spawn. Host rule `Respawn`: `NextLayer` (default) / `Permadeath` / `Immediate` (casual).
- [x] **S** **Host can die too.** The host's world keeps running while the host spectates. If every player is dead, the run ends for everyone with the normal death screen.
- [x] **S** **Leaving and rejoining** keeps your place (see M6 for inventory). *Built, not yet exercised.*
- [x] **M** **Sleep together.** The game fast-forwards 25× while you sleep or are unconscious, which co-op refuses today, so a nap takes real minutes. New rule: the shared clock fast-forwards only while every living player is asleep or down. A lone sleeper sees "waiting for others".

How revival works in the base game:
- There is **no CPR**.
- A defibrillator works only while the patient is still alive. After cardiac arrest, blood pressure falls for about 10 s, then the brain decays for about 67 s of game time.
- Epinephrine gives a 5% per second chance to restart the heart.
- A death that has already happened needs a mod-made revive: set `brainHealth > 0`, and the game removes the death screen itself.

**Self-test:** set the client's `brainHealth` to 0 and check that it spectates, then descend and check it respawns alive on layer 2 with matching fingerprints.

**As built:**

- **Death screen.** `HandleDeathScreen` is skipped while the session lives on and someone is alive.
- **Spectator view.** Each frame, `HandleUnconsciousScreen` is overridden to keep the screen clear. `HandleScreenShaders` gets a healthy body's values plus 45% saturation, and the fog is revealed around the watched teammate. `HandleAmbientSound` lifts the 50 Hz brain-death low-pass, and `customLookPos` points the camera at the teammate.
- **Respawn.** `Body.Heal()` on the dead player's own machine. Rules: `Respawn` = NextLayer / Permadeath / Quick, sent in `RunInfo`.
- **Team wipe.** The host calls it after 2 s with everyone on the layer dead (`TeamWipe`).
- **Rejoining.** The host keeps leavers' places by Steam id (or address and name) and sends `RejoinPlace` when they finish loading the same layer. Under Permadeath, being dead carries across layers.
- **Sleep.** The host races time (25×, or 3.5× if anyone's brain is dying, as the game does) once every living player has been unconscious for 2 s, and stops when anyone wakes. The unconscious and the dead never steer the clock.
- **Creatures** are run by living players only.

---

## M3 · Playing as a team ✅ (except friendly fire and non-crawler enemies)

Verified by the two-instance self-test:
- chat through the character's speech, with game keys blocked while typing
- a ping marker
- host rules reaching the client
- an exit vote that waits for both players, then takes both down
- a crawler biting the other player's puppet, with the damage landing on that player's real body
- threat music spreading to the other player
- the warmth heater on puppets

Voice is built but can't be tested without two Steam accounts.

- [x] **S** **Exit rule.** Descend only when a share of living players stand at an exit (host rule `ExitPercent`, default 50%). Show the on-screen count "2/3 ready". Drill pods need every living player inside. Stragglers are brought along rather than killed.
- [x] **S** **Text chat.** A key opens the input, and game input is blocked while typing. Messages pass through the speaker's own `Talker` distortion (impaired speech, brain damage), so a hurt character sounds hurt.
- [x] **S** **Ping marker.** Middle mouse marks a spot everyone can see, with a sound.
- [x] **M** **Host rules panel** in the co-op window: respawn mode, exit percentage, speed-up, friendly fire, password, max players. It's pushed to clients and shown in the lobby browser. *Friendly fire is not built yet: players can't hurt each other.*
- [x] **L** **Proximity voice.** *Built; needs a two-account test. Wall occlusion and brain-damage distortion are not done.* Uses `StartVoiceRecording` / `GetVoice` / `DecompressVoice` at `GetVoiceOptimalSampleRate`, with push-to-talk or voice activation. Voice goes over the unreliable channel and plays from a 3D AudioSource on the speaker's puppet, with distance falloff and the game's own conditions: muffled underwater or behind walls, silent when unconscious, distorted by brain damage.
- [~] **M** **Creatures hunt everyone.** *Done for crawlers (everything driven by `SpiderHandler`). Guards, turrets, drones and crystal enemies still target only the player whose machine runs them.* Today the owning machine's AI can only see its own player. Give puppets hitboxes on the owner's machine so its creatures can target any player. Bites on a puppet are sent to the victim's machine and applied with the creature's own `DamageLimb` values. Guards, turrets and drones send fire, melee and lunge events, and each machine applies the hit only to its own player.
- [x] **S** **Threat music for everyone.** Creature owners send a "threat" event, so a teammate in a fight hears the music too.
- [x] **S** **Body warmth.** Standing close to a teammate shares temperature. It's cheap and very much in the game's spirit.

---

**As built:**

- **Exits** (`ExitVote`). Every way down (Continue at the exit, a finished drill pod, the cargo pod, a cutscene) goes through `RegenerateWorld` and becomes a vote. The host descends once the vote count reaches its share (`ExitShare` percent) of living players. A drill pod needs every living player crouched in it, and everyone then gets the pod's after-effects (`LayerBegin.Pod` sets `doPod`). While you wait at the exit, the save panel stays shut, and walking away withdraws your vote. The host also waits, for up to a minute, for anyone still loading the layer, so nobody is pulled through two layers at once. A client that falls behind drops a stale snapshot instead of going live with it.
- **Chat** (`Chat`, `InputBlock`). Enter opens it. In a layer, your character says the line through `body.talker.Talk`, so brain damage, a full mouth or water slur it. That slurred text is what everyone reads, and it shows as a speech bubble on your puppet. The unconscious can't speak, and the dead chat only with the dead. A prefix on `KeyBinds.GetBind` returns `None` while you type, which silences every game control.
- **Pings** (`PingSync`). Middle mouse places a ring with your name for 7 s, pinned to the screen edge with a distance when off screen, and plays `scannerbeep`.
- **Rules** (`RulesSync`). The host's config is read every frame. Password and player cap apply to the live session, and the transports accept `MaxPeers + 1` so a full session gives a readable refusal. Changes go out as `RulesMessage` and into lobby data (`ccoop_rules`). The lobby type can change mid-session.
- **Voice** (`VoiceSync`). Steam records, filters silence and compresses. Packets travel unreliably through the host. Each speaker plays through a 100 ms jitter buffer and a streaming clip routed into the game's sound mixer, so underwater or unconscious muffling applies. Volume is full within 5 units, fades to zero at `Voice.Range` (35), and pans toward the speaker. The unconscious don't transmit, and the dead hear only the dead.
- **Crawlers** (`CreatureHunt`). On the owner's machine, a postfix on `SpiderHandler.Update` adds living puppets to its target choice (with the game's own aggro and retreat roll and a ground line-of-sight check) and does bite contact by distance. Puppets get no colliders, because traps and turrets scan the same layers. A `CreatureBite` goes to the victim, whose machine runs the creature's own `DamageLimb` plus the bite sound, scare, speech, mood loss and threat music.
- **Threat** (`TeamSense`). Threat music raised on one machine is announced once a second. Teammates within 40 units hear it too. Only locally raised threat is announced, so two machines can't keep each other's music running.
- **Warmth.** Each living puppet carries the game's own `Heater` (2.5 units, 37 °C, a gentle lerp). It warms you only when you're colder than body temperature.

## M4 · Caring for each other ✅ (built; needs real-player testing)

This is the heart of co-op in a medical survival game, and the largest milestone.

**As built:**

- **Vitals** (`VitalsSync`). Twice a second, each player sends blood, oxygen, pressure, pulse, consciousness, brain, temperature, pain and fibrillation, plus status signs. Each limb adds skin, muscle, bleeding, infection, pain and shrapnel, with flags for broken, dislocated, splinted, tourniquet, infected, missing, prosthetic and bandaged.
- **Teammate panel** (`UI/TeammatePanel`). Hover a teammate for a tooltip, or right-click them (the game's own interact key) for the panel. Puppets stay collider-free, so hovering is worked out from the limb positions. The player list also has a **Check** button. The panel shows the vitals with plain-language warnings, each limb's injuries and treatments, and these actions:
  - use the held item on a limb
  - set a dislocation
  - pull shrapnel by hand
  - take off a tourniquet or splint
  - hold for CPR
  - give, carry, put down
  - search a dead teammate's body
- **Treatment plays the minigame** (`MedicSession`). The treater's own matching limb stands in, briefly given the patient's dislocation and shrapnel.
  - Bandage wraps and syringe pushes (the item's own callbacks) stream to the patient. The patient's machine runs the same callbacks with a copy of the item on the real limb, and reports back what's left of the item, so the bandage runs out and the syringe empties on the treater's screen.
  - Whatever the minigame writes to the stand-in is measured around `MinigameBase.ActiveMinigameUpdate`, sent as a delta, and undone. That covers pain, a slipped needle, shrapnel pulled and the joint popping back.
  - Defibrillator shocks are redirected to the patient. A transpiler makes the AED and manual defibrillator minigames read the patient's fibrillation, pulse, arrest and life signs.
  - Items that are instant in the base game (sutures, creams, injectors, tourniquets, splints) are applied directly on the patient's machine. An item that turns out to open a minigame is learned and played.
- **CPR** holds the patient's pressure at 42 and lets oxygen climb, every frame, for 0.8 s after each half-second compression. That's enough to keep the brain alive, not to restart the heart, so the game's own defibrillator is still needed. Within 30 s of death, a shock has a 40% chance of bringing the brain back before the heart is shocked.
- **Give** (`GiveSync`). An offer, a prompt, and only on "yes" does the item move, with its full state. Nothing can be duplicated or lost.
- **Carry** (`CarrySync`). The carried player's machine ragdolls their body and pulls the chest with a `TargetJoint2D`, scaled to the ragdoll's mass and the layer's gravity, toward the carrier's shoulder at walking pace. Waking up ends it.
- **Lootable bodies** (`LootSync`). While dead, a player's pockets, bags and clothes are listed for teammates (never hands or mouth). The dead player's machine hands each item over. What's left comes back with them.

Verified by the two-instance self-test, with the test playing the minigames' hand: bandage, joint, morphine, AED with the patient's heart readings, CPR, carry, give, loot, revival within the death window.

**Decided (playtest feedback):**

- **Treating a teammate plays the real minigame.** The treater wraps the bandage, finds the vein and pulls the shrapnel themselves. Instant results take the fun out. Items that are instant in the base game (sutures, creams, splints, tourniquets) stay instant, as they are for yourself.
- **Dying costs, but not the run.** A dead player's inventory stays on their body, and teammates can loot it. The items in their hands and mouth stay with them. Whatever is left comes back with them when they respawn.

- [x] **S** **Clickable teammates.** Give each puppet a hitbox on the Descriptor layer, so the game's own hover and click code (`TryPickupFromWorld`) can find them. Add a right-click menu: inspect, treat, give, carry.
- [x] **M** **Teammate vitals.** A 2 Hz per-limb summary: skin, muscle, bleeding, broken, dislocated, infected, tourniquet, splint, prosthetic, plus blood, consciousness and pain. Also send the visible extras: dismembered limbs, prosthetic overlay, broken-bone and bandage sprites.
- [x] **L** **Teammate health panel**, where **the owner executes**:
  1. The treater opens a panel built from the teammate's vitals and plays the normal minigame (bandage, syringe, dislocation, shrapnel, splint, defib) against a read-only shadow.
  2. The result is sent to the patient's machine as primitives: bandage angle and item, liquid id and amount, defib chance, un-dislocate, mend bone, tourniquet or splint on/off, shrapnel removed.
  3. The patient's machine applies them to its real limb with the game's own functions, such as `onHealthUse` and `Defibrillate`, and reports back whether the item was used up.

  Six spots in the game are hard-wired to the local body and need patching:
  - Amputation adrenaline (`AmputationMinigame.cs:72`)
  - The ECG display (`ECGVisualizer.cs:29`)
  - Manual-defib INT (`ManualDefibMinigame.cs:59`)
  - The syringe icon (`SyringeMinigame.cs:55`)
  - `WoundView.limbLookingAt` in `Limb.Dismember`
  - The wound view's body binding

  Treatment is impossible while unconscious in the base game, so this is the only way to help a downed player.
- [x] **M** **Give items.** Drop an item onto a teammate and they get an accept prompt. The item travels with its contents (`ItemCodec`).
- [x] **M** **Carry the downed.** Grab an unconscious teammate. Their own machine pulls the ragdoll toward the carrier's puppet hand.
- [x] **M** **Revive a teammate.** A teammate's defibrillator or epinephrine works through the panel during the arrest window. Add a mod-made **CPR** minigame (the base game has none) that holds blood pressure up to buy time. A death that has already happened can be undone within a short window by setting `brainHealth > 0`, and the game closes the death screen itself.

---

## M4.5 · Moving as a team ✅ (built; catch not yet seen in a test)

Playtest ideas: the team should feel physical together, not just side by side.

- [x] **M** **Three ways to carry a teammate**, each with a cost. The carried player's own machine always moves their body.
  - **Drag:** one hand on them. Slow, and it uses no inventory slot.
  - **On your back:** needs a free back slot. You walk nearly as fast, but you can't jump as high, because they count as weight.
  - **In your arms:** both hands busy, and you walk a little slower.
- [x] **M** **Boost and pull.** Climbing as a team:
  - Stand next to a teammate and give them a leg-up, a stronger jump from your hands.
  - From a ledge, take a teammate's hand and pull them up toward you. They're pulled on their own machine, and their own physics settles them.
- [x] **S** **Snap out of it.** A teammate in deep depression can be shaken or talked out of it. Their mood gets a boost that fades over a few minutes. It's temporary: the cause is still there. It works through the game's own mood total, so being above the "medical cutoff" again lets them treat wounds.
- [x] **M** **Carry heavy things together.** Two players lift a barrel or crate that's too heavy for one: it moves only while both hold it. The host decides where it goes.
- [x] **M** **Catch a falling teammate.** Stand under someone who's falling and hold the catch key: their landing is softened. A fall from very high onto someone hurts the catcher too, shared by how far they fell.

**As built** (`CarrySync`, `TeamMotion`):
- Carry modes from the teammate panel. Drag uses one empty hand and forces a walk. Back needs no back wearable: load 8, jump ×0.7. Arms needs both hands empty: load 6, jump ×0.5, a little slower. The carried machine ragdolls its body and moves the held limb toward the carrier's chest or hand in `FixedUpdate`.
- Leg-up: a 5 s window for a ×1.75 jump next to the helper. Pull up: a temporary climbable from the teammate to the helper's hand, which carries them up on their own machine.
- Snap out of it: +40 mood (+20 a second time), fading over 3 minutes through the game's own mood total; 90 s cooldown per teammate.
- Lift together: a helper with a free hand takes half the weight of a heavy item (weight ≥ 4) through the game's own encumbrance.
- Catch (J): the faller's machine checks for a ready teammate under it. A catch softens the landing to half the game's hard-landing speed; landing on someone unready breaks some of the fall and hurts both. Very high falls hurt the catcher's arms.
- Leg-up, snap, lift, pull and back carry pass the self-test. The catch hasn't triggered in a run yet: the first try dropped a player who was still on the pull-up rope, the second a player knocked out by a coil trap. The test now revives and stands the faller first.

---

## M5 · Closing the sync gaps (in progress)

- [~] **M** **Self-healing world.** Build on the M0 alarm: on a mismatch, the host resends just the affected chunks (terrain, water, objects in them). A desync gets corrected without anyone noticing. Terrain is done: the client asks for chunks that differ twice in a row, the host sends their blocks as a blob, and the client keeps its own edits from the last few seconds on top. Water and objects aren't covered yet.
- [x] **M** **One-shot traps.** Bear trap, spike stabber, landmine press, ground glass, stalactite, coil shock and the cave-tick `started` flag: the trigger state is shared, and damage applies on the victim's own machine.
- [~] **S** **Rolled hazard state.** Piston phase, fan spin, geyser liquid and grabber-plant shape travel with the object (the per-type state codec from M0.5). Pistons are done: their rhythm comes from their position and their phase from the layer clock, which the host now keeps within 0.25 s everywhere. Fan object damage is applied by one machine only. Fan spin, geyser liquid and grabber shape are still rolled per machine.
- [x] **M** **One-shot props, confirmed by the host.** Med station, bio terminal and scrap-eater doors (these currently block progress for others), shuttle doors, drill pod "working" and "everyone inside". Results travel; minigames don't.
- [x] **M** **Traders.** The host owns stock and is the only one to drop death loot. Purchases are cleared with the host. Each player keeps their own reputation and credit (the game measures those from the player's own body). Hostility is shared. Not yet: shared trader movement (ask to move, hostile lunges).
- [x] **M** **Items made outside the item factory.** Unloaded rounds, batteries and magazines, ejected casings, food transforms, splint and tourniquet removal, heater-cooked meat.
- [ ] **S** **Item decay** is decided by the host for loose items. Clients don't self-destroy networked items at 0.
- [ ] **S** **Crafting** consumes networked ground items through the host.
- [ ] **M** **Water after load.** Each peer publishes changed cells inside its own simulated window. Also hook the direct writers: drinking, space drain, traders, geysers, auto-filling containers.
- [x] **S** **Guns.** Tracer, sound, puffs and creature reactions on every screen; muzzle flash on puppets not yet. Plus a **friendly fire** host rule (on in Hard and Brutal).
- [ ] **M** **Story and endings as a team:**
  - Blue Heart cargo embedding.
  - The surface-breach cutscene at the top of layer 11, plus the scientist script and escape timer on layer 12, all driven by the host.
  - Each player unlocks their own wins (`wonchar`).
  - Merc runs: the host decides `canStartMercRun` and spawns the techball and chip, and sends `isMercRun`.
  - Endless loops: the descent request carries "loop".
- [x] **S** **Guard spawners** fire when *any* player is in range.
- [x] **S** **Lifepod shower** only affects players inside the pod.
- [x] **S** **`ShuttleStarter`.** Resolve the one object clients can't spawn.

**As built so far:**
- `WorldEvents`: one message for what one machine sets off. It covers the bear trap, spike stabber, coil, stalactite drop, cave-tick nest (now part of the snapshot), mine press, gun mine, drill pod repair, scrap eater feed (amounts add up), `Backgroundify` (bio terminal and scrap doors, used med stations, one heal per station), guard spawner count, shuttle doors and the Blue Heart. The host keeps the first of each one-shot and replays them for late joiners after the snapshot. A mine whose host copy broke can't explode a second time.
- Blue Heart: it rises for everyone, and every player's own machine embeds their own cargo, which everyone needs for the way out. Endless loops follow the host's own cutscene.
- `ShotSync`: every `TurretScript.Shoot` is replayed body-only elsewhere, with dedupe for turrets that fire on two machines at once. A guard's shot hits only a body under its reticle. Gun sounds are looked up from the gun's prefab.
- `ItemOrigins`: a postfix on `Item.Start` catches every new loose item. Container spills count only on the owner's machine. Clients skip corpse loot rolls, and only the meat's owner cooks it. New spawns are published a frame late, so items the game drops and destroys in the same frame are never sent.
- `TraderSync`: stock, range and hostility travel in the snapshot and are applied after `Start`. Purchases ask the host (`TraderTrade`); the free bandage for a bleeding player goes on the shared shelf.
- Clients now also remove leftover world content of their own (an old start shuttle) using the host's own content test.
- Enemies: guards, drones and crystals are already run by the nearest living player's machine, so each hunts whoever is closest. Retargeting by transpiler (an enemy targeting a teammate's puppet) is still open.

---

## M6 · Saves ✅ (built; continue-run not yet tested)

The game saves only the player: inventory, `[Saveable]` body state, recipes and the layer number. Loading regenerates the layer. That fits co-op well.

- [x] **M** **Co-op save slot.** Each player saves their own character, keyed by run id and Steam id. The host's file also holds run settings and depth. This is done by swapping files around `SaveSystem.SaveGame`/`TryLoadGame`, so the save format stays the game's. The single-player `save.sv` is always backed up first and restored even after a crash.
- [x] **S** Auto-save on every descent and on disconnect.
- [x] **S** **Continue co-op run** on the main menu. Rejoining players get their own character back automatically; newcomers start fresh.

**As built** (`CoopSaves`): saves live in `CasualtiesCoop/saves/<run id>/<steam id>.sv` next to a `run.txt` (layer, players). The run id travels with `RunInfo`. The game's own `SaveGame` writes them, with `save.sv` moved aside and put back, and a marker file restores it after a crash. A save happens 3 s after each new layer goes live and on leaving; none on the last layers, where the game itself refuses to save. A team wipe deletes the run's save, like the game. The host picks a saved run in the Host tab, then presses "Start the saved run".

---

## M7 · Trust, reach and scale

- [~] **M** **GitHub repo and CI.** (workflow written; no public repo yet) Build and test `CCoop.Net` on every push. Build the plugin against stripped reference assemblies, or on a self-hosted runner, so no game code is committed. Publish releases with checksums: Casualties Together players are asking for exactly this ([their issue #51](https://github.com/creaturefeaturelarry/casualties-together/issues/51)).
- [x] **S** **Update check.** Read the latest GitHub release and show a notice. No silent downloads.
- [~] **M** **4–8 players.** Bandwidth budget, interest management for creatures and items, a host FPS budget.
  - Measured (`[net] traffic` lines, every 30 s in the log): three idle players on LAN, host up 12.5 KB/s and down 6 KB/s, each client up 3 KB/s and down 6 KB/s. `PlayerState` (20 Hz, about 136 bytes) is 80% of it; a layer snapshot is 200–300 KB once per join.
  - Four players on one machine (two joined mid-run on layer 4): both late joins got the layer in under 0.1 s on the host and about 2.7 s to apply; no warnings anywhere.
  - Player state now goes through the host, which passes it on at full rate to teammates within 48 m (and to the dead, who spectate) and at 5 Hz to those farther away. At 8 players that keeps the host's upload near 60–80 KB/s instead of 140.
  - Still open: interest management for creatures and items, and a host FPS budget.
- [x] **S** **Linux / Proton / Steam Deck notes.** Document `WINEDLLOVERRIDES="winhttp=n,b" %command%`.
- [x] **S** **Unicode Steam names** (CJK) in the IMGUI font.
- [ ] **S** Ask Orsoniks for permission, Workshop support and rich-presence tokens.

---

## Carried over from M1–M3

These were cut to keep milestones shippable. Each slots into the milestone named.

- [~] **M** **Friendly fire**: guns done in M5 (host rule). Still open: Melee swings and thrown items that reach a puppet are resolved on the attacker's machine by distance, like crawler bites, then applied by the victim's machine with the game's own hit code. Host rule, off by default. → with M4, which adds puppet hit zones.
- [ ] **M** **Every enemy hunts everyone.** Guards, turrets, laser drones, crystal enemies, grabber plants and traders read `PlayerCamera.main.body` directly. On the owner's machine, swap that read (Harmony transpiler or a per-class prefix) for "nearest living player", including puppets. Their raycast hits on a puppet become hit events applied by the victim, as bites are. → M5.
- [~] **S** **Voice polish.** (Built but untested: voice needs Steam on both sides.)
  - [x] Walls muffle a voice: a `Ground` linecast between listener and speaker adds a low-pass. (Cutoff 1.4 kHz and 75% volume; the filter glides so corners don't click.)
  - [ ] The speaker's own condition distorts it: brain damage or being underwater, mirroring `Talker`. (A broken or dislocated jaw already muffles.)
  - [x] Per-player volume and mute in the player list. (A button cycles 100% / 50% / muted / 150%, remembered per Steam id.)
  - [ ] Optional voice activation threshold for open mic. → M8.
- [ ] **S** **Two-account Steam playtest:** browser, password, ban, rich-presence grouping, voice, rejoin. → before 2.1.

---

## M8 · Feeling like one team

Small things that make four people in a cave feel like a squad.

- [x] **S** **Teammate markers.** An arrow at the screen edge for each teammate who is off screen, with distance and state (dead, out cold, bleeding from M4 vitals). Players can turn them off, and harder difficulty presets remove them.
- [-] **M** **Shared map knowledge.** The layer's fog is revealed where *any* teammate has been. The host merges each player's revealed area periodically and pushes it with `SetFog`. This also makes late joiners' fog match. *Dropped:* the game has no explored-area map. Its fog is one haze value for the whole layer (`fogAmount`), which the snapshot already carries.
- [x] **M** **Gesture wheel.** Hold a key for a radial wheel:
  - **Bark** (the game's `PantSound.Bark`, now heard by everyone).
  - **Point**: a line from your hand to the cursor that teammates see.
  - **Wave**, **Come here**, **Wait**, **Thumbs up**, **No**, **Help!** (a louder call, heard further away and marked at screen edges).
  - Gestures show as `*waves*` speech bubbles, a form the game never garbles.
- [x] **M** **Hug.** Stand next to a teammate and hug. It lifts both players' mood through the game's own mood system (the game blocks treating wounds when mood is too low), and it shares body warmth faster. It's the co-op answer to the game's lonely misery.
- [x] **M** **Your condition shapes how you communicate.**
  - A broken or dislocated jaw, a full mouth or a disfigured face already slur chat through `Talker`. Voice gets the same treatment: muffled or crackling.
  - Brain damage and severe insanity garble what you **read** (chat, names), the way the game garbles its descriptions.
  - Deafness (`hearingLoss`) quiets voices.
  - Losing your eyes hides pings and name tags.
- [x] **S** **Screen effects for co-op states.** *(Being carried has no sway yet.)* Spectating gets its own look: a cold desaturated tint, a slow vignette pulse, a faint grain. A short flash and heartbeat when a teammate dies nearby, a gasp and colour return when you're revived, a gentle sway while you're carried.
- [ ] **S** **Co-op death receipt.** The end screen's receipt (`PlayerCamera.EndSequence`) gets a line per teammate: character, time alive, how they died, calories. Add a "copy run summary" button, like the game's own clipboard copy.
- [x] **S** **Co-op milestones.** Mod-local, never Steam achievements: carried a teammate 100 m, revived someone after death, descended 10 layers together, survived a layer with everyone at under 20% blood. Shown in the co-op window. (Built: Pack mule, Safe hands, Stepping stone, Warm company, Field medic, Second chance, Deeper together. They are kept in `CasualtiesCoop/milestones.txt`, and self-tests never write it.)
- [ ] **S** **Ask for an item.** From the teammate panel (M4), "Ask for…" an item they carry. They see a prompt and can hand it over with M4's give.

**Done when:** a four-player session never has to ask "where are you?" or "who has bandages?" out loud.

---

## M9 · Scaling a run to the team

The base game is balanced for one. Four players trivialise some layers and starve on others.

- [x] **M** **Loot rule.** Shared (today: one set of loot for the team) or scaled: the host's generation raises `lootRarityMultiplier` and spawn counts with player count, before the snapshot is taken, so every machine still sees the same world. (Built as a host rule: "More for more players" raises the `baselootdensity` the game reads by 35% per extra player, through a postfix on `GetRunSettingFloat`, so the run settings and saves stay the game's.)
- [x] **M** **Danger rule.** Creature count and strength scale with players (spawn multiplier, `SpiderHandler.strengthMult`). Host rule with Off / Light / Full presets. (Built as "As set / Tougher / Much tougher": +10% or +25% `enemystrength` per extra player, on every machine. Creature *count* isn't scaled: the game has no run setting for it. Friendly gets more loot, Hard gets tougher enemies, and Brutal gets much tougher ones.)
- [x] **M** **Corpse looting.** *(Built in M4: lootable bodies.)* A dead player's inventory becomes a lootable bag at the corpse, or the puppet itself becomes lootable after M4's clickable teammates. Optional host rule, "Respawn with nothing", so dying costs something under Quick respawn.
- [x] **M** **Co-op difficulty presets.** These sit on top of the game's own difficulty (its run settings) and only set the co-op rules:

  | Preset | Arrows | Name tags | Respawn | Descend when | Spawn |
  |---|---|---|---|---|---|
  | Friendly | on | on | quick | first arrives | together |
  | Standard | on | on | next layer | half | together |
  | Hard | off | on | next layer | all | apart |
  | Brutal | off | off | permadeath | all | apart, voice range halved |

  Any rule changed by hand shows the preset as "Custom". The preset shows in the lobby browser.
- [x] **M** **Spawn apart.** *(Not yet exercised by the self-test.)* On a new layer, each player arrives at a different point across the same depth (sideways, never deeper), spread out over the layer's width. The host picks open spots and sends each player theirs. Finding each other becomes part of the layer.
- [ ] **S** **Co-op run presets.** Name and share the game's `runSettings` as presets ("Long night", "Glass bones").
- [x] **S** **Per-player difficulty readout.** In the co-op window, show which run settings the host chose, since clients can't open the host's run-settings screen. (A "Run settings" foldout names the game's preset they match, or lists what differs from normal, in the game's own words.)

**Done when:** a four-player run on default rules feels as tense as a solo run.

---

## M10 · Resilience

What happens when real networks and real crashes meet a two-hour run.

- [ ] **L** **Host migration.**
  - Why it's feasible: every client already holds the whole layer (it's streamed), entity ids are host-assigned and identical everywhere, and Steam hands lobby ownership to another member when the owner leaves.
  - What happens: when the host drops, the best-connected client promotes its copy to authoritative. It re-registers runtime ids, reassigns creature ownership and takes over the clock. The others reconnect to it through the same lobby.
  - Done when the host can quit mid-layer and the others keep playing.
- [x] **M** **Reconnect after a drop.** A client that loses the connection keeps its world for 60 s and reconnects to the same lobby. It receives only what changed since its last state (by epoch and change sequence), not a full snapshot.
  - As built, simpler: the dropped client saves its character (before the layer goes offline), goes back to the menu and retries the same host or lobby every 5 s for 60 s. The normal join then loads its saved character and the host puts it back where it was. A full layer snapshot is sent (200–300 KB, about 2 s), not a delta. A "Reconnecting… Stop" row shows in the co-op window.
  - Self-test: a simulated drop (`NetSession.SimulateDrop`) reconnects on the first attempt and is back in the layer about 7 s later.
- [ ] **M** **Delta and progressive snapshots.** Send terrain and the objects near the joiner first, then the rest. This cuts the 1–2 s join stall on big layers and makes reconnects cheap.
- [x] **S** **Desync report.** (the Bug report button in the co-op window) One button bundles the logs, the last VERIFY-style fingerprints and the session info into a zip on the desktop, ready to attach to a GitHub issue. Nothing is uploaded automatically.
- [x] **S** **Game-update guard.** (`GameCheck` at start-up)
  - At startup, every game member the mod reflects on or patches is checked.
  - If the game's assembly hash is new and anything is missing, the co-op window says which features are affected.
  - A tool lists those members, so after a game update the decompile diff is a short read rather than a hunt.

**Done when:** a client can lose Wi-Fi for 20 s, and the host can crash, without ending the run for everyone else.

---

## M11 · Reach and polish

- [ ] **M** **Controller support.** Gamepad navigation of the co-op window, a radial menu for teammate actions (M4), and ping and push-to-talk on gamepad buttons. Chat goes through Steam's on-screen keyboard (already used on Deck).
- [ ] **M** **Localisation.** All mod text moves into a string table. Ship English, and accept community translations for the languages the game itself ships.
- [x] **S** **Accessibility.** Colour-blind-safe ping and name-tag palettes, a text-size setting separate from UI scale, and a no-flash option for ping bursts. (Built: "Colour-blind safe colours" switches player colours to Okabe and Ito's palette, and "Reduce flashes and shaking" turns the mod's flashes into a faint tint, drops its screen shakes and stills ping and spectator pulses. Both are in the co-op window. Text size follows the window's UI scale; a separate setting wasn't needed.)
- [ ] **S** **A small mod API.** Other BepInEx mods can register their own synced messages through CasualtiesCoop (`CCoopApi.Register<T>()`), with the registry hash covering them so mismatches are refused at the door.
- [ ] **S** **Distribution.**
  - GitHub releases (M7).
  - A Nexus page.
  - A Thunderstore/r2modman package, if the game gets a community there.
  - An installer script that also installs BepInEx.

---

## M12 · New ways to play (ideas to validate)

Only after the core is solid and players ask for it. Each needs a playtest before it's built properly.

- [ ] **M** **Spectator slots.** Join a full session as a camera, with no body, following any player. Good for streamers and for friends waiting for a slot.
- [ ] **M** **Rescue start.** One player begins downed a layer or two deeper, and the rest must reach them before their brain gives out. It uses M2's spectating and M4's revival.
- [ ] **M** **Split roles.** A host rule that gives each character a team job, using what the characters already do differently. A medic's kit, a scout, a hauler. These are emphases built from starting items and run settings, never new mechanics.
- [ ] **L** **Relay challenge.** Players take turns: one plays a layer while the others spectate and advise, then the body passes on. A different way to share a run for groups with one strong player.

---

## Release plan

| Version | Contents | Gate |
|---|---|---|
| 2.0 | M0–M3 (today's build) | Two-player playtest checklist passes |
| 2.1 | M4 and the carried-over items | Two-account Steam test, a 30-minute run without a desync |
| 2.2 | M5 | Self-healing alarm silent through a full run to layer 12 |
| 2.3 | M6 | Save, quit and continue a co-op run, single-player save untouched |
| 3.0 | M7: public GitHub release, CI, update check | A clean install by someone who has never seen the mod |
| 3.x | M8–M11, in whatever order players ask for | |
| 4.0 | M12 ideas that survived playtests | |

## Risks

| Risk | Mitigation |
|---|---|
| A game update renames or removes something the mod patches | `FailedPatches` already isolates each patch. M10's update guard and the member list make the fix a short job. Keep the decompile outside the repo. |
| The developer ships official multiplayer | Stay compatible and small. If it lands, point players to it and keep only what it lacks. |
| Steam-only features (lobbies, voice, avatars) | LAN/IP stays a first-class path, and every Steam call is a no-op without Steam. |
| Bandwidth at 6–8 players (voice, states, creatures) | M7's budget work: interest management by distance and layer, and a voice bitrate cap. |
| Desyncs that only appear deep in long runs | Self-healing (M5), the desync report (M10), and longer automated runs (below). |
| One maintainer, AI-written code | Docs and self-tests are the source of truth. Every milestone extends the self-test before it's called done. |

## Testing plan

The self-test grew into one long script. Next:

- **Scenarios.** `CCOOP_SCENARIO=death|exits|team|items|...` runs one focused scenario, with pass/fail lines instead of fingerprints to eyeball.
- **One command for everything.** `tools/run-scenarios.ps1` runs every scenario with two instances and prints a summary table. Run it before every release.
- **Deep runs.** A long self-play scenario descends to layer 12 with random actions on both sides and checks fingerprints on every layer. Run it overnight before releases.
- **Three and four players.** `launch-instances.ps1 -Count 4` with a scenario that checks the cases two players can't: spectator switching, exit shares and creature ownership churn.

---

## Decisions to make

Recommended defaults are in **bold**. Most become host rules, so they're less final than they look.

| Question | Options |
|---|---|
| Death | **Spectate, respawn next layer** (built, default) · permadeath · quick respawn (both built as host rules) |
| Descending | **Half of living players at the exit** (built, default) · everyone · first arrives |
| Time speed | **Shared, host decides; sleep races only when everyone sleeps** (built) · disabled like Casualties Together |
| Friendly fire | **Off** · on (needs puppet hit zones, M4) |
| Player cap | **4 tested, 8 allowed** |
| Distribution | **GitHub releases** + Nexus page |
| Teammate treatment (M4) | **Decided: the treater plays the real minigame**; the effects land on the patient's body |
| Loot with more players (M9) | **Shared (today)** · scaled with player count |
| Danger with more players (M9) | **Off** · light · full |
| Dying's cost | **Decided: the body stays lootable** (except hands and mouth); you keep what isn't taken |
| Chat key | **Decided: Enter, plus T when no game action uses it** |
| Difficulty | **Decided: co-op difficulty presets** layered on the game's own run settings (see M9) |
| After M4, build next | **M5 (sync gaps)**, since desyncs hurt every session · M8 (team QoL) · M10 (resilience) |

## Playtest checklist

1. Host with Steam (Friends). The friend joins through the overlay invite, then through "Join Game" in the friends list, then by launching the game from an invite while it's closed.
2. **Find games:** the friend sees the session under Friends, with the layer, player count and ping. Set a password, rejoin, and check the prompt and that a wrong password re-prompts. Check the avatars in the player list and on name tags.
3. Both on layer 1: mine the same wall, break the same crate, fight the same creature, throw items to each other, use the lifepod heater.
4. Descend through an exit, then through a drill pod.
5. **Death:** one player dies. Check that they spectate the other (screen clear, sound on, A / D), and come back on the next layer. Repeat with the host dying. Then both die: the death screen comes up for both.
6. **Sleep:** one sleeps (time stays normal, with a "waiting" caption), then both sleep (time races), then one wakes (normal again).
7. **Team:**
   - Chat with Enter: your character says it, and the other screen shows a bubble.
   - Hold V to talk: check the volume falls off with distance.
   - Middle-click a spot: the other player sees the ring, or an edge marker when it's off screen.
   - Let a crawler go after the other player. Check that it bites them and their screen shows the damage.
8. **Exits:** set *Descend when* to *All are there* under Rules. One player continues at the exit (they should wait), then the other (both go down). Repair a drill pod: it needs both crouched inside.
9. Speed up, then pause and alt-tab on each side.
10. Client leaves and rejoins mid-layer: same place, and still dead if they were dead.
11. Kick and ban from the player list. The banned player is refused on retry. Unban them on the Host tab.
12. Collect both `coop-*.log` files.
