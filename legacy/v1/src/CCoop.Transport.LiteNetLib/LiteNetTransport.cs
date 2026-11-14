using System;
using System.Collections.Generic;
using CCoop.Protocol;
using LiteNetLib;

namespace CCoop.Transport.LiteNet
{
    /// <summary>
    /// <see cref="INetworkTransport"/> over LiteNetLib (direct IP / LAN).
    ///
    /// The game ships no Steam API, so Steam lobbies, invites and relay are simply
    /// unavailable to a mod; direct IP with user-provided tunnelling is the only route.
    /// This class exists behind the interface so a future relay backend is an addition
    /// rather than a rewrite.
    ///
    /// Threading: <see cref="NetManager.UnsyncedEvents"/> is left false, so LiteNetLib
    /// raises all callbacks synchronously inside <see cref="Poll"/>. That is what makes
    /// it safe for the session layer to touch Unity objects from our event handlers —
    /// Poll is called from the Unity main thread.
    /// </summary>
    public sealed class LiteNetTransport : INetworkTransport, INetEventListener
    {
        /// <summary>
        /// Rejects unrelated UDP traffic before a connection is established. Not a
        /// security boundary — the real compatibility check is the Hello handshake.
        /// </summary>
        public const string ConnectionKey = "ccoop-v1";

        private NetManager _manager;
        private readonly Dictionary<int, PeerId> _peerIdByLiteId = new Dictionary<int, PeerId>();
        private readonly Dictionary<byte, NetPeer> _peerByCoopId = new Dictionary<byte, NetPeer>();
        private int _maxPeers = ProtocolConstants.MaxPeers;

        public bool IsRunning => _manager != null && _manager.IsRunning;

        /// <summary>
        /// The UDP port actually bound. Pass port 0 to <see cref="StartHost"/> to let the
        /// OS pick one, then read it here — this is what lets tests run in parallel
        /// without colliding on a hardcoded port.
        /// </summary>
        public int LocalPort => _manager?.LocalPort ?? 0;
        public bool IsHost { get; private set; }
        public PeerId LocalPeer { get; set; }
        public int ConnectedPeerCount => _manager?.ConnectedPeersCount ?? 0;

        public event Action<PeerId> PeerConnected;
        public event Action<PeerId, DisconnectReason> PeerDisconnected;
        public event Action<PeerId, byte[], int, int> PacketReceived;
        public event Action<string> TransportError;

        // ---------------------------------------------------------------- channels

        /// <summary>
        /// LiteNetLib needs one channel per ordered stream, or a stalled reliable
        /// transfer (e.g. a fragmented world snapshot) would head-of-line block
        /// unrelated traffic such as movement or handshake messages.
        /// </summary>
        private const byte ChannelCount = 6;

        private static byte ChannelFor(DeliveryClass d) => (byte)d;

        private static DeliveryMethod MethodFor(DeliveryClass d)
        {
            switch (d)
            {
                case DeliveryClass.Snapshots:
                    // Newest-wins. Dropping a stale movement frame is correct behaviour;
                    // retransmitting it would add latency for no benefit.
                    return DeliveryMethod.Sequenced;

                case DeliveryClass.Diagnostics:
                    return DeliveryMethod.ReliableUnordered;

                case DeliveryClass.Control:
                case DeliveryClass.Transactions:
                case DeliveryClass.EntityLifecycle:
                case DeliveryClass.Bulk:
                default:
                    return DeliveryMethod.ReliableOrdered;
            }
        }

        // ---------------------------------------------------------------- lifecycle

        private NetManager CreateManager()
        {
            return new NetManager(this)
            {
                ChannelsCount = ChannelCount,
                AutoRecycle = true,
                UnsyncedEvents = false,
                DisconnectTimeout = 10000,
                UpdateTime = 15,
                EnableStatistics = true,
            };
        }

        public void StartHost(ushort port, int maxPeers)
        {
            if (IsRunning) throw new InvalidOperationException("transport already running");
            if (maxPeers < 1 || maxPeers > ProtocolConstants.MaxPeers)
                throw new ArgumentOutOfRangeException(nameof(maxPeers),
                    "maxPeers must be 1.." + ProtocolConstants.MaxPeers);

            _maxPeers = maxPeers;
            IsHost = true;
            LocalPeer = PeerId.Host;
            _peerByCoopId.Clear();
            _peerIdByLiteId.Clear();

            _manager = CreateManager();
            if (!_manager.Start(port))
                throw new InvalidOperationException("could not bind UDP port " + port);
        }

        public void StartClient(string host, ushort port)
        {
            if (IsRunning) throw new InvalidOperationException("transport already running");
            if (string.IsNullOrEmpty(host)) throw new ArgumentException("host required", nameof(host));

            IsHost = false;
            _manager = CreateManager();
            if (!_manager.Start())
                throw new InvalidOperationException("could not start UDP socket");

            _manager.Connect(host, port, ConnectionKey);
        }

        public void Poll()
        {
            // Raises all queued callbacks on this thread. Must be the Unity main thread.
            _manager?.PollEvents();
        }

        public void Stop()
        {
            if (_manager == null) return;
            _manager.Stop();
            _manager = null;
            _peerIdByLiteId.Clear();
            _peerByCoopId.Clear();
            IsHost = false;
        }

        public void Dispose() => Stop();

        // ---------------------------------------------------------------- sending

        public void Send(PeerId peer, byte[] payload, int offset, int count, DeliveryClass delivery)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            if (count > ProtocolConstants.MaxPacketBytes)
                throw new ArgumentOutOfRangeException(nameof(count),
                    "packet of " + count + " exceeds MaxPacketBytes");

            if (_manager == null) return;

            if (!IsHost)
            {
                // A client only ever talks to the host.
                var host = FirstPeer();
                host?.Send(payload, offset, count, ChannelFor(delivery), MethodFor(delivery));
                return;
            }

            if (_peerByCoopId.TryGetValue(peer.Value, out var target))
                target.Send(payload, offset, count, ChannelFor(delivery), MethodFor(delivery));
        }

        public void Broadcast(byte[] payload, int offset, int count, DeliveryClass delivery)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            if (count > ProtocolConstants.MaxPacketBytes)
                throw new ArgumentOutOfRangeException(nameof(count),
                    "packet of " + count + " exceeds MaxPacketBytes");

            _manager?.SendToAll(payload, offset, count, ChannelFor(delivery), MethodFor(delivery));
        }

        public void Disconnect(PeerId peer, DisconnectReason reason)
        {
            if (_manager == null) return;

            if (!IsHost)
            {
                FirstPeer()?.Disconnect();
                return;
            }

            if (_peerByCoopId.TryGetValue(peer.Value, out var target))
                target.Disconnect();
        }

        private NetPeer FirstPeer()
        {
            foreach (var p in _manager.ConnectedPeerList) return p;
            return null;
        }

        // ---------------------------------------------------------------- stats

        public NetworkStats GetStats(PeerId peer)
        {
            var stats = new NetworkStats();
            if (_manager == null) return stats;

            NetPeer p = IsHost
                ? (_peerByCoopId.TryGetValue(peer.Value, out var found) ? found : null)
                : FirstPeer();

            if (p != null)
            {
                stats.RoundTripMs = p.Ping * 2;
                stats.PacketLossPercent = p.Statistics != null ? p.Statistics.PacketLossPercent : 0f;
                stats.BytesSent = p.Statistics != null ? p.Statistics.BytesSent : 0;
                stats.BytesReceived = p.Statistics != null ? p.Statistics.BytesReceived : 0;
            }
            return stats;
        }

        /// <summary>
        /// Enables LiteNetLib's built-in impairment simulation. Used by the test harness
        /// to exercise the protocol under loss and latency without real network shaping.
        /// </summary>
        public void SimulateConditions(int packetLossPercent, int minLatencyMs, int maxLatencyMs)
        {
            if (_manager == null) throw new InvalidOperationException("transport not started");

            _manager.SimulatePacketLoss = packetLossPercent > 0;
            _manager.SimulationPacketLossChance = packetLossPercent;
            _manager.SimulateLatency = maxLatencyMs > 0;
            _manager.SimulationMinLatency = minLatencyMs;
            _manager.SimulationMaxLatency = maxLatencyMs;
        }

        // ---------------------------------------------------------------- listener

        public void OnConnectionRequest(ConnectionRequest request)
        {
            if (!IsHost) { request.Reject(); return; }

            // Enforce the session cap here, at the transport edge, so a full session
            // never allocates peer state for a connection it cannot serve.
            if (_manager.ConnectedPeersCount >= _maxPeers) { request.Reject(); return; }

            request.AcceptIfKey(ConnectionKey);
        }

        /// <summary>
        /// Allocates the lowest unused peer id in 1..MaxPeers-1.
        ///
        /// Ids MUST be recycled on disconnect. A monotonic counter looks fine in a short
        /// test and then refuses the 8th ever-joining player on a host that has only two
        /// connected — the id space is 3 bits, not a session counter. Caught by
        /// <c>Repeated_connect_disconnect_cycles_leak_no_peer_state</c>.
        /// </summary>
        private bool TryAllocatePeerId(out byte id)
        {
            for (byte candidate = 1; candidate < ProtocolConstants.MaxPeers; candidate++)
            {
                if (!_peerByCoopId.ContainsKey(candidate))
                {
                    id = candidate;
                    return true;
                }
            }
            id = 0;
            return false;
        }

        public void OnPeerConnected(NetPeer peer)
        {
            PeerId id;
            if (IsHost)
            {
                if (!TryAllocatePeerId(out byte allocated))
                {
                    peer.Disconnect();
                    return;
                }
                id = new PeerId(allocated);
                _peerByCoopId[id.Value] = peer;
            }
            else
            {
                // From a client's view the only peer is the host.
                id = PeerId.Host;
            }

            _peerIdByLiteId[peer.Id] = id;
            PeerConnected?.Invoke(id);
        }

        public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
        {
            if (!_peerIdByLiteId.TryGetValue(peer.Id, out var id)) id = PeerId.Host;

            _peerIdByLiteId.Remove(peer.Id);
            _peerByCoopId.Remove(id.Value);

            PeerDisconnected?.Invoke(id, Translate(disconnectInfo.Reason));
        }

        private static DisconnectReason Translate(LiteNetLib.DisconnectReason r)
        {
            switch (r)
            {
                case LiteNetLib.DisconnectReason.ConnectionFailed:
                case LiteNetLib.DisconnectReason.HostUnreachable:
                case LiteNetLib.DisconnectReason.NetworkUnreachable:
                    return DisconnectReason.TransportError;
                case LiteNetLib.DisconnectReason.Timeout:
                case LiteNetLib.DisconnectReason.ConnectionRejected:
                    return r == LiteNetLib.DisconnectReason.Timeout
                        ? DisconnectReason.Timeout
                        : DisconnectReason.HandshakeRejected;
                case LiteNetLib.DisconnectReason.RemoteConnectionClose:
                    return DisconnectReason.RemoteRequest;
                case LiteNetLib.DisconnectReason.DisconnectPeerCalled:
                    return DisconnectReason.LocalRequest;
                default:
                    return DisconnectReason.Unknown;
            }
        }

        public void OnNetworkReceive(NetPeer peer, NetPacketReader reader,
                                     byte channelNumber, DeliveryMethod deliveryMethod)
        {
            if (!_peerIdByLiteId.TryGetValue(peer.Id, out var id)) id = PeerId.Host;

            // Handed to the session layer without copying; AutoRecycle reclaims the
            // buffer once this returns, which is why the interface documents the buffer
            // as valid only for the callback's duration.
            PacketReceived?.Invoke(id, reader.RawData, reader.UserDataOffset, reader.UserDataSize);
        }

        public void OnNetworkError(System.Net.IPEndPoint endPoint, System.Net.Sockets.SocketError socketError)
            => TransportError?.Invoke("socket error " + socketError + " from " + endPoint);

        public void OnNetworkLatencyUpdate(NetPeer peer, int latency) { }

        public void OnNetworkReceiveUnconnected(System.Net.IPEndPoint remoteEndPoint,
                                                NetPacketReader reader, UnconnectedMessageType messageType)
            => reader.Recycle();
    }
}
