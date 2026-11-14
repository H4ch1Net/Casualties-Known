using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using LiteNetLib;

namespace CCoop.Net.Transport
{
    /// <summary>
    /// Direct UDP over LiteNetLib: LAN play, VPN tunnels (Tailscale, Radmin, ZeroTier)
    /// and two instances on one machine for testing.
    /// </summary>
    public sealed class LiteNetTransport : ITransport, INetEventListener
    {
        private const string Key = "ccoop-2";

        private NetManager _net;
        private bool _isHost;
        private int _maxConnections;
        private readonly Dictionary<int, NetPeer> _peers = new Dictionary<int, NetPeer>();

        public string Kind => "LAN/IP";
        public bool IsRunning => _net != null && _net.IsRunning;
        public int LocalPort => _net?.LocalPort ?? 0;

        public event Action<int> Connected;
        public event Action<int, string> Disconnected;
        public event Action<int, byte[], int, int> Received;
        public event Action<string> Error;

        private NetManager Create() => new NetManager(this)
        {
            ChannelsCount = 2,
            AutoRecycle = true,
            UnsyncedEvents = false,
            DisconnectTimeout = 15000,
            UpdateTime = 10,
            IPv6Enabled = false,
            EnableStatistics = true,
        };

        public void StartHost(int port, int maxConnections)
        {
            if (IsRunning) throw new InvalidOperationException("already running");
            _isHost = true;
            _maxConnections = maxConnections;
            _net = Create();
            if (!_net.Start(port)) throw new InvalidOperationException("could not open UDP port " + port);
        }

        public void StartClient(string address, int port)
        {
            if (IsRunning) throw new InvalidOperationException("already running");
            _isHost = false;
            _net = Create();
            if (!_net.Start()) throw new InvalidOperationException("could not open a UDP socket");
            _net.Connect(address, port, Key);
        }

        /// <summary>Test hook: artificial loss and latency.</summary>
        public void Simulate(int lossPercent, int minLatencyMs, int maxLatencyMs)
        {
            _net.SimulatePacketLoss = lossPercent > 0;
            _net.SimulationPacketLossChance = lossPercent;
            _net.SimulateLatency = maxLatencyMs > 0;
            _net.SimulationMinLatency = minLatencyMs;
            _net.SimulationMaxLatency = maxLatencyMs;
        }

        public void Poll() => _net?.PollEvents();

        public void Send(int connection, byte[] data, int offset, int count, Delivery delivery)
        {
            if (!_peers.TryGetValue(connection, out var peer)) return;
            if (delivery == Delivery.Unreliable)
                peer.Send(data, offset, count, 1, DeliveryMethod.Sequenced);
            else
                peer.Send(data, offset, count, 0, DeliveryMethod.ReliableOrdered);
        }

        public void Disconnect(int connection, string reason)
        {
            if (_peers.TryGetValue(connection, out var peer)) peer.Disconnect();
        }

        public int RoundTripMs(int connection) =>
            _peers.TryGetValue(connection, out var p) ? p.Ping * 2 : -1;

        public float LinkQuality(int connection)
        {
            if (!_peers.TryGetValue(connection, out var p) || p.Statistics == null) return -1f;
            float loss = p.Statistics.PacketLossPercent;
            return loss < 0f ? -1f : 1f - Math.Min(100f, loss) / 100f;
        }

        public string RemoteAddress(int connection) =>
            _peers.TryGetValue(connection, out var p) ? p.Address + ":" + p.Port : "";

        public void Shutdown()
        {
            if (_net == null) return;
            _net.Stop(true);
            _net = null;
            _peers.Clear();
        }

        public void Dispose() => Shutdown();

        // ---- INetEventListener ----

        void INetEventListener.OnConnectionRequest(ConnectionRequest request)
        {
            if (!_isHost || _net.ConnectedPeersCount >= _maxConnections) { request.Reject(); return; }
            request.AcceptIfKey(Key);
        }

        void INetEventListener.OnPeerConnected(NetPeer peer)
        {
            _peers[peer.Id] = peer;
            Connected?.Invoke(peer.Id);
        }

        void INetEventListener.OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
        {
            _peers.Remove(peer.Id);
            Disconnected?.Invoke(peer.Id, Describe(info.Reason));
        }

        void INetEventListener.OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod method)
        {
            Received?.Invoke(peer.Id, reader.RawData, reader.UserDataOffset, reader.UserDataSize);
        }

        void INetEventListener.OnNetworkError(IPEndPoint endPoint, SocketError socketError) =>
            Error?.Invoke("socket error " + socketError + " (" + endPoint + ")");

        void INetEventListener.OnNetworkReceiveUnconnected(IPEndPoint remote, NetPacketReader reader, UnconnectedMessageType type) { }
        void INetEventListener.OnNetworkLatencyUpdate(NetPeer peer, int latency) { }

        private static string Describe(DisconnectReason r)
        {
            switch (r)
            {
                case DisconnectReason.ConnectionFailed: return "could not reach the host";
                case DisconnectReason.Timeout: return "connection timed out";
                case DisconnectReason.ConnectionRejected: return "connection refused";
                case DisconnectReason.RemoteConnectionClose: return "closed by the other side";
                case DisconnectReason.DisconnectPeerCalled: return "disconnected";
                case DisconnectReason.HostUnreachable:
                case DisconnectReason.NetworkUnreachable: return "network unreachable";
                default: return r.ToString();
            }
        }
    }
}
