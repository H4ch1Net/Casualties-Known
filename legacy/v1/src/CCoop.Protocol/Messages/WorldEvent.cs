namespace CCoop.Protocol.Messages
{
    /// <summary>Layer-wide events the host schedules on everyone's behalf.</summary>
    public enum WorldEventKind : byte
    {
        None = 0,

        /// <summary>
        /// The ground shakes for <see cref="WorldEventMessage.Duration"/> seconds.
        /// </summary>
        Earthquake = 1,
    }

    /// <summary>
    /// Host -> all. Something is happening to the whole layer.
    ///
    /// Only the *schedule* travels, not the consequences. An earthquake shakes the
    /// camera, throws the body around and tears blocks out around whoever is standing
    /// there — all of which each peer already does correctly for its own player, and the
    /// blocks it tears out already reach the others as terrain edits. The single thing
    /// the peers cannot agree on by themselves is *when*, because the game rolls both the
    /// interval and the duration from the unseeded global RNG.
    ///
    /// So a peer left to itself gets its own earthquakes at its own times, and the other
    /// player watches holes open around them during a quake they are not in.
    /// </summary>
    public sealed class WorldEventMessage
    {
        public WorldEventKind Kind;

        /// <summary>Seconds the event lasts. Must be positive and plausible.</summary>
        public float Duration;

        /// <summary>
        /// Ceiling on a remote-supplied duration. The game rolls 3–25 s; anything past a
        /// couple of minutes is a malformed or hostile peer trying to shake a player
        /// permanently, which they cannot walk out of.
        /// </summary>
        public const float MaxDuration = 120f;

        public byte[] Encode(byte senderPeerId, uint sessionId, uint sequence, uint tick)
        {
            var w = new PacketWriter(32);
            int lp = PacketHeader.Write(w, MessageType.WorldEvent, senderPeerId,
                                        sessionId, sequence, tick);
            w.WriteByte((byte)Kind);
            w.WriteSingle(Duration);
            PacketHeader.PatchPayloadLength(w, lp);
            return w.ToArray();
        }

        public static bool TryDecode(ref PacketReader r, out WorldEventMessage msg)
        {
            msg = null;
            var m = new WorldEventMessage
            {
                Kind = (WorldEventKind)r.ReadByte(),
                Duration = r.ReadSingle(),
            };
            if (r.Failed || !r.FullyConsumed) return false;

            if (m.Kind != WorldEventKind.Earthquake) return false;
            if (m.Duration <= 0f || m.Duration > MaxDuration) return false;

            msg = m;
            return true;
        }
    }
}
