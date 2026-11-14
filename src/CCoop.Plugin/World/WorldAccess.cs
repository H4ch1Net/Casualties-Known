using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using CCoop.Plugin.Runtime;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace CCoop.Plugin.World
{
    /// <summary>Reaches into WorldGeneration's private state, and resolves prefabs by name.</summary>
    internal static class WorldAccess
    {
        private static readonly FieldInfo BlocksField = AccessTools.Field(typeof(WorldGeneration), "worldBlocks");
        private static readonly FieldInfo ChunksField = AccessTools.Field(typeof(WorldGeneration), "chunks");
        private static readonly FieldInfo InstantiatingField = AccessTools.Field(typeof(WorldGeneration), "instantiatingWorld");
        private static readonly FieldInfo DoingRegenField = AccessTools.Field(typeof(WorldGeneration), "doingRegen");

        internal const string SpawnedPrefix = "CCoop_";

        internal static WorldGeneration World => WorldGeneration.world;

        internal static ushort[,] Blocks => World != null ? BlocksField?.GetValue(World) as ushort[,] : null;
        internal static Tilemap[,] Chunks => World != null ? ChunksField?.GetValue(World) as Tilemap[,] : null;
        internal static byte[,] Fluid => FluidManager.main != null ? FluidManager.main.fluid : null;

        internal static bool Instantiating => World != null && InstantiatingField != null && (bool)InstantiatingField.GetValue(World);
        internal static bool Regenerating => World != null && DoingRegenField != null && (bool)DoingRegenField.GetValue(World);

        /// <summary>A built, settled world: not instantiating, not generating.</summary>
        internal static bool Settled
        {
            get
            {
                var w = World;
                return w != null && w.worldExists && !w.generatingWorld && !Instantiating && Blocks != null;
            }
        }

        internal static byte[] CaptureTerrain()
        {
            var blocks = Blocks;
            if (blocks == null) return null;
            var raw = new byte[blocks.Length * 2];
            Buffer.BlockCopy(blocks, 0, raw, 0, raw.Length);
            return raw;
        }

        internal static byte[] CaptureFluid()
        {
            var fluid = Fluid;
            if (fluid == null) return null;
            var raw = new byte[fluid.Length];
            Buffer.BlockCopy(fluid, 0, raw, 0, raw.Length);
            return raw;
        }

        internal static bool WriteTerrain(byte[] raw)
        {
            var blocks = Blocks;
            if (blocks == null || raw == null || raw.Length != blocks.Length * 2) return false;
            Buffer.BlockCopy(raw, 0, blocks, 0, raw.Length);
            return true;
        }

        internal static bool WriteFluid(byte[] raw)
        {
            var fluid = Fluid;
            if (fluid == null || raw == null || raw.Length != fluid.Length) return false;
            Buffer.BlockCopy(raw, 0, fluid, 0, raw.Length);
            return true;
        }

        internal static bool TerrainEquals(byte[] raw)
        {
            var now = CaptureTerrain();
            if (now == null || raw == null || now.Length != raw.Length) return false;
            for (int i = 0; i < now.Length; i++) if (now[i] != raw[i]) return false;
            return true;
        }

        /// <summary>Rebuilds every chunk tilemap, one column per frame.</summary>
        internal static IEnumerator RebuildAllChunks()
        {
            var w = World;
            if (w == null) yield break;
            for (int x = 0; x < w.chunkWidth; x++)
            {
                for (int y = 0; y < w.chunkHeight; y++)
                {
                    try { w.UpdateChunk(new Vector2Int(x, y)); }
                    catch (Exception e) { Log.Debug("chunk " + x + "," + y + ": " + e.Message); }
                }
                yield return null;
            }
        }

        // ------------------------------------------------------------------ prefabs

        private static Dictionary<string, GameObject> _index;

        /// <summary>
        /// Finds a prefab by the name its clones carry. Most resolve by bare name; some
        /// live in subfolders, so fall back to a one-off index of every loadable prefab.
        /// </summary>
        internal static GameObject ResolvePrefab(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            GameObject direct = null;
            try { direct = Resources.Load<GameObject>(key); } catch { /* bad path */ }
            if (direct != null) return direct;
            if (_index == null)
            {
                _index = new Dictionary<string, GameObject>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    foreach (var go in Resources.LoadAll<GameObject>(""))
                        if (go != null && !_index.ContainsKey(go.name)) _index[go.name] = go;
                }
                catch (Exception e) { Log.Warn("prefab index failed: " + e.Message); }
                Log.Info("[world] indexed " + _index.Count + " prefabs");
            }
            if (_index.TryGetValue(key, out var found)) return found;

            // A few objects are made from a part of a prefab (the start shuttle's doors are
            // a child of LifepodStart): index the parts once, on the first miss.
            if (_parts == null)
            {
                _parts = new Dictionary<string, GameObject>(StringComparer.OrdinalIgnoreCase);
                foreach (var root in _index.Values)
                    IndexParts(root.transform, 0);
                Log.Info("[world] indexed " + _parts.Count + " prefab parts");
            }
            if (_parts.TryGetValue(key, out found)) return found;

            // Parts of structure prefabs carry Unity's duplicate suffix ("ladder (1)",
            // "medcrate (2)"); the standalone prefab is the same object.
            var bare = System.Text.RegularExpressions.Regex.Replace(key, @"\s*\(\d+\)$", "");
            if (bare.Length > 0 && bare != key) return ResolvePrefab(bare);
            return null;
        }

        private static Dictionary<string, GameObject> _parts;

        private static void IndexParts(Transform t, int depth)
        {
            if (depth >= 2) return;
            for (int i = 0; i < t.childCount; i++)
            {
                var c = t.GetChild(i);
                if (!_index.ContainsKey(c.name) && !_parts.ContainsKey(c.name)) _parts[c.name] = c.gameObject;
                IndexParts(c, depth + 1);
            }
        }

        internal static string KeyFor(GameObject go)
        {
            if (go == null) return null;
            var item = go.GetComponent<Item>();
            if (item != null && !string.IsNullOrEmpty(item.id)) return item.id;
            string n = go.name;
            if (n.StartsWith(SpawnedPrefix, StringComparison.Ordinal)) n = n.Substring(SpawnedPrefix.Length);
            int clone = n.IndexOf("(Clone)", StringComparison.Ordinal);
            if (clone >= 0) n = n.Substring(0, clone);
            return n.Trim();
        }

        internal static bool IsPlayerObject(GameObject go)
        {
            var body = GameInfo.LocalBody;
            if (body == null || go == null) return false;
            var root = body.transform.root;
            return go.transform == root || go.transform.IsChildOf(root);
        }

        /// <summary>Instantiates under an inactive holder so no Awake/Start runs until we say so.</summary>
        internal static GameObject InstantiateDormant(GameObject prefab, out GameObject holder)
        {
            holder = new GameObject("CCoop_Holder");
            holder.SetActive(false);
            return UnityEngine.Object.Instantiate(prefab, holder.transform);
        }

        internal static void Strip<T>(GameObject root) where T : Component
        {
            foreach (var c in root.GetComponentsInChildren<T>(true))
                if (c != null) UnityEngine.Object.DestroyImmediate(c);
        }
    }
}
