# How CasualtiesCoop works

The mod is written against the full release of Casualties: Unknown (Steam 4576490, Unity 2022.3, Mono). Behaviour notes below were checked against that build's `Assembly-CSharp.dll`. Decompile it into a scratch directory outside the repo, never into it.

## The constraints that shape everything

- **There is no world seed.** Every noise generator is seeded from the unseeded global RNG, so a client cannot regenerate the host's layer. Content must be streamed.
- **`Body` is the local player, through and through.** It moves the camera, drives the HUD, reads input and calls `PlayerCamera.main` in dozens of places. A second live `Body` breaks the real player. Remote players are therefore puppets: the character prefab with every game script, collider and rigidbody stripped, posed over the wire.
- **The game only simulates near its own camera.** Chunk renderers are enabled about three chunks around the camera, and creatures and many props return early from `Update` when their chunk is disabled. Water simulates within ±64 blocks of the camera.
- **Creatures find and hurt players through physics.** They overlap-test the `Body`/`Limb` layers and bite on collision with a `Limb`.
- **The player spawns from a prefab:** `Resources.Load("Special/Characters/Experiment|Milky|Dune")`. So a puppet can be built for any character without cloning the local player.

## Networking (`CCoop.Net`)

- Star topology. Clients talk only to the host, which relays. Every packet carries its origin, which the host overwrites with the connection's real id.
- Two delivery classes. **Reliable** is ordered on one stream per connection and carries everything that changes shared state, including bulk world data. **Unreliable** is newest-wins and carries movement and creature positions.
- Messages are classes registered with a permanent id. The registry hashes itself, and the handshake compares hashes, mod version and game build, so a mismatched peer is refused with a readable reason instead of misreading packets.
- Large payloads (layer snapshots) go as 48 KB reliable chunks and are reassembled by the session.
- Transports: SteamNetworkingSockets P2P (relay, NAT traversal, addressed by Steam id), LiteNetLib UDP for LAN/IP, and a loopback transport for tests. The mod never calls `SteamAPI.Init`: the game's `SteamManager` initialises Steam and pumps callbacks every frame.

## Steam (`Net/`)

- `SteamTransport` gives each connection's real Steam id (`RemoteAddress`) and its ping and quality (`GetConnectionRealTimeStatus`).
- `NetSession` hands the address to the host's admission hook, which checks bans, and keeps it as `PeerInfo.Address`.
- `SteamLobby` handles the advert, the browser (public list plus friends' lobbies), joining (with version checks and the password prompt) and rich presence (`connect`, `steam_player_group`, `steam_player_group_size`).
- `SteamServices` covers names, avatars, played-with, ping estimates from relay ping locations, Deck detection, the on-screen keyboard, the Steam build id and timeline calls.
- The `SteamMoments` module turns session events into timeline markers.
- Every Steam call is a no-op when Steam isn't running, so LAN play works without it.

## The world channel

All changes to the shared layer (terrain, objects, items, creatures, props) go through `WorldNet`:

1. A client sends the change to the host.
2. The host applies it and relays it only to peers that hold the current layer's snapshot.
3. A client still loading its snapshot buffers what arrives and replays it once the layer is live.

Because the snapshot and later changes travel on the same ordered reliable stream, a peer never receives a change that is already baked into its snapshot (which would apply twice), and never one from a layer it has left.

## Layers (`RunFlow`, `LayerSync`)

- **Host:** `InstantiateWorld` begins a new *epoch* and announces it. When generation settles, every world object gets a network id. For each client that is ready, a snapshot is captured from the live world, so late joiners get the layer as it is now.
- **Client:** builds a *scaffold* for the host's depth. Generation runs every presentation step (backdrop, post-processing, music, layer title, starting supplies) but skips every content step: terrain, borders, chips, entities, mini barrels, structures, and the modifier roll. Anything content-like the scaffold still made is removed. Then the snapshot is applied: terrain and water are written, structures are replayed (their tile-carrying children live under the world grid, where no sweep sees them, and `GenerateEntityAtPos` is deterministic), and objects are spawned under the host's ids.
- After spawning, objects run `Start` and roll their own randomness: corpse loot, and blocks edited under props. The client removes unregistered objects and re-asserts the host's terrain.
- The host's layer modifier is applied by index with `ApplySpecificModifier`. Objects it scatters are removed, because the host's copies arrived in the snapshot.
- **Descending:** `RegenerateWorld` is the single funnel for every way down (exit, drill pods, endings). On a client it becomes a request to the host. The host's descent announces a new epoch, and clients follow.

## Objects (`EntitySync`)

- `BuildingEntity.health` is a property in the full game. Its setter is the one hook for damage from any source. Damage travels as amounts, so two players' hits add up.
- Breaking happens in `CheckDamage` and is host-authoritative. A client whose copy dies holds it at death's door and sends `EntityKill`. The host breaks its own copy, captures the loot the game instantiated, and publishes the break and each drop. Clients replay the break with drop fields blanked, so the effects play and the loot doesn't double.
- Ids never collide. The host numbers generated objects. Structure parts are numbered by (placement, child), because replay is exact. Runtime objects are numbered from the creating peer's own range.

## Items (`ItemSync`)

- Pickup of a networked item: the host picks up and announces it, while a client asks and picks up when granted.
- Drops are named by the dropper and published at the end of the frame, after `ThrowItem` has set the velocity.
- The creator owns a loose item's physics. It streams the item's motion while it's awake and sends a reliable "at rest" when it stops.
- Container contents travel with the item (`ItemCodec`), and are re-published when something is loaded into or taken out of a container.

## Creatures (`CreatureSync`)

Each creature is assigned by the host to the player nearest to it, with a 10-unit hysteresis margin. The owner simulates it normally against its own real body and streams its position. Everyone else holds it kinematic and interpolates. Its scripts still run, so a puppet that touches you can still bite. Spawners that run on every peer are restricted: cave ticks hatch on the peer that triggered them and are published, and guard spawners run on the host only.

## Players (`PlayerSync`)

- **State at 20 Hz:** position, velocity, body angle, and each limb's position and angle relative to the body, plus flip flags.
- **At 2 Hz:** the limb shader values that draw wounds, blood, dirt, wetness and snow, plus held and worn item ids.
- **As they happen:** attacks (the arc and the swing sound) and speech lines (the `Talker` text).

## Explosions (`ExplosionSync`)

`CreateExplosion` is fully local. Run on every machine, it would hurt objects once per player and carve different craters.

- The host's explosion is the real one. Its world effects sync as usual, and items it damaged re-announce their state.
- Clients replay it **body-only**: wounds, sound and shake on their own player, while object health, item condition and the crater are suppressed.
- A client's own explosion becomes a request to the host, which drops repeats of a blast it just made. A mine destroyed on every machine asks once from each.

## Structures and per-object state

- `GenerateEntityAtPos` is replaced, line for line, by a version that records every part it creates. Parts are networked as (placement, part) and rebuilt on clients from the same child of the same structure prefab. That's how the End lab's interior, and ladders and crates inside structures, arrive exactly.
- `EntityState` carries what generation rolled per object. Today that's crystal effects with all their rolled fields; piston phase, fan speed and geyser liquid can be added the same way.
- `ItemCodec` serialises every `[Saveable]` component the way the game's save system does.

## Clock (`ClockSync`)

- Normal, Fast and SuperFast are shared and arbitrated by the host.
- Forced changes (pause, death screen) stay local.
- Unconscious and dying fast-forward is the team's: the host applies it once every living player is unconscious, and drops it when anyone wakes. The unconscious and the dead never steer the clock: their screens request normal speed every frame.

## Team play (M3)

`ExitVote`, `Chat` and `InputBlock`, `PingSync`, `RulesSync`, `VoiceSync`, `CreatureHunt` and `TeamSense` are described in the roadmap's M3 "As built". The key design choices:

- **Descents are votes.** Every descent passes through `RegenerateWorld`, so turning that call into a vote covers every way down.
- **Puppets stay collider-free.** Creature contact with a puppet is computed by distance on the creature owner's machine and applied by the victim's machine.
- **Game input is blocked at a single point.** A prefix on `KeyBinds.GetBind` silences every game control while the player is typing.

## Life (`LifeSync`)

- **Dying.** Death is `brainHealth <= 0`, and the game opens its death screen once the screen has faded to black. In a session the death screen waits until every player is dead (the host's `TeamWipe`). Until then the dead spectate: the screen and sound are forced healthy, and the camera follows a teammate.
- **Reviving.** Revival is `Body.Heal()` on the dead player's own machine. The game tears down death state by itself once the brain lives.
- **Who runs creatures.** Only living players do.
- Earthquakes: the host announces the duration, and clients pin their own timer so they never roll one.
- Once a second the host also sends the ambient temperature and the radiation line's state. Both are computed from per-machine time and would otherwise drift.

## Hard-won facts

- Objects created at plugin load without `HideFlags.DontSave` are destroyed when the first scene loads. The runtime object uses `HideAndDontSave`, but Unity does not call `OnGUI` on it, so the overlay lives on a separate object created once a scene is running.
- `WorldGeneration.SetBlock` and `DamageBlock` gained overloads in the full game. Patch them with explicit argument types, or Harmony throws on the ambiguity.
- `RegenerateWorld` runs `Clear()` (which destroys every `BuildingEntity` and root item) before `InstantiateWorld`, all in-scene. There is no scene reload between layers.
- `PlaceBody` scans for ground. On an empty scaffold it finds none, which is why clients are frozen until the snapshot places them.
- Structure parts are named with Unity's duplicate suffix (`ladder (1)`). Resolve their prefab without it.
- `ShuttleStarter` is instantiated from a script-held reference and cannot be resolved by name. It is the one object a client lacks.
