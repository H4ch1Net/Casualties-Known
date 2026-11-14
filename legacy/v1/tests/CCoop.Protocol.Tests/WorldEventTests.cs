using CCoop.Protocol;
using CCoop.Protocol.Messages;
using Xunit;

namespace CCoop.Protocol.Tests
{
    public class WorldEventTests
    {
        private static PacketReader PayloadOf(byte[] packet)
        {
            Assert.Equal(DecodeResult.Ok, PacketHeader.TryRead(packet, 0, packet.Length, out _));
            return PacketHeader.PayloadReader(packet, 0, packet.Length);
        }

        [Fact]
        public void An_earthquake_round_trips()
        {
            var sent = new WorldEventMessage
            {
                Kind = WorldEventKind.Earthquake,
                Duration = 17.5f,
            };

            var r = PayloadOf(sent.Encode(0, 1, 1, 1));
            Assert.True(WorldEventMessage.TryDecode(ref r, out var got));
            Assert.Equal(WorldEventKind.Earthquake, got.Kind);
            Assert.Equal(17.5f, got.Duration);
        }

        [Theory]
        [InlineData(0f)]
        [InlineData(-3f)]
        [InlineData(WorldEventMessage.MaxDuration + 1f)]
        public void An_implausible_duration_is_rejected(float duration)
        {
            // The game rolls 3-25 s. A peer asking for hours of shaking is malformed or
            // hostile, and there is nothing a player could do to walk out of it.
            var w = new PacketWriter();
            int lp = PacketHeader.Write(w, MessageType.WorldEvent, 0, 1, 1, 1);
            w.WriteByte((byte)WorldEventKind.Earthquake);
            w.WriteSingle(duration);
            PacketHeader.PatchPayloadLength(w, lp);

            var r = PayloadOf(w.ToArray());
            Assert.False(WorldEventMessage.TryDecode(ref r, out _));
        }

        [Theory]
        [InlineData(0)]     // None
        [InlineData(200)]   // undefined
        public void An_unknown_event_kind_is_rejected(byte kind)
        {
            var w = new PacketWriter();
            int lp = PacketHeader.Write(w, MessageType.WorldEvent, 0, 1, 1, 1);
            w.WriteByte(kind);
            w.WriteSingle(10f);
            PacketHeader.PatchPayloadLength(w, lp);

            var r = PayloadOf(w.ToArray());
            Assert.False(WorldEventMessage.TryDecode(ref r, out _));
        }

        [Fact]
        public void World_events_are_reliable_and_ordered()
        {
            // A dropped start leaves one player shaking alone, which is exactly the bug
            // this message exists to remove.
            Assert.Equal(DeliveryClass.Control, MessageTypes.DeliveryFor(MessageType.WorldEvent));
            Assert.True(MessageTypes.IsDefined((ushort)MessageType.WorldEvent));
        }
    }
}
