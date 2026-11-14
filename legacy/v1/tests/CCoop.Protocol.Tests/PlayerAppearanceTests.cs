using System;
using CCoop.Protocol;
using CCoop.Protocol.Messages;
using Xunit;

namespace CCoop.Protocol.Tests
{
    public class PlayerAppearanceTests
    {
        private static PacketReader PayloadOf(byte[] packet)
        {
            Assert.Equal(DecodeResult.Ok, PacketHeader.TryRead(packet, 0, packet.Length, out _));
            return PacketHeader.PayloadReader(packet, 0, packet.Length);
        }

        [Fact]
        public void Appearance_round_trips()
        {
            var sent = new PlayerAppearanceMessage { Wetness = 40, Snow = 0, Dirt = 200 };
            sent.Limbs.Add(new LimbAppearance
            {
                SkinDamage = 128, MuscleDamage = 64, Infection = 10, Pain = 90, Blood = 255,
            });
            sent.Limbs.Add(new LimbAppearance());

            var r = PayloadOf(sent.Encode(1, 1, 1, 1));
            Assert.True(PlayerAppearanceMessage.TryDecode(ref r, out var got));

            Assert.Equal(sent.Wetness, got.Wetness);
            Assert.Equal(sent.Snow, got.Snow);
            Assert.Equal(sent.Dirt, got.Dirt);
            Assert.Equal(2, got.Limbs.Count);
            Assert.Equal(128, got.Limbs[0].SkinDamage);
            Assert.Equal(64, got.Limbs[0].MuscleDamage);
            Assert.Equal(10, got.Limbs[0].Infection);
            Assert.Equal(90, got.Limbs[0].Pain);
            Assert.Equal(255, got.Limbs[0].Blood);
            Assert.Equal(0, got.Limbs[1].SkinDamage);
        }

        [Fact]
        public void A_body_with_no_limbs_still_carries_its_dirt()
        {
            // Dirt, wetness and snow belong to the whole body, so they must survive a
            // message that happens to describe no limbs.
            var sent = new PlayerAppearanceMessage { Dirt = 77 };

            var r = PayloadOf(sent.Encode(1, 1, 1, 1));
            Assert.True(PlayerAppearanceMessage.TryDecode(ref r, out var got));
            Assert.Empty(got.Limbs);
            Assert.Equal(77, got.Dirt);
        }

        [Theory]
        [InlineData(0f, 0)]
        [InlineData(1f, 255)]
        [InlineData(0.5f, 128)]
        public void Quantisation_keeps_the_ends_exact(float unit, byte expected)
        {
            // The ends matter more than the middle: an undamaged limb must read as
            // undamaged rather than as very slightly hurt.
            Assert.Equal(expected, PlayerAppearanceMessage.ToByte(unit));
        }

        [Fact]
        public void Quantisation_clamps_rather_than_wrapping()
        {
            // Shader values are read back out of a live material, so an unexpected one
            // must saturate rather than wrap round to "clean".
            Assert.Equal(0, PlayerAppearanceMessage.ToByte(-5f));
            Assert.Equal(255, PlayerAppearanceMessage.ToByte(12f));
            Assert.Equal(0, PlayerAppearanceMessage.ToByte(float.NaN));
        }

        [Fact]
        public void A_round_trip_through_a_byte_stays_close()
        {
            for (float v = 0f; v <= 1f; v += 0.05f)
            {
                float back = PlayerAppearanceMessage.ToUnit(PlayerAppearanceMessage.ToByte(v));
                Assert.True(Math.Abs(back - v) < 0.01f, v + " came back as " + back);
            }
        }

        [Fact]
        public void More_limbs_than_the_cap_is_refused_by_the_sender()
        {
            var sent = new PlayerAppearanceMessage();
            for (int i = 0; i <= PlayerAppearanceMessage.MaxLimbs; i++)
                sent.Limbs.Add(new LimbAppearance());

            Assert.Throws<ArgumentException>(() => sent.Encode(1, 1, 1, 1));
        }

        [Fact]
        public void Claiming_more_limbs_than_it_carries_is_rejected()
        {
            var w = new PacketWriter();
            int lp = PacketHeader.Write(w, MessageType.PlayerAppearance, 1, 1, 1, 1);
            w.WriteByte(0); w.WriteByte(0); w.WriteByte(0);
            w.WriteByte(20);          // claims 20 limbs
            w.WriteByte(1);           // supplies part of one
            PacketHeader.PatchPayloadLength(w, lp);

            var r = PayloadOf(w.ToArray());
            Assert.False(PlayerAppearanceMessage.TryDecode(ref r, out _));
        }

        [Fact]
        public void Appearance_streams_on_the_snapshot_channel()
        {
            // Cosmetic and repeated twice a second: a lost update is corrected by the
            // next one, so it must not compete with reliable traffic.
            Assert.Equal(DeliveryClass.Snapshots,
                         MessageTypes.DeliveryFor(MessageType.PlayerAppearance));
        }
    }
}
