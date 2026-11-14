# Testing

## Headless tests (no game needed)

```bash
dotnet test CasualtiesCoop.slnx
```

`CCoop.Protocol` and `CCoop.Transport*` build and test with no game install, so CI can
run them. The transport suite opens real loopback UDP sockets on OS-assigned ports.

## Two instances on one machine — solved

Running two clients on one PC works. Two things make it work, both non-obvious.

### 1. Launch the .exe directly, not through Steam

```powershell
.\tools\launch-instances.ps1 -Count 2
```

Steam is **not** required. An earlier investigation concluded that BepInEx only
injected under Steam, because a direct launch left the process using
`C:\Windows\System32\WINHTTP.dll` instead of the local proxy. That was an artefact of
launching from a **sandboxed shell**, not a property of the game or of Windows. From a
normal shell, a direct launch injects correctly and both `WINHTTP.dll` copies appear in
the process module list — ours proxying to the system one.

### 2. Defeat single-instance via Doorstop, not by editing game files

`UnityPlayer.dll` enforces single instance from the `single-instance=` key in
`CasualtiesUnknown_Data\boot.config`. With it in force, launching a second copy leaves
exactly one process alive.

The fix uses Doorstop 4.5.0's `boot_config_override`:

- `BepInEx\boot.multi.config` — a copy of `boot.config` with the `single-instance=` line
  removed (the `build-guid` line is preserved).
- `doorstop_config.ini` — `boot_config_override = BepInEx\boot.multi.config`

**No game file is modified**, so Steam has nothing to verify or restore. Clearing that
one line restores stock behaviour.

Verified: two simultaneous processes, both with the proxy injected, both passing the
assembly-hash check, each writing its own probe log.

## What the two instances share

Unity derives `persistentDataPath` from company and product name, not install path, so
copying the game folder would **not** separate these. Both instances share:

| Shared | Consequence |
|---|---|
| `%LOCALAPPDATA%Low\Orsoniks\CasualtiesUnknown` | Same save location and `settings.json` |
| The game's **single global save slot** | `SaveSystem.SaveGame()` takes no arguments — two instances can race |
| `HKCU\Software\Orsoniks\CasualtiesUnknown` | PlayerPrefs collisions (cosmetic) |
| `BepInEx\LogOutput.log` | Last writer wins; a second instance may not write it at all |
| `BepInEx\config\*.cfg` | Same plugin settings for both |

Two consequences that matter:

1. **Diagnostics must be per-process.** `CCoop.Probe` writes
   `<persistentDataPath>\CCoop\probe-<timestamp>-pid<n>.log`, and every future
   networked component must do the same. Relying on BepInEx's shared log will silently
   lose one instance's output — this already caused a wrong conclusion once, when a
   session's output appeared to be missing entirely.

2. **Co-op must never write the vanilla save.** With one global slot and a shared path,
   two instances writing it will corrupt each other. Host-only saves in a separate
   namespace were already the plan; until that guard exists, avoid triggering a save
   while two instances are running.

If true isolation is ever needed (e.g. testing save/load), run the second instance as a
different Windows user or in a VM — that is the only way to separate
`persistentDataPath`.

## Verifying world convergence

The question that matters after the initial transfer is "do these two players still share
a world?", and the only honest answer comes from hashing the **live** block grid on both
peers — not the payload that was transferred, which says nothing about edits that landed
on one side only.

Two ways to get it:

- **F9** on each peer logs a `VERIFY` line. Diff the two.
- **`CCOOP_BLOCKTEST=1`** runs it unattended: it forces the layer modifier to Freezing
  before the world is sent, then eight seconds after the world settles the host writes 25
  blocks at (100..104, 100..104), **destroys one networked crate and wounds another**, and
  four seconds later both peers log `VERIFY`. The modifier is forced because the game only
  rolls one by chance, so a test that waited for one would usually prove nothing.

Not every field in that line means the same thing:

| Field | Use |
|---|---|
| `terrain <sha>` | Must be **equal** — the definitive "same world?" answer |
| `entities <n:hash>` | Compare **n** only. Entities settle under physics and the hash rounds positions, so it legitimately differs |
| `runtime <n>` | Must be **equal**. Runtime ids come only from replication, so this is the clean measure of drops and dropped items |
| `health <n:sum>` | Total health over networked world objects. Compare how it **moves** — replicated damage moves both peers by the same amount, local damage moves one |
| `modifier <name> temp <n>` | Must be **equal**. The layer's modifier and the temperature offset it produced — proof it both crossed the wire and took effect |
| `fluid <hash>` | **Will differ, and that is expected.** Only the starting grid is shared; each peer then simulates water within ±64 blocks of its own camera. To check the transfer itself, compare the `FLUIDHASH` lines instead — those are taken at send and at write, before either side has simulated a frame |
| `items <n>` | Informational. The two worlds hold different item counts by design — `CorpseScript.Start` rolls random loot, and a client re-runs it per corpse |

```powershell
$game = "C:\Program Files (x86)\Steam\steamapps\common\Casualties Unknown Demo"
$env:CCOOP_AUTORUN = "1"; $env:CCOOP_BLOCKTEST = "1"
$env:CCOOP_AUTOSTART = "host"; Start-Process "$game\CasualtiesUnknown.exe" -WorkingDirectory $game
Start-Sleep 12
$env:CCOOP_AUTOSTART = "join"; Start-Process "$game\CasualtiesUnknown.exe" -WorkingDirectory $game
```

The env vars have to be set per process — `launch-instances.ps1` does not set them,
because both instances share `BepInEx\config` and a config setting could not tell the
host from the client.

Expect, roughly 90 seconds later:

```
[host]   BLOCKTEST armed; entities 2116:53f6... health 1807:897300 items 298
[host]   BLOCKTEST host wrote 25 cells at (100..104, 100..104)
[host]   BLOCKTEST destroying entity 0 (dropcapsule)
[host]   BLOCKTEST damaged entity 1 (dropcapsule) by 25, now 2475
[host]   published 2 spawned items
[host]   destroyed entity 0 (dropcapsule), 2 drops
[host]   [block] sent 25 terrain edits (25 this layer)
[host]   [damage] sent 1 damaged object (25 total this layer)
[client] [block] entity 0 destroyed by a peer
[client] [block] spawned 2 items from peers
[client] [damage] applied to 1 object (25 total this layer)
[client] [block] applied 25 remote edits across 1 chunk (25 this layer)
[host]   VERIFY host   terrain 913f7e82... runtime 2 health 1806:894775 items 300
[client] VERIFY client terrain 913f7e82... runtime 2 health 1806:894775 items 355
```

Four things to read out of that, none of them obvious:

- **"across 1 chunk"** is a result, not noise. 25 cells inside one chunk must produce
  exactly one `UpdateChunk`, or the apply path is redrawing a million tiles per batch.
- **The entity counts move together** (+1 each: one crate destroyed, two drops created).
  Their absolute values differ by one because of the known `ShuttleStarter` spawn failure.
- **`runtime` must match exactly.** If the client had also rolled its own loot for that
  crate, its extra items would be unregistered — invisible in `runtime`, but showing up as
  `items` climbing by 4 instead of 2. That is the check that catches duplicated loot.
- **`health` must move by the same amount on both.** Here it drops 2,525 on each side:
  2,500 for the destroyed crate and exactly 25 for the wounded one. If damage were still
  local, only the host would have lost that 25.

**This is a lower bar than it looks.** It proves the transport, batching, gating, apply and
destruction paths. It does not prove that a real player action reaches them — for that,
mine a wall and break a crate in play, then press F9 on both peers.

## In-game experiment plugins

`CCoop.Probe` is read-only and safe to leave installed. `CCoop.Gates` **mutates game
state** and is disabled by default:

```
BepInEx\config\ccoop.gates.cfg
  Enabled = false          # true only when running an experiment
  Mode    = VisualOnly     # or FullBody (known to disturb the local player)
```

### Lessons that shaped these tools

- **Assert functional integrity, not just state independence.** The first gate run
  reported 34/34 pass while the game was visibly broken in play, because it only checked
  that two bodies didn't share state — never that the original still animated, moved or
  talked. Any experiment touching the player must sample the local player's behaviour
  before and after, and capture `Application.logMessageReceived`.
- **Instantiate clones under an inactive holder.** Cloning a live `Body` runs its
  `Awake`/`Start` before anything can be stripped, leaving residue that outlives the
  stripped components.
- **Strip with `DestroyImmediate`, not `Destroy`.** `Destroy` defers to end-of-frame, so
  components keep ticking and keep being counted — this produced a bogus census once.
