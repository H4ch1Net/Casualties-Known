using System;

namespace CCoop.Protocol
{
    /// <summary>
    /// Fixed 24-byte packet header.
    ///
    /// <code>
    ///  offset size field
    ///  0      4    magic            'CCMP'
    ///  4      2    protocolVersion
    ///  6      2    messageType
    ///  8      1    senderPeerId     0 = host
    ///  9      1    flags
    ///  10     4    sessionId
    ///  14     4    sequence
    ///  18     4    tick             simulation tick
    ///  22     2    payloadLength
    /// </code>
    /// </summary>
    public struct PacketHeader
    {
        public MessageType Type;
        public byte SenderPeerId;
        public byte Flags;
        public uint SessionId;
        public uint Sequence;
        public uint Tick;
        public ushort PayloadLength;

        /// <summary>
        /// Writes the header, leaving <see cref="PayloadLength"/> to be backfilled by
        /// <see cref="PatchPayloadLength"/> once the body is known.
        /// </summary>
        public static int Write(PacketWriter w, MessageType type, byte senderPeerId,
                               uint sessionId, uint sequence, uint tick, byte flags = 0)
        {
            w.WriteUInt32(ProtocolConstants.Magic);
            w.WriteUInt16(ProtocolConstants.Version);
            w.WriteUInt16((ushort)type);
            w.WriteByte(senderPeerId);
            w.WriteByte(flags);
            w.WriteUInt32(sessionId);
            w.WriteUInt32(sequence);
            w.WriteUInt32(tick);

            int lengthPosition = w.Length;
            w.WriteUInt16(0); // placeholder
            return lengthPosition;
        }

        /// <summary>Backfills the payload length after the body has been written.</summary>
        public static void PatchPayloadLength(PacketWriter w, int lengthPosition)
        {
            int payload = w.Length - (lengthPosition + 2);
            if (payload < 0 || payload > ProtocolConstants.MaxPayloadBytes)
                throw new InvalidOperationException(
                    "payload length " + payload + " outside 0.." + ProtocolConstants.MaxPayloadBytes);

            w.PatchUInt16(lengthPosition, (ushort)payload);
        }

        /// <summary>
        /// Validates and parses a header. Checks are ordered cheapest-first so junk
        /// traffic is dropped before any real work: length, magic, version, then type.
        /// Returns the reason on failure; never throws.
        /// </summary>
        public static DecodeResult TryRead(byte[] buffer, int offset, int count, out PacketHeader header)
        {
            header = default;

            if (buffer == null) return DecodeResult.TooShort;
            if (offset < 0 || count < 0 || offset > buffer.Length || offset + count > buffer.Length)
                return DecodeResult.TooShort;
            if (count < ProtocolConstants.HeaderSize) return DecodeResult.TooShort;

            var r = new PacketReader(buffer, offset, count);

            uint magic = r.ReadUInt32();
            if (r.Failed) return DecodeResult.TooShort;
            if (magic != ProtocolConstants.Magic) return DecodeResult.BadMagic;

            ushort version = r.ReadUInt16();
            if (version != ProtocolConstants.Version) return DecodeResult.VersionMismatch;

            ushort rawType = r.ReadUInt16();
            if (!MessageTypes.IsDefined(rawType)) return DecodeResult.UnknownMessageType;

            byte peer = r.ReadByte();
            byte flags = r.ReadByte();
            uint sessionId = r.ReadUInt32();
            uint sequence = r.ReadUInt32();
            uint tick = r.ReadUInt32();
            ushort payloadLength = r.ReadUInt16();

            if (r.Failed) return DecodeResult.TooShort;

            // Peer ids are bounded so a peer table can be a fixed-size array.
            if (peer >= ProtocolConstants.MaxPeers) return DecodeResult.InvalidPeerId;

            if (payloadLength > ProtocolConstants.MaxPayloadBytes) return DecodeResult.PayloadTooLarge;

            // The declared length must match the bytes actually delivered. Accepting a
            // short-but-declared-long packet is how you get an out-of-bounds read.
            int actualPayload = count - ProtocolConstants.HeaderSize;
            if (payloadLength != actualPayload) return DecodeResult.LengthMismatch;

            header = new PacketHeader
            {
                Type = (MessageType)rawType,
                SenderPeerId = peer,
                Flags = flags,
                SessionId = sessionId,
                Sequence = sequence,
                Tick = tick,
                PayloadLength = payloadLength,
            };
            return DecodeResult.Ok;
        }

        /// <summary>A reader positioned at the payload, given a validated header.</summary>
        public static PacketReader PayloadReader(byte[] buffer, int offset, int count)
        {
            return new PacketReader(buffer,
                                    offset + ProtocolConstants.HeaderSize,
                                    count - ProtocolConstants.HeaderSize);
        }
    }
}
