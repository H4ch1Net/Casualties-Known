using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CCoop.Core;
using CCoop.Protocol.Messages;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace CCoop.Plugin
{
    /// <summary>
    /// Suppresses world generation on clients.
    ///
    /// <c>WorldGeneration.InstantiateWorld(bool generate)</c> builds all the scaffolding
    /// — the <c>worldBlocks</c> array, the per-chunk Tilemaps, colliders and
    /// <c>ChunkUpdated</c> events — and only then does <c>if (generate)
    /// StartCoroutine(GenerateWorld())</c>. Forcing that flag false gives a client a
    /// fully-formed but empty world, ready to have the host's terrain written into it.
    ///
    /// This is required, not an optimisation: the game has no world seed, so a client
    /// that generates locally produces a *different* world from the host's and the two
    /// players occupy the same coordinates in unrelated terrain.
    /// </summary>
    [HarmonyPatch(typeof(WorldGeneration), "InstantiateWorld")]
    internal static class InstantiateWorldPatch
    {
        /// <summary>Set while this peer is a client in an established session.</summary>
        internal static bool SuppressGeneration;

        // ReSharper disable once InconsistentNaming
        private static void Prefix(ref bool generate)
        {
            // A new world is being built, so last layer's structure placements are stale.
            // Cleared here rather than after a capture: one layer can be captured more
            // than once (a second player joining re-captures), and clearing there would
            // send the second client a layer with no decoration in it.
            StructureSync.Clear();

            if (!SuppressGeneration || !generate) return;

            generate = false;
            CoopPlugin.Say("[world] local generation suppressed; awaiting the host's world");
        }
    }

    /// <summary>
    /// Moves terrain between host and client.
    ///
    /// Host: captures <c>worldBlocks</c> once generation finishes and broadcasts it.
    /// Client: writes a received grid into <c>worldBlocks</c> and rebuilds the tilemaps.
    /// </summary>
    internal sealed class WorldSync
    {
        private static FieldInfo _worldBlocksField;

        private readonly Func<string, object> _log;

        /// <summary>
        /// Shared id space for this layer. An entity's id is its manifest index, so host
        /// and client agree without negotiating anything.
        /// </summary>
        internal readonly NetEntityRegistry Entities = new NetEntityRegistry();

        internal WorldSync(Func<string, object> log) { _log = log; }

        private void Note(string s) => _log?.Invoke(s);

        private static FieldInfo WorldBlocksField =>
            _worldBlocksField ?? (_worldBlocksField = AccessTools.Field(typeof(WorldGeneration), "worldBlocks"));

        /// <summary>True once the world exists and is not mid-generation.</summary>
        internal static bool WorldReady => CurrentBlocks != null;

        /// <summary>
        /// The live block grid, or null if the world is not in a usable state.
        ///
        /// Exposed as an object identity so callers can detect that
        /// <c>InstantiateWorld</c> has swapped in a fresh array — which happens on every
        /// layer transition and silently discards previously applied terrain.
        /// </summary>
        internal static Array CurrentBlocks
        {
            get
            {
                var w = WorldGeneration.world;
                if (w == null || w.generatingWorld) return null;
                return WorldBlocksField?.GetValue(w) as Array;
            }
        }

        /// <summary>
        /// The same grid, typed, for code that writes individual cells.
        ///
        /// Terrain deltas are written straight into the array and the affected chunks
        /// rebuilt once, rather than going through <c>SetBlock</c> per cell: that method
        /// rebuilds a whole 64x64 chunk on every call, so applying an explosion's few
        /// hundred cells through it would redraw a million tiles.
        /// </summary>
        internal static ushort[,] CurrentGrid => CurrentBlocks as ushort[,];

        // ---------------------------------------------------------------- capture

        /// <summary>
        /// Copies the live block grid into a flat byte array (2 bytes per block).
        /// Returns null if the world is not in a readable state.
        /// </summary>
        internal byte[] CaptureTerrain(out ushort chunkW, out ushort chunkH, out ushort chunkSize)
        {
            chunkW = chunkH = chunkSize = 0;

            var world = WorldGeneration.world;
            if (world == null) return null;

            if (!(WorldBlocksField?.GetValue(world) is Array blocks))
            {
                Note("[world] worldBlocks not readable; cannot capture");
                return null;
            }

            chunkW = (ushort)world.chunkWidth;
            chunkH = (ushort)world.chunkHeight;
            chunkSize = (ushort)WorldGeneration.CHUNKSIZE;

            var raw = new byte[blocks.GetLength(0) * blocks.GetLength(1) * sizeof(ushort)];
            Buffer.BlockCopy(blocks, 0, raw, 0, raw.Length);
            return raw;
        }

        // ---------------------------------------------------------------- background

        private static FieldInfo _chunksField;

        private static FieldInfo ChunksField =>
            _chunksField ?? (_chunksField = AccessTools.Field(typeof(WorldGeneration), "chunks"));

        /// <summary>
        /// Rebuilds the layer's backdrop on a client.
        ///
        /// <c>WorldCreateBackground</c> lives inside the suppressed <c>GenerateWorld</c>,
        /// and it parents its sprites to the chunk transforms rather than the scene root
        /// — so the entity manifest, which records roots only, never sees them. The
        /// result is a client looking through its world at empty space: no rock behind
        /// the tunnels, and none of the background detail that makes a layer readable.
        ///
        /// Recreated rather than transmitted because it is a pure function of the layer
        /// depth: one tiled sprite per chunk, chosen by <c>biomeDepth</c>, which the
        /// client already has.
        /// </summary>
        internal void RestoreBackground(int biomeDepth)
        {
            var world = WorldGeneration.world;
            if (world == null) return;

            string path = BackgroundSpriteFor(biomeDepth);
            if (path == null)
            {
                Note("[world] layer " + biomeDepth + " has no backdrop");
                return;
            }

            if (!(ChunksField?.GetValue(world) is Array chunks))
            {
                Note("[world] chunk tilemaps not readable; backdrop skipped");
                return;
            }

            int created = 0, cleared = 0;
            for (int x = 0; x < chunks.GetLength(0); x++)
            {
                for (int y = 0; y < chunks.GetLength(1); y++)
                {
                    var chunk = chunks.GetValue(x, y) as Tilemap;
                    if (chunk == null) continue;

                    // A re-apply would otherwise stack a second backdrop on every chunk.
                    cleared += ClearBackdrop(chunk.transform);

                    try
                    {
                        world.CreateBackground(path, chunk);
                        created++;
                    }
                    catch (Exception e)
                    {
                        Note("[world] backdrop for chunk " + x + "," + y + " failed: " + e.Message);
                        return;
                    }
                }
            }

            Note("[world] backdrop '" + path + "' created for " + created + " chunks" +
                 (cleared > 0 ? " (replaced " + cleared + ")" : ""));
        }

        /// <summary>The sprite <c>WorldCreateBackground</c> would have chosen.</summary>
        private static string BackgroundSpriteFor(int biomeDepth)
        {
            switch (biomeDepth)
            {
                case 0: return "rockBackground";
                case 1: return "soilBackground";
                case 2: return "sandBackground";
                case 3: return "wastelandBackground";
                case 4: return "grassBackground";
                default: return null;   // deeper layers have none
            }
        }

        private static int ClearBackdrop(Transform chunk)
        {
            int removed = 0;
            for (int i = chunk.childCount - 1; i >= 0; i--)
            {
                var child = chunk.GetChild(i);
                if (child.name != "ChunkBack") continue;
                UnityEngine.Object.DestroyImmediate(child.gameObject);
                removed++;
            }
            return removed;
        }

        // ---------------------------------------------------------------- fluid

        /// <summary>
        /// The live fluid grid, or null if there is none to read.
        ///
        /// One byte per cell, the same dimensions as the block grid.
        /// <c>InstantiateWorld</c> allocates it — and clients run that, suppressing only
        /// <c>GenerateWorld</c> — so a client always has a correctly sized, empty grid
        /// waiting for the host's.
        /// </summary>
        internal static byte[,] CurrentFluid
        {
            get
            {
                var w = WorldGeneration.world;
                if (w == null || w.generatingWorld) return null;
                return FluidManager.main != null ? FluidManager.main.fluid : null;
            }
        }

        /// <summary>Copies the live fluid grid out, or returns null if unreadable.</summary>
        internal byte[] CaptureFluid()
        {
            var grid = CurrentFluid;
            if (grid == null)
            {
                Note("[world] fluid grid not readable; sending none");
                return null;
            }

            var raw = new byte[grid.GetLength(0) * grid.GetLength(1)];
            Buffer.BlockCopy(grid, 0, raw, 0, raw.Length);
            return raw;
        }

        /// <summary>
        /// Writes a received fluid grid into the live world.
        ///
        /// Only the *initial* state travels. The game simulates fluid within ±64 blocks
        /// of each peer's own camera, so once players separate each side keeps flowing
        /// its own water — see the fluid note in STATUS. Sharing the starting grid is
        /// still the difference between two players seeing the same lake and one of them
        /// seeing dry rock.
        /// </summary>
        internal void ApplyFluid(byte[] raw)
        {
            if (raw == null || raw.Length == 0) return;

            var grid = CurrentFluid;
            if (grid == null)
            {
                Note("[world] no fluid grid to write into");
                return;
            }

            int expected = grid.GetLength(0) * grid.GetLength(1);
            if (raw.Length != expected)
            {
                // Copying a mismatched grid would smear water across unrelated cells
                // rather than fail, so refuse it outright.
                Note("[world] fluid is " + raw.Length + " B, expected " + expected + "; skipped");
                return;
            }

            Buffer.BlockCopy(raw, 0, grid, 0, raw.Length);

            // Hashed at the moment of the write, not later. Fluid simulates within ±64
            // blocks of each peer's own camera, so the two grids legitimately drift apart
            // from here — a hash taken any later cannot tell a transfer bug from water
            // that simply flowed.
            Note("[world] FLUIDHASH client " + WorldSnapshotCodec.Sha256Hex(raw) +
                 " (" + raw.Length + " B)");
        }

        // ---------------------------------------------------------------- entities

        private const string CloneSuffix = "(Clone)";

        /// <summary>Marks objects we spawned, so a re-apply can clear them first.</summary>
        internal const string SpawnedNamePrefix = "CCoopSpawn_";

        /// <summary>
        /// Records every object world generation instantiated.
        ///
        /// Only scene-ROOT objects whose name ends with "(Clone)" are recorded. Two
        /// reasons, both measured: generated objects are parented at the scene root
        /// (2,256 of 2,326 roots on a real layer), and nested children come back for free
        /// when their parent prefab is instantiated. Recording children separately is how
        /// you end up with unresolvable keys like "DisinfectButton" and "shuttledoor (1)",
        /// which are parts of prefabs rather than prefabs.
        /// </summary>
        internal EntityManifest CaptureEntities()
        {
            var manifest = new EntityManifest();
            var body = PlayerCamera.main != null ? PlayerCamera.main.body : null;

            var roots = UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects();
            int skipped = 0;

            foreach (var go in roots)
            {
                if (go == null) continue;
                if (body != null && go.transform == body.transform) continue;
                if (go.name.StartsWith(SpawnedNamePrefix, StringComparison.Ordinal)) continue;

                if (!go.name.EndsWith(CloneSuffix, StringComparison.Ordinal)) { skipped++; continue; }

                // Items carry an explicit id that Utils.Create consumes directly; it
                // matched the GameObject name on every one of 289 sampled items, but the
                // id is authoritative so prefer it.
                string key;
                var item = go.GetComponent<Item>();
                if (item != null && !string.IsNullOrEmpty(item.id)) key = item.id;
                else key = go.name.Substring(0, go.name.Length - CloneSuffix.Length);

                if (string.IsNullOrEmpty(key)) continue;

                // The id IS the manifest index, so register before adding.
                var id = (uint)manifest.Entities.Count;

                var p = go.transform.position;
                manifest.Add(key, p.x, p.y, go.transform.eulerAngles.z);
                Entities.Register(id, go, key);
            }

            int underChunks = CaptureChunkChildren(manifest);
            int structures = CaptureStructures(manifest);

            Note("[world] captured " + manifest.Entities.Count + " entities across " +
                 manifest.Keys.Count + " prefabs (" + underChunks + " under chunks, " +
                 structures + " structures, skipped " + skipped + " non-generated roots)");
            Note("[world] REGISTRY host " + Entities.Fingerprint());
            return manifest;
        }

        /// <summary>
        /// Picks up generated objects parented to a chunk rather than to the scene root.
        ///
        /// Almost everything generation makes is a root object, which is why the sweep
        /// above only looks there. The exception is decoration placed into a chunk —
        /// on the first layer that is ~100 <c>wallholes</c>, the openings light shines
        /// through, at random positions. They were simply absent on clients.
        ///
        /// Recreated at the scene root rather than back under their chunk. Parenting is
        /// organisational here: chunk visibility toggles the tilemap renderer, not the
        /// GameObject, so a child's own renderer is unaffected either way.
        /// </summary>
        private int CaptureChunkChildren(EntityManifest manifest)
        {
            var world = WorldGeneration.world;
            if (world == null) return 0;
            if (!(ChunksField?.GetValue(world) is Array chunks)) return 0;

            int captured = 0;
            for (int x = 0; x < chunks.GetLength(0); x++)
            {
                for (int y = 0; y < chunks.GetLength(1); y++)
                {
                    var chunk = chunks.GetValue(x, y) as Tilemap;
                    if (chunk == null) continue;

                    foreach (Transform child in chunk.transform)
                    {
                        if (!child.name.EndsWith(CloneSuffix, StringComparison.Ordinal)) continue;

                        var key = child.name.Substring(0, child.name.Length - CloneSuffix.Length);
                        if (key.Length == 0) continue;

                        var id = (uint)manifest.Entities.Count;
                        var p = child.position;
                        manifest.Add(key, p.x, p.y, child.eulerAngles.z);
                        Entities.Register(id, child.gameObject, key);
                        captured++;
                    }
                }
            }
            return captured;
        }

        /// <summary>
        /// Appends the layer's structure placements, so clients can replay the tile
        /// decoration that lives under <c>worldGrid</c> and is invisible to both sweeps
        /// above. See <see cref="StructureSync"/> for why only some children travel.
        ///
        /// Appended LAST, and deliberately: an entity's id is its manifest index, so
        /// anything inserted earlier would renumber every object after it. Structures
        /// claim indices too — they are skipped identically on both peers when the
        /// registry is built, so the numbering stays aligned without either side needing
        /// to know which entries the other skipped.
        /// </summary>
        private int CaptureStructures(EntityManifest manifest)
        {
            int added = 0;
            foreach (var placement in StructureSync.Placements)
            {
                manifest.Add(StructureSync.KeyPrefix + placement.Prefab,
                             placement.X, placement.Y, 0f);
                added++;
            }
            return added;
        }

        /// <summary>
        /// Resolves a prefab key to a loadable resource.
        ///
        /// A plain <c>Resources.Load(key)</c> covers most keys, but prefabs living in
        /// subfolders (e.g. "Special/...") do not resolve by bare name. Rather than
        /// guessing folders, fall back to indexing every loadable prefab once and looking
        /// the name up. The index is built lazily so the cost is only paid by clients
        /// that actually hit a miss.
        /// </summary>
        private static Dictionary<string, UnityEngine.Object> _prefabIndex;

        /// <summary>
        /// Prefab lookup for callers outside world sync — the proxy needs it to build the
        /// items a remote player is carrying, and the same subfolder problem applies.
        /// </summary>
        internal static UnityEngine.Object ResolvePrefabPublic(string key) => ResolvePrefab(key);

        private static UnityEngine.Object ResolvePrefab(string key)
        {
            var direct = Resources.Load(key);
            if (direct != null) return direct;

            if (_prefabIndex == null)
            {
                _prefabIndex = new Dictionary<string, UnityEngine.Object>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    foreach (var obj in Resources.LoadAll<GameObject>(""))
                    {
                        if (obj != null && !_prefabIndex.ContainsKey(obj.name))
                            _prefabIndex[obj.name] = obj;
                    }
                }
                catch (Exception e)
                {
                    CoopPlugin.Warn("[world] prefab index failed: " + e.Message);
                }
            }

            return _prefabIndex.TryGetValue(key, out var found) ? found : null;
        }

        /// <summary>
        /// Recreates the host's world objects. Spread across frames — instantiating a
        /// couple of thousand prefabs in one frame would stall for seconds.
        /// </summary>
        internal IEnumerator SpawnEntities(EntityManifest manifest, Action<int, int> onFinished)
        {
            if (manifest == null || manifest.Entities.Count == 0)
            {
                onFinished?.Invoke(0, 0);
                yield break;
            }

            ClearSpawnedEntities();
            Entities.Clear();

            int spawned = 0, failed = 0;
            int structures = 0, structureParts = 0;
            var unresolved = new HashSet<string>();
            const int perFrame = 100;

            for (int i = 0; i < manifest.Entities.Count; i++)
            {
                var record = manifest.Entities[i];
                var key = manifest.KeyFor(record);

                if (!string.IsNullOrEmpty(key) &&
                    key.StartsWith(StructureSync.KeyPrefix, StringComparison.Ordinal))
                {
                    // A structure placement, not an object: replay it and claim the index
                    // without registering anything. The host registered nothing here
                    // either, so both peers skip the same ids and stay aligned.
                    var name = key.Substring(StructureSync.KeyPrefix.Length);
                    try
                    {
                        var prefab = ResolvePrefab(name) as GameObject;
                        if (prefab == null)
                        {
                            failed++;
                            unresolved.Add(key);
                        }
                        else
                        {
                            structureParts += StructureSync.Replay(
                                prefab, record.X, record.Y, SpawnedNamePrefix);
                            structures++;
                        }
                    }
                    catch (Exception e)
                    {
                        failed++;
                        if (unresolved.Count < 20) unresolved.Add(key + " (" + e.GetType().Name + ")");
                    }
                }
                else if (!string.IsNullOrEmpty(key))
                {
                    try
                    {
                        var prefab = ResolvePrefab(key);
                        if (prefab == null)
                        {
                            failed++;
                            unresolved.Add(key);
                        }
                        else
                        {
                            var go = UnityEngine.Object.Instantiate(
                                prefab,
                                new Vector3(record.X, record.Y, 0f),
                                Quaternion.Euler(0f, 0f, record.Rotation)) as GameObject;

                            if (go != null)
                            {
                                // Prefixed so a later re-apply can remove exactly what we
                                // created without touching the local player's own things.
                                go.name = SpawnedNamePrefix + key;

                                // Same id the host used: the manifest index.
                                Entities.Register((uint)i, go, key);
                                spawned++;
                            }
                            else failed++;
                        }
                    }
                    catch (Exception e)
                    {
                        failed++;
                        if (unresolved.Count < 20) unresolved.Add(key + " (" + e.GetType().Name + ")");
                    }
                }

                if ((i + 1) % perFrame == 0) yield return null;
            }

            if (unresolved.Count > 0)
                Note("[world] could not resolve: " + string.Join(", ",
                     unresolved.Take(15).ToArray()) + (unresolved.Count > 15 ? " ..." : ""));

            Note("[world] spawned " + spawned + " entities, " + failed + " failed");
            if (structures > 0)
                Note("[world] replayed " + structures + " structures -> " + structureParts +
                     " tile decorations");
            Note("[world] REGISTRY client " + Entities.Fingerprint());
            onFinished?.Invoke(spawned, failed);
        }

        /// <summary>Destroys objects a previous apply created.</summary>
        internal void ClearSpawnedEntities()
        {
            int removed = 0;
            foreach (var go in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
            {
                if (go != null && go.name.StartsWith(SpawnedNamePrefix, StringComparison.Ordinal))
                {
                    UnityEngine.Object.Destroy(go);
                    removed++;
                }
            }

            // Replayed structure decoration is parented to worldGrid, so the root sweep
            // cannot see it and every re-apply would leave another copy of every vent and
            // window behind. Matched by name, never by "everything under worldGrid" —
            // the terrain chunks live there too.
            var world = WorldGeneration.world;
            if (world != null && world.worldGrid != null)
            {
                var doomed = new List<GameObject>();
                foreach (Transform child in world.worldGrid.transform)
                {
                    if (child != null &&
                        child.name.StartsWith(SpawnedNamePrefix, StringComparison.Ordinal))
                        doomed.Add(child.gameObject);
                }
                foreach (var go in doomed)
                {
                    UnityEngine.Object.Destroy(go);
                    removed++;
                }
            }

            if (removed > 0) Note("[world] cleared " + removed + " previously spawned entities");
        }

        // ---------------------------------------------------------------- apply

        /// <summary>
        /// Writes a received grid into the live world and rebuilds every chunk.
        ///
        /// Yields between chunks: rebuilding 16x16 chunks of 64x64 tiles is ~1M
        /// <c>SetTile</c> calls, which in one frame would freeze the game for seconds.
        /// The game's own generator spreads the same work the same way.
        /// </summary>
        internal IEnumerator ApplyTerrain(LayerSnapshot snapshot, Action<bool, string> onFinished)
        {
            var prepare = snapshot.Prepare;
            var raw = snapshot.Terrain;
            var entities = snapshot.Entities;

            var world = WorldGeneration.world;
            if (world == null)
            {
                onFinished?.Invoke(false, "no world to apply into");
                yield break;
            }

            if (!(WorldBlocksField?.GetValue(world) is Array blocks))
            {
                onFinished?.Invoke(false, "worldBlocks not writable");
                yield break;
            }

            // The host's grid must match ours exactly, or the copy would misalign rows
            // and produce scrambled terrain rather than an obvious failure.
            int localW = blocks.GetLength(0), localH = blocks.GetLength(1);
            int remoteW = prepare.ChunkWidth * prepare.ChunkSize;
            int remoteH = prepare.ChunkHeight * prepare.ChunkSize;

            if (localW != remoteW || localH != remoteH)
            {
                onFinished?.Invoke(false,
                    "world size mismatch: local " + localW + "x" + localH +
                    ", host " + remoteW + "x" + remoteH);
                yield break;
            }

            int expected = localW * localH * sizeof(ushort);
            if (raw.Length != expected)
            {
                onFinished?.Invoke(false, "payload is " + raw.Length + " B, expected " + expected);
                yield break;
            }

            // Bulk copy straight into the array. SetBlockNoUpdate would work but clamps
            // to width-2/height-2, so it cannot address the last row or column.
            Buffer.BlockCopy(raw, 0, blocks, 0, raw.Length);
            Note("[world] terrain written (" + localW + "x" + localH + "), rebuilding chunks...");

            // Stage hashes so a mismatch can be localised to transfer, write, or rebuild
            // rather than guessed at.
            Note("[world] HASH received " + WorldSnapshotCodec.Sha256Hex(raw));
            Note("[world] HASH afterwrite " + HashLiveWorld(blocks));

            // Fluid before the chunk rebuild: entities placed below settle against it,
            // and it is a straight array write with nothing to redraw.
            ApplyFluid(snapshot.Fluid);

            // The backdrop the suppressed generator would have made. Before the rebuild
            // so the world is never briefly shown against empty space.
            RestoreBackground(prepare.BiomeDepth);

            yield return null;

            yield return RebuildChunks(world);
            Note("[world] HASH afterrebuild " + HashLiveWorld(blocks));

            try { world.UpdateChunkVisibility(); }
            catch (Exception e) { Note("[world] visibility refresh failed: " + e.Message); }

            // Entities after terrain: many of them (plants, traps, ropes) sit on blocks
            // and would otherwise be placed against a world that is still empty.
            int spawned = 0, failed = 0;
            yield return SpawnEntities(entities, (s, f) => { spawned = s; failed = f; });

            // One frame before checking. Unity runs Start() on freshly instantiated
            // objects at the top of the NEXT frame, so the last batch's terrain edits
            // have not happened yet at this point and the check below would miss them.
            yield return null;

            // Spawning mutates terrain — plants, geysers and building entities edit the
            // blocks they sit on. The host's grid was captured AFTER generation, so it
            // already contains those edits; letting them apply a second time here makes
            // the two worlds diverge. Re-assert the host's terrain so it wins.
            if (spawned > 0)
            {
                var after = WorldBlocksField?.GetValue(world) as Array;
                if (after != null && !TerrainMatches(after, raw))
                {
                    Note("[world] entities altered terrain; re-applying the host's grid");
                    Buffer.BlockCopy(raw, 0, after, 0, raw.Length);
                    yield return RebuildChunks(world);
                }
            }

            PlaceLocalBody(prepare);

            // A partial spawn is reported as success with a count: the layer is playable,
            // and hiding the shortfall would make a later "why is this missing?" much
            // harder to trace.
            onFinished?.Invoke(true, spawned + " spawned, " + failed + " failed");
        }

        /// <summary>
        /// Rebuilds every chunk's tilemap from the block grid, one chunk-row per frame.
        /// Doing all 256 chunks (~1M SetTile calls) in a single frame stalls for seconds.
        /// </summary>
        private IEnumerator RebuildChunks(WorldGeneration world)
        {
            int rebuilt = 0;
            for (int x = 0; x < world.chunkWidth; x++)
            {
                for (int y = 0; y < world.chunkHeight; y++)
                {
                    bool ok = true;
                    try { world.UpdateChunk(new Vector2Int(x, y)); }
                    catch (Exception e)
                    {
                        ok = false;
                        Note("[world] chunk " + x + "," + y + " failed: " + e.Message);
                    }
                    if (ok) rebuilt++;
                }
                yield return null;
            }
            Note("[world] rebuilt " + rebuilt + " chunks");
        }

        /// <summary>Cheap equality check between the live grid and a reference payload.</summary>
        private static bool TerrainMatches(Array blocks, byte[] raw)
        {
            var current = new byte[raw.Length];
            Buffer.BlockCopy(blocks, 0, current, 0, current.Length);
            for (int i = 0; i < current.Length; i++)
                if (current[i] != raw[i]) return false;
            return true;
        }

        /// <summary>
        /// Freezes or releases the local body.
        ///
        /// A client enters the run with generation suppressed, so for a few seconds the
        /// world is completely empty — the character falls through nothing, takes fall
        /// damage, and is only then repositioned when the terrain arrives. Holding the
        /// ragdoll still until the world is applied avoids injuring the player for a
        /// loading artefact.
        /// </summary>
        internal void FreezeLocalBody(bool frozen)
        {
            try
            {
                var body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
                if (body == null) return;

                foreach (var rb in body.GetComponentsInChildren<Rigidbody2D>(true))
                {
                    if (rb == null) continue;
                    if (frozen)
                    {
                        rb.velocity = Vector2.zero;
                        rb.angularVelocity = 0f;
                        // Kinematic rather than simulated=false: collisions stay
                        // registered, so the body settles correctly once released.
                        rb.isKinematic = true;
                    }
                    else
                    {
                        rb.isKinematic = false;
                        rb.velocity = Vector2.zero;
                        rb.angularVelocity = 0f;
                    }
                }

                Note("[world] local body " + (frozen ? "frozen for loading" : "released"));
            }
            catch (Exception e)
            {
                Note("[world] could not " + (frozen ? "freeze" : "release") + " body: " + e.Message);
            }
        }

        /// <summary>Hashes the live block grid, for staged mismatch diagnosis.</summary>
        private static string HashLiveWorld(Array blocks)
        {
            var bytes = new byte[blocks.GetLength(0) * blocks.GetLength(1) * sizeof(ushort)];
            Buffer.BlockCopy(blocks, 0, bytes, 0, bytes.Length);
            return WorldSnapshotCodec.Sha256Hex(bytes);
        }

        /// <summary>
        /// Puts the local body at the host's spawn point.
        ///
        /// Needed because <c>WorldPlacePlayer</c> lives inside <c>GenerateWorld</c>,
        /// which we suppressed — without this the client's body stays wherever it was.
        /// </summary>
        private void PlaceLocalBody(LayerPrepareMessage prepare)
        {
            try
            {
                var body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
                if (body == null) return;

                var spawn = new Vector3(prepare.SpawnX, prepare.SpawnY, body.transform.position.z);
                body.transform.position = spawn;

                // Limbs are independent rigidbodies, not children of a moved root, so
                // they must be carried across too or the ragdoll stays behind and is
                // dragged back by its joints.
                if (body.limbs != null)
                {
                    foreach (var limb in body.limbs)
                    {
                        if (limb == null) continue;
                        limb.transform.position = spawn;
                        var rb = limb.GetComponent<Rigidbody2D>();
                        if (rb != null) rb.velocity = Vector2.zero;
                    }
                }

                var bodyRb = body.GetComponent<Rigidbody2D>();
                if (bodyRb != null) bodyRb.velocity = Vector2.zero;

                Note("[world] local body placed at " + prepare.SpawnX.ToString("F1") + "," +
                     prepare.SpawnY.ToString("F1"));
            }
            catch (Exception e)
            {
                Note("[world] could not place body: " + e.Message);
            }
        }
    }
}
