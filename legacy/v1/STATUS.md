# CasualtiesCoop — working status

Living summary of what works, what is broken, and what is known but unbuilt.
Written to survive a context reset. Update it as things change.

Detailed rationale lives in the plan; the game API map is in `docs/game-api-map.md`;
testing procedure in `docs/testing.md`.

---

## Start here

**The decompiled game source is not in the repo and must never be committed.** Keep a
decompile of `Assembly-CSharp.dll` in a scratch folder outside the repo.

1.5 MB, one file, no namespaces. **Grep it before reading anything** - nearly every
question this project has asked ("who calls this?", "is that field public?", "does a
client run this path?") was answered by one `grep -n`. If it has been cleaned up,
re-decompile `Assembly-CSharp.dll` into a scratch directory outside the repo.

Faster still for "can I touch this member?": reflect over the shipped DLL rather than
trusting the decompile's access modifiers.

```powershell
$asm = [Reflection.Assembly]::LoadFrom("C:\Program Files (x86)\Steam\steamapps\common\Casualties Unknown Demo\CasualtiesUnknown_Data\Managed\Assembly-CSharp.dll")
$asm.GetType("WorldGeneration").GetFields() | Where-Object Name -match "earthquake"
```

That is also the only way to be sure a Harmony patch will bind: parameter **names**
matter for injection, and the argument-type array is not compile-checked.

The loop that has worked all session: **grep the decompile -> reflect to confirm access
-> patch -> `dotnet build src/CCoop.Plugin/CCoop.Plugin.csproj` -> two instances with
`CCOOP_BLOCKTEST=1` -> diff the two `VERIFY` lines.**

---

## Build / test

```bash
dotnet test CasualtiesCoop.slnx                       # 195 tests, no game needed
dotnet build src/CCoop.Plugin/CCoop.Plugin.csproj      # deploys to BepInEx/plugins/CCoop
.\tools\launch-instances.ps1 -Count 2                  # two instances
```

**The plugin is not in the solution** (it needs the game install to compile), so
`dotnet build CasualtiesCoop.slnx` silently does not build it. Always build the csproj.

Per-process env vars for unattended testing (config files are SHARED between instances,
so these must be env vars):

| Variable | Effect |
|---|---|
| `CCOOP_AUTOSTART=host\|join` | Host or join on startup |
| `CCOOP_AUTORUN=1` | Enter a run without menu input (clients wait for the session first) |
| `CCOOP_POSEDIAG=1` | Log local + proxy limb poses for cross-machine diffing |
| `CCOOP_BLOCKTEST=1` | Forces the layer modifier to Freezing, then once the world settles the host writes 25 blocks, destroys one networked crate, wounds another and sets the speed to Fast; both peers log `VERIFY`. Well after that — long enough for every peer to have measured first — the host triggers an earthquake. Proves terrain, fluid, destruction, loot, damage, layer-modifier, time-scale and world-event sync end to end with no human input. Also logs a `TIMESCALE` line with the calling stack for every speed change |

**Touch `CCoop\verify.trigger` to make every running instance log a `VERIFY` line.**
Both processes share `persistentDataPath`, so one touch samples both peers within ~20 ms
of each other:

```powershell
$t = "$env:USERPROFILE\AppData\LocalLow\Orsoniks\CasualtiesUnknown\CCoop\verify.trigger"
New-Item -ItemType File $t -Force | Out-Null; (Get-Item $t).LastWriteTime = Get-Date
```

Simultaneity is the point, not convenience: two worlds are being compared *at a moment*,
and anything the players do between two separate keypresses is indistinguishable from real
divergence. The file is never deleted — each process remembers the timestamp it last acted
on, so neither can consume the trigger before the other sees it.

The line carries live terrain and fluid hashes, entity fingerprint, runtime-entity count,
total entity health, layer modifier and temperature, item count, and blocks/damage sent
and applied. It reads the *live* world, so it catches changes that landed on one peer only,
long after the transfer succeeded. F9 does the same thing and also writes the deciding
fields into the window title — but see the hotkey warning below before relying on it.

Which field to trust for what:

| Field | Use |
|---|---|
| `terrain <sha>` | Must be **equal**. The definitive answer to "same world?" |
| `entities <n:hash>` | Compare **n**, not the hash: positions are rounded but entities settle under physics, so the hash legitimately differs |
| `runtime <n>` | Must be **equal**. Runtime ids are only created by replication, so this is the noise-free measure of drops and dropped items |
| `health <n:sum>` | Total health across networked world objects. Compare how it **moves**: replicated damage moves it identically on both peers, damage that stayed local moves only one |
| `fluid <hash>` | Expected to **drift**. Only the starting grid is shared; each peer then simulates water near its own camera. Compare the `FLUIDHASH` lines instead — taken at send and at write, before either side has simulated a frame |
| `timescale <n>` | Must be **equal**. `Time.timeScale` drives physics, animation and AI, so peers at different values are diverging in every system at once |
| `modifier <name> temp <n>` | Must be **equal**. The layer's modifier and the temperature offset it set — the modifier having crossed AND taken effect |
| `items <n>` | Informational only. The two worlds hold genuinely different item counts — `CorpseScript.Start` rolls random loot, so a client re-running it produces different results |

Logs: `%LOCALAPPDATA%Low\Orsoniks\CasualtiesUnknown\CCoop\coop-*-pid*.log` (per process —
BepInEx's shared log is contended and loses one instance).

---

## Pinned build

| | |
|---|---|
| Steam App ID | `4576510` (Casualties: Unknown Demo) |
| Build ID | `23560597`, game version 7.0.1 |
| Unity | 2022.3.62f3, Mono, x64 |
| `Assembly-CSharp.dll` SHA-256 | `fd0ad2851f6bae4ab2589634643b92e16696c0e36f090bdd3f4c41e8f1c97095` |

---

## What works (verified in play)

- **Session**: host/join over LiteNetLib, compatibility handshake (protocol, mod version,
  App ID, assembly hash, plugin manifest) with player-readable rejections.
- **Remote players**: sprite-only proxies with full limb pose sync (local position +
  rotation per limb), buffered interpolation ~0.12 s. Verified by cross-machine diff.
- **Terrain streaming**: 2 MB grid → ~155 KiB deflated, 23–27 chunks; host and client
  terrain SHA-256 match exactly. With fluid and entities the whole layer is 186–196 KB
  in 27–28 chunks.
- **Entity spawning**: ~2,000 objects per layer from a string-table manifest; 2,002/2,003
  spawn (1 known failure, `ShuttleStarter`).
- **Shared entity ids**: an entity's id is its manifest index, so both peers agree with no
  negotiation. `NetEntityRegistry` maps id ↔ GameObject with revisions.
- **Item pickup**: client requests, host validates (entity, revision, range against the
  *requester's* position) and broadcasts; all peers apply. Confirmed by a real player.
- **SaveGuard**: vanilla saves blocked during co-op (one global save slot is shared by
  both instances — saving would corrupt single-player).
- **Terrain edits sync** (was issue #1). `WorldGeneration.SetBlock` is patched; edits are
  batched per frame and published peer-to-peer on the Transactions channel, and receivers
  write `worldBlocks` directly then rebuild each affected chunk once.
  **Verified 2026-07-31 across two real processes** with `CCOOP_BLOCKTEST=1`: identical
  live terrain hash before the edit (`6a521987…`), host wrote 25 cells, client logged
  "applied 25 remote edits across 1 chunk", identical hash after (`95fe9c5d…`). Mining,
  explosions, block placement and earthquakes all funnel through `SetBlock`, so all of
  them are covered by the same hook.

  Caveat, in the spirit of the testing lessons below: the edit was made by a test hook
  calling `SetBlock`, not by a player swinging a pickaxe. The transport, batching, gating
  and apply path are proven; that a real mining swing reaches `SetBlock` is read from the
  assembly, not observed. **Confirm by mining in play and pressing F9 on both peers.**

  Peer-to-peer rather than host-arbitrated, deliberately: an edit names the *resulting*
  block rather than requesting a change, so it is idempotent and two players digging the
  same cell converge instead of racing. There is nothing to duplicate and so nothing to
  arbitrate — and mining does not wait a round trip per swing.

- **Entity destruction and its loot** (was issues #1/#2 and §5c, "boxes are entirely
  unsynced"). A crate broken on one peer is now broken on the other, and its contents are
  the *same* contents.

  Ownership is first-to-die, not host-arbitrated. Entity health is local, so the peer
  whose copy dies is the one that actually broke it — and host arbitration would mean a
  client chopping a crate got nothing, because the host's untouched copy would never die.
  That peer rolls the drops itself through `Utils.Create` (so `SpawnCapture` sees them),
  blanks the entity's drop fields so the game cannot roll them a second time, then sends
  `EntityDespawn` followed by an `EntitySpawn` per drop. Receivers blank their copy's
  drops and set `health = 0`, so the game destroys it with the right particles and sound
  but no loot of its own.

  **Verified 2026-08-01 across two real processes.** Host destroyed entity 0
  (`dropcapsule`) and published 2 drops; client logged "entity 0 destroyed by a peer" and
  "spawned 2 items from peers". Registry counts moved in lockstep (1965→1966 and
  1964→1965), `runtime` was 2 on both, and `Item.allItems` moved +2 on both — a client
  that had also rolled its own loot would have shown +4.

- **Entity damage stacks across peers** (was issue #1). Health is not a method call
  anywhere — damage arrives as a bare `health -=` from a dozen unrelated call sites, with
  no event to hook — so it is *observed* instead: `BuildingEntityUpdatePatch` already
  visits every world object every frame, and a drop since the last visit is damage
  somebody just dealt here. That amount is broadcast and applied on top of the receiver's
  own, so two players chopping the same crate now add up.

  Sent as an amount, never a resulting health. Absolute values would race — each peer
  would report what it saw and the last message to arrive would erase the other player's
  work. Applying a peer's damage also moves our own baseline, or the next look would read
  it as locally dealt and send it straight back forever.

  The **killing** blow is deliberately not sent: the despawn that follows carries strictly
  more information, and both ride the same ordered channel so a peer cannot see its copy
  die before learning who dealt the blow — which is what would let it claim the kill.

  **Verified 2026-08-01 across two real processes.** Host damaged one crate by 25. Total
  health across every networked object read `1807:897300` on both peers before, and
  `1806:894775` on both after — identical count and identical sum, the second reflecting
  one destroyed crate plus exactly 25 damage. `damage sent/applied` read 25/0 on the host
  and 0/25 on the client.

- **Layer modifiers reach clients** (was issue #2). The game rolls one per layer at the
  very end of `GenerateWorld` — the coroutine clients suppress — so a client stood in the
  host's terrain, at the host's coordinates, in a different climate. The chosen index now
  travels in `LayerPrepare` and the client applies it after the world lands. It cannot be
  re-rolled: the roll consumes the unseeded global RNG and would pick something else.

  Two of the eleven are applied differently. `Infested` and `Ionized` scatter world
  objects, and they do it *before* the host captures the world — so the shadecrawlers and
  rad barrels already arrive in the entity manifest. Those two are marked active but not
  re-initialised, or the client would scatter a second, different set on top.

  **Verified 2026-08-01 across two real processes.** With the modifier forced to Freezing,
  the host logged `temperatureOffset now -10` and the client `[layer] modifier Freezing
  applied`; both `VERIFY` lines read `modifier Freezing temp -10`. A later run where the
  game rolled `Hot` of its own accord came out `modifier Hot temp 8` on both peers, which
  is the stronger result — nothing about it was staged.

- **Fluid ships with the world** (was issue #2). `PlaceLiquids` runs during generation, so
  a client's `FluidManager.main.fluid` was empty while the host had lakes: one player
  waded where the other saw dry rock. The grid now travels as a third section of the
  layer payload, after terrain and before the entity manifest.

  Nearly free, despite being a megabyte raw: it is one byte per cell and mostly zeroes, so
  deflate takes the whole layer from ~184 KB to **186–196 KB** depending on how much water
  the layer holds. `InstantiateWorld` already allocates the client's grid at the right
  size — clients suppress `GenerateWorld`, not `InstantiateWorld` — so it is a straight
  `BlockCopy` into an array that is already waiting.

  **Verified 2026-08-01 across two real processes.** `FLUIDHASH host` and `FLUIDHASH
  client` both read `9d062a07…` for the same 1,048,576 B. Those are taken at send and at
  write, deliberately: fluid simulates per-peer afterwards, so a hash taken any later
  cannot tell a broken transfer from water that simply flowed (see §2).

- **Time scale is shared** (was issue #5). `Time.timeScale` is global to a process and
  drives physics, animation, AI and fluid together, so a peer that speeds up does not
  merely see things sooner — its whole simulation runs away from everyone else's. Of
  everything that was unsynchronised this was the cheapest to trigger and the widest in
  effect: one keypress put the two worlds 20x out of step.

  `PlayerCamera.SetTimeScale` is the single funnel, so one patch sees every change and
  sorts them three ways:

  | Kind | Treatment | Why |
  |---|---|---|
  | Normal / Fast / SuperFast | Host arbitrates; clients request and cancel locally | The session's speed, and only one peer can own it |
  | `force: true` (pause, unpause, death screen, start-up) | Local, never sent | One player opening a menu must not freeze another's game |
  | UnconsciousFast / DyingFast / Slowmo | Refused in a session | Driven by one body or one screen — see "An unconscious player now waits in real time" |

  Requests for the speed already running are dropped without a packet: the game re-asserts
  its current speed from several places, harmlessly in single player but once per call on
  the wire here.

  **Verified 2026-08-01 across two real processes.** Host set `Fast`; `timeScale` went to
  5 on the host, the client logged `session speed is now Fast (5x)` 24 ms later, and both
  `VERIFY` lines read `timescale 5`.

  **Confirmed in play by the user 2026-08-01.** The drop back to Normal seen during an
  earlier test was the player moving — the game's own "movement cancels speed-up" rule,
  working as intended. Note that rule now applies to the *shared* clock: either player
  moving cancels the fast-forward for everyone. That is consistent, but if it turns out to
  be annoying in practice the fix is to ignore the movement reset when it comes from a
  peer who is not the one who asked for the speed.

  `CCOOP_BLOCKTEST=1` logs a `TIMESCALE` line with the calling stack for every speed
  change, which is how to attribute one of these if it ever looks wrong again.

- **Layer backdrop and wall holes reach clients.** Reported missing in play by the user:
  no rock behind the tunnels, and none of the openings light shines through. Both come
  from `WorldCreateBackground`, inside the suppressed `GenerateWorld`, and both parent
  their objects to *chunk transforms* — which the entity capture never looked at, because
  it records scene roots only.

  Handled differently, for a reason worth remembering: the backdrop is one tiled sprite
  per chunk chosen purely by `biomeDepth`, so the client recreates it exactly and nothing
  goes on the wire. The wall holes are ~100 objects at random positions, so they are
  captured into the manifest like any other entity — the capture now sweeps chunk children
  as well as roots.

- **Block cracks and hit sounds are shared.** Also reported in play: a wall would simply
  vanish on the other screen, with no cracks deepening and nothing audible, because only
  the peer swinging accumulates `blockDamages`.

  Sent as the cell's *total* accumulated damage rather than each blow, so it is idempotent
  — a dropped or duplicated hit still leaves the crack at the right depth. Read back out
  of the game after the hit rather than computed, which avoids reproducing its damage
  scaling. Applied by writing the receiver's `BlockDamage` entry directly and **never**
  through `DamageBlock`: running that would break the block a second time and roll a
  second set of loot.

- **Earthquakes are shared** (was issue #4, re-confirmed in play by the user).
  `WorldGeneration.Update` rolled both the interval (`Random.Range(600, 1750)`) and the
  duration (`Random.Range(3, 25)`) per peer from the unseeded global RNG, so each player
  had their own quakes at their own times.

  Terrain sync had made the symptom *worse* rather than better: the blocks a quake tears
  out do now converge, but it tears them out around **the shaking player**, so the other
  one watched holes open around themselves during a quake they were not in.

  Only the schedule travels. Shake, stumbling and block damage are all produced correctly
  by each peer for its own player once they agree on *when* — and both players being in
  the same quake, each carving around themselves, is the right co-op behaviour. The host
  announces the duration; clients hold `earthquakeDelay` high so they never roll their own.

  Watch for one thing when reading the code: the game drops `Time.timeScale` to 1 at the
  start of a quake by **assigning the field directly**, going around `SetTimeScale` and so
  around the shared clock. The host pushes that back through the proper path, or the peers
  would silently disagree about the speed from then on.

  **Verified 2026-08-01 across two real processes.** Host logged `earthquake for 4s;
  announced`, client `earthquake from the host, 4s` 12 ms later. The same run had both
  peers agreeing on terrain (`3982a9ae…`), fluid, health (`1760:953825`), runtime count,
  time scale and layer modifier (`Dry`, rolled by the game itself).

- **Mining, block cracks and block loot — confirmed in play 2026-08-01.** A real player
  mined with a pickaxe for a full session: 365 terrain edits sent by the host, 365 applied
  by the client, one for one, with cracks deepening and the block vanishing on the other
  screen. This closes the caveat the terrain entry carried for a day — the old proof came
  from a test hook calling `SetBlock`, not from a swing. Block-break loot went with it
  (`published 1 spawned item` → `[block] spawned 1 item from peers`).

- **Item drop, both directions — confirmed in play 2026-08-01.** Host→client twice and
  client→host once in one session, each registered as a shared entity. This is the path
  that was silently dead because `DropItemPatch` was never registered with Harmony.

- **Entity destruction initiated by a CLIENT — confirmed in play 2026-08-01.** The
  untested direction. A client lockpicked and broke `medcrate` 883 and published 1 drop;
  the host logged `entity 883 destroyed by a peer` and `spawned 1 item from peers`, and
  the player confirmed both peers received the same item.

- **Lockpicking and keypads work by construction.** All three `Openable.OnUse` paths —
  instant, keypad, lockpick minigame — end in `entity.health = 0`, which is the same
  death entity destruction already hooks. Confirmed in play by the user opening a crate
  with the lockpick minigame. What is *not* shared is the minigame itself: the other
  player sees the container pop open with no idea it was being picked.

- **Fall damage while the world loads — confirmed in play 2026-08-01.** The player joined
  and arrived undamaged.

- **Remote heads follow aim — settled 2026-08-01, and not by watching.** Was §5. Two
  instances on one PC cannot answer it by eye: both processes read the same cursor, so a
  proxy tracking its peer and a proxy tracking the local mouse look identical. The
  construction settles it instead — `ProxyFactory` strips `Body` and `Limb`, and `Body`
  is what owns `targetLookPos` and writes the head angle. A proxy has no code that reads
  a mouse, so any head motion on it arrived over the wire. With the earlier `POSEDIAG`
  measurement (host head 355.4° → client proxy 347.2°), this is done.

## Built and since confirmed in play

Everything here was found or fixed during the 2026-08-01 play session and then
watched working by a real player. Kept separate from the older verified list only
because several were fixed twice, and the failed first attempts are the useful part.

- **Attack swings — fixed after a play test found the real gate, 2026-08-01.** The arc
  now travels, but the first version published only the swings that *connected*: it
  guarded on `Body.Attack`'s return value, and that method returns true only when its
  raycast damaged a block or a `BuildingEntity`. A clean miss runs the whole visible half
  — instantiates `attackAnim`, plays the swing sound — and then returns false. So a player
  watching their partner saw arcs when they hit something and nothing at all when they
  swung at air, which is exactly what was reported. Now gated on the game's own entry
  condition, `conscious && attackCooldown <= 0f`, captured in a prefix because the method
  assigns `attackCooldown` partway through. **Confirmed fixed in play.**

  Known cosmetic nit: `AttackSync.SwingSound` re-rolls from `atk.swingSounds` rather than
  carrying the clip the attacker actually played, so peers can hear different swings from
  the same set.

- **Structure tile decoration — CONFIRMED IN PLAY 2026-08-01.** Was issue #3, reported
  twice. Glass, vents and panelling inside lifepods and corridors were missing on clients:
  `GenerateEntityAtPos` instantiates a structure's children at the scene root **except**
  those carrying a `Tilemap`, which it re-parents to `worldGrid` — neither a scene root nor
  a chunk, so neither capture sweep saw them.

  The two designs sketched earlier both worried about double-spawning the `Item` and
  `BuildingEntity` children the manifest already carries. That worry dissolves on a close
  read: those are exactly the children that *stay* at the scene root, and the Tilemap ones
  are exactly the children that do not. **The two sets are disjoint by construction**, so
  replaying only the Tilemap children cannot duplicate anything — no suppression window,
  no compound keys, no new message. Placements ride the entity manifest under reserved
  `@struct:` keys, because a manifest record is already `(key, x, y, rotation)`, which is
  precisely a placement. The method has no randomness, so the replay is exact.

  Follow-on defect found by the same play test: the decoration is *there* but its
  **interactive props are not synced** — see "Structure props are unsynced" below.

- **Lifepod prop state (heat toggle, shower)** — built 2026-08-01, unplayed. Reported the
  moment structure decoration started arriving: two players in the same pod had different
  temperatures, and one could run a shower the other could not see.

  There is a single funnel for "the player used this" —
  `usableObject.gameObject.SendMessage("OnUse")` — and hooking it wholesale, the way
  `SetBlock` is hooked for terrain, would be a bad bug: most of the ten `OnUse` classes
  open a screen for the player who pressed it, and **`Openable.OnUse` starts the lockpick
  or keypad minigame**. Replaying that would open a minigame on a partner's screen because
  you opened a crate. So this is a whitelist, and `LifepodButton` is the only class
  examined and found safe — it does nothing but call `ToggleHeatState` or `ActivateShower`.

  The **resulting state** travels, not the press, so the message is idempotent: the host
  applies its own broadcasts, and converging on a state twice is a no-op where replaying a
  press twice would toggle it back off. Receivers reach it by calling `ToggleHeatState`
  until their state matches, which brings the heater temperature, sprite colours and
  description text along without this code knowing any of them. The shower is momentary
  rather than a state and is skipped for the presser, who has already run it.

  Uses the existing interaction messages — a new `InteractionKind.UseProp` with the state
  in the `Slot` byte — so no new message type.

  **CONFIRMED IN PLAY 2026-08-01**, after a first attempt failed: it addressed the button
  by entity id, and there is no entity id to address. The buttons carry a `Tilemap`, so `GenerateEntityAtPos` parents
  them to `worldGrid`, which neither capture sweep looks at — both peers logged "button is
  not a networked entity". They are now addressed by **position**, which is sound because
  the structure replay is deterministic. Every press also logs the prop's parent chain, so
  the next prop that cannot be addressed says why. **Needs both players in one lifepod.**

- **Proxies no longer wear the local player's wounds — CONFIRMED IN PLAY 2026-08-01.** See
  the `Limb.Awake` material note under hard-won facts. The player who got hurt had been
  watching their *partner's* body take the damage, while the partner saw nothing wrong on
  either character — and that asymmetry is what identified it, because a sync fault would
  have looked the same on both screens.

- **Player appearance travels — CONFIRMED IN PLAY 2026-08-01.** The immediate consequence of
  the fix above: with proxies no longer sharing materials they showed the wrong wounds,
  they showed none at all, and a remote player stayed exactly as they looked when their
  proxy was cloned. Reported in play as "they can't see each other's dirt or damage".

  Nothing about a body's appearance was ever replicated; the material bug had merely been
  drawing *something*, which hid the gap.

  **Shader values travel, not the health behind them.** `Limb.Update` writes eight floats
  into the limb's material every frame, and a receiver that recomputed them from
  `skinHealth` and friends would be carrying a copy of the game's formulas to drift out of
  date. Reading the values back out of the sender's own material and writing them straight
  onto the proxy's means neither side does any arithmetic. Three of the eight belong to the
  whole body — wetness, snow, dirt — so they are sent once rather than thirteen times.

  Quantised to a byte each and sent at 2 Hz: ~70 bytes, against a pose at 20 Hz. Sent
  unconditionally rather than on change, which also repairs a proxy created mid-session.

- **Client drops were duplicated on the host — found and fixed 2026-08-01.** For a
  client's drop the host created the world item while arbitrating the request, then
  created a *second* one when its own broadcast came back to it (it is not the actor, so
  nothing skipped it) and re-registered the id over the first. The orphan stayed in the
  world, visible and pickable but unaddressable.

  Present since drops were written and invisible the whole time, because two identical
  items at the same position look like one. Throwing exposed it: one copy flew and the
  other sat still. And because the *registered* copy was the stationary one, motion sync
  streamed that back over the thrower's own item — so each throw worked for a moment and
  was then cancelled, and after enough orphans accumulated throwing appeared to stop
  working altogether. Reported in play in exactly that order.

  `ApplyGrantedDrop` now refuses an id it already holds, and `NetEntityRegistry.Register`
  warns when an id is re-registered over a live object — the silent overwrite is what let
  this hide.

- **Client-side throwing — first fix FAILED in play, corrected 2026-08-01.** Host throws
  worked; a client's item still just dropped. The first attempt applied the velocity
  locally when the host's grant arrived, which was not enough — and the reason is a hole
  in motion sync's ownership rule that is worth remembering.

  Motion sync gives an item to the peer whose id block it came from. But `HandleDropRequest`
  allocates a **host-range** id for a client's drop, because the host is the peer creating
  the object. So the host owns a client's thrown item, its copy was created at rest, and it
  streamed that rest straight back over the thrower's own copy. The throw was not merely
  invisible to others — it was actively cancelled a frame after it began.

  **The acting peer is not always the owner.** Anywhere authority and ownership can come
  apart like this, the initiating peer has to hand over what it knows.

  So the launch velocity now travels in the drop *request* and back in the *result*: the
  host applies it to the copy it creates, and every peer starts the item identically. It is
  computed in a `ThrowItem` prefix because the game assigns `rb.velocity` only after
  `DropItem` has returned, by which point the announce has already gone out saying the item
  is stationary. `ThrowVelocity` is pure, so computing it early gives the same answer.
  **Needs a client to throw something.**

- **Fresh-loot float — first design was WRONG, corrected 2026-08-01.** Loot bursting from
  a broken container floats for a few seconds with an outline and particles so it can be
  grabbed. The first attempt decided this **per peer**, reproducing the game's own rule
  (`FreshItemDrop` is added only when the drop lands within 8 units of the local body) and
  reasoning that a float is presentation, like an earthquake's shake, and so belongs to
  each screen.

  That reasoning was wrong, and play showed exactly how: `FreshItemDrop.Awake` sets
  `gravityScale = 0`, which is **simulation state, not presentation**. A peer that declined
  the float ran different physics from the peer that took it — so with motion sync holding
  the item up and local gravity pulling it down, the far player watched it jitter in
  mid-air with no outline and no particles. The decision now travels with the spawn and
  every peer obeys the spawner.

  The general lesson, worth keeping: **"reproduce it locally" is only safe for things that
  touch nothing but the screen.** Check whether the component writes to physics first.

  Residual: `FreshItemDrop` deletes itself when the game's item-floating setting is off, so
  two peers configured differently would disagree about gravity again. Both instances share
  `settings.json` here; a genuinely split install would need the flag to override.

- **Role in the OS window title** — `WindowTitle` sets `HOST` / `CLIENT` via
  `user32!SetWindowText`, since the game's canvas appears to draw over the IMGUI overlay.
  `WindowTitle.SetNote` appends a marker to the same title; F9 uses it to put the fields
  being compared somewhere a human can actually see them.

---

---

## Built but NOT yet confirmed in play

- **Held and worn items on remote players** — built 2026-08-01, unplayed. Proxies destroy
  every `Item` they inherit from the clone, and must: a live one sits in `Item.allItems`
  and can be picked up out of a remote player's hands. The cost was that everyone else
  looked empty-handed no matter what they carried.

  **Only the slot and the item id travel.** Everything about how an item sits in a hand is
  derived from data both peers already hold — the rotation and the back-slot quarter-turn
  come from `ItemInfo`, the sort order and the limb from the slot — so transmitting any of
  it would be transmitting a value the receiver can look up. The placement in
  `RemoteProxy.BuildSlot` is `Body.PickUpItem` reproduced line for line.

  The visual copy is instantiated **under an inactive holder**, which is what keeps it out
  of `Item.allItems`: the registration happens in `Item.Start`, and `Start` does not run on
  an inactive object. Its `Item`, `Rigidbody2D` and `Collider2D` are then destroyed before
  it is parented and activated, so it is a sprite and nothing else.

  Slots are found on the proxy by name, the same trick limbs use, because the
  `InventorySlot` components do not survive the strip but their GameObjects do.

  Empty slots are omitted and the receiver clears anything unmentioned, so a player
  carrying one thing costs a couple of dozen bytes. Sent at 2 Hz and diffed on arrival:
  only a slot whose id actually changed is rebuilt, or an unchanged item would be
  re-instantiated twice a second and visibly flicker.

  **Needs a partner to watch someone pick something up, swap it, and drop it.**

- **Item motion** — see "Loose items drifted apart" below. Partly exercised: throwing and
  dropping were confirmed working in play, which cannot happen unless motion is streaming
  and the at-rest message is landing. What has NOT been watched directly is two players
  standing apart and agreeing where an item finally settled, which is the thing the
  at-rest message exists for. **Drop something on a slope, walk away, and compare where it
  ended up on both screens.**

- **Replicated hit feedback** — a peer's blows moved `health` and nothing else, so the
  other player watched a crate lose condition in silence and then burst. Reported in play
  as "the crate didn't flash red". Receivers now reproduce all three things `Body.Attack`
  gives the attacker: a red flash of the object's own sprite, a floating damage number and
  the entity's `hitSound`. Block damage numbers went the same way — cracks and hit sounds
  were already shared, the number was not; the blow is the difference between the incoming
  total and our own, which also means a duplicate delivery shows no second number.

  Never watched directly: the play tests since have covered destruction and loot rather
  than the feedback during the hits. **Chop a crate slowly while a partner watches — they
  should see it flash red, hear it, and see damage numbers.**

---

## Known broken / missing

Ordered roughly by how much damage each does.

### 1. Two peers can claim the same kill
If two peers' copies of an entity die within a round trip of each other, both claim
ownership and both publish loot. Any symmetric rule fails here: in a genuine tie, each
peer sees "the other one's damage finished it", so both stand down (no loot) or both
step forward (double loot). The only sound fix is a fixed authority.

That authority is now viable, because health *is* replicated: the host's copy of a crate
dies too, even when a client dealt every blow. **Host-claims-everything** would remove
the race entirely, at the cost of one round trip before a client sees its own loot — and
of making destruction depend on damage replication, so a fault there loses the loot
instead of duplicating it. Worth doing once damage sync has been confirmed in play.

Not urgent: two players chopping the same crate do not normally tie, because each copy
dies from that player's own final blow at a different moment.

### 2. Fluid diverges once players separate
The *initial* grid is now streamed with the world, so both peers start with the same
lakes. After that each simulates its own: `FluidManager.SimulationRange()` is ±64 blocks
around **that peer's own camera**, so water near player A flows only on A's machine.

Consequence: water is right where the two players are together, and drifts apart where
they are not. Nothing corrupts — the two grids simply stop agreeing at the edges.

To finish it, the simulated window is the natural unit: a peer owns the ±64 box around
its own camera and publishes changes within it, which is a small, bounded delta and
matches how the game already thinks about fluid. Overlapping boxes when players are
close together need a tiebreak.

### 3. Loose items drifted apart (built 2026-08-01, unplayed)
An `EntitySpawn` described the instant an item appeared and nothing ever spoke about it
again, so from that moment each peer ran its own physics on it. Two rigid bodies given
identical starting conditions do not stay together — they meet the ground at slightly
different moments and settle somewhere else.

Reported in play three separate ways before it was recognised as one fault: a thrown item
that merely dropped straight down on the other screen, loot scattering differently, and an
item that rolled downhill for one player while the other still saw it at their feet.

`EntityMotion` now carries it. **Ownership costs nothing** — runtime ids come from a
per-peer block, so an item's id already names the peer entitled to speak for it; no
election, no handover, and two peers can never both claim one.

The `AtRest` flag splits it into two messages sharing a shape, and the split is the whole
design. While an item is awake its position is a stream: newest wins, a lost packet costs
nothing, so it goes on `Snapshots` at 12 Hz. The moment it stops is the opposite — that
single message is what makes both worlds agree where the item *finished*, and losing it
leaves them disagreeing indefinitely, so it goes reliably on `EntityLifecycle`. It is also
what makes this cheap: an item at rest is silent, and almost every item is at rest.

Receivers set velocity first and correct position only beyond 5 cm, so local physics
carries the item plausibly between updates instead of teleporting it 12 times a second,
and resting items do not shiver.

Not yet addressed: items already in the world manifest (below `RuntimeIdBase`) are nobody's
to move, so a crate kicked across the floor still drifts. Only spawned loose items sync.

### 4. An unconscious player now waits in real time
`UnconsciousFast` (25x) and `DyingFast` (3.5x) are refused in a session, because they are
driven by *one body's* consciousness and there is only one `Time.timeScale` per process:
sharing them would race everybody's world because one player passed out, and not sharing
them desynchronises that peer by 25x.

So an unconscious player now waits at 1x while their partner keeps playing — which is
ordinary co-op behaviour, but it is a deliberate behaviour change and worth confirming a
real player finds it acceptable rather than tedious. If not, the alternative is a vote:
speed up only while *every* peer is unconscious or idle.

`Slowmo` (the note-reading effect) is refused for the same reason and is purely cosmetic.

### 5. A second joining client re-numbers every entity
`CaptureEntities` derives ids from the manifest index and runs again whenever a new player
joins. If anything was destroyed in between, the indices shift, so the second client gets
different ids for the same objects than the first client holds. Harmless with one client
(the re-capture happens before anything is destroyed) and fatal with three players. Part of
the late-join work: ids must be assigned once per layer and re-sent verbatim.

### 6. Dialogue invisible to other players (task #15)
Proxies strip `Talker`. Note `Talker.text` is created **unparented at runtime**, so a
proxy must instantiate its own rather than inherit the reference.

### 7. Creature AI runs independently
Creatures spawn identically from the manifest, then diverge immediately — each peer runs
its own AI. Nothing is host-authoritative yet.

### 8. Not implemented at all
Container transfers, crafting, body/damage/medical (M5), death, layer transitions between
depths, persistence (M6), late join, host migration.

---

## Hard-won facts (do not re-derive)

- **`Body.Attack` returns true only when the swing HIT something.** It sets a local
  `flag` when its raycast damages a block or a `BuildingEntity` and returns false
  otherwise — so a clean miss still instantiates the arc and plays the sound, then answers
  false. Never treat the return value as "a swing happened"; the condition that means that
  is `conscious && attackCooldown <= 0f`, read on entry.

- **`ThrowItem` assigns the launch velocity AFTER calling `DropItem`.** Anything hooked on
  the drop therefore sees a velocity of zero. `ThrowVelocity` itself is pure and can be
  recomputed safely; the angular spin next to it is `Random.Range(-800, 800)` and cannot.

- **`FreshItemDrop` — the float on fresh loot — is applied by proximity to the LOCAL
  player** (`< 8f` in `BuildingEntity`), not as a property of the drop. Anything replicating
  loot has to decide it per peer, or one player's screen is wrong either way.

- **BepInEx's `KeyboardShortcut.IsDown()` requires that no other key is held.** Its
  `ModifierKeyTest` walks every supported key code except the mouse buttons and fails if
  any is down. Sensible in a config menu; in a game it means a hotkey silently does nothing
  whenever the player is holding a movement key. Read `Input.GetKeyDown(MainKey)` directly
  and check the configured modifiers yourself.

  **Still unexplained:** even after that fix, F9 produced no `VERIFY` line, while
  `CCOOP_KEYPROBE=1` proved the plugin does see other keys (it logged `LeftAlt`). So
  something outside the game appears to swallow F9 specifically. Not worth more time — the
  `verify.trigger` file replaced it and is better anyway. If F6/F7 ever matter for a real
  player who is not launched with env vars, start from the key probe.

- **A host applies its own broadcasts.** `BroadcastInteractionResult` ends with
  `RaiseInteractionResolved`, so the host runs the same apply path as everyone else. Any
  apply that also happens during arbitration therefore runs **twice** on the host. This
  duplicated every client drop for days without being noticed, because both copies sat in
  the same spot; it only became visible when throwing gave them different velocities.
  Apply paths must be idempotent, and the cheap way is to check the registry first.

- **`Limb.Awake` gives each limb its own `Material` and installs it as
  `sharedMaterial`** (`mat = new Material(renderer.material); renderer.sharedMaterial = mat;`,
  plus the limb's `bonusRendererToAffect`). Wounds are drawn into that material. A clone
  copies the *reference*, and stripping `Limb` means the clone never makes its own — so a
  proxy renders the LOCAL player's injuries. Anything cloned from the player must be given
  its own material instances, and they must be destroyed with it because Unity does not
  collect script-created materials.

- **A structure's interactive props have no network id on either peer.** Buttons and
  switches built into a structure carry a `Tilemap`, so `GenerateEntityAtPos` re-parents
  them to `worldGrid` — which is neither a scene root nor a chunk child, and so is invisible
  to both capture sweeps. They cannot be addressed by entity id at all. Address them by
  **position**: structure placement is deterministic and clients replay it exactly, so both
  peers agree on where they are.

- **There is no world seed.** `UnityEngine.Random.InitState` is never called; every noise
  generator is seeded from the unseeded global RNG. Clients *cannot* regenerate the host's
  world — it must be streamed. Proven by three runs producing three different terrain
  hashes on identical settings.
- **A "player" is three siblings under `/Experiment`**: `Body` (physics ragdoll), `Anim`
  (the rig limbs follow via `Limb.animLimb`), `TalkText`. Cloning only `Body` leaves 18
  references pointing at the original → the clone drives the original's animation and
  overwrites its dialogue. **Always clone the player root.**
- **Limbs are flat siblings**, not a skeleton, joined by `HingeJoint2D`. Pose sync needs
  local **position** as well as rotation.
- **Facing is `Body.isRight`**, a bool — not `transform.localScale`.
- **Proxies must be parented to the scene root**, never under `/Experiment`, or they
  inherit the local player's transform and appear to mirror them.
- **Instantiate clones under an inactive holder**; strip with `DestroyImmediate`.
  `Destroy` is deferred and components keep ticking for the rest of the frame.
- **`SetBlock` is the single funnel for terrain state.** Mining, explosions, block
  placement and earthquakes all reach it; generation uses `SetBlockNoUpdate` and does
  not. So one hook captures every gameplay edit and no generation traffic.
- **`SetBlock` rebuilds the whole 64x64 chunk** (`UpdateChunkClosest` → `UpdateChunk` →
  4096 `SetTile`). Applying a batch cell-by-cell through it would redraw a million tiles;
  write `worldBlocks` directly and call `UpdateChunk` once per affected chunk.
- **`CheckSeating` is a `ChunkUpdated` listener.** Props with `requireGround` zero their
  own health when the block under them goes, so entity destruction rides along with any
  terrain change — including a remote one. That is useful and dangerous: it also means
  both peers independently roll that entity's loot unless one of them is told not to.
- **All drop rolls are `UnityEngine.Random`** — chance, condition, rotation, velocity and
  loot-category pick. Never recompute a drop remotely; transmit the outcome.
- **`BuildingEntity` drops use `Object.Instantiate`, not `Utils.Create`**, so a
  `Utils.Create` hook does not see them. `DamageBlock` block loot *does* use
  `Utils.Create` and is captured. `BuildingDrops.Roll` therefore re-rolls the same
  distribution through `Utils.Create` and blanks the entity's drop fields — the one place
  this codebase deliberately duplicates game logic, and it is safe only because the roll
  is driven entirely by public data on the component rather than by the hardcoded
  delegates in `SetupItems`. **If those fields change shape, it must change with them.**
- **`BuildingEntity` damage has no hook.** It arrives as a bare `health -=` from a dozen
  call sites — melee, bullets, explosions, a cactus, a burrowing creature — and Harmony
  cannot patch a field write. Observing health once per frame from the `Update` patch is
  the only route, and it costs one dictionary lookup per entity per frame.
- **Clients suppress `GenerateWorld`, not `InstantiateWorld`.** The latter still runs and
  allocates `worldBlocks`, the chunk tilemaps *and* `FluidManager.main.fluid` — so a
  client always has correctly sized, empty grids waiting for the host's data.
- **Fluid simulates per-peer, near each peer's own camera.** `FluidManager.SimulationRange`
  is ±64 blocks around `PlayerCamera.main.transform.position`, so water only moves where
  a player is. A one-time transfer therefore stays exact wherever nobody is standing, and
  drifts only where someone is.
- **`Item.allItems` is not a usable cross-peer metric.** The two worlds hold genuinely
  different item counts, because prefabs like `CorpseScript` roll random loot in `Start`
  and a client re-runs that when it instantiates them from the manifest. Count runtime
  entity ids instead — those only ever come from replication.
- **`Body.DropItem(int)` delegates to `Body.DropItem(Item)`**, and most callers use the
  `Item` overload directly. Patch the `Item` one.
- **Unity runs `Start()` on instantiated objects at the top of the next frame**, not
  before the coroutine resumes. Any check on what a batch of `Instantiate` calls did to
  the world needs a `yield return null` first.
- **Saves are player-only** — `SaveGame()` stores inventory, `[Saveable]` components,
  recipes and layer number; no terrain, no entities. One global slot, parameterless.
- **`Utils.GetBody`** resolves any collider → `Limb` → `Body` and is body-agnostic, so
  damage sources need no patching for multiplayer.
- **BepInEx plugin components are destroyed on scene load.** Session state must live on
  our own `DontDestroyOnLoad` object.
- **Two instances share** `persistentDataPath`, the save slot, PlayerPrefs and BepInEx
  config — copying the game folder does NOT separate them (Unity derives the path from
  company/product). Diagnostics must be per-process.
- **`single-instance` is enforced by UnityPlayer** from `boot.config`; defeated via
  Doorstop's `boot_config_override` → `BepInEx\boot.multi.config`. No game file modified.
- **Runtime entity ids are peer-scoped**: `RuntimeIdBase + peerId * PerPeerStride + n`,
  well above the manifest indices. Any peer can create a world object without asking
  anyone, which is what lets a client's block loot exist the instant it is rolled.
- **The protocol version must be bumped on any wire change** (`ProtocolConstants.Version`,
  now 2). Bump `CoopPlugin.Version` with it: the handshake compares mod versions and gives
  a readable rejection, whereas a protocol mismatch just drops packets at the header.
- **LiteNetLib must stay on 1.3.5** — 2.x ships only `net8.0`/`netstandard2.1`, which a
  `net472` plugin cannot reference.
- **Steam networking IS possible** for a mod (earlier claim retracted): ship
  `steam_api64.dll` + Steamworks.NET and init against App ID 4576510.
- The game has a built-in dev console (`ConsoleScript`, 95 methods, `RegisterAllCommands`)
  — useful for spawning test items and dumping session state.

## Testing lessons

- **Automated PASS has hidden a real defect three times** (M1 gates, first world transfer,
  FullBody clone). Structural checks are necessary but never sufficient — confirm in play.
- **Verify proxy state against the owner's body on the *other* machine.** A proxy that
  mirrors the local player is perfectly self-consistent and completely wrong.
- **Stage your hashes** (received → after write → after rebuild). A single end-to-end hash
  only says "mismatch"; staged hashes say where.
- Pose-motion sampling is confounded by player input and only detects a fully frozen
  character. Do not trust it for anything subtler.
- **Make the feature drive itself.** `CCOOP_BLOCKTEST` makes the host mutate terrain and
  destroy a crate, then both peers hash the live world, so the whole path is provable
  with no human and no guesswork about whether the code even ran. Prefer this shape for
  anything new: a change the peers must converge on, then a measurement from the live
  world on both sides.
- **Pick a metric the two worlds can actually agree on.** The first destruction test
  compared `Item.allItems` and read +2 on the host against +1 on the client — alarming,
  and pure noise: the two worlds already differed by 40 items because corpses roll their
  loot in `Start`. Counting *runtime entity ids* instead gave 2 and 2, because those only
  ever come from replication. A metric with a moving baseline cannot answer a question
  about a delta.
- **"Check whether it already works" is worth an hour before it is worth a feature.**
  The remote-head entry had carried a note to verify before building. Doing so found that
  aim already crosses the wire — it enters `limbs[0].bonusRot`, which the game applies to
  the limb transform that pose sync already sends. Building the obvious fix would have
  added a per-frame wire field for nothing, and made a second entry (attack swings) look
  blocked on it when it was not.
- **The two peers do not reach the same test stage at the same moment.** A client arms
  its self-test only once the world has arrived and applied — several seconds after the
  host. An earthquake scheduled "just after the fingerprint" therefore landed *between*
  the host's `VERIFY` and the client's, and the resulting terrain-hash mismatch looked
  exactly like a desync. Anything in a self-test that mutates the world must be far enough
  after the last measurement that every peer has taken theirs.
- **Say what a wrong answer would look like.** A duplicated-loot bug shows up as a
  *surplus*, so the client reading lower than the host ruled it out immediately even
  before the cause was known. Knowing the shape of the failure makes an ambiguous
  measurement much cheaper to interpret.
- **`dotnet build CasualtiesCoop.slnx` does not build the plugin** and reports success
  anyway. A run that "should" show a fix and does not is worth checking against the
  deployed DLL's timestamp before debugging anything else.
