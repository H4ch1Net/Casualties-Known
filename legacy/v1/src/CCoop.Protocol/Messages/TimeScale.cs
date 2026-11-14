namespace CCoop.Protocol.Messages
{
    /// <summary>
    /// The game's speed settings, mirroring <c>PlayerCamera.SpeedType</c>.
    ///
    /// Duplicated here rather than referenced because the protocol assembly must build
    /// with no game install. The values must stay in step with the game's enum — the
    /// assembly hash in the handshake is what catches a build where they no longer do.
    /// </summary>
    public enum SessionSpeed : byte
    {
        Normal = 0,
        Fast = 1,
        SuperFast = 2,
        /// <summary>25x, driven by a body passing out. Never shared.</summary>
        UnconsciousFast = 3,
        /// <summary>3.5x, driven by a body dying. Never shared.</summary>
        DyingFast = 4,
        /// <summary>0.16x, a local reading effect. Never shared.</summary>
        Slowmo = 5,
        /// <summary>0x. Stays local so one player's menu cannot freeze another's game.</summary>
        Paused = 6,
    }

    /// <summary>
    /// How fast the session runs.
    ///
    /// From a client this is a *request*; from the host it is the answer, and every peer
    /// applies it. Host-arbitrated rather than peer-to-peer because there is only one
    /// <c>Time.timeScale</c> per process and no way to merge two opinions about it — two
    /// peers each broadcasting their own would flip the whole session back and forth.
    ///
    /// Only the three deliberate speeds travel. The automatic ones belong to one body or
    /// one player's screen: sharing <c>UnconsciousFast</c> would run everybody's world at
    /// 25x because one player passed out, and sharing <c>Paused</c> would let anyone
    /// freeze the game for everyone by opening a menu.
    /// </summary>
    public sealed class TimeScaleMessage
    {
        public SessionSpeed Speed;

        /// <summary>True when a speed is shareable at all.</summary>
        public static bool IsShareable(SessionSpeed speed) =>
            speed == SessionSpeed.Normal ||
            speed == SessionSpeed.Fast ||
            speed == SessionSpeed.SuperFast;

        public byte[] Encode(byte senderPeerId, uint sessionId, uint sequence, uint tick)
        {
            var w = new PacketWriter(32);
            int lp = PacketHeader.Write(w, MessageType.TimeScale, senderPeerId,
                                        sessionId, sequence, tick);
            w.WriteByte((byte)Speed);
            PacketHeader.PatchPayloadLength(w, lp);
            return w.ToArray();
        }

        public static bool TryDecode(ref PacketReader r, out TimeScaleMessage msg)
        {
            msg = null;
            var m = new TimeScaleMessage { Speed = (SessionSpeed)r.ReadByte() };
            if (r.Failed || !r.FullyConsumed) return false;

            // Refuse anything that is not shareable rather than let a peer set the
            // session to a speed the design says belongs to one screen.
            if (!IsShareable(m.Speed)) return false;

            msg = m;
            return true;
        }
    }
}
