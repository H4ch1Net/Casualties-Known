using System;
using System.Collections.Generic;
using CCoop.Net.Session;
using CCoop.Plugin.Protocol;
using CCoop.Plugin.Runtime;
using CCoop.Plugin.World;
using HarmonyLib;
using UnityEngine;

namespace CCoop.Plugin.Sync
{
    /// <summary>
    /// Terrain edits and block cracks. <c>SetBlock(Vector2Int, ushort)</c> is the single
    /// funnel for every runtime terrain change — mining, explosions, placing, quakes —
    /// while generation writes the array directly, so one hook sees exactly the gameplay.
    /// </summary>
    internal sealed class TerrainSync : CoopModule
    {
        private readonly Dictionary<int, ushort> _edits = new Dictionary<int, ushort>();
        private readonly Dictionary<int, float> _cracks = new Dictionary<int, float>();
        internal int Sent, Applied;

        private static int Key(int x, int y) => (x << 16) | (y & 0xFFFF);

        internal override void Attach(NetSession s)
        {
            WorldNet.Register<BlockEdits>(s, (o, m) => ApplyEdits(m));
            WorldNet.Register<BlockCracks>(s, (o, m) => ApplyCracks(m));
        }

        internal override void Detach() => Clear();
        internal override void OnWorldLost() => Clear();

        private void Clear()
        {
            _edits.Clear();
            _cracks.Clear();
            Sent = Applied = 0;
        }

        internal void RecordEdit(WorldGeneration w, Vector2Int pos, ushort block)
        {
            int x = Mathf.Clamp(pos.x, 0, (int)w.width - 1);
            int y = Mathf.Clamp(pos.y, 0, (int)w.height - 1);
            if (_edits.Count < 65536) _edits[Key(x, y)] = block;
        }

        internal void RecordCrack(Vector2Int pos, float damage)
        {
            if (_cracks.Count < 4096) _cracks[Key(pos.x, pos.y)] = damage;
        }

        internal override void LateTick()
        {
            if (!Coop.WorldLive) { _edits.Clear(); _cracks.Clear(); return; }
            if (_edits.Count > 0)
            {
                var batch = new BlockEdits();
                foreach (var kv in _edits)
                {
                    batch.Edits.Add(new BlockEdit { X = (ushort)(kv.Key >> 16), Y = (ushort)(kv.Key & 0xFFFF), Block = kv.Value });
                    if (batch.Edits.Count == BlockEdits.Max) { WorldNet.Publish(batch); batch = new BlockEdits(); }
                }
                if (batch.Edits.Count > 0) WorldNet.Publish(batch);
                Sent += _edits.Count;
                Log.Debug("[terrain] sent " + _edits.Count + " edits");
                _edits.Clear();
            }
            if (_cracks.Count > 0)
            {
                var batch = new BlockCracks();
                foreach (var kv in _cracks)
                {
                    batch.Cracks.Add(new BlockCrack { X = (ushort)(kv.Key >> 16), Y = (ushort)(kv.Key & 0xFFFF), Damage = kv.Value });
                    if (batch.Cracks.Count == BlockCracks.Max) { WorldNet.Publish(batch); batch = new BlockCracks(); }
                }
                if (batch.Cracks.Count > 0) WorldNet.Publish(batch);
                _cracks.Clear();
            }
        }

        /// <summary>
        /// Writes cells straight into the grid and rebuilds each touched chunk once.
        /// Going through SetBlock would redraw a 64x64 chunk per cell.
        /// </summary>
        private void ApplyEdits(BlockEdits m)
        {
            var w = WorldGeneration.world;
            var grid = WorldAccess.Blocks;
            if (w == null || grid == null) return;
            int width = grid.GetLength(0), height = grid.GetLength(1);
            var dirty = new HashSet<int>();
            int effects = 0;
            foreach (var e in m.Edits)
            {
                if (e.X >= width || e.Y >= height) continue;
                // A block someone else broke: the dust, debris and sound the game makes for the breaker.
                if (e.Block == 0 && grid[e.X, e.Y] != 0 && effects < MaxBreakEffects && BreakEffect(w, new Vector2Int(e.X, e.Y), grid[e.X, e.Y], effects < 3)) effects++;
                grid[e.X, e.Y] = e.Block;
                if (e.Block == 0) ClearCrack(w, new Vector2Int(e.X, e.Y));
                dirty.Add((e.X / WorldGeneration.CHUNKSIZE) * 1024 + e.Y / WorldGeneration.CHUNKSIZE);
            }
            foreach (int k in dirty)
            {
                try { w.UpdateChunk(new Vector2Int(k / 1024, k % 1024)); }
                catch (Exception ex) { Log.Debug("chunk rebuild: " + ex.Message); }
            }
            Applied += m.Edits.Count;

            // The host is the hub: the edit must also reach the other clients, which
            // WorldNet does, and must be part of the host's own world, which this is.
        }

        private const int MaxBreakEffects = 12;
        private const float EffectRange = 40f;
        private static GameObject _dust;

        /// <summary>As WorldGeneration.DamageBlock does when a block breaks: debris in the block's own texture, a dust cloud, its hit sound.</summary>
        private static bool BreakEffect(WorldGeneration w, Vector2Int pos, ushort was, bool sound)
        {
            try
            {
                Vector2 at = w.BlockToWorldPos(pos);
                var cam = Camera.main;
                if (cam == null || Vector2.Distance(cam.transform.position, at) > EffectRange) return false;
                var sprite = w.GetClosestChunk(pos).GetSprite(new Vector3Int(pos.x % WorldGeneration.CHUNKSIZE - w.HALFCHUNKSIZE, pos.y % WorldGeneration.CHUNKSIZE - w.HALFCHUNKSIZE, 0));
                if (sprite != null && w.blockBreakPrefab != null)
                {
                    var debris = UnityEngine.Object.Instantiate(w.blockBreakPrefab, at, Quaternion.identity);
                    var ps = debris.GetComponent<ParticleSystem>();
                    if (ps != null)
                    {
                        var shape = ps.shape;
                        shape.texture = sprite.texture;
                        ps.Play();
                    }
                }
                if (_dust == null) _dust = Resources.Load<GameObject>("DustBig");
                if (_dust != null) UnityEngine.Object.Instantiate(_dust, at, Quaternion.identity);
                if (sound)
                {
                    var info = w.GetBlockInfo(was);
                    if (info != null && !string.IsNullOrEmpty(info.hitsound)) Sound.Play(info.hitsound, at);
                }
                return true;
            }
            catch (Exception e)
            {
                Log.Debug("break effect: " + e.Message);
                return false;
            }
        }

        private static void ClearCrack(WorldGeneration w, Vector2Int pos)
        {
            var list = w.blockDamages;
            if (list == null) return;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (list[i] == null || list[i].pos != pos) continue;
                list[i].DestroySprite();
                list.RemoveAt(i);
            }
        }

        /// <summary>
        /// Cracks are written directly, never through DamageBlock, which would break the
        /// block a second time and roll a second set of loot.
        /// </summary>
        private static void ApplyCracks(BlockCracks m)
        {
            var w = WorldGeneration.world;
            if (w == null || w.blockDamages == null) return;
            foreach (var c in m.Cracks)
            {
                var pos = new Vector2Int(c.X, c.Y);
                if (w.GetBlock(pos) == 0) continue;
                var existing = w.GetBlockDamage(pos);
                float before = existing != null ? existing.damage : 0f;
                if (existing == null)
                {
                    while (w.blockDamages.Count >= 128)
                    {
                        w.blockDamages[0].DestroySprite();
                        w.blockDamages.RemoveAt(0);
                    }
                    existing = new BlockDamage { pos = pos, damage = c.Damage };
                    w.blockDamages.Add(existing);
                }
                else existing.damage = c.Damage;
                existing.UpdateSprite();

                var at = w.BlockToWorldPos(pos);
                int dealt = Mathf.RoundToInt(c.Damage - before);
                if (dealt > 0) WorldGeneration.CreateDamageNumber(at, dealt);
                var info = w.GetBlockInfo(w.GetBlock(pos));
                if (info != null && !string.IsNullOrEmpty(info.hitsound)) Sound.Play(info.hitsound, at);
            }
        }
    }

    [HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.SetBlock), new[] { typeof(Vector2Int), typeof(ushort) })]
    internal static class SetBlockPatch
    {
        private static void Postfix(WorldGeneration __instance, Vector2Int pos, ushort block)
        {
            if (!Coop.WorldLive || Coop.Applying) return;
            Coop.Get<TerrainSync>()?.RecordEdit(__instance, pos, block);
            Coop.Get<WorldCheck>()?.RecordLocal(pos.x, pos.y, block);
        }
    }

    [HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.DamageBlock), new[] { typeof(Vector2Int), typeof(float), typeof(bool), typeof(bool), typeof(bool) })]
    internal static class DamageBlockPatch
    {
        private static void Postfix(WorldGeneration __instance, Vector2Int pos)
        {
            if (!Coop.WorldLive || Coop.Applying) return;
            try
            {
                var d = __instance.GetBlockDamage(pos);
                if (d != null) Coop.Get<TerrainSync>()?.RecordCrack(pos, d.damage);
            }
            catch (Exception e) { Log.Debug("crack hook: " + e.Message); }
        }
    }
}
