using System;
using System.Collections.Generic;
using CCoop.Protocol;

namespace CCoop.Core
{
    /// <summary>One generated world object, as the client needs to recreate it.</summary>
    public struct EntityRecord
    {
        /// <summary>Index into <see cref="EntityManifest.Keys"/>.</summary>
        public ushort KeyIndex;

        public float X;
        public float Y;

        /// <summary>Z rotation in degrees.</summary>
        public float Rotation;
    }

    /// <summary>
    /// The non-terrain half of a layer: every object world generation instantiated.
    ///
    /// Needed because clients suppress generation to get the host's terrain, which also
    /// suppresses everything else <c>GenerateWorld</c> creates — lifepods, capsules,
    /// crates, plants, traps, corpses. Without this a client stands in correct terrain
    /// in an otherwise empty world.
    ///
    /// Measured on a real layer: ~2,256 objects across ~46 distinct prefabs. A string
    /// table plus 14 bytes per record keeps that near 33 KiB before compression, which
    /// is small beside the ~150 KiB of terrain it travels with.
    /// </summary>
    public sealed class EntityManifest
    {
        /// <summary>Distinct prefab keys, referenced by index.</summary>
        public List<string> Keys = new List<string>();

        public List<EntityRecord> Entities = new List<EntityRecord>();

        /// <summary>Guards against a hostile or corrupt manifest.</summary>
        public const int MaxEntities = 200000;
        public const int MaxKeys = 4096;

        private readonly Dictionary<string, ushort> _keyLookup = new Dictionary<string, ushort>(StringComparer.Ordinal);

        /// <summary>Interns a key and returns its index.</summary>
        public ushort KeyIndexOf(string key)
        {
            if (key == null) key = string.Empty;
            if (_keyLookup.TryGetValue(key, out var existing)) return existing;

            if (Keys.Count >= MaxKeys)
                throw new InvalidOperationException("too many distinct prefab keys");

            var index = (ushort)Keys.Count;
            Keys.Add(key);
            _keyLookup[key] = index;
            return index;
        }

        public void Add(string key, float x, float y, float rotation)
        {
            Entities.Add(new EntityRecord
            {
                KeyIndex = KeyIndexOf(key),
                X = x,
                Y = y,
                Rotation = rotation,
            });
        }

        public string KeyFor(EntityRecord record) =>
            record.KeyIndex < Keys.Count ? Keys[record.KeyIndex] : null;

        // ---------------------------------------------------------------- wire form

        public byte[] Serialize()
        {
            var w = new PacketWriter(Entities.Count * 16 + Keys.Count * 24 + 64);

            w.WriteUInt16((ushort)Keys.Count);
            foreach (var k in Keys) w.WriteString(k);

            w.WriteInt32(Entities.Count);
            foreach (var e in Entities)
            {
                w.WriteUInt16(e.KeyIndex);
                w.WriteSingle(e.X);
                w.WriteSingle(e.Y);
                w.WriteSingle(e.Rotation);
            }

            return w.ToArray();
        }

        public static bool TryDeserialize(byte[] data, out EntityManifest manifest, out string error)
        {
            manifest = null;
            error = null;

            if (data == null) { error = "no data"; return false; }

            var r = new PacketReader(data, 0, data.Length);
            var m = new EntityManifest();

            int keyCount = r.ReadUInt16();
            if (r.Failed) { error = "truncated key count"; return false; }
            if (keyCount > MaxKeys) { error = "too many keys: " + keyCount; return false; }

            for (int i = 0; i < keyCount; i++)
            {
                var key = r.ReadString();
                if (r.Failed) { error = "truncated key " + i; return false; }
                m.Keys.Add(key);
                if (!m._keyLookup.ContainsKey(key)) m._keyLookup[key] = (ushort)i;
            }

            int count = r.ReadInt32();
            if (r.Failed) { error = "truncated entity count"; return false; }
            if (count < 0 || count > MaxEntities) { error = "implausible entity count: " + count; return false; }

            // Each record is a fixed 14 bytes, so the declared count must fit in what is
            // actually present. Checking up front avoids allocating for a count that the
            // payload could never satisfy.
            const int recordSize = 2 + 4 + 4 + 4;
            if ((long)count * recordSize > r.Remaining)
            {
                error = "entity count " + count + " exceeds remaining " + r.Remaining + " bytes";
                return false;
            }

            m.Entities.Capacity = count;
            for (int i = 0; i < count; i++)
            {
                var e = new EntityRecord
                {
                    KeyIndex = r.ReadUInt16(),
                    X = r.ReadSingle(),
                    Y = r.ReadSingle(),
                    Rotation = r.ReadSingle(),
                };
                if (r.Failed) { error = "truncated entity " + i; return false; }

                // A key index outside the table would produce a null prefab lookup later,
                // far from the cause.
                if (e.KeyIndex >= m.Keys.Count)
                {
                    error = "entity " + i + " references key " + e.KeyIndex + " of " + m.Keys.Count;
                    return false;
                }
                m.Entities.Add(e);
            }

            if (!r.FullyConsumed) { error = "trailing bytes in manifest"; return false; }

            manifest = m;
            return true;
        }
    }

    /// <summary>
    /// Bundles terrain and entities into the one transfer, so a client cannot end up
    /// holding a layer's terrain without its contents (or vice versa).
    /// </summary>
    public static class WorldPayload
    {
        /// <summary>
        /// Packs a layer into one payload: terrain, then fluid, then the entity manifest.
        ///
        /// One transfer rather than three so a client can never end up holding a layer's
        /// terrain but not its contents — a world that looks right and is unplayable is
        /// far worse than a transfer that fails outright.
        /// </summary>
        public static byte[] Combine(byte[] terrain, byte[] fluid, byte[] entities)
        {
            if (terrain == null) throw new ArgumentNullException(nameof(terrain));
            if (fluid == null) fluid = new byte[0];
            if (entities == null) entities = new byte[0];

            var combined = new byte[terrain.Length + fluid.Length + entities.Length];
            Buffer.BlockCopy(terrain, 0, combined, 0, terrain.Length);
            Buffer.BlockCopy(fluid, 0, combined, terrain.Length, fluid.Length);
            Buffer.BlockCopy(entities, 0, combined, terrain.Length + fluid.Length, entities.Length);
            return combined;
        }

        public static bool TrySplit(byte[] combined, int terrainLength, int fluidLength,
                                    out byte[] terrain, out byte[] fluid, out byte[] entities,
                                    out string error)
        {
            terrain = null;
            fluid = null;
            entities = null;
            error = null;

            if (combined == null) { error = "no payload"; return false; }

            if (terrainLength < 0 || fluidLength < 0)
            {
                error = "negative section length";
                return false;
            }

            // Checked as a sum, and in long arithmetic: two lengths that each look
            // plausible can still overflow past the payload together.
            long head = (long)terrainLength + fluidLength;
            if (head > combined.Length)
            {
                error = "terrain " + terrainLength + " + fluid " + fluidLength +
                        " exceed payload of " + combined.Length;
                return false;
            }

            terrain = new byte[terrainLength];
            Buffer.BlockCopy(combined, 0, terrain, 0, terrainLength);

            fluid = new byte[fluidLength];
            Buffer.BlockCopy(combined, terrainLength, fluid, 0, fluidLength);

            entities = new byte[combined.Length - (int)head];
            Buffer.BlockCopy(combined, (int)head, entities, 0, entities.Length);
            return true;
        }
    }
}
