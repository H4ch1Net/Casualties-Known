using System;
using CCoop.Protocol;
using CCoop.Protocol.Messages;
using Xunit;

namespace CCoop.Protocol.Tests
{
    public class WorldMutationTests
    {
        private static PacketReader PayloadOf(byte[] packet)
        {
            Assert.Equal(DecodeResult.Ok, PacketHeader.TryRead(packet, 0, packet.Length, out _));
            return PacketHeader.PayloadReader(packet, 0, packet.Length);
        }

        // ------------------------------------------------------------ block deltas

        [Fact]
        public void Block_delta_batch_round_trips()
        {
            var sent = new BlockDeltaMessage();
            sent.Edits.Add(new BlockEdit(0, 0, 0));
            sent.Edits.Add(new BlockEdit(1023, 1023, 35));
            sent.Edits.Add(new BlockEdit(512, 7, 6));

            var r = PayloadOf(sent.Encode(2, 1, 1, 1));
            Assert.True(BlockDeltaMessage.TryDecode(ref r, out var got));

            Assert.Equal(sent.Edits.Count, got.Edits.Count);
            for (int i = 0; i < sent.Edits.Count; i++)
            {
                Assert.Equal(sent.Edits[i].X, got.Edits[i].X);
                Assert.Equal(sent.Edits[i].Y, got.Edits[i].Y);
                Assert.Equal(sent.Edits[i].Block, got.Edits[i].Block);
            }
        }

        [Fact]
        public void A_full_batch_still_fits_in_one_packet()
        {
            // The cap exists to keep a burst inside MaxPayloadBytes; if that stops being
            // true the encoder throws at send time, in the middle of play.
            var sent = new BlockDeltaMessage();
            for (int i = 0; i < BlockDeltaMessage.MaxEditsPerMessage; i++)
                sent.Edits.Add(new BlockEdit((ushort)i, (ushort)i, 3));

            var packet = sent.Encode(1, 1, 1, 1);
            Assert.True(packet.Length <= ProtocolConstants.MaxPacketBytes);

            var r = PayloadOf(packet);
            Assert.True(BlockDeltaMessage.TryDecode(ref r, out var got));
            Assert.Equal(BlockDeltaMessage.MaxEditsPerMessage, got.Edits.Count);
        }

        [Fact]
        public void An_empty_batch_is_rejected()
        {
            var w = new PacketWriter();
            int lp = PacketHeader.Write(w, MessageType.BlockDelta, 1, 1, 1, 1);
            w.WriteUInt16(0);
            PacketHeader.PatchPayloadLength(w, lp);

            var r = PayloadOf(w.ToArray());
            Assert.False(BlockDeltaMessage.TryDecode(ref r, out _));
        }

        [Fact]
        public void A_batch_claiming_more_edits_than_it_carries_is_rejected()
        {
            // The classic hostile packet: a large count with no data behind it.
            var w = new PacketWriter();
            int lp = PacketHeader.Write(w, MessageType.BlockDelta, 1, 1, 1, 1);
            w.WriteUInt16(400);
            w.WriteUInt16(1); w.WriteUInt16(2); w.WriteUInt16(3);   // only one edit
            PacketHeader.PatchPayloadLength(w, lp);

            var r = PayloadOf(w.ToArray());
            Assert.False(BlockDeltaMessage.TryDecode(ref r, out _));
        }

        [Fact]
        public void A_batch_over_the_cap_is_rejected()
        {
            var w = new PacketWriter();
            int lp = PacketHeader.Write(w, MessageType.BlockDelta, 1, 1, 1, 1);
            w.WriteUInt16((ushort)(BlockDeltaMessage.MaxEditsPerMessage + 1));
            PacketHeader.PatchPayloadLength(w, lp);

            var r = PayloadOf(w.ToArray());
            Assert.False(BlockDeltaMessage.TryDecode(ref r, out _));
        }

        [Fact]
        public void A_batch_with_trailing_bytes_is_rejected()
        {
            var w = new PacketWriter();
            int lp = PacketHeader.Write(w, MessageType.BlockDelta, 1, 1, 1, 1);
            w.WriteUInt16(1);
            w.WriteUInt16(1); w.WriteUInt16(2); w.WriteUInt16(3);
            w.WriteByte(0xFF);                                       // unexplained tail
            PacketHeader.PatchPayloadLength(w, lp);

            var r = PayloadOf(w.ToArray());
            Assert.False(BlockDeltaMessage.TryDecode(ref r, out _));
        }

        [Fact]
        public void Block_deltas_are_reliable_and_ordered()
        {
            // A dropped edit is a hole that exists on one peer only, forever: nothing
            // later in the protocol would ever correct it.
            Assert.Equal(DeliveryClass.Transactions,
                MessageTypes.DeliveryFor(MessageType.BlockDelta));
        }

        [Fact]
        public void Block_delta_is_a_recognised_message_type()
        {
            Assert.True(MessageTypes.IsDefined((ushort)MessageType.BlockDelta));
        }

        // -------------------------------------------------------------- block hits

        [Fact]
        public void Block_hit_batch_round_trips()
        {
            var sent = new BlockHitMessage();
            sent.Cracks.Add(new BlockCrack(300, 400, 37.5f));
            sent.Cracks.Add(new BlockCrack(0, 1023, 1f));

            var r = PayloadOf(sent.Encode(1, 1, 1, 1));
            Assert.True(BlockHitMessage.TryDecode(ref r, out var got));

            Assert.Equal(2, got.Cracks.Count);
            Assert.Equal(300, got.Cracks[0].X);
            Assert.Equal(37.5f, got.Cracks[0].Damage);
            Assert.Equal(1023, got.Cracks[1].Y);
        }

        [Theory]
        [InlineData(0f)]
        [InlineData(-1f)]
        public void A_crack_with_no_damage_is_rejected(float damage)
        {
            // An undamaged cell has no overlay to draw, so the message would be a packet
            // that does nothing.
            var w = new PacketWriter();
            int lp = PacketHeader.Write(w, MessageType.BlockHit, 1, 1, 1, 1);
            w.WriteUInt16(1);
            w.WriteUInt16(1); w.WriteUInt16(2); w.WriteSingle(damage);
            PacketHeader.PatchPayloadLength(w, lp);

            var r = PayloadOf(w.ToArray());
            Assert.False(BlockHitMessage.TryDecode(ref r, out _));
        }

        [Fact]
        public void A_full_hit_batch_still_fits_in_one_packet()
        {
            var sent = new BlockHitMessage();
            for (int i = 0; i < BlockHitMessage.MaxCracksPerMessage; i++)
                sent.Cracks.Add(new BlockCrack((ushort)i, (ushort)i, 1f));

            var packet = sent.Encode(1, 1, 1, 1);
            Assert.True(packet.Length <= ProtocolConstants.MaxPacketBytes);

            var r = PayloadOf(packet);
            Assert.True(BlockHitMessage.TryDecode(ref r, out var got));
            Assert.Equal(BlockHitMessage.MaxCracksPerMessage, got.Cracks.Count);
        }

        [Fact]
        public void Block_hits_share_the_ordered_channel_with_terrain_edits()
        {
            // A crack must not arrive after the edit that removed the block it belongs to.
            Assert.Equal(DeliveryClass.Transactions, MessageTypes.DeliveryFor(MessageType.BlockHit));
            Assert.Equal(MessageTypes.DeliveryFor(MessageType.BlockDelta),
                         MessageTypes.DeliveryFor(MessageType.BlockHit));
        }

        // ----------------------------------------------------------- entity spawns

        [Fact]
        public void Entity_spawn_round_trips()
        {
            var sent = new EntitySpawnMessage
            {
                EntityId = 0x0110_0007,
                ItemId = "scrapmetal",
                PositionX = -12.5f,
                PositionY = 340.25f,
                Rotation = 271.5f,
                Condition = 0.125f,
                VelocityX = -3.5f,
                VelocityY = 6.25f,
            };

            var r = PayloadOf(sent.Encode(1, 1, 1, 1));
            Assert.True(EntitySpawnMessage.TryDecode(ref r, out var got));

            Assert.Equal(sent.EntityId, got.EntityId);
            Assert.Equal(sent.ItemId, got.ItemId);
            Assert.Equal(sent.PositionX, got.PositionX);
            Assert.Equal(sent.PositionY, got.PositionY);
            Assert.Equal(sent.Rotation, got.Rotation);
            Assert.Equal(sent.Condition, got.Condition);
            Assert.Equal(sent.VelocityX, got.VelocityX);
            Assert.Equal(sent.VelocityY, got.VelocityY);
        }

        [Fact]
        public void Entity_spawn_carries_the_float_decision()
        {
            // The spawner decides whether a drop floats and everyone obeys, because
            // FreshItemDrop turns gravity off — a peer that re-decided it locally would
            // run different physics from the peer that rolled the loot.
            var floating = new EntitySpawnMessage
            {
                EntityId = 3, ItemId = "bandage", Condition = 1f, Floats = true,
            };
            var r = PayloadOf(floating.Encode(1, 1, 1, 1));
            Assert.True(EntitySpawnMessage.TryDecode(ref r, out var got));
            Assert.True(got.Floats);

            var sinking = new EntitySpawnMessage
            {
                EntityId = 4, ItemId = "bandage", Condition = 1f, Floats = false,
            };
            var r2 = PayloadOf(sinking.Encode(1, 1, 1, 1));
            Assert.True(EntitySpawnMessage.TryDecode(ref r2, out var got2));
            Assert.False(got2.Floats);
        }

        [Fact]
        public void Entity_spawn_without_an_item_id_is_rejected()
        {
            var sent = new EntitySpawnMessage { EntityId = 5, ItemId = "" };
            var r = PayloadOf(sent.Encode(1, 1, 1, 1));
            Assert.False(EntitySpawnMessage.TryDecode(ref r, out _));
        }

        [Fact]
        public void Entity_spawn_with_a_non_finite_coordinate_is_rejected()
        {
            // NaN would propagate straight into a transform and physics.
            var w = new PacketWriter();
            int lp = PacketHeader.Write(w, MessageType.EntitySpawn, 1, 1, 1, 1);
            w.WriteUInt32(1);
            w.WriteString("stick");
            w.WriteSingle(float.NaN);
            w.WriteSingle(0f); w.WriteSingle(0f); w.WriteSingle(1f);
            w.WriteSingle(0f); w.WriteSingle(0f);
            PacketHeader.PatchPayloadLength(w, lp);

            var r = PayloadOf(w.ToArray());
            Assert.False(EntitySpawnMessage.TryDecode(ref r, out _));
        }

        // ----------------------------------------------------------- entity motion

        [Fact]
        public void Entity_motion_round_trips()
        {
            var sent = new EntityMotionMessage { AtRest = false };
            sent.Motions.Add(new EntityMotion
            {
                EntityId = 0x0110_0007,
                X = -12.5f,
                Y = 340.25f,
                Rotation = 271.5f,
                VelocityX = -3.5f,
                VelocityY = 6.25f,
            });
            sent.Motions.Add(new EntityMotion { EntityId = 0x0120_0001, X = 1f, Y = 2f });

            var r = PayloadOf(sent.Encode(1, 1, 1, 1));
            Assert.True(EntityMotionMessage.TryDecode(ref r, out var got));

            Assert.False(got.AtRest);
            Assert.Equal(2, got.Motions.Count);
            Assert.Equal(sent.Motions[0].EntityId, got.Motions[0].EntityId);
            Assert.Equal(sent.Motions[0].X, got.Motions[0].X);
            Assert.Equal(sent.Motions[0].Y, got.Motions[0].Y);
            Assert.Equal(sent.Motions[0].Rotation, got.Motions[0].Rotation);
            Assert.Equal(sent.Motions[0].VelocityX, got.Motions[0].VelocityX);
            Assert.Equal(sent.Motions[0].VelocityY, got.Motions[0].VelocityY);
            Assert.Equal(sent.Motions[1].EntityId, got.Motions[1].EntityId);
        }

        [Fact]
        public void Entity_motion_carries_the_at_rest_flag()
        {
            // The flag decides whether a receiver snaps and sleeps the item or merely
            // corrects it, so it has to survive the wire distinctly from the positions.
            var sent = new EntityMotionMessage { AtRest = true };
            sent.Motions.Add(new EntityMotion { EntityId = 7, X = 3f, Y = 4f });

            var r = PayloadOf(sent.Encode(1, 1, 1, 1));
            Assert.True(EntityMotionMessage.TryDecode(ref r, out var got));
            Assert.True(got.AtRest);
        }

        [Fact]
        public void Entity_motion_over_the_batch_cap_is_refused_by_the_sender()
        {
            var sent = new EntityMotionMessage();
            for (int i = 0; i <= EntityMotionMessage.MaxMotionsPerMessage; i++)
                sent.Motions.Add(new EntityMotion { EntityId = (uint)i });

            Assert.Throws<ArgumentException>(() => sent.Encode(1, 1, 1, 1));
        }

        [Fact]
        public void Entity_motion_claiming_more_records_than_it_carries_is_rejected()
        {
            // The count is remote-supplied and sizes a loop; a claim the payload cannot
            // satisfy must be refused rather than read past the end.
            var w = new PacketWriter();
            int lp = PacketHeader.Write(w, MessageType.EntityMotion, 1, 1, 1, 1);
            w.WriteByte(0);
            w.WriteUInt16(64);          // claims 64 records
            w.WriteUInt32(1);           // supplies part of one
            w.WriteSingle(0f);
            PacketHeader.PatchPayloadLength(w, lp);

            var r = PayloadOf(w.ToArray());
            Assert.False(EntityMotionMessage.TryDecode(ref r, out _));
        }

        [Fact]
        public void Entity_motion_with_a_non_finite_coordinate_is_rejected()
        {
            // NaN would go straight into a transform and then into physics.
            var w = new PacketWriter();
            int lp = PacketHeader.Write(w, MessageType.EntityMotion, 1, 1, 1, 1);
            w.WriteByte(0);
            w.WriteUInt16(1);
            w.WriteUInt32(1);
            w.WriteSingle(float.NaN);
            w.WriteSingle(0f); w.WriteSingle(0f); w.WriteSingle(0f); w.WriteSingle(0f);
            PacketHeader.PatchPayloadLength(w, lp);

            var r = PayloadOf(w.ToArray());
            Assert.False(EntityMotionMessage.TryDecode(ref r, out _));
        }

        [Fact]
        public void Entity_motion_streams_on_the_snapshot_channel()
        {
            // Its default is the player-movement channel: newest wins, drops are fine.
            // The at-rest message deviates deliberately and is sent reliably instead.
            Assert.Equal(DeliveryClass.Snapshots,
                         MessageTypes.DeliveryFor(MessageType.EntityMotion));
            Assert.Equal(MessageTypes.DeliveryFor(MessageType.PlayerTransform),
                         MessageTypes.DeliveryFor(MessageType.EntityMotion));
        }

        [Fact]
        public void Entity_spawns_are_reliable_and_ordered()
        {
            Assert.Equal(DeliveryClass.EntityLifecycle,
                MessageTypes.DeliveryFor(MessageType.EntitySpawn));
        }

        // --------------------------------------------------------- entity despawns

        [Fact]
        public void Entity_despawn_round_trips()
        {
            var sent = new EntityDespawnMessage { EntityId = 1943 };

            var r = PayloadOf(sent.Encode(1, 1, 1, 1));
            Assert.True(EntityDespawnMessage.TryDecode(ref r, out var got));
            Assert.Equal(1943u, got.EntityId);
        }

        [Fact]
        public void Entity_despawn_with_trailing_bytes_is_rejected()
        {
            var w = new PacketWriter();
            int lp = PacketHeader.Write(w, MessageType.EntityDespawn, 1, 1, 1, 1);
            w.WriteUInt32(7);
            w.WriteByte(0);          // unexplained tail
            PacketHeader.PatchPayloadLength(w, lp);

            var r = PayloadOf(w.ToArray());
            Assert.False(EntityDespawnMessage.TryDecode(ref r, out _));
        }

        [Fact]
        public void Entity_despawn_that_is_too_short_is_rejected()
        {
            var w = new PacketWriter();
            int lp = PacketHeader.Write(w, MessageType.EntityDespawn, 1, 1, 1, 1);
            w.WriteUInt16(7);        // half an id
            PacketHeader.PatchPayloadLength(w, lp);

            var r = PayloadOf(w.ToArray());
            Assert.False(EntityDespawnMessage.TryDecode(ref r, out _));
        }

        [Fact]
        public void Entity_despawns_are_reliable_and_ordered()
        {
            // A dropped despawn leaves a crate standing on one peer forever, and a peer
            // that never learns of it will roll a second set of loot from it.
            Assert.Equal(DeliveryClass.EntityLifecycle,
                MessageTypes.DeliveryFor(MessageType.EntityDespawn));
        }

        // ---------------------------------------------------------- entity damage

        [Fact]
        public void Entity_damage_batch_round_trips()
        {
            var sent = new EntityDamageMessage();
            sent.Damages.Add(new EntityDamage(12, 37.5f));
            sent.Damages.Add(new EntityDamage(0x0110_0001, 0.25f));

            var r = PayloadOf(sent.Encode(1, 1, 1, 1));
            Assert.True(EntityDamageMessage.TryDecode(ref r, out var got));

            Assert.Equal(2, got.Damages.Count);
            Assert.Equal(12u, got.Damages[0].EntityId);
            Assert.Equal(37.5f, got.Damages[0].Amount);
            Assert.Equal(0.25f, got.Damages[1].Amount);
        }

        [Fact]
        public void A_full_damage_batch_still_fits_in_one_packet()
        {
            var sent = new EntityDamageMessage();
            for (int i = 0; i < EntityDamageMessage.MaxDamagesPerMessage; i++)
                sent.Damages.Add(new EntityDamage((uint)i, 1f));

            var packet = sent.Encode(1, 1, 1, 1);
            Assert.True(packet.Length <= ProtocolConstants.MaxPacketBytes);

            var r = PayloadOf(packet);
            Assert.True(EntityDamageMessage.TryDecode(ref r, out var got));
            Assert.Equal(EntityDamageMessage.MaxDamagesPerMessage, got.Damages.Count);
        }

        [Theory]
        [InlineData(0f)]
        [InlineData(-5f)]
        public void Damage_that_would_heal_or_do_nothing_is_rejected(float amount)
        {
            // Negative damage repairs a crate somebody else is breaking; zero is a packet
            // that says nothing. Neither is something a peer should be able to ask for.
            var w = new PacketWriter();
            int lp = PacketHeader.Write(w, MessageType.EntityStateDelta, 1, 1, 1, 1);
            w.WriteUInt16(1);
            w.WriteUInt32(4);
            w.WriteSingle(amount);
            PacketHeader.PatchPayloadLength(w, lp);

            var r = PayloadOf(w.ToArray());
            Assert.False(EntityDamageMessage.TryDecode(ref r, out _));
        }

        [Fact]
        public void An_empty_damage_batch_is_rejected()
        {
            var w = new PacketWriter();
            int lp = PacketHeader.Write(w, MessageType.EntityStateDelta, 1, 1, 1, 1);
            w.WriteUInt16(0);
            PacketHeader.PatchPayloadLength(w, lp);

            var r = PayloadOf(w.ToArray());
            Assert.False(EntityDamageMessage.TryDecode(ref r, out _));
        }

        [Fact]
        public void A_damage_batch_claiming_more_than_it_carries_is_rejected()
        {
            var w = new PacketWriter();
            int lp = PacketHeader.Write(w, MessageType.EntityStateDelta, 1, 1, 1, 1);
            w.WriteUInt16(200);
            w.WriteUInt32(1); w.WriteSingle(1f);   // only one
            PacketHeader.PatchPayloadLength(w, lp);

            var r = PayloadOf(w.ToArray());
            Assert.False(EntityDamageMessage.TryDecode(ref r, out _));
        }

        [Fact]
        public void Damage_shares_the_ordered_channel_with_destruction()
        {
            // Not merely "reliable": damage and the despawn that follows the killing blow
            // must stay in order, or a peer sees the death before the damage that caused
            // it and claims the kill — and its loot — as its own.
            Assert.Equal(DeliveryClass.EntityLifecycle,
                MessageTypes.DeliveryFor(MessageType.EntityStateDelta));
            Assert.Equal(MessageTypes.DeliveryFor(MessageType.EntityDespawn),
                MessageTypes.DeliveryFor(MessageType.EntityStateDelta));
        }
    }
}
