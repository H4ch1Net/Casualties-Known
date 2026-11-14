using CCoop.Protocol;
using CCoop.Protocol.Messages;
using Xunit;

namespace CCoop.Protocol.Tests
{
    public class PlayerAttackTests
    {
        private static PacketReader PayloadOf(byte[] packet)
        {
            Assert.Equal(DecodeResult.Ok, PacketHeader.TryRead(packet, 0, packet.Length, out _));
            return PacketHeader.PayloadReader(packet, 0, packet.Length);
        }

        [Fact]
        public void A_swing_round_trips()
        {
            var sent = new PlayerAttackMessage
            {
                Anim = "SwingAnim",
                Rotation = -37.25f,
                FacingRight = false,
                Sound = "BSSwing3",
            };

            var r = PayloadOf(sent.Encode(2, 1, 1, 1));
            Assert.True(PlayerAttackMessage.TryDecode(ref r, out var got));

            Assert.Equal("SwingAnim", got.Anim);
            Assert.Equal(-37.25f, got.Rotation);
            Assert.False(got.FacingRight);
            Assert.Equal("BSSwing3", got.Sound);
        }

        [Fact]
        public void A_silent_swing_round_trips()
        {
            // Not every attack has a sound, and an empty one must not be mistaken for a
            // truncated message.
            var sent = new PlayerAttackMessage { Anim = "ClawAnim", Rotation = 0f };

            var r = PayloadOf(sent.Encode(1, 1, 1, 1));
            Assert.True(PlayerAttackMessage.TryDecode(ref r, out var got));
            Assert.Equal("ClawAnim", got.Anim);
            Assert.Equal(string.Empty, got.Sound);
        }

        [Fact]
        public void A_swing_with_no_animation_is_rejected()
        {
            var sent = new PlayerAttackMessage { Anim = "", Rotation = 10f };
            var r = PayloadOf(sent.Encode(1, 1, 1, 1));
            Assert.False(PlayerAttackMessage.TryDecode(ref r, out _));
        }

        [Fact]
        public void A_swing_with_a_non_finite_rotation_is_rejected()
        {
            var w = new PacketWriter();
            int lp = PacketHeader.Write(w, MessageType.PlayerAttack, 1, 1, 1, 1);
            w.WriteString("SwingAnim");
            w.WriteSingle(float.NaN);
            w.WriteBool(true);
            w.WriteString("");
            PacketHeader.PatchPayloadLength(w, lp);

            var r = PayloadOf(w.ToArray());
            Assert.False(PlayerAttackMessage.TryDecode(ref r, out _));
        }

        [Fact]
        public void Swings_are_reliable_and_ordered()
        {
            // Low frequency — one per attack cooldown — and a dropped one is feedback the
            // other player never gets, so it is not worth making unreliable.
            Assert.Equal(DeliveryClass.Transactions,
                MessageTypes.DeliveryFor(MessageType.PlayerAttack));
            Assert.True(MessageTypes.IsDefined((ushort)MessageType.PlayerAttack));
        }
    }
}
