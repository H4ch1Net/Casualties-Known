using System;
using System.Collections.Generic;
using CCoop.Net.Session;
using CCoop.Plugin.Protocol;
using CCoop.Plugin.Runtime;
using CCoop.Plugin.World;
using UnityEngine;

namespace CCoop.Plugin.Sync
{
    /// <summary>
    /// Water, lava and the other liquids after a layer has loaded. The game simulates
    /// liquid only within 64 cells of its own camera, so each machine moves the water near
    /// its own player and leaves the rest still: a lake the host never visits would stay
    /// as it loaded on the host while it drains away on a client.
    ///
    /// The layer is split into 16-cell patches. A patch belongs to the nearest living
    /// player whose machine simulates it; that machine sends the patch whenever its
    /// liquid changes (flowing, drunk from, filled by a geyser or anything else that
    /// writes liquid), at most twice a second, and everyone else takes its copy. Where two
    /// players' areas overlap both machines simulate, and the owner's copy wins. Owners
    /// also send each patch the first time it comes into their area, and all of them
    /// again every 15 s, so differences left from loading or from overlaps are corrected.
    /// </summary>
    internal sealed class FluidSync : CoopModule
    {
        private const int Size = 16;
        // FluidManager simulates around the camera: 64 cells each side, 64 below and 48
        // above (its row bands run from y-64 to y+48). A teammate's camera isn't known,
        // only their body, so their area is taken a patch smaller on every side.
        private const int Side = 64, Below = 64, Above = 48;
        private const int Margin = Size;
        private const float Every = 0.5f;
        private const int MaxPerPass = 32;

        private readonly Dictionary<int, byte[]> _known = new Dictionary<int, byte[]>();
        private readonly List<Vector2Int> _others = new List<Vector2Int>();
        private readonly byte[] _scratch = new byte[Size * Size];
        private float _next, _refreshAt;

        /// <summary>Every patch an owner holds is sent again this often, fixing drift where two players' areas overlap.</summary>
        private const float Refresh = 15f;

        internal static int Sent, Applied;

        internal override void Attach(NetSession s) => WorldNet.Register<FluidChunks>(s, OnChunks);
        internal override void Detach() => Clear();
        internal override void OnWorldLost() => Clear();

        private void Clear()
        {
            _known.Clear();
            _others.Clear();
        }

        private static int Key(int cx, int cy) => (cx << 16) | (cy & 0xFFFF);

        private static int Ceil(int cell) => cell <= 0 ? 0 : (cell + Size - 1) / Size;

        internal override void LateTick()
        {
            if (!Coop.WorldLive || !WorldAccess.Settled || WorldAccess.Regenerating || Coop.Players.Count < 2) return;
            float now = Time.unscaledTime;
            if (now < _next) return;
            _next = now + Every;
            if (now >= _refreshAt)
            {
                _refreshAt = now + Refresh;
                _known.Clear();
            }

            var fm = FluidManager.main;
            var w = WorldGeneration.world;
            var body = GameInfo.LocalBody;
            if (fm == null || fm.fluid == null || w == null || body == null || !body.alive) return;
            var grid = fm.fluid;
            int width = grid.GetLength(0), height = grid.GetLength(1);
            var me = w.WorldToBlockPos(body.transform.position);
            var cam = PlayerCamera.main != null ? w.WorldToBlockPos(PlayerCamera.main.transform.position) : me;

            _others.Clear();
            foreach (var rec in Coop.Players.Values)
                if (rec.Id != Coop.LocalId && Coop.InLayer(rec) && rec.Alive)
                    _others.Add(w.WorldToBlockPos(rec.Position));

            // Whole patches inside what this machine simulates.
            int cx0 = Mathf.Max(0, Ceil(cam.x - Side)), cx1 = Mathf.Min((width - 1) / Size, (cam.x + Side) / Size - 1);
            int cy0 = Mathf.Max(0, Ceil(cam.y - Below)), cy1 = Mathf.Min((height - 1) / Size, (cam.y + Above) / Size - 1);
            FluidChunks batch = null;
            for (int cx = cx0; cx <= cx1; cx++)
            for (int cy = cy0; cy <= cy1; cy++)
            {
                var centre = new Vector2Int(cx * Size + Size / 2, cy * Size + Size / 2);
                if (!Mine(me, centre)) continue;
                int n = Read(grid, cx, cy, width, height);
                int key = Key(cx, cy);
                // Unknown (first look, or the slow refresh): sent anyway. A layer's liquid
                // can already differ by the time it is live on every machine, and a
                // patch that never changes would never be corrected otherwise.
                if (_known.TryGetValue(key, out var known) && Same(known, n)) continue;
                _known[key] = Copy(n);
                if (batch == null) batch = new FluidChunks();
                batch.Chunks.Add(new FluidChunk { X = (ushort)cx, Y = (ushort)cy, Cells = Rle(n) });
                if (batch.Chunks.Count >= MaxPerPass) goto send;
            }
            send:
            if (batch == null) return;
            WorldNet.Publish(batch);
            Sent += batch.Chunks.Count;
        }

        /// <summary>This machine owns the patch: no living teammate simulating it is closer.</summary>
        private bool Mine(Vector2Int me, Vector2Int centre)
        {
            float mine = (me - centre).sqrMagnitude;
            int i = 0;
            foreach (var rec in Coop.Players.Values)
            {
                if (rec.Id == Coop.LocalId || !Coop.InLayer(rec) || !rec.Alive) continue;
                var p = _others[i++];
                if (Mathf.Abs(p.x - centre.x) > Side - Margin || centre.y < p.y - Below + Margin || centre.y > p.y + Above - Margin) continue;
                float d = (p - centre).sqrMagnitude;
                if (d < mine || (d == mine && rec.Id < Coop.LocalId)) return false;
            }
            return true;
        }

        private int Read(byte[,] grid, int cx, int cy, int width, int height)
        {
            int n = 0;
            for (int x = cx * Size; x < cx * Size + Size; x++)
            for (int y = cy * Size; y < cy * Size + Size; y++)
                _scratch[n++] = x < width && y < height ? grid[x, y] : (byte)0;
            return n;
        }

        private bool Same(byte[] known, int n)
        {
            if (known.Length != n) return false;
            for (int i = 0; i < n; i++) if (known[i] != _scratch[i]) return false;
            return true;
        }

        private byte[] Copy(int n)
        {
            var b = new byte[n];
            Buffer.BlockCopy(_scratch, 0, b, 0, n);
            return b;
        }

        /// <summary>Runs of (count, liquid): a still pool or dry rock is a couple of bytes.</summary>
        private byte[] Rle(int n)
        {
            var o = new List<byte>(32);
            int i = 0;
            while (i < n)
            {
                byte v = _scratch[i];
                int run = 1;
                while (i + run < n && run < 255 && _scratch[i + run] == v) run++;
                o.Add((byte)run);
                o.Add(v);
                i += run;
            }
            return o.ToArray();
        }

        private void OnChunks(byte origin, FluidChunks m)
        {
            var fm = FluidManager.main;
            if (fm == null || fm.fluid == null) return;
            var grid = fm.fluid;
            int width = grid.GetLength(0), height = grid.GetLength(1);
            foreach (var c in m.Chunks)
            {
                // Expand, then write; a malformed patch is dropped whole.
                int n = 0;
                bool ok = true;
                for (int i = 0; i + 1 < c.Cells.Length && ok; i += 2)
                {
                    int run = c.Cells[i];
                    if (n + run > _scratch.Length) { ok = false; break; }
                    for (int k = 0; k < run; k++) _scratch[n++] = c.Cells[i + 1];
                }
                if (!ok || n != Size * Size) continue;
                int at = 0;
                for (int x = c.X * Size; x < c.X * Size + Size; x++)
                for (int y = c.Y * Size; y < c.Y * Size + Size; y++, at++)
                    if (x < width && y < height) grid[x, y] = _scratch[at];
                _known[Key(c.X, c.Y)] = Copy(n);
                Applied++;
            }
        }
    }
}
