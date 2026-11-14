using System;
using CCoop.Protocol;

namespace CCoop.Transport
{
    /// <summary>Identifies a connected peer. 0 is always the host.</summary>
    public struct PeerId : IEquatable<PeerId>
    {
        public readonly byte Value;
        public PeerId(byte value) { Value = value; }

        public static readonly PeerId Host = new PeerId(ProtocolConstants.HostPeerId);
        public bool IsHost => Value == ProtocolConstants.HostPeerId;

        public bool Equals(PeerId other) => Value == other.Value;
        public override bool Equals(object obj) => obj is PeerId p && Equals(p);
        public override int GetHashCode() => Value;
        public override string ToString() => IsHost ? "host" : "peer" + Value;
    }

    public enum DisconnectReason : byte
    {
        Unknown = 0,
        LocalRequest,
        RemoteRequest,
        Timeout,
        HandshakeRejected,
        ProtocolError,
        TransportError,
        HostShutdown,
    }

    /// <summary>
    /// Transport-agnostic networking surface.
    ///
    /// Gameplay and session code must never reference a concrete transport. Today the
    /// only implementation is LiteNetLib over direct IP/LAN; the game ships no Steam
    /// API, so Steam lobbies and relay are unavailable to a mod and cannot be assumed.
    /// Keeping this seam means a future relay or tunnel backend is an added class, not
    /// a rewrite.
    ///
    /// Threading: implementations may receive on a background thread, but
    /// <see cref="Poll"/> must raise all callbacks on the caller's thread. Unity API
    /// access off the main thread is undefined behaviour, so decoded messages are
    /// queued and drained from <c>Poll</c> inside the game loop.
    /// </summary>
    public interface INetworkTransport : IDisposable
    {
        bool IsRunning { get; }
        bool IsHost { get; }

        /// <summary>Local peer id. Meaningful only once connected.</summary>
        PeerId LocalPeer { get; }

        /// <summary>Currently connected peers, excluding the local one.</summary>
        int ConnectedPeerCount { get; }

        void StartHost(ushort port, int maxPeers);
        void StartClient(string host, ushort port);

        /// <summary>
        /// Pumps the transport and raises queued events on the calling thread. Must be
        /// called every frame from the Unity main thread.
        /// </summary>
        void Poll();

        void Send(PeerId peer, byte[] payload, int offset, int count, DeliveryClass delivery);
        void Broadcast(byte[] payload, int offset, int count, DeliveryClass delivery);

        void Disconnect(PeerId peer, DisconnectReason reason);
        void Stop();

        event Action<PeerId> PeerConnected;
        event Action<PeerId, DisconnectReason> PeerDisconnected;

        /// <summary>
        /// A received packet. The buffer is owned by the transport and is only valid
        /// for the duration of the callback — copy anything you retain.
        /// </summary>
        event Action<PeerId, byte[], int, int> PacketReceived;

        /// <summary>Transport-level failure that is not tied to one peer.</summary>
        event Action<string> TransportError;

        NetworkStats GetStats(PeerId peer);
    }

    /// <summary>Per-peer diagnostics, surfaced in the in-game overlay.</summary>
    public struct NetworkStats
    {
        public int RoundTripMs;
        public float PacketLossPercent;
        public long BytesSent;
        public long BytesReceived;
        public int ReliableQueueDepth;
        public uint LastReceivedTick;
    }
}
