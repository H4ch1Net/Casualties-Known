using System;

namespace CCoop.Protocol.Messages
{
    /// <summary>What a client is asking to do to a world object.</summary>
    public enum InteractionKind : byte
    {
        None = 0,
        /// <summary>Pick a loose world item into an inventory slot.</summary>
        PickUpItem = 1,
        /// <summary>Drop a carried item into the world.</summary>
        DropItem = 2,
        /// <summary>Open a container, which is also when its loot is rolled.</summary>
        OpenContainer = 3,
        /// <summary>
        /// Operate a fixed world prop — a switch or a button — carrying the state it is
        /// now in rather than the fact that it was pressed.
        ///
        /// State rather than event, for the same reason a terrain edit names the resulting
        /// block: it is then idempotent, so a peer that applies it twice (and the host
        /// applies its own broadcasts) converges instead of toggling back off again.
        /// </summary>
        UseProp = 4,
    }

    /// <summary>Why the host refused an interaction.</summary>
    public enum InteractionFailure : byte
    {
        None = 0,
        UnknownEntity = 1,
        /// <summary>Someone else took it first, or it no longer exists.</summary>
        AlreadyTaken = 2,
        OutOfRange = 3,
        InventoryFull = 4,
        /// <summary>The client's view of the entity is stale.</summary>
        RevisionMismatch = 5,
        NotPermitted = 6,
    }

    /// <summary>
    /// Client -> host. An intent, never an assertion.
    ///
    /// Clients ask; the host decides. Anything else lets two players pick up the same
    /// item and duplicate it, which is the classic co-op dupe bug — and with an item
    /// economy this tight, a dupe is a broken run.
    ///
    /// <see cref="ExpectedRevision"/> carries the client's view of the entity so the host
    /// can reject an action based on stale information rather than silently applying it.
    /// </summary>
    public sealed class InteractionRequestMessage
    {
        /// <summary>Correlates the host's answer with this request.</summary>
        public uint RequestId;

        public InteractionKind Kind;

        /// <summary>Network entity id — the entity manifest index, shared by all peers.</summary>
        public uint EntityId;

        /// <summary>Target inventory slot, where the interaction needs one.</summary>
        public byte Slot;

        public uint ExpectedRevision;

        /// <summary>
        /// Item being dropped. Required because the host does not yet simulate remote
        /// players' inventories, so for a drop it has no way to know what the client is
        /// holding — the client must name it. Ignored for pickups, where the entity id
        /// already identifies the item.
        /// </summary>
        public string ItemId = string.Empty;

        /// <summary>
        /// Launch velocity for a thrown item, zero for an ordinary drop.
        ///
        /// Needed because the host creates the world item for a client's drop and
        /// allocates it a host-range id — which makes the HOST its owner for motion sync.
        /// Without this the host's copy is created at rest and then streams that rest back
        /// over the thrower's own copy, so a client's throw was not merely invisible to
        /// others: it was actively cancelled a frame later.
        ///
        /// <c>ThrowItem</c> computes this only after calling <c>DropItem</c>, so it is
        /// recomputed in a prefix instead. <c>ThrowVelocity</c> is pure — aim, jump speed,
        /// strength, weight and body velocity, no randomness — so the answer is the same.
        /// </summary>
        public float VelocityX;
        public float VelocityY;

        /// <summary>
        /// Where the prop being operated stands, for <see cref="InteractionKind.UseProp"/>.
        ///
        /// Props are addressed by position rather than by entity id because a switch built
        /// into a structure's tile decoration is parented to <c>worldGrid</c> — neither a
        /// scene root nor a chunk child — so neither capture sweep ever gave it an id, on
        /// either peer. Position works anyway: structure placement is deterministic and
        /// the client replays it exactly, so both peers agree to the millimetre about
        /// where a lifepod's buttons are.
        /// </summary>
        public float PositionX;
        public float PositionY;

        public byte[] Encode(byte senderPeerId, uint sessionId, uint sequence, uint tick)
        {
            var w = new PacketWriter(128);
            int lp = PacketHeader.Write(w, MessageType.InteractionRequest, senderPeerId,
                                        sessionId, sequence, tick);
            w.WriteUInt32(RequestId);
            w.WriteByte((byte)Kind);
            w.WriteUInt32(EntityId);
            w.WriteByte(Slot);
            w.WriteUInt32(ExpectedRevision);
            w.WriteString(ItemId);
            w.WriteSingle(VelocityX);
            w.WriteSingle(VelocityY);
            w.WriteSingle(PositionX);
            w.WriteSingle(PositionY);
            PacketHeader.PatchPayloadLength(w, lp);
            return w.ToArray();
        }

        public static bool TryDecode(ref PacketReader r, out InteractionRequestMessage msg)
        {
            msg = null;
            var m = new InteractionRequestMessage
            {
                RequestId = r.ReadUInt32(),
                Kind = (InteractionKind)r.ReadByte(),
                EntityId = r.ReadUInt32(),
                Slot = r.ReadByte(),
                ExpectedRevision = r.ReadUInt32(),
                ItemId = r.ReadString(),
                VelocityX = r.ReadSingle(),
                VelocityY = r.ReadSingle(),
                PositionX = r.ReadSingle(),
                PositionY = r.ReadSingle(),
            };
            if (r.Failed || !r.FullyConsumed) return false;
            if (m.Kind == InteractionKind.None || m.Kind > InteractionKind.UseProp) return false;

            // A drop names an item that does not yet exist in the world; without it the
            // host cannot spawn anything.
            if (m.Kind == InteractionKind.DropItem && m.ItemId.Length == 0) return false;

            msg = m;
            return true;
        }
    }

    /// <summary>
    /// Host -> all. The authoritative outcome.
    ///
    /// Broadcast rather than sent only to the requester: every peer must remove a taken
    /// item from its world, not just the player who took it.
    /// </summary>
    public sealed class InteractionResultMessage
    {
        public uint RequestId;

        /// <summary>Who acted. Receivers apply the effect to this peer's player.</summary>
        public byte ActorPeerId;

        public InteractionKind Kind;
        public uint EntityId;
        public byte Slot;
        public bool Granted;
        public InteractionFailure Failure;

        /// <summary>The entity's revision after the change, for later staleness checks.</summary>
        public uint NewRevision;

        /// <summary>Item id when the result spawns or moves a specific item.</summary>
        public string ItemId = string.Empty;

        /// <summary>Where a dropped item lands. The host decides, so all peers agree.</summary>
        public float PositionX;
        public float PositionY;

        /// <summary>Launch velocity, echoed so every peer starts the item the same way.</summary>
        public float VelocityX;
        public float VelocityY;

        public byte[] Encode(uint sessionId, uint sequence, uint tick)
        {
            var w = new PacketWriter(128);
            int lp = PacketHeader.Write(w, MessageType.InteractionResult, ProtocolConstants.HostPeerId,
                                        sessionId, sequence, tick);
            w.WriteUInt32(RequestId);
            w.WriteByte(ActorPeerId);
            w.WriteByte((byte)Kind);
            w.WriteUInt32(EntityId);
            w.WriteByte(Slot);
            w.WriteBool(Granted);
            w.WriteByte((byte)Failure);
            w.WriteUInt32(NewRevision);
            w.WriteString(ItemId);
            w.WriteSingle(PositionX);
            w.WriteSingle(PositionY);
            w.WriteSingle(VelocityX);
            w.WriteSingle(VelocityY);
            PacketHeader.PatchPayloadLength(w, lp);
            return w.ToArray();
        }

        public static bool TryDecode(ref PacketReader r, out InteractionResultMessage msg)
        {
            msg = null;
            var m = new InteractionResultMessage
            {
                RequestId = r.ReadUInt32(),
                ActorPeerId = r.ReadByte(),
                Kind = (InteractionKind)r.ReadByte(),
                EntityId = r.ReadUInt32(),
                Slot = r.ReadByte(),
                Granted = r.ReadBool(),
                Failure = (InteractionFailure)r.ReadByte(),
                NewRevision = r.ReadUInt32(),
                ItemId = r.ReadString(),
                PositionX = r.ReadSingle(),
                PositionY = r.ReadSingle(),
                VelocityX = r.ReadSingle(),
                VelocityY = r.ReadSingle(),
            };
            if (r.Failed || !r.FullyConsumed) return false;
            if (m.ActorPeerId >= ProtocolConstants.MaxPeers) return false;
            if (m.Kind == InteractionKind.None || m.Kind > InteractionKind.UseProp) return false;
            if (m.Failure > InteractionFailure.NotPermitted) return false;

            // A granted result with a failure code (or vice versa) is contradictory, and
            // would leave the receiver guessing which field to believe.
            if (m.Granted && m.Failure != InteractionFailure.None) return false;
            if (!m.Granted && m.Failure == InteractionFailure.None) return false;

            msg = m;
            return true;
        }

        /// <summary>Player-facing explanation of a refusal.</summary>
        public string Describe()
        {
            switch (Failure)
            {
                case InteractionFailure.AlreadyTaken: return "Someone else got there first.";
                case InteractionFailure.OutOfRange: return "Too far away.";
                case InteractionFailure.InventoryFull: return "No room for that.";
                case InteractionFailure.UnknownEntity: return "That is no longer there.";
                case InteractionFailure.RevisionMismatch: return "That changed before you acted.";
                case InteractionFailure.NotPermitted: return "You cannot do that.";
                default: return Granted ? "Done." : "Refused.";
            }
        }
    }
}
