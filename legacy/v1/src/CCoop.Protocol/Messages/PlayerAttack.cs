namespace CCoop.Protocol.Messages
{
    /// <summary>
    /// Owner -> host -> other clients. Somebody swung.
    ///
    /// Purely presentational: the damage an attack does already reaches the others as
    /// terrain edits and entity damage. What did not was the swing itself —
    /// <c>Body.Attack</c> instantiates <c>AttackInfo.attackAnim</c> locally, so on every
    /// other screen a player's arm moved and nothing happened.
    ///
    /// The *result* travels, not the aim it came from. The attacker has already worked
    /// out which way the arc points, so sending the finished rotation avoids the receiver
    /// re-deriving it from an aim vector that would have to be transmitted too.
    ///
    /// No position: the receiver anchors the arc to its own proxy's torso, which is
    /// interpolated and therefore where that player *appears* to be. A transmitted
    /// position would be a step ahead of the body it belongs to.
    /// </summary>
    public sealed class PlayerAttackMessage
    {
        /// <summary>Prefab to instantiate — <c>SwingAnim</c>, <c>ClawAnim</c>.</summary>
        public string Anim = string.Empty;

        /// <summary>Z rotation of the arc, in degrees, as the attacker computed it.</summary>
        public float Rotation;

        /// <summary>Attacker's facing, which mirrors the arc.</summary>
        public bool FacingRight;

        /// <summary>The swing sound the attacker rolled, or empty for silence.</summary>
        public string Sound = string.Empty;

        public byte[] Encode(byte senderPeerId, uint sessionId, uint sequence, uint tick)
        {
            var w = new PacketWriter(128);
            int lp = PacketHeader.Write(w, MessageType.PlayerAttack, senderPeerId,
                                        sessionId, sequence, tick);
            w.WriteString(Anim);
            w.WriteSingle(Rotation);
            w.WriteBool(FacingRight);
            w.WriteString(Sound);
            PacketHeader.PatchPayloadLength(w, lp);
            return w.ToArray();
        }

        public static bool TryDecode(ref PacketReader r, out PlayerAttackMessage msg)
        {
            msg = null;
            var m = new PlayerAttackMessage
            {
                Anim = r.ReadString(),
                Rotation = r.ReadSingle(),
                FacingRight = r.ReadBool(),
                Sound = r.ReadString(),
            };
            if (r.Failed || !r.FullyConsumed) return false;

            // Without a prefab there is nothing to show, so the message is a wasted packet.
            if (m.Anim.Length == 0) return false;

            msg = m;
            return true;
        }
    }
}
