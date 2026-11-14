using System;

namespace CCoop.Protocol
{
    /// <summary>
    /// Wire-format constants. Every variable-length field in the protocol has a hard
    /// maximum here: a decoder that trusts a length field from the network is how you
    /// get an allocation-based denial of service, so nothing is allocated from a
    /// remote-supplied size without being checked against one of these bounds first.
    /// </summary>
    public static class ProtocolConstants
    {
        /// <summary>Magic bytes 'C','C','M','P' — cheap rejection of unrelated traffic.</summary>
        public const uint Magic = 0x504D4343; // little-endian "CCMP"

        /// <summary>
        /// Incremented on ANY wire-format change. Peers must match exactly; there is no
        /// backward compatibility while the format is still moving.
        /// </summary>
        public const ushort Version = 9;

        /// <summary>Fixed header size in bytes. See <see cref="PacketHeader"/>.</summary>
        public const int HeaderSize = 24;

        /// <summary>
        /// Largest payload we will accept. Bulk transfers (e.g. the ~145 KiB world
        /// snapshot) are fragmented by the transport into payloads under this bound
        /// rather than sent as one packet.
        /// </summary>
        public const int MaxPayloadBytes = 8 * 1024;

        /// <summary>Total packet size ceiling, used to size receive buffers.</summary>
        public const int MaxPacketBytes = HeaderSize + MaxPayloadBytes;

        /// <summary>Byte ceiling for any UTF-8 string on the wire.</summary>
        public const int MaxStringBytes = 512;

        /// <summary>Byte ceiling for a length-prefixed blob inside a payload.</summary>
        public const int MaxBlobBytes = MaxPayloadBytes - 64;

        /// <summary>Hard cap on session size. Peer ids are a single byte; 0 is the host.</summary>
        public const int MaxPeers = 8;

        /// <summary>The host always occupies this peer id.</summary>
        public const byte HostPeerId = 0;

        /// <summary>Hex length of a SHA-256 fingerprint (used for the game assembly).</summary>
        public const int Sha256HexLength = 64;
    }

    /// <summary>
    /// Delivery guarantees a message needs. The transport maps these onto its own
    /// channels; gameplay code never names a concrete transport channel.
    /// </summary>
    public enum DeliveryClass : byte
    {
        /// <summary>Handshake, session lifecycle, layer transitions.</summary>
        Control = 0,

        /// <summary>Inventory, crafting, interactions, damage. Order matters.</summary>
        Transactions = 1,

        /// <summary>Entity spawn/despawn. Order matters.</summary>
        EntityLifecycle = 2,

        /// <summary>Movement, aim, animation. Newest wins; drops are fine.</summary>
        Snapshots = 3,

        /// <summary>World snapshots and resyncs. Reliable, fragmented.</summary>
        Bulk = 4,

        /// <summary>State hashes and network stats. Reliable, order irrelevant.</summary>
        Diagnostics = 5,
    }

    /// <summary>Why a decode attempt was rejected. Never throws; always one of these.</summary>
    public enum DecodeResult : byte
    {
        Ok = 0,
        /// <summary>Fewer bytes than the fixed header requires.</summary>
        TooShort,
        /// <summary>Magic mismatch — not our protocol.</summary>
        BadMagic,
        /// <summary>Protocol version differs from <see cref="ProtocolConstants.Version"/>.</summary>
        VersionMismatch,
        /// <summary>Message type is not a defined <see cref="MessageType"/>.</summary>
        UnknownMessageType,
        /// <summary>Declared payload length exceeds <see cref="ProtocolConstants.MaxPayloadBytes"/>.</summary>
        PayloadTooLarge,
        /// <summary>Declared payload length does not match the bytes actually present.</summary>
        LengthMismatch,
        /// <summary>Peer id outside the legal range.</summary>
        InvalidPeerId,
        /// <summary>Payload body was truncated, over-long, or contained an invalid value.</summary>
        MalformedPayload,
    }
}
