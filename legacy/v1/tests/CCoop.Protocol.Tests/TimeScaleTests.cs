using CCoop.Protocol;
using CCoop.Protocol.Messages;
using Xunit;

namespace CCoop.Protocol.Tests
{
    public class TimeScaleTests
    {
        private static PacketReader PayloadOf(byte[] packet)
        {
            Assert.Equal(DecodeResult.Ok, PacketHeader.TryRead(packet, 0, packet.Length, out _));
            return PacketHeader.PayloadReader(packet, 0, packet.Length);
        }

        [Theory]
        [InlineData(SessionSpeed.Normal)]
        [InlineData(SessionSpeed.Fast)]
        [InlineData(SessionSpeed.SuperFast)]
        public void A_shareable_speed_round_trips(SessionSpeed speed)
        {
            var sent = new TimeScaleMessage { Speed = speed };

            var r = PayloadOf(sent.Encode(1, 1, 1, 1));
            Assert.True(TimeScaleMessage.TryDecode(ref r, out var got));
            Assert.Equal(speed, got.Speed);
        }

        [Theory]
        [InlineData(SessionSpeed.UnconsciousFast)]
        [InlineData(SessionSpeed.DyingFast)]
        [InlineData(SessionSpeed.Slowmo)]
        [InlineData(SessionSpeed.Paused)]
        public void Speeds_that_belong_to_one_screen_are_rejected(SessionSpeed speed)
        {
            // Sharing UnconsciousFast would run everybody's world at 25x because one
            // player passed out; sharing Paused would let anyone freeze the session by
            // opening a menu. Neither is a decision a peer gets to make for the others.
            var w = new PacketWriter();
            int lp = PacketHeader.Write(w, MessageType.TimeScale, 1, 1, 1, 1);
            w.WriteByte((byte)speed);
            PacketHeader.PatchPayloadLength(w, lp);

            var r = PayloadOf(w.ToArray());
            Assert.False(TimeScaleMessage.TryDecode(ref r, out _));
        }

        [Fact]
        public void An_undefined_speed_is_rejected()
        {
            var w = new PacketWriter();
            int lp = PacketHeader.Write(w, MessageType.TimeScale, 1, 1, 1, 1);
            w.WriteByte(200);
            PacketHeader.PatchPayloadLength(w, lp);

            var r = PayloadOf(w.ToArray());
            Assert.False(TimeScaleMessage.TryDecode(ref r, out _));
        }

        [Fact]
        public void A_message_with_trailing_bytes_is_rejected()
        {
            var w = new PacketWriter();
            int lp = PacketHeader.Write(w, MessageType.TimeScale, 1, 1, 1, 1);
            w.WriteByte((byte)SessionSpeed.Fast);
            w.WriteByte(0);
            PacketHeader.PatchPayloadLength(w, lp);

            var r = PayloadOf(w.ToArray());
            Assert.False(TimeScaleMessage.TryDecode(ref r, out _));
        }

        [Fact]
        public void The_speed_values_still_match_the_games_enum()
        {
            // SessionSpeed is a copy of PlayerCamera.SpeedType, kept here so the protocol
            // builds with no game install. If the game ever reorders its enum these stop
            // meaning the same thing, and the plugin casts straight between them.
            Assert.Equal(0, (byte)SessionSpeed.Normal);
            Assert.Equal(1, (byte)SessionSpeed.Fast);
            Assert.Equal(2, (byte)SessionSpeed.SuperFast);
            Assert.Equal(3, (byte)SessionSpeed.UnconsciousFast);
            Assert.Equal(4, (byte)SessionSpeed.DyingFast);
            Assert.Equal(5, (byte)SessionSpeed.Slowmo);
            Assert.Equal(6, (byte)SessionSpeed.Paused);
        }

        [Fact]
        public void Time_scale_is_reliable_and_ordered()
        {
            // Two changes arriving out of order would leave the session stuck at the
            // wrong speed with nothing to correct it.
            Assert.Equal(DeliveryClass.Control, MessageTypes.DeliveryFor(MessageType.TimeScale));
            Assert.True(MessageTypes.IsDefined((ushort)MessageType.TimeScale));
        }
    }
}
