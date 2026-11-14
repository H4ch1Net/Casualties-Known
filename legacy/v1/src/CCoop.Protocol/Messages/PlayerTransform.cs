using System;

namespace CCoop.Protocol.Messages
{
    /// <summary>
    /// One limb's pose, expressed in the body root's local space.
    ///
    /// Local rather than world coordinates so that flipping the body root's
    /// <c>localScale.x</c> to change facing mirrors the whole pose for free, instead of
    /// requiring every limb to be mirrored individually on the receiving side.
    /// </summary>
    public struct LimbPose
    {
        public float LocalX;
        public float LocalY;

        /// <summary>Local Z rotation in degrees.</summary>
        public float Rotation;
    }

    /// <summary>
    /// Owner -> host -> other clients. A player's position and pose.
    ///
    /// Sent unreliable-sequenced: a dropped frame is replaced by a newer one a moment
    /// later, so retransmitting stale movement would only add latency. The sequence
    /// number lets receivers discard out-of-order arrivals.
    ///
    /// Pose is carried as local position AND rotation per limb.
    ///
    /// Rotation alone is not enough: the 15 limbs are flat *siblings* under the body
    /// root, held together by <c>HingeJoint2D</c> rather than nested in a skeleton, so
    /// a limb's position does not follow from its parent's rotation. Sending rotation
    /// only produced a character that floated with limbs spinning in place at their
    /// rest offsets. Local (not world) values are used so the body root's
    /// <c>localScale.x</c> facing flip mirrors the pose automatically.
    /// </summary>
    public sealed class PlayerTransformMessage
    {
        /// <summary>Matches <c>Body.limbs.Length</c> on this build.</summary>
        public const int ExpectedLimbCount = 15;

        /// <summary>Hard cap so a hostile peer cannot force a large allocation.</summary>
        public const int MaxLimbCount = 32;

        public float PositionX;
        public float PositionY;
        public float VelocityX;
        public float VelocityY;

        /// <summary>True when the character faces right.</summary>
        public bool FacingRight;

        /// <summary>Locomotion/state bits (grounded, crouching, ragdolled, ...).</summary>
        public byte StateFlags;

        /// <summary>Per-limb pose in the body root's local space.</summary>
        public LimbPose[] Limbs = new LimbPose[0];

        public byte[] Encode(byte senderPeerId, uint sessionId, uint sequence, uint tick)
        {
            if (Limbs == null) Limbs = new LimbPose[0];
            if (Limbs.Length > MaxLimbCount)
                throw new ArgumentException("too many limbs: " + Limbs.Length);

            var w = new PacketWriter(256);
            int lp = PacketHeader.Write(w, MessageType.PlayerTransform, senderPeerId,
                                        sessionId, sequence, tick);
            w.WriteSingle(PositionX);
            w.WriteSingle(PositionY);
            w.WriteSingle(VelocityX);
            w.WriteSingle(VelocityY);
            w.WriteBool(FacingRight);
            w.WriteByte(StateFlags);
            w.WriteByte((byte)Limbs.Length);
            for (int i = 0; i < Limbs.Length; i++)
            {
                w.WriteSingle(Limbs[i].LocalX);
                w.WriteSingle(Limbs[i].LocalY);
                w.WriteSingle(Limbs[i].Rotation);
            }

            PacketHeader.PatchPayloadLength(w, lp);
            return w.ToArray();
        }

        public static bool TryDecode(ref PacketReader r, out PlayerTransformMessage msg)
        {
            msg = null;

            var m = new PlayerTransformMessage
            {
                PositionX = r.ReadSingle(),
                PositionY = r.ReadSingle(),
                VelocityX = r.ReadSingle(),
                VelocityY = r.ReadSingle(),
                FacingRight = r.ReadBool(),
                StateFlags = r.ReadByte(),
            };
            if (r.Failed) return false;

            byte limbCount = r.ReadByte();
            if (r.Failed) return false;

            // Bound before allocating: the count came off the wire.
            if (limbCount > MaxLimbCount) { r.Invalidate(); return false; }

            var limbs = new LimbPose[limbCount];
            for (int i = 0; i < limbCount; i++)
            {
                // ReadSingle rejects NaN/Infinity, which would otherwise propagate
                // straight into transform assignments.
                limbs[i].LocalX = r.ReadSingle();
                limbs[i].LocalY = r.ReadSingle();
                limbs[i].Rotation = r.ReadSingle();
            }

            if (r.Failed || !r.FullyConsumed) return false;

            m.Limbs = limbs;
            msg = m;
            return true;
        }
    }

    /// <summary>Host -> all. A remote player appeared and needs a visual proxy.</summary>
    public sealed class PlayerSpawnMessage
    {
        public byte PeerId;
        public string PlayerName = string.Empty;
        public float PositionX;
        public float PositionY;

        public byte[] Encode(uint sessionId, uint sequence, uint tick)
        {
            var w = new PacketWriter(128);
            int lp = PacketHeader.Write(w, MessageType.PlayerSpawn, ProtocolConstants.HostPeerId,
                                        sessionId, sequence, tick);
            w.WriteByte(PeerId);
            w.WriteString(PlayerName);
            w.WriteSingle(PositionX);
            w.WriteSingle(PositionY);
            PacketHeader.PatchPayloadLength(w, lp);
            return w.ToArray();
        }

        public static bool TryDecode(ref PacketReader r, out PlayerSpawnMessage msg)
        {
            msg = null;
            var m = new PlayerSpawnMessage
            {
                PeerId = r.ReadByte(),
                PlayerName = r.ReadString(),
                PositionX = r.ReadSingle(),
                PositionY = r.ReadSingle(),
            };
            if (r.Failed || !r.FullyConsumed) return false;
            if (m.PeerId >= ProtocolConstants.MaxPeers) return false;
            msg = m;
            return true;
        }
    }

    /// <summary>Host -> all. A remote player left; destroy its proxy.</summary>
    public sealed class PlayerDespawnMessage
    {
        public byte PeerId;

        public byte[] Encode(uint sessionId, uint sequence, uint tick)
        {
            var w = new PacketWriter(64);
            int lp = PacketHeader.Write(w, MessageType.PlayerDespawn, ProtocolConstants.HostPeerId,
                                        sessionId, sequence, tick);
            w.WriteByte(PeerId);
            PacketHeader.PatchPayloadLength(w, lp);
            return w.ToArray();
        }

        public static bool TryDecode(ref PacketReader r, out PlayerDespawnMessage msg)
        {
            msg = null;
            var m = new PlayerDespawnMessage { PeerId = r.ReadByte() };
            if (r.Failed || !r.FullyConsumed) return false;
            if (m.PeerId >= ProtocolConstants.MaxPeers) return false;
            msg = m;
            return true;
        }
    }
}
