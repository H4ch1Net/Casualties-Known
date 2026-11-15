using System;
using System.Collections.Generic;
using CCoop.Net.Wire;

namespace CCoop.Plugin.World
{
    /// <summary>
    /// Everything a client needs to stand in the host's layer: terrain, water, every
    /// world object with its id, structure placements and layer-wide state.
    /// Deflated as a whole: ~2 MB raw becomes a few hundred KB.
    /// </summary>
    internal sealed class LayerSnapshot
    {
        private const uint Magic = 0x4C434343; // "CCCL"
        private const int Version = 6;
        private const int MaxInflated = 48 * 1024 * 1024;

        internal sealed class Entity
        {
            public uint Id;
            public string Key;
            public float X, Y, Rotation, ScaleX = 1f, ScaleY = 1f;
            public bool HasHealth;
            public float Health;
            public byte[] ItemState;

            /// <summary>Rolled per-object state (crystal effects), see EntityState.</summary>
            public byte[] Extra;

            /// <summary>For a structure part: which placement and which part, else -1.</summary>
            public int Placement = -1, Part = -1;
        }

        internal sealed class Structure
        {
            public string Prefab;
            public float X, Y;
        }

        /// <summary>A decoration on a chunk's back wall (wall holes, blast marks): placed at random, so the host's.</summary>
        internal sealed class Decal
        {
            public string Key;
            public float X, Y, Rotation, Pitch = 1f;
        }

        public ushort Epoch;
        public int Depth;
        public int ModifierSlot = -1;   // index into LayerModifier.availableModifiers
        public float Fog;
        public float LayerTime;
        public float SpawnX, SpawnY;
        public float GravityX, GravityY = -55f;
        public int Width, Height;
        public byte[] Terrain = new byte[0];
        public byte[] Fluid = new byte[0];
        public List<Entity> Entities = new List<Entity>();
        public List<Structure> Structures = new List<Structure>();
        public List<Decal> Decals = new List<Decal>();

        /// <summary>The sky the game rolled for the run (colour at the top, rain), the host's for everyone.</summary>
        public float SkyR = -1f, SkyG, SkyB, Rain;

        public byte[] Encode()
        {
            var w = new PacketWriter(Terrain.Length + Fluid.Length + Entities.Count * 32 + 1024);
            w.WriteUInt32(Magic);
            w.WriteInt32(Version);
            w.WriteUInt16(Epoch);
            w.WriteInt32(Depth);
            w.WriteInt32(ModifierSlot);
            w.WriteSingle(Fog);
            w.WriteSingle(LayerTime);
            w.WriteSingle(SpawnX);
            w.WriteSingle(SpawnY);
            w.WriteSingle(GravityX);
            w.WriteSingle(GravityY);
            w.WriteInt32(Width);
            w.WriteInt32(Height);
            w.WriteInt32(Terrain.Length);
            w.WriteRaw(Terrain, 0, Terrain.Length);
            w.WriteInt32(Fluid.Length);
            w.WriteRaw(Fluid, 0, Fluid.Length);

            var keys = new List<string>();
            var keyIndex = new Dictionary<string, int>();
            int Key(string k)
            {
                k = k ?? "";
                if (!keyIndex.TryGetValue(k, out var i)) { i = keys.Count; keys.Add(k); keyIndex[k] = i; }
                return i;
            }
            foreach (var e in Entities) Key(e.Key);
            foreach (var s in Structures) Key(s.Prefab);
            foreach (var d in Decals) Key(d.Key);

            w.WriteInt32(keys.Count);
            foreach (var k in keys) w.WriteString(k);

            w.WriteInt32(Entities.Count);
            foreach (var e in Entities)
            {
                w.WriteUInt32(e.Id);
                w.WriteInt32(keyIndex[e.Key ?? ""]);
                w.WriteSingle(e.X);
                w.WriteSingle(e.Y);
                w.WriteSingle(e.Rotation);
                w.WriteSingle(e.ScaleX);
                w.WriteSingle(e.ScaleY);
                byte flags = (byte)((e.HasHealth ? 1 : 0) | (e.ItemState != null && e.ItemState.Length > 0 ? 2 : 0) |
                                    (e.Extra != null && e.Extra.Length > 0 ? 4 : 0) | (e.Placement >= 0 ? 8 : 0));
                w.WriteByte(flags);
                if (e.HasHealth) w.WriteSingle(e.Health);
                if ((flags & 2) != 0) w.WriteBlob(e.ItemState);
                if ((flags & 4) != 0) w.WriteBlob(e.Extra);
                if ((flags & 8) != 0)
                {
                    w.WriteInt32(e.Placement);
                    w.WriteInt32(e.Part);
                }
            }

            w.WriteInt32(Structures.Count);
            foreach (var s in Structures)
            {
                w.WriteInt32(keyIndex[s.Prefab ?? ""]);
                w.WriteSingle(s.X);
                w.WriteSingle(s.Y);
            }

            w.WriteInt32(Decals.Count);
            foreach (var d in Decals)
            {
                w.WriteInt32(keyIndex[d.Key ?? ""]);
                w.WriteSingle(d.X);
                w.WriteSingle(d.Y);
                w.WriteSingle(d.Rotation);
                w.WriteSingle(d.Pitch);
            }

            w.WriteSingle(SkyR);
            w.WriteSingle(SkyG);
            w.WriteSingle(SkyB);
            w.WriteSingle(Rain);

            return Compression.Deflate(w.Buffer, 0, w.Length);
        }

        public static LayerSnapshot Decode(byte[] compressed)
        {
            var raw = Compression.Inflate(compressed, MaxInflated);
            var r = new PacketReader(raw, 0, raw.Length);
            if (r.ReadUInt32() != Magic) throw new FormatException("not a layer snapshot");
            if (r.ReadInt32() != Version) throw new FormatException("snapshot version mismatch");
            var s = new LayerSnapshot
            {
                Epoch = r.ReadUInt16(),
                Depth = r.ReadInt32(),
                ModifierSlot = r.ReadInt32(),
                Fog = r.ReadSingle(),
                LayerTime = r.ReadSingle(),
                SpawnX = r.ReadSingle(),
                SpawnY = r.ReadSingle(),
                GravityX = r.ReadSingle(),
                GravityY = r.ReadSingle(),
                Width = r.ReadInt32(),
                Height = r.ReadInt32(),
            };
            s.Terrain = ReadSized(ref r);
            s.Fluid = ReadSized(ref r);

            int keyCount = r.ReadInt32();
            if (keyCount < 0 || keyCount > 100000) throw new FormatException("bad key table");
            var keys = new string[keyCount];
            for (int i = 0; i < keyCount; i++) keys[i] = r.ReadString();

            int n = r.ReadInt32();
            if (n < 0 || n > 500000) throw new FormatException("bad entity count");
            for (int i = 0; i < n && !r.Failed; i++)
            {
                var e = new Entity { Id = r.ReadUInt32() };
                int k = r.ReadInt32();
                e.Key = k >= 0 && k < keys.Length ? keys[k] : "";
                e.X = r.ReadSingle();
                e.Y = r.ReadSingle();
                e.Rotation = r.ReadSingle();
                e.ScaleX = r.ReadSingle();
                e.ScaleY = r.ReadSingle();
                byte flags = r.ReadByte();
                e.HasHealth = (flags & 1) != 0;
                if (e.HasHealth) e.Health = r.ReadSingle();
                if ((flags & 2) != 0) e.ItemState = r.ReadBlob();
                if ((flags & 4) != 0) e.Extra = r.ReadBlob();
                if ((flags & 8) != 0)
                {
                    e.Placement = r.ReadInt32();
                    e.Part = r.ReadInt32();
                }
                s.Entities.Add(e);
            }

            int sc = r.ReadInt32();
            if (sc < 0 || sc > 100000) throw new FormatException("bad structure count");
            for (int i = 0; i < sc && !r.Failed; i++)
            {
                int k = r.ReadInt32();
                s.Structures.Add(new Structure { Prefab = k >= 0 && k < keys.Length ? keys[k] : "", X = r.ReadSingle(), Y = r.ReadSingle() });
            }

            int decals = r.ReadInt32();
            if (decals < 0 || decals > 200000) throw new FormatException("bad decal count");
            for (int i = 0; i < decals && !r.Failed; i++)
            {
                int k = r.ReadInt32();
                s.Decals.Add(new Decal { Key = k >= 0 && k < keys.Length ? keys[k] : "", X = r.ReadSingle(), Y = r.ReadSingle(), Rotation = r.ReadSingle(), Pitch = r.ReadSingle() });
            }

            s.SkyR = r.ReadSingle();
            s.SkyG = r.ReadSingle();
            s.SkyB = r.ReadSingle();
            s.Rain = r.ReadSingle();

            if (r.Failed || !r.AtEnd) throw new FormatException("snapshot is truncated or malformed");
            return s;
        }

        private static byte[] ReadSized(ref PacketReader r)
        {
            int n = r.ReadInt32();
            if (n < 0 || n > r.Remaining) { r.Fail(); return new byte[0]; }
            var data = new byte[n];
            if (n > 0) Buffer.BlockCopy(r.ReadRaw(n), 0, data, 0, n);
            return data;
        }
    }
}
