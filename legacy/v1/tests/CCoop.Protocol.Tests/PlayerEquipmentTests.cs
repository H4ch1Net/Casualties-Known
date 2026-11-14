using System;
using CCoop.Protocol;
using CCoop.Protocol.Messages;
using Xunit;

namespace CCoop.Protocol.Tests
{
    public class PlayerEquipmentTests
    {
        private static PacketReader PayloadOf(byte[] packet)
        {
            Assert.Equal(DecodeResult.Ok, PacketHeader.TryRead(packet, 0, packet.Length, out _));
            return PacketHeader.PayloadReader(packet, 0, packet.Length);
        }

        [Fact]
        public void Equipment_round_trips()
        {
            var sent = new PlayerEquipmentMessage();
            sent.Items.Add(new EquippedItem { Slot = 0, ItemId = "pickaxe" });
            sent.Items.Add(new EquippedItem { Slot = 4, ItemId = "backpack" });

            var r = PayloadOf(sent.Encode(1, 1, 1, 1));
            Assert.True(PlayerEquipmentMessage.TryDecode(ref r, out var got));

            Assert.Equal(2, got.Items.Count);
            Assert.Equal(0, got.Items[0].Slot);
            Assert.Equal("pickaxe", got.Items[0].ItemId);
            Assert.Equal(4, got.Items[1].Slot);
            Assert.Equal("backpack", got.Items[1].ItemId);
        }

        [Fact]
        public void Empty_hands_are_an_empty_message_not_a_blank_slot()
        {
            // A receiver clears every slot the message does not mention, so carrying
            // nothing has to survive the wire as a message with no items in it.
            var sent = new PlayerEquipmentMessage();

            var r = PayloadOf(sent.Encode(1, 1, 1, 1));
            Assert.True(PlayerEquipmentMessage.TryDecode(ref r, out var got));
            Assert.Empty(got.Items);
        }

        [Fact]
        public void A_named_slot_with_no_item_is_rejected()
        {
            // Omission already means "empty", so a blank id is a malformed message
            // rather than a second way of saying the same thing.
            var w = new PacketWriter();
            int lp = PacketHeader.Write(w, MessageType.PlayerEquipment, 1, 1, 1, 1);
            w.WriteByte(1);
            w.WriteByte(2);
            w.WriteString("");
            PacketHeader.PatchPayloadLength(w, lp);

            var r = PayloadOf(w.ToArray());
            Assert.False(PlayerEquipmentMessage.TryDecode(ref r, out _));
        }

        [Fact]
        public void Claiming_more_items_than_it_carries_is_rejected()
        {
            var w = new PacketWriter();
            int lp = PacketHeader.Write(w, MessageType.PlayerEquipment, 1, 1, 1, 1);
            w.WriteByte(9);           // claims nine
            w.WriteByte(0);
            w.WriteString("rock");    // supplies one
            PacketHeader.PatchPayloadLength(w, lp);

            var r = PayloadOf(w.ToArray());
            Assert.False(PlayerEquipmentMessage.TryDecode(ref r, out _));
        }

        [Fact]
        public void More_items_than_the_cap_is_refused_by_the_sender()
        {
            var sent = new PlayerEquipmentMessage();
            for (int i = 0; i <= PlayerEquipmentMessage.MaxItems; i++)
                sent.Items.Add(new EquippedItem { Slot = (byte)i, ItemId = "rock" });

            Assert.Throws<ArgumentException>(() => sent.Encode(1, 1, 1, 1));
        }

        [Fact]
        public void Equipment_streams_on_the_snapshot_channel()
        {
            // Repeated twice a second and self-correcting, so a lost one costs nothing
            // and it must not compete with reliable traffic.
            Assert.Equal(DeliveryClass.Snapshots,
                         MessageTypes.DeliveryFor(MessageType.PlayerEquipment));
        }
    }
}
