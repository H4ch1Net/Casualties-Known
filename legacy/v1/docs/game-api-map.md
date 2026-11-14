# Game API map

Derived from `Assembly-CSharp.dll` for **Steam App 4576510 (Casualties: Unknown Demo), Build 23560597**.

| | |
|---|---|
| Assembly SHA-256 | `fd0ad2851f6bae4ab2589634643b92e16696c0e36f090bdd3f4c41e8f1c97095` |
| Game version | 7.0.1 |
| Unity | 2022.3.62f3, Mono, x64 |
| Assembly | 489 TypeDefs / 3318 MethodDefs, **no namespaces** (all global) |
| Save location | `%LOCALAPPDATA%Low\Orsoniks\CasualtiesUnknown` |

Everything below was verified against the live assembly (the probe's Harmony patches resolve successfully at runtime). Re-verify if the hash changes.

> Never commit decompiled game source. Decompile to a scratch directory outside the repo.

## Singletons and global accessors

| Accessor | Notes |
|---|---|
| `PlayerCamera.main` | The god class: 130 methods / 187 fields. Input, HUD **and all interaction logic**. |
| `SaveSystem.body` | `private static Body body => PlayerCamera.main.body;` — **private**, so not a general accessor; but `SaveGame()` reads it, so it decides whose inventory is saved. |
| `PlayerCamera.main.body` | The actual global "local player" accessor used across the codebase. |
| `WorldGeneration.world` | World singleton. |
| `WorldGeneration.runSettings` | `static` run configuration. |
| `WorldGeneration.CHUNKSIZE` | `static int` = 64. |
| `PauseHandler.main` | `main.isPaused` is a plain public bool. |
| `Minigame.game` | `static` — exactly one active minigame globally. |
| `MinigameBase.body` | `=> PlayerCamera.main.body` — minigames are hardwired to the local player. |
| `ConsoleScript.instance` | Built-in dev console (95 methods) — use as a test harness. |
| Others | `SettingsMenu.instance`, `PreRunScript.instance`, `LiquidTransfer.instance`, `RPCManager.instance` (Discord RPC, *not* networking). |

## Player: Body and Limb

`Body : MonoBehaviour` — 151 methods, 211 fields. **No static instance**; its only statics are config (`interactionRange`, `consciousnessRiseRate`, `consciousnessFallRate`, `censorPain`, `censorMood`). Multiple instances are therefore structurally plausible.

- Inventory: `slots[]`, `HoldingItem(int)`, `GetItem(int)`, `PickUpItem(Item, int slot, bool force)`, `GetAllWearables()`
- Anatomy: `limbs[]` (`Limb : MonoBehaviour`), `LimbNum` enum
- Placement: `PlaceBody()`
- `Utils.GetBody(GameObject g, out Body body)` resolves via `Limb.body` — the generic collider→limb→player mapping.

**The Body is a pre-existing scene object.** `WorldGeneration.WorldPlacePlayer()` only calls `PlayerCamera.main.body.PlaceBody()`; there is no player prefab. A second player must clone the live hierarchy.

## Interaction — all on PlayerCamera

| Member | Access |
|---|---|
| `TryPickupFromWorld()` | private |
| `TryPerformInventoryAction()` | private |
| `TryPerformWorldActions()` | private |
| `TryCraft()` | public |
| `OpenContainer()` | public |
| `SetTimeScale()` | public |

`TryPickupFromWorld` flow:

```
Input.mousePosition
  -> Camera.main.ScreenToWorldPoint
  -> Physics2D.OverlapPoint(LayerMask.GetMask(...))
  -> Component.CompareTag
  -> Vector2.Distance(target, PlayerCamera.body.transform.position) vs UsableObject.rangeMultiplier
  -> GameObject.SendMessage(...)      // string-based dispatch, no IInteractable
  -> PlayerCamera.PlayUISound()
```

Two consequences: interaction is **camera/mouse-relative**, and dispatch is **untyped `SendMessage`**. The range check against `PlayerCamera.body` is the specific place where "the acting player" is hardcoded.

## Items and containers

- `Item : MonoBehaviour` — `id` (string), `condition`, `favourited`, `Stats.wearSlotId`, `TryGetParentContainer()`
- `Utils.Create(string id, Vector2 pos, float rot)` — the item factory (`Resources.Load` by string id)
- `Container : MonoBehaviour` — `CanHoldItem(item)`, `LoadItem(item)`, **`UnloadItem(item, body)`**, `GetHoldingWeight()`, `AboveHoldingWeight()`, `GetEncumberance()`, `UnloadAllItems()`, `ContainerBroke()`

`UnloadItem` already takes a `body`, so container transfers are not hardcoded to the local player.

Item *behaviour* is hardcoded delegates (`Use`, `UseLimb`, `OnDrink`, `OnHealthUse`, `OnBandageUse`, `OnSyringeUse`, `LimbCondition`) registered in one giant `SetupItems` with 145+ lambdas. Replicate **outcomes**, never attempt to re-run these remotely.

## World generation

`WorldGeneration : MonoBehaviour` — 134 methods / 88 fields. Pipeline (all coroutines, in order):

```
GenerateWorld
  -> WorldPreprocess
  -> WorldGenerateTerrain
  -> WorldGenerateStructures
  -> WorldGenerateWorldBorders
  -> WorldPlaceEntities
  -> WorldPlacePlayer
  -> WorldCreateBackground
  -> FinishWorldGeneration
```

Also `InstantiateWorld`, `RegenerateWorld`, `ApplyLayerModifiers` / `ResetLayerModifiers`.

State and terrain:

- `private ushort[,] worldBlocks` — the terrain grid
- `GetBlock(Vector2Int)`, `SetBlock(pos, ushort)`, `GetBlockInfo(ushort)` → `BlockInfo { health, name, hitsound, stepsound, noVariation, metallic, toxicity, slippery, sleep }`
- `BlockToWorldPos` / `WorldToBlockPos`
- `chunkWidth` / `chunkHeight` = **16×16 chunks** (4×4 for tutorial) → 1024×1024 blocks
- `generatingWorld`, `worldExists` (public bools — the reliable completion signal)
- `biomeDepth`, `amountOfLayers`, `realTimeElapsed`, `timeSinceFinishedGeneration`, `blockDamages`
- `static GetRunSettingFloat/Int/Bool(string name)`

### There is no world seed

- **`UnityEngine.Random.InitState` is never called anywhere in the assembly.**
- Every noise generator is seeded from the unseeded global RNG: `new FastNoiseLite(UnityEngine.Random.Range(0, int.MaxValue))` (~10 call sites).
- `RunSettings.settingTypes` has no seed entry.
- `GlobalDark.GetDeterministicRandom(seed)` (`new System.Random(seed)`) and `KeypadMinigame.GenerateCode` cover specific derived values only, not layout.

The same run settings therefore produce a different world every run. **Clients cannot regenerate the host's world; it must be streamed.**

## Run settings

`RunSettings` — `static GetSetting(name)`, `static GetPreset(presetName)`, fields `settingTypes`, `presets`. Types: `RunSettingFloat` / `RunSettingBool` / `RunSettingDropdown`, plus `RunSettingsPreset`.

Keys: `unchipped`, `baselootdensity`, `lootmultiplier`, `basetrapdensity`, `trapincrease`, `ambientlight`, `timelimit`, `startingsupplies`, `xpgain`, `metabolismrate`, `healingrate`, `fracturepain`, `bleedrate`, `infectionspeed`.

Flow: `PreRunScript.runSettings` → `WorldGeneration.runSettings`. Entry point `PreRunScript.StartRun()` → `WaitLoad()`.

**Reusable win:** `PreRunScript.CopyToClipboard()` / `PasteFromClipboard()` with `static CompressString` / `DecompressString` already round-trip the run configuration as a portable compressed string — use it as the session config payload.

## Saves — player-only

`SaveSystem` (static): `SaveGame()`, `HasSave()`, `TryLoadGame()` (**returns `void`**), `Zip`/`Unzip`, `DicToTupleList`/`TupleListToDic`, `loadedRun`, `savedRunTime`.

`SaveGame()` captures **only the player**:

- each held item (`id`, `condition`, `slot`, `favourited`) plus items nested in carried containers
- worn items (`wearSlot`)
- `[Saveable]`-attributed components on the body and on each limb (reflection-gathered)
- recipe knowledge (`Recipes.recipes[].hasMadeBefore`, `.INT`)
- `runTime`, `biome` (= layer number)

It stores **no terrain and no world entities** — `WorldSaveData` is just `chunkWidth`/`chunkHeight`, and loading regenerates a fresh layer. `Saveable` is an `Attribute`, not an interface. All entry points are parameterless statics → **one global save slot**.

## Terrain mutation — one funnel

`SetBlock(Vector2Int, ushort)` is the **only** way terrain state changes at runtime.
Everything else ends there:

| Path | Reaches `SetBlock` via |
|---|---|
| Mining / melee / bullets | `DamageBlock` → threshold reached → `SetBlock(pos, 0)` |
| Explosions | `GenerateBlockCircle(..., autoUpdateChunk: true)` → per-cell `SetBlock` |
| Placing a block | item `useAction` → `SetBlock(pos, 3)` directly |
| Earthquakes | `SetBlock(random, 0)` |
| Generation | `SetBlockNoUpdate` — **does not** pass through `SetBlock` |

That split is convenient: hooking `SetBlock` captures every gameplay edit and no
generation traffic. Note `SetBlock` clamps `pos` into range and then calls
`UpdateChunkClosest`, which redraws the chunk's full 64×64 tilemap — so applying many
cells is far cheaper by writing `worldBlocks` directly and calling `UpdateChunk` once
per affected chunk.

`DamageBlock(pos, dmg, hitSound, bonusMetal, ignoreLoot)` accumulates into
`blockDamages` (capped at 128, oldest force-broken) and on reaching `BlockInfo.health`
spawns loot **randomly** — 40% glass shards from block 7, 50%/75% scrap from 3/6/10,
one of three woods from 11/24 — via `Utils.Create`, with random condition. None of it is
reproducible, so outcomes must be transmitted rather than recomputed. `ignoreLoot: true`
suppresses it (creatures burrowing use this).

## Entity destruction and drops

**`BuildingEntity` is the world-object class** (crates, plants, corpses, props — 1,914 on
a real layer). Its destruction is *not* a method: `Update()` checks `health < 0.5f` and,
when true, plays effects, rolls drops and calls `Destroy(gameObject)`.

Drops come from three sources, all rolled with `UnityEngine.Random`:

- `itemsDropOnDestroy[]` — each an `ItemDrop { id, chance, conditionMin, conditionMax }`,
  gated on `Random.Range(0,1) < chance * dropChanceMultiplier`
- `guaranteedDropAmount` picks from `ItemLootPool.pool[itemCategoriesToAdd[random]]`
- `alwaysDrop[]`

Each drop is created with **`Object.Instantiate(Resources.Load(id))`, not `Utils.Create`**,
gets a random rotation and a random velocity in ±7, and a random condition. A
`FreshItemDrop` component is added when within 8 units of the local player — purely a
highlight outline, safe to add or omit per peer.

`Container.ContainerBroke()` only unloads pre-existing child items; it is the *carried*
container path (backpacks), not world crates. Crates hold nothing until destroyed.

`ItemLootPool` is used by `BuildingEntity`, `CorpseScript` and `TraderScript` — not only
the trader.

**Damage has no hook.** `health` is a public float written directly (`component.health -=
…`) from melee, bullets, explosions, `CactusScript`, burrowing creatures and
`CheckSeating`. There is no `Damage()` method and no event, so the only way to observe it
is to read `health` each frame — `Update` is already the funnel for that.

**`CheckSeating()`** is registered as a `ChunkUpdated` listener for props with
`requireGround`, and sets `health = 0` when the block beneath is gone. So any terrain
change propagates entity destruction for free — including across peers, once terrain is
synchronised.

**`CorpseScript.Start()` spawns 0–3 random items.** Anything a client instantiates from
the manifest re-runs its `Start()`, so prefabs that spawn on start produce items the host
never had. Watch for this class of prefab.

## Minigames

`Minigame` (abstract) + `MinigameBase : MonoBehaviour`. Members: `static game`, `HandType()`, `GuideLocaleString()`, `NeedsItem()`, `HandRotOffset()`, `Start()`, `PhysicsUpdate(deltaTime)`, `CanExit()`, `Update(uiCasts)`.

11 subclasses — medical: `BandageMinigame`, `SyringeMinigame`, `ShrapnelMinigame`, `DislocationMinigame`, `AmputationMinigame`, `ManualDefibMinigame`, `AEDMinigame`, `HandCrankMinigame`, `SelfHarmMinigame`; other: `LockpingMinigame`, `KeypadMinigame`.

Blocking, local, and globally singular.

## Creatures and hazards — no shared abstraction

`ISimEntity` is only a chunk-update optimization (`ChunkUpdate`, `CheckCurrentChunk`) implemented by `CrystalBehaviour`. `Damageable : MonoBehaviour` is a thin bridge (`Damage(damage)`, fields `build`, `item`).

Creatures are bespoke: `SpiderHandler` / `SpiderHandlerTBE` (virtual `DamageLimb`), `CaveTicks`, `CaveTickSpawner`, `CrystalEnemy`, `ElderThornbackBehaviour`, `ScrapEaterScript`, `GrabberPlant`, `XalorisScript`, `DroneScript`, `TurretScript`, `Observer`, `Vomiter`.

Hazards: `BearTrap`, `BarbedFence`, `MineScript`, `GunmineScript`, `SawbladeScript`, `SpikeStabberScript`, `StalactiteDropper`, `DamagingCrate`, `GeyserScript`, `RadiationLine`, `RadioactiveObject`, `CactusScript`, `BananaPlantSlip`, `BounceShroom`, `JumpPadScript`.

Replication must be per-type.

## Pause and time scale

`PauseHandler.TogglePause()` flips `main.isPaused`, consults `disallowPause` (`=> WorldGeneration.world.generatingWorld`), toggles `SettingsMenu.instance`, plays a UI sound. **It never touches `Time.timeScale`.**

Time scale is separate: `PlayerCamera.SetTimeScale`, `AutoTimeScaleSet`, `curTimeScale`, `ChangeTimeScaleWhenFinishedReading`.

`SetTimeScale(SpeedType, bool switchSound, bool force)` is the **single funnel** for
`Time.timeScale`, and `curTimeScale` records what is currently set:

| SpeedType | `Time.timeScale` | Set by |
|---|---|---|
| Normal | 1 | speed1 key, movement input, most UI sequences |
| Fast | 5 | speed2 key |
| SuperFast | 20 | speed3 key |
| UnconsciousFast | 25 | the local body passing out — **every frame** while it lasts |
| DyingFast | 3.5 | the local body's brain dying |
| Slowmo | 0.16 | reading a note |
| Paused | 0 | `PauseHandler`, always with `force: true` |

`force: true` is a usable discriminator: only the pause handler, the death screen and
start-up pass it — player input never does. Note the game re-asserts its *current* speed
from several places, which is a no-op locally but worth filtering before putting it on a
wire.

## Other subsystems

- Fluids: `FluidManager`, `Liquids`, `LiquidStack`, `LiquidType`, `LiquidTransfer.instance`,
  `PourLiquidAt`, `CombineLiquids`, `WaterContainerItem`.
  `FluidManager.main.fluid` is a **public `byte[,]`** the same dimensions as the block
  grid, allocated in `InstantiateWorld` (so clients get one even with generation
  suppressed) and filled by `PlaceLiquids` during generation (so clients get it empty).
  `SimulationRange()` limits simulation to ±64 blocks around
  `PlayerCamera.main.transform.position` — fluid only moves near a player, and near
  *that peer's own* player.
- Terrain damage: `ChunkScript`, `BlockDamage { damage, pos }`, `DamageBlock`, `GetBlockDamage`, `ClearBlockDamages`
- `LayerModifier` + 11 subclasses: Foggy, Warm, Hot, Cold, Freezing, Flooded, Dry, Rainy,
  Infested, Ionized, Unchipped. `availableModifiers` is a **static** array whose `active`
  flags outlive the world, and the game's reset lives in `WorldPreprocess` — inside
  `GenerateWorld`. Anything suppressing generation must reset them itself or the previous
  layer's modifier stays in force.
  `ApplyLayerModifiers` rolls one at the end of `FinishWorldGeneration`, *before*
  `generatingWorld = false` — so `Infested` and `Ionized`, the two that scatter objects,
  are already in the world when it is captured.
- Crafting: `Recipes`, `Recipe`, `RecipeResult`, `RecipeItem`, `RecipeCategory`
- Misc: `Skills`, `MoodleManager`, `TraderScript`, `TutorialHandler` + `TutorialCourse` subclasses
