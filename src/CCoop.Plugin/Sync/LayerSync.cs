using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using CCoop.Net.Session;
using CCoop.Plugin.Protocol;
using CCoop.Plugin.Runtime;
using CCoop.Plugin.World;
using UnityEngine;
using CCoop.Plugin.UI;

namespace CCoop.Plugin.Sync
{
    /// <summary>
    /// Captures the host's layer and rebuilds it on clients.
    ///
    /// Host: once generation settles, every world object gets a network id; a snapshot
    /// is captured per joining client from the live world, so a late joiner gets the
    /// layer as it is now, not as it was generated.
    /// Client: writes terrain and water into its scaffold, replays structures, spawns
    /// every object under the host's ids, then removes anything the spawned objects
    /// rolled for themselves on Start (corpse loot and the like).
    /// </summary>
    internal sealed class LayerSync : CoopModule
    {
        internal static bool Applying { get; private set; }
        internal static float Progress { get; private set; }

        internal override void Attach(NetSession s)
        {
            s.BlobReceived += OnBlob;
            s.BlobProgress += (origin, tag, got, total) =>
            {
                if (tag == CoopProtocol.BlobLayer && total > 0) Progress = 0.5f * got / total;
            };
        }

        internal override void Detach()
        {
            Applying = false;
            Progress = 0f;
        }

        internal override void OnWorldLost()
        {
            if (Coop.IsHost) EntityRegistry.Clear();
        }

        // ------------------------------------------------------------------ host

        internal void HostRegisterWorld()
        {
            var sw = Stopwatch.StartNew();
            EntityRegistry.Clear();
            // Structure parts get structure ids below; keep them out of the generic sweep.
            var parts = new HashSet<int>();
            foreach (var placement in StructureRecorder.Placements)
                foreach (var child in placement.Children)
                    if (child != null) parts.Add(child.GetInstanceID());
            int generated = 0;
            foreach (var go in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
            {
                if (parts.Contains(go.GetInstanceID()) || !IsWorldContent(go)) continue;
                EntityRegistry.Register(EntityRegistry.NextGeneratedId(), go, WorldAccess.KeyFor(go));
                generated++;
            }
            int structureParts = 0;
            for (int p = 0; p < StructureRecorder.Placements.Count; p++)
            {
                var placement = StructureRecorder.Placements[p];
                for (int c = 0; c < placement.Children.Count; c++)
                {
                    var child = placement.Children[c];
                    uint id = EntityRegistry.StructureId(p, c);
                    if (child == null) continue;
                    EntityRegistry.Register(id, child, placement.Prefab + "#" + c);
                    structureParts++;
                }
            }
            Log.Info("[layer] registered " + generated + " objects and " + structureParts + " structure parts (" +
                     StructureRecorder.Placements.Count + " structures) in " + sw.ElapsedMilliseconds + " ms");
        }

        /// <summary>Generated objects worth sending: anything with gameplay, not effects.</summary>
        internal static bool IsWorldContent(GameObject go)
        {
            if (go == null || !go.activeInHierarchy) return false;
            if (WorldAccess.IsPlayerObject(go)) return false;
            if (go.name.StartsWith("CCoop", StringComparison.Ordinal)) return false;
            if (go.GetComponent<BuildingEntity>() != null) return true;
            if (go.GetComponent<CaveTickSpawner>() != null) return true;  // a trap, though it looks like an effect
            var item = go.GetComponent<Item>();
            if (item != null) return go.transform.parent == null;
            if (!go.name.Contains("(Clone)")) return false;
            if (go.GetComponent<ParticleSystem>() != null) return false;
            return go.GetComponentInChildren<Collider2D>(true) != null || go.GetComponent<SpriteRenderer>() != null;
        }

        internal void SendSnapshot(byte peer)
        {
            var w = WorldGeneration.world;
            if (w == null || !Coop.WorldLive) return;
            var sw = Stopwatch.StartNew();
            var snap = Capture(w);
            byte[] data;
            try { data = snap.Encode(); }
            catch (Exception e)
            {
                Log.Error("[layer] could not encode the snapshot: " + e);
                return;
            }
            var rec = Coop.Player(peer);
            rec.WorldEpoch = Coop.Epoch;
            rec.InWorld = false;
            Session.SendBlob(peer, CoopProtocol.BlobLayer, data);
            Coop.Get<WorldEvents>()?.SendLogTo(peer);  // traps sprung and doors opened since the layer began
            Log.Info("[layer] sent layer " + (snap.Depth + 1) + " to " + rec.Name + ": " + snap.Entities.Count + " objects, " +
                     (data.Length / 1024) + " KB in " + sw.ElapsedMilliseconds + " ms");
        }

        private LayerSnapshot Capture(WorldGeneration w)
        {
            EntityRegistry.Prune();
            var snap = new LayerSnapshot
            {
                Epoch = Coop.Epoch,
                Depth = w.biomeDepth,
                ModifierSlot = ActiveModifierSlot(),
                Fog = w.fogAmount,
                LayerTime = w.layerTimeSpent,
                Width = (int)w.width,
                Height = (int)w.height,
                Terrain = WorldAccess.CaptureTerrain() ?? new byte[0],
                Fluid = WorldAccess.CaptureFluid() ?? new byte[0],
                GravityX = Physics2D.gravity.x,
                GravityY = Physics2D.gravity.y,
            };
            var body = GameInfo.LocalBody;
            if (body != null)
            {
                snap.SpawnX = body.transform.position.x;
                snap.SpawnY = body.transform.position.y + 0.5f;
            }

            foreach (var kv in EntityRegistry.All)
            {
                var go = kv.Value;
                if (go == null) continue;
                var item = go.GetComponent<Item>();
                if (item != null && go.transform.parent != null) continue;  // picked up meanwhile
                var t = go.transform;
                var e = new LayerSnapshot.Entity
                {
                    Id = kv.Key,
                    Key = EntityRegistry.KeyOf(kv.Key) ?? WorldAccess.KeyFor(go),
                    X = t.position.x,
                    Y = t.position.y,
                    Rotation = t.eulerAngles.z,
                    ScaleX = t.localScale.x,
                    ScaleY = t.localScale.y,
                };
                if (kv.Key >= EntityRegistry.StructureBase && kv.Key < EntityRegistry.RuntimeBase)
                {
                    e.Placement = (int)((kv.Key - EntityRegistry.StructureBase) / 64u);
                    e.Part = (int)((kv.Key - EntityRegistry.StructureBase) % 64u);
                }
                var building = go.GetComponent<BuildingEntity>();
                if (building != null)
                {
                    e.HasHealth = true;
                    e.Health = building.health;
                }
                if (item != null) e.ItemState = ItemCodec.Encode(item);
                try { e.Extra = EntityState.Capture(go); }
                catch (Exception ex) { Log.Debug("[layer] state of " + e.Key + ": " + ex.Message); }
                snap.Entities.Add(e);
            }

            foreach (var p in StructureRecorder.Placements)
                snap.Structures.Add(new LayerSnapshot.Structure { Prefab = p.Prefab, X = p.Pos.x, Y = p.Pos.y });
            CaptureDecals(w, snap);
            try
            {
                if (w.skyMaterial != null)
                {
                    var sky = w.skyMaterial.GetColor("_TopColor");
                    snap.SkyR = sky.r; snap.SkyG = sky.g; snap.SkyB = sky.b;
                    snap.Rain = w.skyMaterial.GetFloat("_RainIntensity");
                }
            }
            catch (Exception e) { Log.Debug("[layer] sky: " + e.Message); }
            return snap;
        }

        /// <summary>The back-wall decorations' kinds: they live under the chunks, not at the root, so the object sweep misses them.</summary>
        private static string DecalKey(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (name.StartsWith("wallholes", StringComparison.Ordinal)) return "wallholes";
            if (name.StartsWith("blastmark", StringComparison.Ordinal)) return "blastmark";
            return null;
        }

        private static void CaptureDecals(WorldGeneration w, LayerSnapshot snap)
        {
            try
            {
                var chunks = WorldAccess.Chunks;
                if (chunks == null) return;
                foreach (var chunk in chunks)
                {
                    if (chunk == null) continue;
                    var t = chunk.transform;
                    for (int i = 0; i < t.childCount; i++)
                    {
                        var c = t.GetChild(i);
                        string key = DecalKey(c.name);
                        if (key == null) continue;
                        var audio = c.GetComponent<AudioSource>();
                        snap.Decals.Add(new LayerSnapshot.Decal { Key = key, X = c.position.x, Y = c.position.y, Rotation = c.eulerAngles.z, Pitch = audio != null ? audio.pitch : 1f });
                    }
                }
            }
            catch (Exception e) { Log.Debug("[layer] decals: " + e.Message); }
        }

        /// <summary>Client: its own randomly placed decorations go; the host's take their places.</summary>
        private static int ApplyDecals(WorldGeneration w, LayerSnapshot snap)
        {
            int made = 0;
            try
            {
                var chunks = WorldAccess.Chunks;
                if (chunks == null) return 0;
                foreach (var chunk in chunks)
                {
                    if (chunk == null) continue;
                    var t = chunk.transform;
                    for (int i = t.childCount - 1; i >= 0; i--)
                        if (DecalKey(t.GetChild(i).name) != null) UnityEngine.Object.Destroy(t.GetChild(i).gameObject);
                }
                var prefabs = new Dictionary<string, GameObject>();
                foreach (var d in snap.Decals)
                {
                    if (!prefabs.TryGetValue(d.Key, out var prefab)) prefabs[d.Key] = prefab = Resources.Load("Special/" + d.Key) as GameObject;
                    if (prefab == null) continue;
                    var pos = new Vector2(d.X, d.Y);
                    var chunk = w.GetClosestChunk(w.WorldToBlockPos(pos));
                    if (chunk == null) continue;
                    var go = UnityEngine.Object.Instantiate(prefab, pos, Quaternion.Euler(0f, 0f, d.Rotation), chunk.transform);
                    var audio = go.GetComponent<AudioSource>();
                    if (audio != null) audio.pitch = d.Pitch;
                    made++;
                }
            }
            catch (Exception e) { Log.Debug("[layer] decals: " + e.Message); }
            return made;
        }

        private static int ActiveModifierSlot()
        {
            var all = LayerModifier.availableModifiers;
            if (all == null) return -1;
            for (int i = 0; i < all.Length; i++)
                if (all[i] != null && all[i].active) return i;
            return -1;
        }

        // ------------------------------------------------------------------ client

        private void OnBlob(byte origin, ushort tag, byte[] data)
        {
            if (tag != CoopProtocol.BlobLayer || Coop.IsHost || origin != NetSession.HostId) return;
            LayerSnapshot snap;
            try { snap = LayerSnapshot.Decode(data); }
            catch (Exception e)
            {
                Log.Error("[layer] the host's world could not be read: " + e.Message);
                Coop.Get<RunFlow>()?.ClientLayerApplied(Coop.Epoch, false, "unreadable snapshot");
                return;
            }
            if (snap.Epoch != Coop.Epoch)
            {
                Log.Info("[layer] ignoring a snapshot for epoch " + snap.Epoch + " (now " + Coop.Epoch + ")");
                return;
            }
            if (!WorldAccess.Settled)
            {
                Log.Warn("[layer] snapshot arrived before the scaffold was ready");
                return;
            }
            Coop.Runtime.StartCoroutine(Apply(snap, data.Length));
        }

        private IEnumerator Apply(LayerSnapshot snap, int bytes)
        {
            Applying = true;
            Progress = 0.5f;
            var sw = Stopwatch.StartNew();
            string failure = null;
            int spawned = 0, failed = 0, purged = 0;
            var w = WorldGeneration.world;

            Coop.ApplyingRemote++;
            try
            {
                if (w == null || snap.Width != (int)w.width || snap.Height != (int)w.height)
                    failure = "world size differs from the host's";
                else if (!WorldAccess.WriteTerrain(snap.Terrain))
                    failure = "terrain did not fit";
                else
                {
                    WorldAccess.WriteFluid(snap.Fluid);
                    EntityRegistry.Clear();
                    RemoveUnregisteredContent();
                }
            }
            finally { Coop.ApplyingRemote--; }

            if (failure != null)
            {
                Finish(snap, false, failure);
                yield break;
            }

            yield return WorldAccess.RebuildAllChunks();
            Progress = 0.65f;

            const int perFrame = 120;
            for (int i = 0; i < snap.Entities.Count; i++)
            {
                Coop.ApplyingRemote++;
                try
                {
                    if (Spawn(snap, snap.Entities[i])) spawned++;
                    else failed++;
                }
                catch (Exception e)
                {
                    failed++;
                    Log.Debug("[layer] spawn " + snap.Entities[i].Key + ": " + e.Message);
                }
                finally { Coop.ApplyingRemote--; }
                if ((i + 1) % perFrame == 0)
                {
                    Progress = 0.65f + 0.3f * i / snap.Entities.Count;
                    yield return null;
                    if (Superseded(snap)) yield break;
                }
            }

            // Start() runs on the next frame; objects roll their own loot and edit the
            // blocks they sit on there. Let that happen, then undo it.
            yield return null;
            yield return null;
            if (Superseded(snap)) yield break;

            Coop.ApplyingRemote++;
            try
            {
                purged = RemoveUnregisteredContent();
                int decals = ApplyDecals(w, snap);
                TileLook.Redraw();
                // The host's sky (each game rolls its own colour and rain); the last layer's
                // sky is each player's own (the game colours it by what they carry).
                if (snap.SkyR >= 0f && w.skyMaterial != null && snap.Depth != 10)
                {
                    try
                    {
                        w.skyMaterial.SetColor("_TopColor", new Color(snap.SkyR, snap.SkyG, snap.SkyB, 1f));
                        w.skyMaterial.SetFloat("_RainIntensity", snap.Rain);
                    }
                    catch (Exception ex) { Log.Debug("[layer] sky: " + ex.Message); }
                }
                if (decals > 0) Log.Info("[layer] " + decals + " back-wall decorations from the host");
                ApplyModifier(snap.ModifierSlot);
                // Content modifiers (Flooded, Dried) rewrite water as they initialise, and
                // Turbulent rolls its own gravity: the host's state wins over both.
                WorldAccess.WriteFluid(snap.Fluid);
                Physics2D.gravity = new Vector2(snap.GravityX, snap.GravityY);
                w.SetFog(snap.Fog);
                w.layerTimeSpent = snap.LayerTime;
                try
                {
                    // FinishWorldGeneration set this before the modifier (Shiny) existed here.
                    if (WWMRLight.inst != null && WWMRLight.inst.light != null)
                        WWMRLight.inst.light.enabled = w.biomeDepth == 9 || w.specialLightEnable;
                }
                catch (Exception ex) { Log.Debug("[layer] light: " + ex.Message); }
            }
            catch (Exception e) { Log.Warn("[layer] layer state: " + e.Message); }
            finally { Coop.ApplyingRemote--; }

            yield return null;
            if (Superseded(snap)) yield break;
            Coop.ApplyingRemote++;
            try { purged += RemoveUnregisteredContent(); }
            finally { Coop.ApplyingRemote--; }

            if (!WorldAccess.TerrainEquals(snap.Terrain))
            {
                WorldAccess.WriteTerrain(snap.Terrain);
                yield return WorldAccess.RebuildAllChunks();
            }

            BodyFreeze.PlaceAt(new Vector2(snap.SpawnX, snap.SpawnY));
            // The first layer opens with the pod's crash: the game put its smoke under where this
            // body was before it was placed. Under the pod instead, unless the layer is long under way.
            if (snap.Depth == 0 && snap.LayerTime < 20f)
            {
                try
                {
                    var smoke = Resources.Load("Special/ExplosionParticle");
                    if (smoke != null) UnityEngine.Object.Instantiate(smoke, new Vector3(snap.SpawnX, snap.SpawnY - 0.5f - 11f, 0f), Quaternion.identity);
                }
                catch (Exception e) { Log.Debug("[layer] crash smoke: " + e.Message); }
            }
            try { w.UpdateChunkVisibility(); } catch { /* camera not ready */ }
            yield return null;
            if (Superseded(snap)) yield break;
            BodyFreeze.Set(false);

            Log.Info("[layer] applied layer " + (snap.Depth + 1) + ": " + spawned + " objects (" + failed + " unresolved, " +
                     purged + " local extras removed), " + (bytes / 1024) + " KB, " + sw.ElapsedMilliseconds + " ms");
            Finish(snap, true, spawned + " objects");
        }

        /// <summary>
        /// The host moved on to another layer while this one was still loading: stop, and
        /// never mark the old world live under the new layer's number.
        /// </summary>
        private static bool Superseded(LayerSnapshot snap)
        {
            if (snap.Epoch == Coop.Epoch && Coop.InSession) return false;
            Applying = false;
            Progress = 0f;
            Log.Info("[layer] layer " + (snap.Depth + 1) + " (epoch " + snap.Epoch + ") was superseded while loading");
            return true;
        }

        private void Finish(LayerSnapshot snap, bool ok, string detail)
        {
            if (Superseded(snap)) return;
            Applying = false;
            Progress = ok ? 1f : 0f;
            if (!ok)
            {
                Log.Error("[layer] could not apply the host's world: " + detail);
                CoopRuntime.Notice(Lang.F("Could not load the host's world: {0}", detail));
                BodyFreeze.Set(false);
            }
            else
            {
                Coop.SetWorldLive(true);
                int replayed = WorldNet.Drain();
                if (replayed > 0) Log.Info("[layer] replayed " + replayed + " changes that arrived while loading");
            }
            Coop.Get<RunFlow>()?.ClientLayerApplied(snap.Epoch, ok, detail);
        }

        private static bool Spawn(LayerSnapshot snap, LayerSnapshot.Entity e)
        {
            GameObject prefab;
            if (e.Placement >= 0)
            {
                // A part of a structure: the same child of the same structure prefab.
                var structure = e.Placement < snap.Structures.Count ? WorldAccess.ResolvePrefab(snap.Structures[e.Placement].Prefab) : null;
                prefab = structure != null ? StructureRecorder.Part(structure, e.Part) : null;
            }
            else prefab = WorldAccess.ResolvePrefab(e.Key);
            if (prefab == null)
            {
                Log.Info("[layer] no prefab for '" + e.Key + "'");
                return false;
            }
            if (e.HasHealth && e.Health < 0.5f)
            {
                // Broken objects are gone, except indestructible ones, which stay as corpses.
                var proto = prefab.GetComponent<BuildingEntity>();
                if (proto == null || !proto.indestructible) return true;
            }
            var go = UnityEngine.Object.Instantiate(prefab, new Vector3(e.X, e.Y, 0f), Quaternion.Euler(0f, 0f, e.Rotation));
            if (go == null) return false;
            if (e.Placement >= 0 && go.GetComponent<UnityEngine.Tilemaps.Tilemap>() != null && WorldGeneration.world.worldGrid != null)
                go.transform.SetParent(WorldGeneration.world.worldGrid.transform, true);
            // Rolled state first: some of it (crystal effects) rescales the object or
            // sets its health in Start, and the host's transform and health must win.
            try { EntityState.Apply(go, e.Extra); }
            catch (Exception ex) { Log.Debug("[layer] state of " + e.Key + ": " + ex.Message); }
            go.transform.position = new Vector3(e.X, e.Y, 0f);
            go.transform.localScale = new Vector3(e.ScaleX, e.ScaleY, go.transform.localScale.z);
            if (e.HasHealth)
            {
                var b = go.GetComponent<BuildingEntity>();
                if (b != null) b.health = e.Health;
            }
            if (e.ItemState != null)
            {
                var item = go.GetComponent<Item>();
                if (item != null) ItemCodec.Apply(item, ItemCodec.Decode(e.ItemState));
            }
            EntityRegistry.Register(e.Id, go, e.Key);
            return true;
        }

        /// <summary>Destroys root world objects that have no network id.</summary>
        internal static int RemoveUnregisteredContent()
        {
            int removed = 0;
            foreach (var go in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
            {
                if (go == null || WorldAccess.IsPlayerObject(go)) continue;
                if (go.name.StartsWith("CCoop", StringComparison.Ordinal)) continue;
                // The same test the host uses to decide what it sends: anything else of that
                // kind here is this machine's own (a start shuttle left from layer 1, say).
                bool content = go.GetComponent<BuildingEntity>() != null || go.GetComponent<Item>() != null || IsWorldContent(go);
                if (!content) continue;
                if (EntityRegistry.TryGetId(go, out _)) continue;
                UnityEngine.Object.Destroy(go);
                removed++;
            }
            return removed;
        }

        private static void ApplyModifier(int slot)
        {
            var w = WorldGeneration.world;
            if (w == null) return;
            w.ResetLayerModifiers();
            var all = LayerModifier.availableModifiers;
            if (slot < 0 || all == null || slot >= all.Length) return;
            w.ApplySpecificModifier(slot);
            Log.Info("[layer] modifier " + all[slot].GetType().Name + " applied");
        }
    }
}
