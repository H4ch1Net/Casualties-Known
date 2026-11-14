using System;
using CCoop.Protocol;
using CCoop.Protocol.Messages;
using Xunit;

namespace CCoop.Protocol.Tests
{
    public class InteractionTests
    {
        private static PacketReader PayloadOf(byte[] packet)
        {
            Assert.Equal(DecodeResult.Ok, PacketHeader.TryRead(packet, 0, packet.Length, out _));
            return PacketHeader.PayloadReader(packet, 0, packet.Length);
        }

        [Fact]
        public void Request_round_trips()
        {
            var sent = new InteractionRequestMessage
            {
                RequestId = 1234,
                Kind = InteractionKind.PickUpItem,
                EntityId = 987654,
                Slot = 3,
                ExpectedRevision = 7,
            };

            var r = PayloadOf(sent.Encode(2, 1, 1, 1));
            Assert.True(InteractionRequestMessage.TryDecode(ref r, out var got));

            Assert.Equal(sent.RequestId, got.RequestId);
            Assert.Equal(sent.Kind, got.Kind);
            Assert.Equal(sent.EntityId, got.EntityId);
            Assert.Equal(sent.Slot, got.Slot);
            Assert.Equal(sent.ExpectedRevision, got.ExpectedRevision);
        }

        [Fact]
        public void A_throw_carries_its_launch_velocity_both_ways()
        {
            // The host creates the world item for a client's drop and owns it from then
            // on, so the velocity has to reach the host in the request and come back in
            // the answer. Without it the host's copy sits still and then streams that
            // stillness back over the thrower's own copy, cancelling the throw.
            var request = new InteractionRequestMessage
            {
                RequestId = 9,
                Kind = InteractionKind.DropItem,
                ItemId = "crowbar",
                VelocityX = -14.25f,
                VelocityY = 7.5f,
            };

            var r = PayloadOf(request.Encode(2, 1, 1, 1));
            Assert.True(InteractionRequestMessage.TryDecode(ref r, out var gotRequest));
            Assert.Equal(request.VelocityX, gotRequest.VelocityX);
            Assert.Equal(request.VelocityY, gotRequest.VelocityY);

            var result = new InteractionResultMessage
            {
                RequestId = 9,
                ActorPeerId = 1,
                Kind = InteractionKind.DropItem,
                EntityId = 0x0100_0003,
                Granted = true,
                ItemId = "crowbar",
                PositionX = 4f,
                PositionY = 5f,
                VelocityX = -14.25f,
                VelocityY = 7.5f,
            };

            var r2 = PayloadOf(result.Encode(1, 1, 1));
            Assert.True(InteractionResultMessage.TryDecode(ref r2, out var gotResult));
            Assert.Equal(result.VelocityX, gotResult.VelocityX);
            Assert.Equal(result.VelocityY, gotResult.VelocityY);
        }

        [Fact]
        public void An_ordinary_drop_carries_no_velocity()
        {
            var sent = new InteractionRequestMessage
            {
                RequestId = 4,
                Kind = InteractionKind.DropItem,
                ItemId = "stick",
            };

            var r = PayloadOf(sent.Encode(2, 1, 1, 1));
            Assert.True(InteractionRequestMessage.TryDecode(ref r, out var got));
            Assert.Equal(0f, got.VelocityX);
            Assert.Equal(0f, got.VelocityY);
        }

        [Fact]
        public void Prop_use_carries_the_resulting_state_not_the_press()
        {
            // The state travels so the message is idempotent: the host applies its own
            // broadcasts, and a peer converging on a state twice is a no-op where a peer
            // replaying a press twice would toggle it back off.
            var request = new InteractionRequestMessage
            {
                RequestId = 11,
                Kind = InteractionKind.UseProp,
                EntityId = 4231,
                Slot = 2,
            };

            var r = PayloadOf(request.Encode(1, 1, 1, 1));
            Assert.True(InteractionRequestMessage.TryDecode(ref r, out var got));
            Assert.Equal(InteractionKind.UseProp, got.Kind);
            Assert.Equal(4231u, got.EntityId);
            Assert.Equal(2, got.Slot);

            var result = new InteractionResultMessage
            {
                RequestId = 11,
                ActorPeerId = 1,
                Kind = InteractionKind.UseProp,
                EntityId = 4231,
                Slot = 2,
                Granted = true,
            };

            var r2 = PayloadOf(result.Encode(1, 1, 1));
            Assert.True(InteractionResultMessage.TryDecode(ref r2, out var got2));
            Assert.Equal(InteractionKind.UseProp, got2.Kind);
            Assert.Equal(2, got2.Slot);
        }

        [Fact]
        public void A_prop_use_needs_no_item_id()
        {
            // Drops are refused without one; a switch has no item to name.
            var sent = new InteractionRequestMessage
            {
                RequestId = 12,
                Kind = InteractionKind.UseProp,
                EntityId = 99,
                Slot = 255,
            };

            var r = PayloadOf(sent.Encode(1, 1, 1, 1));
            Assert.True(InteractionRequestMessage.TryDecode(ref r, out var got));
            Assert.Equal(255, got.Slot);
        }

        [Fact]
        public void Request_with_an_undefined_kind_is_rejected()
        {
            var w = new PacketWriter();
            int lp = PacketHeader.Write(w, MessageType.InteractionRequest, 1, 1, 1, 1);
            w.WriteUInt32(1);
            w.WriteByte(200);        // not a defined InteractionKind
            w.WriteUInt32(1);
            w.WriteByte(0);
            w.WriteUInt32(0);
            PacketHeader.PatchPayloadLength(w, lp);

            var r = PayloadOf(w.ToArray());
            Assert.False(InteractionRequestMessage.TryDecode(ref r, out _));
        }

        [Fact]
        public void Request_with_kind_none_is_rejected()
        {
            var sent = new InteractionRequestMessage { Kind = InteractionKind.None, EntityId = 1 };
            var r = PayloadOf(sent.Encode(1, 1, 1, 1));
            Assert.False(InteractionRequestMessage.TryDecode(ref r, out _));
        }

        [Fact]
        public void Granted_result_round_trips()
        {
            var sent = new InteractionResultMessage
            {
                RequestId = 42,
                ActorPeerId = 2,
                Kind = InteractionKind.PickUpItem,
                EntityId = 555,
                Slot = 1,
                Granted = true,
                Failure = InteractionFailure.None,
                NewRevision = 9,
                ItemId = "bandage",
            };

            var r = PayloadOf(sent.Encode(1, 1, 1));
            Assert.True(InteractionResultMessage.TryDecode(ref r, out var got));

            Assert.True(got.Granted);
            Assert.Equal("bandage", got.ItemId);
            Assert.Equal(9u, got.NewRevision);
            Assert.Equal(2, got.ActorPeerId);
        }

        [Theory]
        [InlineData(InteractionFailure.AlreadyTaken)]
        [InlineData(InteractionFailure.OutOfRange)]
        [InlineData(InteractionFailure.InventoryFull)]
        [InlineData(InteractionFailure.UnknownEntity)]
        [InlineData(InteractionFailure.RevisionMismatch)]
        [InlineData(InteractionFailure.NotPermitted)]
        public void Refusals_round_trip_and_explain_themselves(InteractionFailure failure)
        {
            var sent = new InteractionResultMessage
            {
                RequestId = 1,
                ActorPeerId = 1,
                Kind = InteractionKind.PickUpItem,
                EntityId = 1,
                Granted = false,
                Failure = failure,
            };

            var r = PayloadOf(sent.Encode(1, 1, 1));
            Assert.True(InteractionResultMessage.TryDecode(ref r, out var got));
            Assert.Equal(failure, got.Failure);

            // Must be presentable to a player, not an enum name.
            var text = got.Describe();
            Assert.False(string.IsNullOrWhiteSpace(text));
            Assert.DoesNotContain(failure.ToString(), text);
        }

        [Fact]
        public void Granted_result_carrying_a_failure_code_is_rejected()
        {
            // Contradictory: the receiver would have to guess which field to believe.
            var w = new PacketWriter();
            int lp = PacketHeader.Write(w, MessageType.InteractionResult, 0, 1, 1, 1);
            w.WriteUInt32(1); w.WriteByte(1); w.WriteByte((byte)InteractionKind.PickUpItem);
            w.WriteUInt32(1); w.WriteByte(0);
            w.WriteBool(true);                                   // granted...
            w.WriteByte((byte)InteractionFailure.AlreadyTaken);  // ...yet failed
            w.WriteUInt32(0); w.WriteString("");
            PacketHeader.PatchPayloadLength(w, lp);

            var r = PayloadOf(w.ToArray());
            Assert.False(InteractionResultMessage.TryDecode(ref r, out _));
        }

        [Fact]
        public void Refused_result_without_a_reason_is_rejected()
        {
            var w = new PacketWriter();
            int lp = PacketHeader.Write(w, MessageType.InteractionResult, 0, 1, 1, 1);
            w.WriteUInt32(1); w.WriteByte(1); w.WriteByte((byte)InteractionKind.PickUpItem);
            w.WriteUInt32(1); w.WriteByte(0);
            w.WriteBool(false);                          // refused...
            w.WriteByte((byte)InteractionFailure.None);  // ...with no reason
            w.WriteUInt32(0); w.WriteString("");
            PacketHeader.PatchPayloadLength(w, lp);

            var r = PayloadOf(w.ToArray());
            Assert.False(InteractionResultMessage.TryDecode(ref r, out _));
        }

        [Fact]
        public void Result_with_an_out_of_range_actor_is_rejected()
        {
            var w = new PacketWriter();
            int lp = PacketHeader.Write(w, MessageType.InteractionResult, 0, 1, 1, 1);
            w.WriteUInt32(1);
            w.WriteByte(ProtocolConstants.MaxPeers); // beyond the peer table
            w.WriteByte((byte)InteractionKind.PickUpItem);
            w.WriteUInt32(1); w.WriteByte(0);
            w.WriteBool(true); w.WriteByte(0);
            w.WriteUInt32(0); w.WriteString("");
            PacketHeader.PatchPayloadLength(w, lp);

            var r = PayloadOf(w.ToArray());
            Assert.False(InteractionResultMessage.TryDecode(ref r, out _));
        }

        [Fact]
        public void Interaction_messages_use_the_transaction_channel()
        {
            // Reliable ordered: a lost pickup that silently vanished would either dupe
            // the item or lose it, depending on which side dropped it.
            Assert.Equal(DeliveryClass.Transactions,
                MessageTypes.DeliveryFor(MessageType.InteractionRequest));
            Assert.Equal(DeliveryClass.Transactions,
                MessageTypes.DeliveryFor(MessageType.InteractionResult));
        }
    }
}
