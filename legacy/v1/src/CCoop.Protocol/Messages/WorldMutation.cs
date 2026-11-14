using System;
using System.Collections.Generic;

namespace CCoop.Protocol.Messages
{
    /// <summary>A single terrain cell being set to a block type.</summary>
    public struct BlockEdit
    {
        /// <summary>Block-grid coordinates, not world coordinates.</summary>
        public ushort X;
        public ushort Y;

        /// <summary>The block id the cell now holds; 0 is empty.</summary>
        public ushort Block;

        public BlockEdit(ushort x, ushort y, ushort block)
        {
            X = x;
            Y = y;
            Block = block;
        }
    }

    /// <summary>
    /// A batch of terrain edits from one peer.
    ///
    /// Batched rather than one message per cell because the game routinely edits terrain
    /// in bursts: a single explosion runs <c>GenerateBlockCircle</c> over hundreds of
    /// cells in one frame, and a packet each would be pure overhead.
    ///
    /// Edits carry the resulting block id, not a damage amount, so they are idempotent
    /// and order-independent within a frame. Two players breaking the same wall converge
    /// on the same terrain instead of one of them ending up a hit behind.
    /// </summary>
    public sealed class BlockDeltaMessage
    {
        /// <summary>
        /// Cap per message, chosen so the encoded body stays well inside
        /// <see cref="ProtocolConstants.MaxPayloadBytes"/>: 2 bytes of count plus 6 per
        /// edit. Senders split larger bursts across several messages.
        /// </summary>
        public const int MaxEditsPerMessage = 512;

        public readonly List<BlockEdit> Edits = new List<BlockEdit>();

        public byte[] Encode(byte senderPeerId, uint sessionId, uint sequence, uint tick)
        {
            if (Edits.Count > MaxEditsPerMessage)
                throw new ArgumentException(
                    "block delta batch of " + Edits.Count + " exceeds " + MaxEditsPerMessage);

            var w = new PacketWriter(16 + Edits.Count * 6);
            int lp = PacketHeader.Write(w, MessageType.BlockDelta, senderPeerId,
                                        sessionId, sequence, tick);
            w.WriteUInt16((ushort)Edits.Count);
            for (int i = 0; i < Edits.Count; i++)
            {
                var e = Edits[i];
                w.WriteUInt16(e.X);
                w.WriteUInt16(e.Y);
                w.WriteUInt16(e.Block);
            }
            PacketHeader.PatchPayloadLength(w, lp);
            return w.ToArray();
        }

        public static bool TryDecode(ref PacketReader r, out BlockDeltaMessage msg)
        {
            msg = null;

            int count = r.ReadUInt16();
            if (r.Failed) return false;

            // Bound before allocating: the count is remote-supplied, and the batch cap
            // is also what the sender promised to respect.
            if (count > MaxEditsPerMessage) return false;

            var m = new BlockDeltaMessage();
            for (int i = 0; i < count; i++)
            {
                ushort x = r.ReadUInt16();
                ushort y = r.ReadUInt16();
                ushort block = r.ReadUInt16();
                if (r.Failed) return false;
                m.Edits.Add(new BlockEdit(x, y, block));
            }

            if (r.Failed || !r.FullyConsumed) return false;

            // An empty batch is a wasted packet, and accepting it would let a peer spam
            // the reliable channel for free.
            if (count == 0) return false;

            msg = m;
            return true;
        }
    }

    /// <summary>How damaged one terrain cell currently looks.</summary>
    public struct BlockCrack
    {
        public ushort X;
        public ushort Y;

        /// <summary>
        /// Total accumulated damage on the cell, not the increment. Absolute so it is
        /// idempotent and self-correcting: a dropped or duplicated hit leaves the crack
        /// at the right depth anyway.
        /// </summary>
        public float Damage;

        public BlockCrack(ushort x, ushort y, float damage)
        {
            X = x;
            Y = y;
            Damage = damage;
        }
    }

    /// <summary>
    /// Cosmetic block damage — the crack overlay and the hit sound.
    ///
    /// Terrain sync already carries the *result* of mining, so both peers agree on which
    /// blocks exist. What it does not carry is the swinging: on the other screen a wall
    /// simply disappears, with no cracks deepening and nothing audible. This exists only
    /// to put that feedback back.
    ///
    /// Deliberately separate from <see cref="BlockDeltaMessage"/> and never applied
    /// through the game's <c>DamageBlock</c>: running that on the receiver would break the
    /// block a second time and roll a second set of loot.
    /// </summary>
    public sealed class BlockHitMessage
    {
        public const int MaxCracksPerMessage = 256;

        public readonly List<BlockCrack> Cracks = new List<BlockCrack>();

        public byte[] Encode(byte senderPeerId, uint sessionId, uint sequence, uint tick)
        {
            if (Cracks.Count > MaxCracksPerMessage)
                throw new ArgumentException(
                    "crack batch of " + Cracks.Count + " exceeds " + MaxCracksPerMessage);

            var w = new PacketWriter(16 + Cracks.Count * 8);
            int lp = PacketHeader.Write(w, MessageType.BlockHit, senderPeerId,
                                        sessionId, sequence, tick);
            w.WriteUInt16((ushort)Cracks.Count);
            for (int i = 0; i < Cracks.Count; i++)
            {
                w.WriteUInt16(Cracks[i].X);
                w.WriteUInt16(Cracks[i].Y);
                w.WriteSingle(Cracks[i].Damage);
            }
            PacketHeader.PatchPayloadLength(w, lp);
            return w.ToArray();
        }

        public static bool TryDecode(ref PacketReader r, out BlockHitMessage msg)
        {
            msg = null;

            int count = r.ReadUInt16();
            if (r.Failed) return false;
            if (count == 0 || count > MaxCracksPerMessage) return false;

            var m = new BlockHitMessage();
            for (int i = 0; i < count; i++)
            {
                ushort x = r.ReadUInt16();
                ushort y = r.ReadUInt16();
                float damage = r.ReadSingle();
                if (r.Failed) return false;
                if (damage <= 0f) return false;   // an undamaged cell has nothing to draw

                m.Cracks.Add(new BlockCrack(x, y, damage));
            }

            if (r.Failed || !r.FullyConsumed) return false;

            msg = m;
            return true;
        }
    }

    /// <summary>Damage one peer dealt to a world object.</summary>
    public struct EntityDamage
    {
        public uint EntityId;

        /// <summary>Health to subtract. Always positive; healing is not a thing here.</summary>
        public float Amount;

        public EntityDamage(uint entityId, float amount)
        {
            EntityId = entityId;
            Amount = amount;
        }
    }

    /// <summary>
    /// Damage dealt to world objects, batched per frame.
    ///
    /// Each peer only damages its own copy of a crate — the game applies
    /// <c>health -=</c> locally and nothing carries it further — so two players chopping
    /// the same crate together each have to fell it alone. Worse, if they split the work
    /// neither may manage it: 60 damage from one player and 50 from the other leaves both
    /// copies standing.
    ///
    /// Sent as an *amount* rather than a resulting health so that blows accumulate.
    /// Absolute values would race: the two peers would each report what they saw and the
    /// last message to arrive would erase the other player's work.
    /// </summary>
    public sealed class EntityDamageMessage
    {
        /// <summary>
        /// Cap per message. Well above any real frame — an explosion damages tens of
        /// objects, not hundreds — and keeps the body inside one packet at 8 bytes each.
        /// </summary>
        public const int MaxDamagesPerMessage = 256;

        public readonly List<EntityDamage> Damages = new List<EntityDamage>();

        public byte[] Encode(byte senderPeerId, uint sessionId, uint sequence, uint tick)
        {
            if (Damages.Count > MaxDamagesPerMessage)
                throw new ArgumentException(
                    "damage batch of " + Damages.Count + " exceeds " + MaxDamagesPerMessage);

            var w = new PacketWriter(16 + Damages.Count * 8);
            int lp = PacketHeader.Write(w, MessageType.EntityStateDelta, senderPeerId,
                                        sessionId, sequence, tick);
            w.WriteUInt16((ushort)Damages.Count);
            for (int i = 0; i < Damages.Count; i++)
            {
                w.WriteUInt32(Damages[i].EntityId);
                w.WriteSingle(Damages[i].Amount);
            }
            PacketHeader.PatchPayloadLength(w, lp);
            return w.ToArray();
        }

        public static bool TryDecode(ref PacketReader r, out EntityDamageMessage msg)
        {
            msg = null;

            int count = r.ReadUInt16();
            if (r.Failed) return false;
            if (count == 0 || count > MaxDamagesPerMessage) return false;

            var m = new EntityDamageMessage();
            for (int i = 0; i < count; i++)
            {
                uint id = r.ReadUInt32();
                float amount = r.ReadSingle();
                if (r.Failed) return false;

                // Negative or zero damage would either heal an object or waste a packet,
                // and neither is something this peer should be able to ask for.
                if (amount <= 0f) return false;

                m.Damages.Add(new EntityDamage(id, amount));
            }

            if (r.Failed || !r.FullyConsumed) return false;

            msg = m;
            return true;
        }
    }

    /// <summary>
    /// A world object one peer destroyed and every other peer must destroy too.
    ///
    /// Entity health is not replicated — each peer damages only its own copy — so a crate
    /// chopped down by one player still stands for the other until it is told. The peer
    /// whose copy died first owns the destruction, and its loot; whoever receives this
    /// kills their copy without rolling any loot of their own.
    /// </summary>
    public sealed class EntityDespawnMessage
    {
        public uint EntityId;

        public byte[] Encode(byte senderPeerId, uint sessionId, uint sequence, uint tick)
        {
            var w = new PacketWriter(32);
            int lp = PacketHeader.Write(w, MessageType.EntityDespawn, senderPeerId,
                                        sessionId, sequence, tick);
            w.WriteUInt32(EntityId);
            PacketHeader.PatchPayloadLength(w, lp);
            return w.ToArray();
        }

        public static bool TryDecode(ref PacketReader r, out EntityDespawnMessage msg)
        {
            msg = null;
            var m = new EntityDespawnMessage { EntityId = r.ReadUInt32() };
            if (r.Failed || !r.FullyConsumed) return false;
            msg = m;
            return true;
        }
    }

    /// <summary>
    /// A world item one peer created and every other peer must create too.
    ///
    /// Needed because the game's drop paths roll dice — <c>DamageBlock</c> spawns scrap
    /// or glass on a <c>Random.value</c> check, and <c>BuildingEntity</c> rolls chance,
    /// condition, rotation and velocity per drop. Nothing about that is reproducible
    /// from the same inputs, so the outcome has to travel rather than be recomputed.
    ///
    /// The spawning peer allocates the id from its own reserved range (see
    /// <c>NetEntityRegistry</c>), so no round trip is needed before the item exists.
    /// </summary>
    public sealed class EntitySpawnMessage
    {
        /// <summary>Network entity id, allocated by the spawning peer.</summary>
        public uint EntityId;

        /// <summary>Prefab / item id, as consumed by <c>Utils.Create</c>.</summary>
        public string ItemId = string.Empty;

        public float PositionX;
        public float PositionY;
        public float Rotation;

        /// <summary>Item condition, 0..1. Rolled by the spawner; never re-rolled.</summary>
        public float Condition;

        /// <summary>
        /// Launch velocity. Drops are thrown outwards with a random impulse, so without
        /// it the item lands in a visibly different place on each peer.
        /// </summary>
        public float VelocityX;
        public float VelocityY;

        /// <summary>
        /// Whether this drop floats — the game's <c>FreshItemDrop</c>, which outlines the
        /// item, gives it particles and lets it be grabbed before it settles.
        ///
        /// Decided by the spawner and obeyed by everyone, NOT re-decided per peer, even
        /// though the game's own rule is proximity to the local player. The first attempt
        /// did re-decide it, reasoning that this was presentation like an earthquake's
        /// shake. It is not: <c>FreshItemDrop.Awake</c> sets <c>gravityScale = 0</c>, so a
        /// peer that declines the float runs different physics from the one that took it.
        /// Observed in play exactly as that predicts — the far player's copy was held in
        /// place by motion sync while its own gravity pulled it down, so it jittered, with
        /// no outline and no particles.
        /// </summary>
        public bool Floats;

        public byte[] Encode(byte senderPeerId, uint sessionId, uint sequence, uint tick)
        {
            var w = new PacketWriter(128);
            int lp = PacketHeader.Write(w, MessageType.EntitySpawn, senderPeerId,
                                        sessionId, sequence, tick);
            w.WriteUInt32(EntityId);
            w.WriteString(ItemId);
            w.WriteSingle(PositionX);
            w.WriteSingle(PositionY);
            w.WriteSingle(Rotation);
            w.WriteSingle(Condition);
            w.WriteSingle(VelocityX);
            w.WriteSingle(VelocityY);
            w.WriteByte((byte)(Floats ? 1 : 0));
            PacketHeader.PatchPayloadLength(w, lp);
            return w.ToArray();
        }

        public static bool TryDecode(ref PacketReader r, out EntitySpawnMessage msg)
        {
            msg = null;
            var m = new EntitySpawnMessage
            {
                EntityId = r.ReadUInt32(),
                ItemId = r.ReadString(),
                PositionX = r.ReadSingle(),
                PositionY = r.ReadSingle(),
                Rotation = r.ReadSingle(),
                Condition = r.ReadSingle(),
                VelocityX = r.ReadSingle(),
                VelocityY = r.ReadSingle(),
            };
            m.Floats = r.ReadByte() != 0;
            if (r.Failed || !r.FullyConsumed) return false;

            // Without an id there is nothing to instantiate, and the receiver would
            // silently do nothing rather than report a broken spawn.
            if (m.ItemId.Length == 0) return false;

            msg = m;
            return true;
        }
    }

    /// <summary>Where one loose item is and how it is moving.</summary>
    public struct EntityMotion
    {
        public uint EntityId;

        public float X;
        public float Y;

        /// <summary>Z rotation in degrees.</summary>
        public float Rotation;

        public float VelocityX;
        public float VelocityY;

        /// <summary>Bytes on the wire per record.</summary>
        public const int Size = 4 + 4 + 4 + 4 + 4 + 4;
    }

    /// <summary>
    /// Where the sender's loose items are.
    ///
    /// A spawn says where an item appeared and with what impulse, and nothing ever said
    /// anything about it again — so from that moment each peer ran its own physics. Two
    /// simulations given the same start do not stay together: they meet different
    /// contacts in a different order and settle in different places. Reported in play
    /// three separate ways before it was recognised as one fault — a thrown item that
    /// merely dropped on the other screen, loot that scattered differently, and an item
    /// that rolled downhill for one player while the other still saw it at their feet.
    ///
    /// Ownership needs no negotiation: runtime ids are allocated from a per-peer block,
    /// so the id of an item already names the peer entitled to speak for it.
    ///
    /// <see cref="AtRest"/> splits this into two quite different messages sharing a
    /// shape. While an item is moving its position is a stream — the newest is the only
    /// one worth having and a lost packet costs nothing, so it goes unreliably. The
    /// moment it stops is the opposite: that single message is what makes the two worlds
    /// agree about where the item finally ended up, and losing it leaves them disagreeing
    /// indefinitely, so it goes reliably.
    /// </summary>
    public sealed class EntityMotionMessage
    {
        /// <summary>
        /// Cap per message, sized so the encoded body stays well inside
        /// <see cref="ProtocolConstants.MaxPayloadBytes"/>: 3 bytes of header fields plus
        /// 24 per record. Senders split larger sets across several messages.
        /// </summary>
        public const int MaxMotionsPerMessage = 128;

        /// <summary>
        /// True when these items have come to rest and this is their final position.
        /// Receivers snap rather than smooth, and the sender stops talking about them.
        /// </summary>
        public bool AtRest;

        public readonly List<EntityMotion> Motions = new List<EntityMotion>();

        public byte[] Encode(byte senderPeerId, uint sessionId, uint sequence, uint tick)
        {
            if (Motions.Count > MaxMotionsPerMessage)
                throw new ArgumentException(
                    "motion batch of " + Motions.Count + " exceeds " + MaxMotionsPerMessage);

            var w = new PacketWriter(16 + Motions.Count * EntityMotion.Size);
            int lp = PacketHeader.Write(w, MessageType.EntityMotion, senderPeerId,
                                        sessionId, sequence, tick);
            w.WriteByte((byte)(AtRest ? 1 : 0));
            w.WriteUInt16((ushort)Motions.Count);
            for (int i = 0; i < Motions.Count; i++)
            {
                var m = Motions[i];
                w.WriteUInt32(m.EntityId);
                w.WriteSingle(m.X);
                w.WriteSingle(m.Y);
                w.WriteSingle(m.Rotation);
                w.WriteSingle(m.VelocityX);
                w.WriteSingle(m.VelocityY);
            }
            PacketHeader.PatchPayloadLength(w, lp);
            return w.ToArray();
        }

        public static bool TryDecode(ref PacketReader r, out EntityMotionMessage msg)
        {
            msg = null;

            byte rest = r.ReadByte();
            int count = r.ReadUInt16();
            if (r.Failed) return false;

            // Bound before allocating: the count is remote-supplied, and the batch cap is
            // also what the sender promised to respect.
            if (count > MaxMotionsPerMessage) return false;
            if ((long)count * EntityMotion.Size > r.Remaining) return false;

            var m = new EntityMotionMessage { AtRest = rest != 0 };
            for (int i = 0; i < count; i++)
            {
                m.Motions.Add(new EntityMotion
                {
                    EntityId = r.ReadUInt32(),
                    X = r.ReadSingle(),
                    Y = r.ReadSingle(),
                    Rotation = r.ReadSingle(),
                    VelocityX = r.ReadSingle(),
                    VelocityY = r.ReadSingle(),
                });
                if (r.Failed) return false;
            }

            if (!r.FullyConsumed) return false;

            msg = m;
            return true;
        }
    }
}
