using System;
using CCoop.Protocol;
using Xunit;

namespace CCoop.Protocol.Tests
{
    public class PrimitiveRoundTripTests
    {
        [Fact]
        public void Integers_and_floats_round_trip()
        {
            var w = new PacketWriter();
            w.WriteByte(0xAB);
            w.WriteBool(true);
            w.WriteBool(false);
            w.WriteUInt16(0xBEEF);
            w.WriteUInt32(0xDEADBEEF);
            w.WriteInt32(-123456);
            w.WriteUInt64(0x0123456789ABCDEF);
            w.WriteSingle(3.5f);
            w.WriteSingle(-0.125f);

            var bytes = w.ToArray();
            var r = new PacketReader(bytes, 0, bytes.Length);

            Assert.Equal(0xAB, r.ReadByte());
            Assert.True(r.ReadBool());
            Assert.False(r.ReadBool());
            Assert.Equal(0xBEEF, r.ReadUInt16());
            Assert.Equal(0xDEADBEEF, r.ReadUInt32());
            Assert.Equal(-123456, r.ReadInt32());
            Assert.Equal(0x0123456789ABCDEFUL, r.ReadUInt64());
            Assert.Equal(3.5f, r.ReadSingle());
            Assert.Equal(-0.125f, r.ReadSingle());
            Assert.False(r.Failed);
            Assert.True(r.FullyConsumed);
        }

        [Theory]
        [InlineData("")]
        [InlineData("simple")]
        [InlineData("unicode: éü中文 \U0001F600")]
        public void Strings_round_trip(string value)
        {
            var w = new PacketWriter();
            w.WriteString(value);
            var bytes = w.ToArray();

            var r = new PacketReader(bytes, 0, bytes.Length);
            Assert.Equal(value, r.ReadString());
            Assert.True(r.FullyConsumed);
        }

        [Fact]
        public void Blobs_round_trip()
        {
            var payload = new byte[1024];
            new Random(42).NextBytes(payload);

            var w = new PacketWriter();
            w.WriteBlob(payload);
            var bytes = w.ToArray();

            var r = new PacketReader(bytes, 0, bytes.Length);
            Assert.Equal(payload, r.ReadBlob());
            Assert.True(r.FullyConsumed);
        }

        [Fact]
        public void Writer_rejects_oversized_string()
        {
            var w = new PacketWriter();
            var tooLong = new string('x', ProtocolConstants.MaxStringBytes + 1);
            Assert.Throws<ArgumentException>(() => w.WriteString(tooLong));
        }

        [Fact]
        public void Writer_rejects_oversized_blob()
        {
            var w = new PacketWriter();
            Assert.Throws<ArgumentException>(
                () => w.WriteBlob(new byte[ProtocolConstants.MaxBlobBytes + 1]));
        }
    }

    public class ReaderSafetyTests
    {
        [Fact]
        public void Reading_past_end_fails_instead_of_throwing()
        {
            var r = new PacketReader(new byte[2], 0, 2);
            r.ReadUInt32();
            Assert.True(r.Failed);
            Assert.Equal(0, r.Remaining);
        }

        [Fact]
        public void Bad_framing_produces_failed_reader_not_exception()
        {
            var r = new PacketReader(new byte[4], 2, 100);
            Assert.True(r.Failed);
        }

        [Fact]
        public void Null_buffer_throws_argument_null()
        {
            Assert.Throws<ArgumentNullException>(() => new PacketReader(null, 0, 0));
        }

        [Fact]
        public void Non_canonical_boolean_is_rejected()
        {
            var r = new PacketReader(new byte[] { 2 }, 0, 1);
            r.ReadBool();
            Assert.True(r.Failed);
        }

        [Theory]
        [InlineData(float.NaN)]
        [InlineData(float.PositiveInfinity)]
        [InlineData(float.NegativeInfinity)]
        public void NaN_and_infinity_are_rejected(float bad)
        {
            // Written via raw bytes: the writer would happily emit these, but a decoder
            // must not let them into physics or interpolation.
            var raw = BitConverter.GetBytes(bad);
            var r = new PacketReader(raw, 0, raw.Length);
            r.ReadSingle();
            Assert.True(r.Failed);
        }

        [Fact]
        public void Oversized_declared_string_length_is_rejected_without_allocating()
        {
            var w = new PacketWriter();
            w.WriteUInt16(ushort.MaxValue); // claims 65535 bytes follow
            w.WriteByte(1);                 // but only one is present
            var bytes = w.ToArray();

            var r = new PacketReader(bytes, 0, bytes.Length);
            r.ReadString();
            Assert.True(r.Failed);
        }

        [Fact]
        public void Negative_declared_blob_length_is_rejected()
        {
            var w = new PacketWriter();
            w.WriteInt32(-1);
            var bytes = w.ToArray();

            var r = new PacketReader(bytes, 0, bytes.Length);
            Assert.Empty(r.ReadBlob());
            Assert.True(r.Failed);
        }

        [Fact]
        public void Invalid_utf8_is_rejected()
        {
            var w = new PacketWriter();
            w.WriteUInt16(2);
            w.WriteByte(0xC3); // start of a 2-byte sequence...
            w.WriteByte(0x28); // ...followed by an illegal continuation
            var bytes = w.ToArray();

            var r = new PacketReader(bytes, 0, bytes.Length);
            r.ReadString();
            Assert.True(r.Failed);
        }
    }

    public class HeaderTests
    {
        private static byte[] BuildPacket(MessageType type, byte peer, Action<PacketWriter> body)
        {
            var w = new PacketWriter();
            int lp = PacketHeader.Write(w, type, peer, sessionId: 7, sequence: 99, tick: 1234);
            body?.Invoke(w);
            PacketHeader.PatchPayloadLength(w, lp);
            return w.ToArray();
        }

        [Fact]
        public void Valid_header_round_trips()
        {
            var packet = BuildPacket(MessageType.Ping, 3, w => w.WriteUInt32(0xCAFEBABE));

            var result = PacketHeader.TryRead(packet, 0, packet.Length, out var h);

            Assert.Equal(DecodeResult.Ok, result);
            Assert.Equal(MessageType.Ping, h.Type);
            Assert.Equal(3, h.SenderPeerId);
            Assert.Equal(7u, h.SessionId);
            Assert.Equal(99u, h.Sequence);
            Assert.Equal(1234u, h.Tick);
            Assert.Equal(4, h.PayloadLength);

            var pr = PacketHeader.PayloadReader(packet, 0, packet.Length);
            Assert.Equal(0xCAFEBABE, pr.ReadUInt32());
            Assert.True(pr.FullyConsumed);
        }

        [Fact]
        public void Header_size_constant_matches_reality()
        {
            var packet = BuildPacket(MessageType.Ping, 0, null);
            Assert.Equal(ProtocolConstants.HeaderSize, packet.Length);
        }

        [Fact]
        public void Truncated_packet_is_rejected()
        {
            var packet = BuildPacket(MessageType.Ping, 0, w => w.WriteUInt32(1));

            for (int len = 0; len < ProtocolConstants.HeaderSize; len++)
                Assert.Equal(DecodeResult.TooShort, PacketHeader.TryRead(packet, 0, len, out _));
        }

        [Fact]
        public void Bad_magic_is_rejected()
        {
            var packet = BuildPacket(MessageType.Ping, 0, null);
            packet[0] ^= 0xFF;
            Assert.Equal(DecodeResult.BadMagic, PacketHeader.TryRead(packet, 0, packet.Length, out _));
        }

        [Fact]
        public void Version_mismatch_is_rejected()
        {
            var packet = BuildPacket(MessageType.Ping, 0, null);
            packet[4] = 0xFE;
            packet[5] = 0xFF;
            Assert.Equal(DecodeResult.VersionMismatch, PacketHeader.TryRead(packet, 0, packet.Length, out _));
        }

        [Fact]
        public void Unknown_message_type_is_rejected()
        {
            var packet = BuildPacket(MessageType.Ping, 0, null);
            packet[6] = 0xFF;
            packet[7] = 0xFF;
            Assert.Equal(DecodeResult.UnknownMessageType, PacketHeader.TryRead(packet, 0, packet.Length, out _));
        }

        [Fact]
        public void Out_of_range_peer_id_is_rejected()
        {
            var packet = BuildPacket(MessageType.Ping, 0, null);
            packet[8] = ProtocolConstants.MaxPeers;
            Assert.Equal(DecodeResult.InvalidPeerId, PacketHeader.TryRead(packet, 0, packet.Length, out _));
        }

        [Fact]
        public void Declared_length_shorter_than_actual_is_rejected()
        {
            var packet = BuildPacket(MessageType.Ping, 0, w => w.WriteUInt32(1));
            packet[22] = 0; // claim an empty payload while 4 bytes follow
            packet[23] = 0;
            Assert.Equal(DecodeResult.LengthMismatch, PacketHeader.TryRead(packet, 0, packet.Length, out _));
        }

        [Fact]
        public void Declared_length_longer_than_actual_is_rejected()
        {
            var packet = BuildPacket(MessageType.Ping, 0, w => w.WriteUInt32(1));
            packet[22] = 200; // claim 200 bytes while 4 follow
            packet[23] = 0;
            Assert.Equal(DecodeResult.LengthMismatch, PacketHeader.TryRead(packet, 0, packet.Length, out _));
        }

        [Fact]
        public void Declared_length_over_maximum_is_rejected()
        {
            var packet = BuildPacket(MessageType.Ping, 0, null);
            packet[22] = 0xFF;
            packet[23] = 0xFF;
            Assert.Equal(DecodeResult.PayloadTooLarge, PacketHeader.TryRead(packet, 0, packet.Length, out _));
        }

        [Fact]
        public void Every_defined_message_type_survives_the_header()
        {
            foreach (MessageType type in Enum.GetValues(typeof(MessageType)))
            {
                var packet = BuildPacket(type, 1, null);
                Assert.Equal(DecodeResult.Ok, PacketHeader.TryRead(packet, 0, packet.Length, out var h));
                Assert.Equal(type, h.Type);
            }
        }

        [Fact]
        public void IsDefined_agrees_with_the_enum()
        {
            foreach (MessageType type in Enum.GetValues(typeof(MessageType)))
                Assert.True(MessageTypes.IsDefined((ushort)type), type.ToString() + " missing from IsDefined");

            // Spot-check gaps so a newly added value cannot silently skip IsDefined.
            foreach (ushort gap in new ushort[] { 0, 9, 19, 39, 59, 79, 99, 119, 199, ushort.MaxValue })
                Assert.False(MessageTypes.IsDefined(gap), "value " + gap + " should be undefined");
        }
    }

    public class FuzzTests
    {
        /// <summary>
        /// Random bytes must never throw and must never be accepted. This is the
        /// property that matters most: a decoder is the only code path a hostile peer
        /// can reach before authentication.
        /// </summary>
        [Fact]
        public void Random_bytes_never_throw_and_are_never_accepted()
        {
            var rng = new Random(1234);
            int accepted = 0;

            for (int i = 0; i < 20000; i++)
            {
                var buf = new byte[rng.Next(0, 96)];
                rng.NextBytes(buf);

                var result = PacketHeader.TryRead(buf, 0, buf.Length, out _);
                if (result == DecodeResult.Ok) accepted++;
            }

            // 32 bits of magic plus 16 of version make a chance hit essentially impossible.
            Assert.Equal(0, accepted);
        }

        /// <summary>
        /// Bit-flipping a valid packet must either still decode or fail cleanly — never
        /// throw, and never yield a header claiming more payload than was delivered.
        /// </summary>
        [Fact]
        public void Corrupting_valid_packets_never_throws()
        {
            var rng = new Random(99);

            var w = new PacketWriter();
            int lp = PacketHeader.Write(w, MessageType.PlayerTransform, 2, 5, 6, 7);
            w.WriteSingle(1f); w.WriteSingle(2f); w.WriteSingle(3f);
            PacketHeader.PatchPayloadLength(w, lp);
            var original = w.ToArray();

            for (int i = 0; i < 20000; i++)
            {
                var copy = (byte[])original.Clone();
                copy[rng.Next(copy.Length)] ^= (byte)(1 << rng.Next(8));

                var result = PacketHeader.TryRead(copy, 0, copy.Length, out var h);
                if (result != DecodeResult.Ok) continue;

                Assert.Equal(copy.Length - ProtocolConstants.HeaderSize, h.PayloadLength);

                // Payload reads must also stay safe on corrupted bodies.
                var pr = PacketHeader.PayloadReader(copy, 0, copy.Length);
                pr.ReadSingle(); pr.ReadSingle(); pr.ReadSingle();
            }
        }

        /// <summary>Fuzzed payloads must not produce an accepted handshake.</summary>
        [Fact]
        public void Fuzzed_hello_payloads_never_decode()
        {
            var rng = new Random(7);
            int accepted = 0;

            for (int i = 0; i < 20000; i++)
            {
                var body = new byte[rng.Next(0, 160)];
                rng.NextBytes(body);

                var w = new PacketWriter();
                int lp = PacketHeader.Write(w, MessageType.Hello, 0, 0, 0, 0);
                w.WriteBytesRaw(body, 0, body.Length);
                PacketHeader.PatchPayloadLength(w, lp);
                var packet = w.ToArray();

                Assert.Equal(DecodeResult.Ok, PacketHeader.TryRead(packet, 0, packet.Length, out _));

                var pr = PacketHeader.PayloadReader(packet, 0, packet.Length);
                if (Messages.HelloMessage.TryDecode(ref pr, out _)) accepted++;
            }

            // Random bytes cannot satisfy a 64-char lowercase-hex hash plus exact framing.
            Assert.Equal(0, accepted);
        }
    }
}
