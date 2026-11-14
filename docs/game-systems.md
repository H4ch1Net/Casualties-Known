# Casualties: Unknown — game systems and what they mean for co-op

A map of the full game (Steam 4576490, Unity 2022.3, Mono), built from its decompiled assembly and public sources. For each system: how it works, how the mod handles it today, and what should happen.

Status key: ✅ synced · ⚠️ partly · ❌ not synced · 🐛 broken in v2 · 🏠 local-only, and that is correct

Code references look like `Type.cs:line` and point into the decompiled type files. Never commit decompiled source.

---

## 1. The game at a glance

**Premise.** A surgically modified creature is dropped into an alien planet's caves to retrieve lost cargo. It's a run-based survival sim with a full cardiovascular and limb-damage model, three skills (STR / RES / INT), about 300 items, crafting, and permadeath. It's officially single-player.

### Layers (12, `amountOfLayers = temperatureCurves.Length`)

| # | Title | What's special |
|---|---|---|
| 1 | Gravel lands | Lifepod start, starting supplies, seasonal props, challenge settings applied. Shade crawlers, spike traps, jump pads, geysers |
| 2 | Deeper gravel lands | Dark. Cave ticks, bear traps, barbed fences, lumalgae. Chip: regeneration |
| 3 | Dried desert | Sandvines (climbable), turrets, landmines, waste pipes, stalactites, sand stalkers. Chip: capacitor |
| 4 | Wasteland | Radioactive, oil, coils, sound cannons, spent fuel. Chip: swap |
| 5 | Overgrown depths | Thornbacks and an elder thornback, tentacle plants, skullcrushers. Chip: rejuvenation |
| 6 | Frozen chasm | Fog 0.8–1, snow on the body |
| 7 | Fungal pools | Fungal rain. Chip: ai |
| 8 | Crystalline hollow | Crystals with rolled effects |
| 9 | Geothermal vents | Magma; lava cools into rock near the player. Chip: kinetic |
| 10 | WHERE WE MUST REACH | Blue Heart: embed the cargo. No radiation line, no exit panel, no modifiers. Barotrauma |
| 11 | CRIMSON ASCENT | Climb up. No quakes. The exit is the **top**, where the surface breach cutscene plays |
| 12 | Earth, a lab | The End lab, scientist cutscene, all three endings |

### Characters (prefabs `Special/Characters/<name>`)

| | Base STR/RES/INT | Quirks |
|---|---|---|
| **Experiment** (SAW-01) | 9 / 8 / 7 | Canine (chocolate), bioluminescent sight |
| **Milky** (SAW-03) | 4 / 6 / 12, STR/RES gain ×0.5 | Narcoleptic (short sleep, no rough-surface penalty), night vision, marksman |
| **Dune** (SAW-12) | 17 / 18 / 0, INT gain ×0.5 | Aquatic (gills), furless (cold), can't read, mood ×0.66 |

Unlock order: Experiment's ending unlocks Milky, and Milky's unlocks Dune. Wins are stored per machine (`PlayerPrefs wonchar<n>`).

### Endings (all from layer 12, after the cargo is embedded on layer 10)

| Ending | Trigger |
|---|---|
| **Bio-pod** | Reach the lab exit, or the brain starts dying in the lab |
| **Escape** | Shoot the scientist on an unchipped run, then survive the guard escape timer (60.75 s) and the cargo surgery |
| **M.E.R.C.** | Reach the top of layer 11 wearing the M.E.R.C. scarf (unlocked after a bio-pod or escape ending) |
| Endless | The Blue Heart sends you back to layer 1 and the run only ends in death |

### Layer modifiers (19)

Foggy, Hot, Scorching, Cold, Freezing, Flooded, Dried, Rainy, Infested, Ionized, Solar-struck (unchipped), Turbulent (gravity), Shiny, Oxygenated, Sterile, Stable (no quakes), Beautiful, Unsightly, Rich (ores). None of them has an Update method; their effects live in fields. Rolled once per layer with chance `layermodifierchance`.

### Run settings

About 45 keys in five groups:
- **World:** loot and trap density and growth, ore amount, ambient light, time limit, quake interval, temperature offset.
- **Body:** metabolism, healing, bleeding, infection, fibrillation, mood, armour, stamina, damage, sleep, encumbrance.
- **Traders:** chance, stock, reputation.
- **Toggles:** strokes, brain-damage effects, forced sleep, low-mood events, liquid pushing, disfigurement, crush depth.
- **Challenges:** permanent addiction, mindwiped, zero skills, blind, amputee, immortal snail.

Presets are normal, relaxed, paradise, desolate, unchipped and endless. All settings are read in `WorldGeneration.Start`, so they must be set before the scene loads.

---

## 2. How the game is built, and the rules that follow for co-op

Six facts shape everything:

1. **Everything is the local player.**
   - `PlayerCamera.main.body` appears about 350 times.
   - Traps, creatures, heaters, showers, traders and minigames all sense either that body or physics on the Body/Limb layers.
   - Puppets have no colliders, so on each machine the world only ever sees and hurts that machine's own player.
   - Damage therefore lands on the right machine for free. The problems are shared state and duplicated side effects.
2. **There is no damage API.** About 35 types write limb fields directly (`limb.skinHealth -= …`). Hurting a player from another machine means sending a request to *their* machine.
3. **Simulation follows the camera.** Chunk renderers are enabled about ±2 chunks around the local camera. Creatures, hazards and many traps return early when their chunk is off or they aren't being rendered. Fluid simulates ±64 blocks around the camera. Every machine simulates only around its own player.
4. **Randomness is unseeded.** Generation, loot rolls, trap phases, trader stock, crystal effects, keypad codes and blueprint recipes are all rolled with the global `UnityEngine.Random`. Any per-object randomness rolled in `Awake`/`Start` differs per machine unless it's transferred.
5. **Interaction is `SendMessage("OnUse")`** on whatever is under the cursor within range. OnUse bodies spend from the local player's inventory and open local UI.
6. **Time is one global.** `Time.timeScale` is changed by many things:
   - pause (0×)
   - unconsciousness and sleep (25×)
   - dying (3.5×)
   - notes (0.16×)
   - the speed keys
   - direct writes in quakes, cutscenes and the console

### Rules the mod follows (or must)

| Rule | Why |
|---|---|
| **One author per world effect.** Terrain, object damage, loot, explosions, spawns and fluid writes have exactly one machine that publishes them: the host for the environment, the owner for a creature, the actor for a player action. Other machines may *show* the effect but must not re-apply it or report it. | Damage is synced as amounts, so logic that runs on every machine (a mine exploding on each one) multiplies the damage and duplicates the loot. |
| **The owner executes on its own body.** Anything that affects a player runs on that player's machine, triggered by a message. | Bodies exist only locally, and there is no damage API. |
| **Send state, not events, for things that persist.** | Idempotent and safe to replay to late joiners. Already used for terrain, heaters and cracks. |
| **Transfer rolled state.** Per-object fields rolled in `Awake`/`Start` travel with the object. | Seeding can't fix objects the host generated before ids existed. |
| **Never freeze or race the shared clock.** | One player's pause, sleep or death can't stop or speed up everyone. |
| **The owner's AI should see every player.** | The nearest-player owner chases its own body today. Puppet hitboxes would let it chase anyone, with bites forwarded to the victim. |

---

## 3. System by system

### World and layers

| System | Status | Notes and plan |
|---|---|---|
| Terrain | ✅ | Streamed; `SetBlock` deltas |
| Block cracks | ✅ | Totals, idempotent |
| Water (initial) | ✅ | Streamed with the layer |
| Water (after load) | ❌ | Per-camera simulation plus direct `fluid[]` writes (drinking, space drain, traders, geysers, auto-filling containers). Plan: owner-of-region deltas; hook `SetLiquid` and the direct writers |
| Structures | ✅ | Placement replay (deterministic) |
| Layer modifier | ✅ | Index applied, but content modifiers run their world changes on clients after the host's state is written. Flooded and Dried rewrite water; Turbulent rolls its own gravity |
| Earthquakes | ✅ | Schedule from the host; each machine shakes and quarries around itself (the edits sync) |
| Radiation line (time limit) | ✅ | Per-machine timer, and slower while *the local* player is unconscious. Send the host's position |
| Ambient temperature | ✅ | `curve.Evaluate(Time.timeSinceLevelLoad)` depends on per-machine time. Send the host's curve time |
| Layer 12 (End lab) | ✅ | Clients throw in `Body.PlaceBody` (`ENDLABSPAWN` comes from a skipped step), so generation never finishes |
| Crystals (layer 8) | ✅ | Effects are rolled in a step clients skip. Their crystals have `effects == null` and throw on every touch |
| Climbables (ropes, vines, veins) | ✅ / ⚠️ | Generated ones travel as objects. A runtime climbing rope spawns before its points are set |
| Chips and merc run | ❌ | Chip position and indicator, `isMercRun`, `canStartMercRun` from local PlayerPrefs, the techball spawned on every machine |
| Endless loop | ❌ | The Blue Heart sets depth to 0 before `RegenerateWorld`; the request must say "loop" |
| Descending | ✅ | Clients ask; the host's descent brings everyone. Next: an exit rule |
| Bottom exit panel | ✅ | "Save & exit" drops a client out of the run. "Continue" asks the host (fine) |
| Surface breach (layer 11 top) | ❌ | A local cutscene per body. Should be team-wide |
| Endings | ❌ | Local cutscenes, scientist script per machine, escape timer per machine. Plan: host-driven story state; each player unlocks their own wins |

### Objects, items, crafting

| System | Status | Notes and plan |
|---|---|---|
| Object damage and breaking | ✅ | Amounts plus host-authoritative breaking with captured loot |
| Item pickup, drop, throw | ✅ | Host-arbitrated pickup; dropper-named drops |
| Loose item physics | ✅ | Creator streams motion and rest |
| Item state | ✅ | The codec sends id, condition, label, colour, favourite and contents. It loses water-container liquids (and `Start` overwrites condition from them), battery type and charge (`Awake` re-adds a battery, which duplicates it), gun and magazine rounds, chamber, safety and racked state, flashlight mode, a lit dynamite timer, and per-item rolls (blueprint recipe, mystery can, note text). Plan: serialise every `[Saveable]` component's `[JsonProperty]` fields, as the save system does, plus a few extras |
| Items created outside `Utils.Create` | ❌ | Unloading rounds, batteries and magazines, ejected casings, food transforms, splint and tourniquet removal, heater-cooked meat, corpse loot. Hook `Object.Instantiate` for Item prefabs, or the specific call sites |
| Item decay | ❌ | Runs on every machine and self-destroys at 0. Host owns loose items' condition |
| Containers | ⚠️ | Contents travel on spawn and change. Live moves into world containers should be host-arbitrated |
| Crafting | ⚠️ | Results are local inventory (fine). It also **uses up ground items within 10 units**, which others still see. Route consumption of networked items through the host |
| Explosions | ✅ | `CreateExplosion` is fully local: body, objects, items and a random crater. Mines (`OnDestroy`), turret self-destruct, dynamite, gravbag and crystals run it on every machine, so damage is multiplied and craters differ. Plan: an explosion event. The authority applies world effects; every machine applies body effects to its own player |
| Guns | ⚠️ | Player bullets only hit objects and blocks (synced). Shots are invisible and silent to others; send a muzzle/tracer/sound event |
| Traders | ❌ | Stock rolled per machine, hostility per machine, death drops duplicated. Host-authoritative trader with requests for give, buy, haggle, threaten and hug |

### Creatures, traps, hazards

| System | Status | Notes and plan |
|---|---|---|
| Creature movement and AI | ✅ ⚠️ | Proximity owner simulates; others keep it kinematic. Gaps: some scripts still move non-owner copies (guards, crystal enemies, laser drones, techballs), and a few set Dynamic again (drone open, fan dislodge, stalactite drop) |
| Creature targeting | ⚠️ | Owner AI sees only the owner. Plan: puppet hitboxes on the owner, with bites forwarded to the victim |
| Creature attacks seen by others | ⚠️ | Non-owner copies still bite on contact (good), but guards, turrets and drones fire on their own timers on every machine (about 2× fire rate). Owner should send fire, melee and lunge events; each machine applies damage only to its own player |
| Laser drone | ✅ | Open/close halves and doubles `health`, which amount sync reports as damage |
| Creature deaths | ✅ | Breaking is host-only, but on other machines `GuardScript.Die` loot, `TraderScript.DropInventory` and `FanTrap.BreakFan` still roll. Host-only drops; everyone keeps their own XP and gore |
| Spawners | ⚠️ | Guard spawner is host-only, but fires only when the *host* is near; it should consider every player. Cave ticks are published, but the `started` flag isn't shared |
| One-shot traps | ❌ | Bear trap, spike stabber, landmine press, ground glass, stalactite, coil shock. Triggered on one machine and still armed on the others. Send trigger state |
| Phased hazards | ❌ | Piston phase, fan spin speed, geyser liquid, grabber plant shape. Rolled per machine; transfer the state |
| Proximity props | ❌ | Med station (one heal each), bio terminal and scrap eater doors (block progress for others), shuttle doors, drill pod "working". Host-confirmed one-shots |

### The player

| System | Status | Notes and plan |
|---|---|---|
| Body simulation (circulation, needs, mood, skills) | 🏠 | Runs on the owner's machine, which is correct |
| Appearance (wounds, blood, dirt, wet, held items) | ✅ | Shader values at 2 Hz. Still to send: dismembered limbs (`SetActive(false)`), prosthetic overlay, broken-bone and bandage sprites, eye-gone and disfigured, ragdoll vs standing, sleeping, cargo |
| Speech bubbles | ✅ | `Talker` text |
| Death | ✅ | Spectate, then respawn by the host's rule; the run ends only when everyone is dead (M2, `LifeSync`) |
| Revival | ❌ | The game has **no CPR**. Revival is by defibrillator (only while still alive: arrest, then about 10 s to brain death, then about 67 s of brain decay) or epinephrine (5%/s restart). A co-op revive could be a mod feature (CPR minigame) or a defib by a teammate |
| Unconsciousness and sleep | ⚠️ | The 25× fast-forward is refused in a session, so sleep runs in real time. Plan: fast-forward only while every living player is asleep or down |
| Treating teammates | ❌ | The main co-op opportunity. Wound view and minigames bind to the local body (six hard-wired spots). Plan: the owner executes, the treater sends primitives (see the roadmap) |
| Carrying | ❌ | No grab system in the game. The owner pulls its ragdoll toward the carrier's hand |
| Shared warmth | ❌ | No mechanism. Postfix `HandleBodyTemperature` near teammates |
| Saves | ⚠️ | Blocked in a session. The save format holds only the player, which suits per-player co-op saves |

### Interaction and UI

| System | Status | Notes and plan |
|---|---|---|
| Lifepod heater and shower | ✅ / 🐛 | Heater state synced. The shower affects the local body *anywhere*; gate it by distance |
| Doors, keypads, lockpicks | ✅ | Result is `health = 0`, which syncs. Minigames stay local |
| Drill pods, cargo pod | ⚠️ | Descent goes through the host. "Working" state and "everyone in the pod" aren't shared |
| Clicking a teammate | ❌ | Puppets have no colliders, so they can't be hovered, clicked or dropped on. Add a Descriptor-layer hitbox |
| Chat input | ❌ | Block game input by swapping `KeyBinds.cachedBinds` while typing |
| Spectator camera | ✅ | `customLookPos` on the watched teammate, healthy screen shaders, fog revealed at the teammate, death screen held (M2) |
| Pause | ⚠️ | Pausing would set 0× (refused in a session, so the menu opens while time runs on). Make that explicit in the UI |
| Notes (slow-mo) | ✅ | Slow-mo is refused in a session |
| Music threat state | 🏠 | Local; optional "threat" event from creature owners |
| Console | 🏠 | Block `saveandquit` and `skiplayer` for clients in a session; route `timescale` through the shared clock |

---

## 4. Bugs this audit found in v2

These are concrete defects in the build as it was. **All ten are now fixed** (roadmap M0.5); the list stays here because the reasoning behind each fix lives in it.

1. **Layer 12 breaks for clients.** `Body.PlaceBody` throws because `ENDLABSPAWN` comes from a skipped step. Fix: skip `PlaceBody` on scaffolds; the snapshot places the body.
2. **Explosions multiply.** Mines, turret self-destructs and dynamite run `CreateExplosion` on every machine, and each reports the object damage. Fix: an explosion event with world effects applied by the authority only.
3. **Death drops duplicate.** Guards, traders and fans roll loot on every machine during a remote break. Fix: host-only drops during `RemoteBreak`.
4. **Crystals throw** on clients (null effects). Fix: send effects in the snapshot.
5. **Laser drone open/close** is reported as damage. Fix: suppress damage recording inside `Open`/`Close`.
6. **Content modifiers** rewrite water after the snapshot, and Turbulent gravity differs. Fix: rewrite the host's water after the modifier; send the gravity value.
7. **Item state loss:** liquids, batteries (duplicated), ammo. Fix: a `[Saveable]` JSON codec.
8. **Non-owner creature copies still move** (guards, crystals, drones) or switch back to Dynamic. Fix: skip their movement on puppets and force Kinematic after the known writers.
9. **"Save & exit" on the bottom panel** drops a client out of the run. Fix: hide it in a session, or make it "leave session".
10. **Radiation line and ambient temperature** differ per machine. Fix: send the host's values.
