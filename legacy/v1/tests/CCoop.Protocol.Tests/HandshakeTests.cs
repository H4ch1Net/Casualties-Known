using System;
using CCoop.Protocol;
using CCoop.Protocol.Messages;
using Xunit;

namespace CCoop.Protocol.Tests
{
    public class HandshakeTests
    {
        private const string RealHash =
            "fd0ad2851f6bae4ab2589634643b92e16696c0e36f090bdd3f4c41e8f1c97095";

        private static HelloMessage ValidHello() => new HelloMessage
        {
            ModVersion = "0.1.0",
            GameAppId = 4576510,
            AssemblyHash = RealHash,
            UnityVersion = "2022.3.62f3",
            PluginManifest = "ccoop.plugin@0.1.0,ccoop.probe@0.1.0",
            PlayerName = "tester",
            FeatureFlags = 0b1011,
        };

        private static PacketReader PayloadOf(byte[] packet)
        {
            Assert.Equal(DecodeResult.Ok, PacketHeader.TryRead(packet, 0, packet.Length, out _));
            return PacketHeader.PayloadReader(packet, 0, packet.Length);
        }

        [Fact]
        public void Hello_round_trips()
        {
            var sent = ValidHello();
            var packet = sent.Encode(sequence: 1);

            var r = PayloadOf(packet);
            Assert.True(HelloMessage.TryDecode(ref r, out var got));

            Assert.Equal(sent.ModVersion, got.ModVersion);
            Assert.Equal(sent.GameAppId, got.GameAppId);
            Assert.Equal(sent.AssemblyHash, got.AssemblyHash);
            Assert.Equal(sent.UnityVersion, got.UnityVersion);
            Assert.Equal(sent.PluginManifest, got.PluginManifest);
            Assert.Equal(sent.PlayerName, got.PlayerName);
            Assert.Equal(sent.FeatureFlags, got.FeatureFlags);
        }

        [Fact]
        public void Hello_with_uppercase_hash_is_rejected()
        {
            var hello = ValidHello();
            hello.AssemblyHash = RealHash.ToUpperInvariant();
            var r = PayloadOf(hello.Encode(1));
            Assert.False(HelloMessage.TryDecode(ref r, out _));
        }

        [Theory]
        [InlineData("")]
        [InlineData("abc")]
        [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
        public void Hello_with_malformed_hash_is_rejected(string hash)
        {
            var hello = ValidHello();
            hello.AssemblyHash = hash;
            var r = PayloadOf(hello.Encode(1));
            Assert.False(HelloMessage.TryDecode(ref r, out _));
        }

        [Fact]
        public void Hello_with_empty_mod_version_is_rejected()
        {
            var hello = ValidHello();
            hello.ModVersion = "";
            var r = PayloadOf(hello.Encode(1));
            Assert.False(HelloMessage.TryDecode(ref r, out _));
        }

        [Fact]
        public void Hello_with_trailing_bytes_is_rejected()
        {
            // Trailing bytes mean the sender disagrees about the format; accepting them
            // would let a mismatched build past the one check designed to catch it.
            var hello = ValidHello();
            var w = new PacketWriter(512);
            int lp = PacketHeader.Write(w, MessageType.Hello, 0, 0, 1, 0);
            w.WriteString(hello.ModVersion);
            w.WriteUInt32(hello.GameAppId);
            w.WriteString(hello.AssemblyHash);
            w.WriteString(hello.UnityVersion);
            w.WriteString(hello.PluginManifest);
            w.WriteString(hello.PlayerName);
            w.WriteUInt32(hello.FeatureFlags);
            w.WriteUInt32(0xDEADBEEF); // extra
            PacketHeader.PatchPayloadLength(w, lp);

            var r = PayloadOf(w.ToArray());
            Assert.False(HelloMessage.TryDecode(ref r, out _));
        }

        [Fact]
        public void Hello_truncated_mid_payload_is_rejected()
        {
            var full = ValidHello().Encode(1);

            for (int cut = 1; cut < full.Length - ProtocolConstants.HeaderSize; cut++)
            {
                var truncated = new byte[full.Length - cut];
                Array.Copy(full, truncated, truncated.Length);

                // Fix the declared length so the header itself stays valid; the payload
                // decoder must be what catches the truncation.
                ushort newPayload = (ushort)(truncated.Length - ProtocolConstants.HeaderSize);
                truncated[22] = (byte)newPayload;
                truncated[23] = (byte)(newPayload >> 8);

                Assert.Equal(DecodeResult.Ok,
                    PacketHeader.TryRead(truncated, 0, truncated.Length, out _));

                var r = PacketHeader.PayloadReader(truncated, 0, truncated.Length);
                Assert.False(HelloMessage.TryDecode(ref r, out _),
                    "truncation at " + cut + " was wrongly accepted");
            }
        }

        [Fact]
        public void Welcome_round_trips()
        {
            var sent = new WelcomeMessage
            {
                SessionId = 0xABCDEF01,
                AssignedPeerId = 2,
                HostPeerCount = 3,
                ServerTick = 555,
                HostPlayerName = "host",
            };
            var packet = sent.Encode(1);

            Assert.Equal(DecodeResult.Ok, PacketHeader.TryRead(packet, 0, packet.Length, out var h));
            var r = PacketHeader.PayloadReader(packet, 0, packet.Length);

            Assert.True(WelcomeMessage.TryDecode(ref r, h, out var got));
            Assert.Equal(sent.SessionId, got.SessionId);
            Assert.Equal(sent.AssignedPeerId, got.AssignedPeerId);
            Assert.Equal(sent.HostPeerCount, got.HostPeerCount);
            Assert.Equal(sent.ServerTick, got.ServerTick);
            Assert.Equal(sent.HostPlayerName, got.HostPlayerName);
        }

        [Fact]
        public void Welcome_assigning_the_host_id_is_rejected()
        {
            var sent = new WelcomeMessage
            {
                AssignedPeerId = ProtocolConstants.HostPeerId,
                HostPeerCount = 1,
            };
            var packet = sent.Encode(1);
            PacketHeader.TryRead(packet, 0, packet.Length, out var h);
            var r = PacketHeader.PayloadReader(packet, 0, packet.Length);

            Assert.False(WelcomeMessage.TryDecode(ref r, h, out _));
        }

        [Fact]
        public void Welcome_with_out_of_range_peer_id_is_rejected()
        {
            var sent = new WelcomeMessage
            {
                AssignedPeerId = ProtocolConstants.MaxPeers,
                HostPeerCount = 1,
            };
            var packet = sent.Encode(1);
            PacketHeader.TryRead(packet, 0, packet.Length, out var h);
            var r = PacketHeader.PayloadReader(packet, 0, packet.Length);

            Assert.False(WelcomeMessage.TryDecode(ref r, h, out _));
        }

        [Fact]
        public void Reject_round_trips_for_every_reason_and_describes_itself()
        {
            foreach (RejectReason reason in Enum.GetValues(typeof(RejectReason)))
            {
                var sent = new RejectMessage { Reason = reason, Detail = "expected abc123" };
                var r = PayloadOf(sent.Encode(1));

                Assert.True(RejectMessage.TryDecode(ref r, out var got));
                Assert.Equal(reason, got.Reason);
                Assert.Equal("expected abc123", got.Detail);

                // Must be user-presentable, not an enum name dumped to the UI.
                var text = got.Describe();
                Assert.False(string.IsNullOrWhiteSpace(text));
                Assert.DoesNotContain(reason.ToString(), text);
            }
        }

        [Fact]
        public void Reject_with_undefined_reason_is_rejected()
        {
            var w = new PacketWriter();
            int lp = PacketHeader.Write(w, MessageType.Reject, 0, 0, 1, 0);
            w.WriteByte(200); // not a defined reason
            w.WriteString("");
            PacketHeader.PatchPayloadLength(w, lp);

            var r = PayloadOf(w.ToArray());
            Assert.False(RejectMessage.TryDecode(ref r, out _));
        }

        [Fact]
        public void Delivery_classes_are_assigned_sensibly()
        {
            // Movement must be droppable; transactions and lifecycle must not be.
            Assert.Equal(DeliveryClass.Snapshots,
                MessageTypes.DeliveryFor(MessageType.PlayerTransform));
            Assert.Equal(DeliveryClass.Bulk,
                MessageTypes.DeliveryFor(MessageType.WorldSnapshot));
            Assert.Equal(DeliveryClass.Transactions,
                MessageTypes.DeliveryFor(MessageType.InteractionRequest));
            Assert.Equal(DeliveryClass.EntityLifecycle,
                MessageTypes.DeliveryFor(MessageType.EntitySpawn));
            Assert.Equal(DeliveryClass.Control,
                MessageTypes.DeliveryFor(MessageType.Hello));
            Assert.Equal(DeliveryClass.Diagnostics,
                MessageTypes.DeliveryFor(MessageType.StateHash));
        }
    }
}
