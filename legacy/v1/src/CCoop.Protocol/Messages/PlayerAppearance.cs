using System;
using System.Collections.Generic;

namespace CCoop.Protocol.Messages
{
    /// <summary>How battered one limb looks.</summary>
    public struct LimbAppearance
    {
        /// <summary>Cuts and grazes. <c>_SkinDamage</c>, scaled from its 0..100 range.</summary>
        public byte SkinDamage;

        /// <summary>Deeper wounds. <c>_MuscleDamage</c>, scaled from its 0..100 range.</summary>
        public byte MuscleDamage;

        /// <summary><c>_InfectionPercent</c>, already 0..1.</summary>
        public byte Infection;

        /// <summary><c>_Pain</c>, already 0..1.</summary>
        public byte Pain;

        /// <summary>Blood on fur. <c>_BloodOverlay</c>, scaled from its 0..10 range.</summary>
        public byte Blood;

        /// <summary>Bytes on the wire per limb.</summary>
        public const int Size = 5;
    }

    /// <summary>
    /// Owner -> host -> other clients. What a player's body looks like.
    ///
    /// Wounds, dirt and blood are drawn by a shader, and every value it reads is written
    /// by <c>Limb.Update</c> into a material the limb makes for itself in <c>Awake</c>.
    /// None of it was ever replicated: a remote player's body stayed exactly as it looked
    /// the moment the proxy was cloned.
    ///
    /// For a while that was masked by a bug rather than fixed — proxies inherited the
    /// LOCAL player's materials, so a remote character did change, but it changed to show
    /// *your* injuries. Giving proxies their own materials corrected the wrong picture and
    /// left no picture at all, which is what this message supplies.
    ///
    /// Shader values travel, not the health figures behind them. The receiver then writes
    /// exactly what the sender's game computed, with no arithmetic of its own and no copy
    /// of the game's formulas to drift out of date.
    ///
    /// Three of the eight values the shader takes are properties of the whole body rather
    /// than of a limb — wetness, snow and dirt — so they are sent once instead of thirteen
    /// times.
    ///
    /// Quantised to a byte each and sent a couple of times a second. Appearance changes on
    /// the timescale of taking a wound or wading through a puddle, and a byte is finer
    /// than the eye can read off a shader at this resolution.
    /// </summary>
    public sealed class PlayerAppearanceMessage
    {
        /// <summary>
        /// Generous ceiling on limbs. The build has thirteen; this only has to stop a
        /// hostile count sizing an allocation.
        /// </summary>
        public const int MaxLimbs = 64;

        /// <summary>Divisor taking <c>_SkinDamage</c> and <c>_MuscleDamage</c> to 0..1.</summary>
        public const float DamageScale = 100f;

        /// <summary>Divisor taking <c>_BloodOverlay</c> to 0..1.</summary>
        public const float BloodScale = 10f;

        /// <summary><c>_Wetness</c>, 0..1.</summary>
        public byte Wetness;

        /// <summary><c>_SnowAmount</c>, 0..1.</summary>
        public byte Snow;

        /// <summary><c>_Dirtyness</c>, 0..1.</summary>
        public byte Dirt;

        public readonly List<LimbAppearance> Limbs = new List<LimbAppearance>();

        /// <summary>Quantises a 0..1 value to a byte.</summary>
        public static byte ToByte(float unit)
        {
            if (float.IsNaN(unit)) return 0;
            if (unit <= 0f) return 0;
            if (unit >= 1f) return 255;
            return (byte)(unit * 255f + 0.5f);
        }

        /// <summary>The inverse of <see cref="ToByte"/>.</summary>
        public static float ToUnit(byte value) => value / 255f;

        public byte[] Encode(byte senderPeerId, uint sessionId, uint sequence, uint tick)
        {
            if (Limbs.Count > MaxLimbs)
                throw new ArgumentException(
                    "appearance of " + Limbs.Count + " limbs exceeds " + MaxLimbs);

            var w = new PacketWriter(16 + Limbs.Count * LimbAppearance.Size);
            int lp = PacketHeader.Write(w, MessageType.PlayerAppearance, senderPeerId,
                                        sessionId, sequence, tick);
            w.WriteByte(Wetness);
            w.WriteByte(Snow);
            w.WriteByte(Dirt);
            w.WriteByte((byte)Limbs.Count);
            for (int i = 0; i < Limbs.Count; i++)
            {
                var l = Limbs[i];
                w.WriteByte(l.SkinDamage);
                w.WriteByte(l.MuscleDamage);
                w.WriteByte(l.Infection);
                w.WriteByte(l.Pain);
                w.WriteByte(l.Blood);
            }
            PacketHeader.PatchPayloadLength(w, lp);
            return w.ToArray();
        }

        public static bool TryDecode(ref PacketReader r, out PlayerAppearanceMessage msg)
        {
            msg = null;

            var m = new PlayerAppearanceMessage
            {
                Wetness = r.ReadByte(),
                Snow = r.ReadByte(),
                Dirt = r.ReadByte(),
            };
            int count = r.ReadByte();
            if (r.Failed) return false;

            // Bound before allocating: the count is remote-supplied.
            if (count > MaxLimbs) return false;
            if ((long)count * LimbAppearance.Size > r.Remaining) return false;

            for (int i = 0; i < count; i++)
            {
                m.Limbs.Add(new LimbAppearance
                {
                    SkinDamage = r.ReadByte(),
                    MuscleDamage = r.ReadByte(),
                    Infection = r.ReadByte(),
                    Pain = r.ReadByte(),
                    Blood = r.ReadByte(),
                });
                if (r.Failed) return false;
            }

            if (!r.FullyConsumed) return false;

            msg = m;
            return true;
        }
    }
}
