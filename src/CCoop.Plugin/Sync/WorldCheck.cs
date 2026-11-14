using System;
using System.Collections.Generic;
using CCoop.Net.Session;
using CCoop.Net.Wire;
using CCoop.Plugin.Protocol;
using CCoop.Plugin.Runtime;
using CCoop.Plugin.World;
using UnityEngine;

namespace CCoop.Plugin.Sync
{
    /// <summary>
    /// The desync alarm, and the start of a self-healing world.
    ///
    /// Every 30 s the host sends a fingerprint of each terrain chunk (64×64 blocks).
    /// A client compares its own. A chunk that differs on two checks in a row is not an
    /// edit still in flight but a real desync: the client says so in the log and asks
    /// for that chunk, and the host sends its blocks. The client's own edits from the
    /// last few seconds are put back on top, so nothing it just dug is undone.
    /// </summary>
    internal sealed class WorldCheck : CoopModule
    {
        private static float Interval => Dev.DevHarness.CheckEveryOverride > 0f ? Dev.DevHarness.CheckEveryOverride : 30f;
        private float _next;
        private HashSet<int> _lastMismatch = new HashSet<int>();
        private readonly List<KeyValuePair<float, BlockEdit>> _recentLocal = new List<KeyValuePair<float, BlockEdit>>();
        private float _askedAt;
        internal int Checks, Mismatched, Repaired;

        internal override void Attach(NetSession s)
        {
            WorldNet.Register<WorldFingerprint>(s, (o, m) => Compare(m), queueWhilePending: false);
            WorldNet.Register<ChunkResync>(s, OnResync, queueWhilePending: false, relay: false);
            s.BlobReceived += OnBlob;
        }

        internal override void Detach() => Clear();
        internal override void OnWorldLost() => Clear();
        internal override void OnWorldLive() { Clear(); _next = Time.unscaledTime + Interval; }

        private void Clear()
        {
            _lastMismatch.Clear();
            _recentLocal.Clear();
            Checks = Mismatched = Repaired = 0;
        }

        // ------------------------------------------------------------------ fingerprints

        private static int ChunkCols(ushort[,] g) => (g.GetLength(0) + WorldGeneration.CHUNKSIZE - 1) / WorldGeneration.CHUNKSIZE;
        private static int ChunkRows(ushort[,] g) => (g.GetLength(1) + WorldGeneration.CHUNKSIZE - 1) / WorldGeneration.CHUNKSIZE;

        /// <summary>FNV-1a over each chunk's blocks.</summary>
        internal static ulong[] Fingerprint(ushort[,] g)
        {
            int cols = ChunkCols(g), rows = ChunkRows(g), size = WorldGeneration.CHUNKSIZE;
            int width = g.GetLength(0), height = g.GetLength(1);
            var hashes = new ulong[cols * rows];
            for (int cx = 0; cx < cols; cx++)
            for (int cy = 0; cy < rows; cy++)
            {
                ulong h = 14695981039346656037UL;
                int x1 = Mathf.Min(width, (cx + 1) * size), y1 = Mathf.Min(height, (cy + 1) * size);
                for (int x = cx * size; x < x1; x++)
                for (int y = cy * size; y < y1; y++)
                {
                    ushort b = g[x, y];
                    h = (h ^ (byte)b) * 1099511628211UL;
                    h = (h ^ (byte)(b >> 8)) * 1099511628211UL;
                }
                hashes[cx * rows + cy] = h;
            }
            return hashes;
        }

        internal override void Tick()
        {
            if (!Coop.IsHost || !Coop.WorldLive || Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + Interval;
            var g = WorldAccess.Blocks;
            if (g == null || !WorldAccess.Settled || WorldAccess.Regenerating) return;
            WorldNet.Publish(new WorldFingerprint { Epoch = Coop.Epoch, Cols = (ushort)ChunkCols(g), Rows = (ushort)ChunkRows(g), Chunks = Fingerprint(g) });
        }

        private void Compare(WorldFingerprint m)
        {
            if (Coop.IsHost || m.Epoch != Coop.Epoch || !WorldAccess.Settled) return;
            var g = WorldAccess.Blocks;
            if (g == null) return;
            Checks++;
            var mine = Fingerprint(g);
            if (mine.Length != m.Chunks.Length || ChunkRows(g) != m.Rows)
            {
                Log.Warn("[desync] the world's size differs from the host's (" + mine.Length + " vs " + m.Chunks.Length + " chunks)");
                return;
            }
            var now = new HashSet<int>();
            for (int i = 0; i < mine.Length; i++)
                if (mine[i] != m.Chunks[i]) now.Add(i);
            var persistent = new List<ushort>();
            foreach (int i in now)
                if (_lastMismatch.Contains(i)) persistent.Add((ushort)i);
            _lastMismatch = now;
            if (persistent.Count == 0) return;

            Mismatched += persistent.Count;
            Log.Warn("[desync] terrain differs from the host's in " + persistent.Count + " chunk(s) on two checks in a row; asking for them");
            _askedAt = Time.unscaledTime;
            if (persistent.Count > 32) persistent.RemoveRange(32, persistent.Count - 32);
            WorldNet.Publish(new ChunkResync { Epoch = Coop.Epoch, Chunks = persistent });
        }

        // ------------------------------------------------------------------ repair

        private void OnResync(byte origin, ChunkResync m)
        {
            if (!Coop.IsHost || m.Epoch != Coop.Epoch) return;
            var g = WorldAccess.Blocks;
            if (g == null) return;
            int rows = ChunkRows(g), cols = ChunkCols(g), size = WorldGeneration.CHUNKSIZE;
            int width = g.GetLength(0), height = g.GetLength(1);
            var w = new PacketWriter(m.Chunks.Count * size * size * 2 + 16);
            w.WriteUInt16(Coop.Epoch);
            w.WriteUInt16((ushort)m.Chunks.Count);
            foreach (var i in m.Chunks)
            {
                int cx = i / rows, cy = i % rows;
                w.WriteUInt16((ushort)cx);
                w.WriteUInt16((ushort)cy);
                if (cx >= cols) continue;
                int x1 = Mathf.Min(width, (cx + 1) * size), y1 = Mathf.Min(height, (cy + 1) * size);
                for (int x = cx * size; x < x1; x++)
                for (int y = cy * size; y < y1; y++)
                    w.WriteUInt16(g[x, y]);
            }
            Session.SendBlob(origin, CoopProtocol.BlobChunks, w.ToArray());
            Log.Info("[desync] resent " + m.Chunks.Count + " chunk(s) to " + Coop.Player(origin).Name);
        }

        private void OnBlob(byte origin, ushort tag, byte[] data)
        {
            if (tag != CoopProtocol.BlobChunks || Coop.IsHost || origin != NetSession.HostId || !Coop.WorldLive) return;
            var world = WorldGeneration.world;
            var g = WorldAccess.Blocks;
            if (world == null || g == null) return;
            try
            {
                var r = new PacketReader(data, 0, data.Length);
                if (r.ReadUInt16() != Coop.Epoch) return;
                int n = r.ReadUInt16(), size = WorldGeneration.CHUNKSIZE;
                int width = g.GetLength(0), height = g.GetLength(1), cols = ChunkCols(g);
                for (int k = 0; k < n && !r.Failed; k++)
                {
                    int cx = r.ReadUInt16(), cy = r.ReadUInt16();
                    if (cx >= cols) continue;
                    int x1 = Mathf.Min(width, (cx + 1) * size), y1 = Mathf.Min(height, (cy + 1) * size);
                    for (int x = cx * size; x < x1; x++)
                    for (int y = cy * size; y < y1; y++)
                        g[x, y] = r.ReadUInt16();
                    // What this player dug or placed since asking stays theirs.
                    foreach (var e in _recentLocal)
                        if (e.Key >= _askedAt - 1f && e.Value.X / size == cx && e.Value.Y / size == cy) g[e.Value.X, e.Value.Y] = e.Value.Block;
                    try { world.UpdateChunk(new Vector2Int(cx, cy)); }
                    catch (Exception ex) { Log.Debug("chunk rebuild: " + ex.Message); }
                    Repaired++;
                }
                Log.Info("[desync] repaired " + n + " chunk(s) from the host's copy");
            }
            catch (Exception e) { Log.Warn("[desync] could not apply the host's chunks: " + e.Message); }
        }

        /// <summary>From the terrain hook: a block changed here by this player.</summary>
        internal void RecordLocal(int x, int y, ushort block)
        {
            float now = Time.unscaledTime;
            _recentLocal.RemoveAll(e => now - e.Key > 10f);
            if (_recentLocal.Count < 4096) _recentLocal.Add(new KeyValuePair<float, BlockEdit>(now, new BlockEdit { X = (ushort)x, Y = (ushort)y, Block = block }));
        }
    }
}
