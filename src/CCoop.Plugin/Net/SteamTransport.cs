using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using CCoop.Net;
using CCoop.Net.Transport;
using CCoop.Plugin.Runtime;
using Steamworks;

namespace CCoop.Plugin.Net
{
    /// <summary>
    /// Steam Datagram Relay over SteamNetworkingSockets: NAT traversal and relaying by
    /// Valve, addressed by Steam id, no ports to forward. Connection-oriented, so peers
    /// get proper connect and disconnect events.
    ///
    /// Callbacks are dispatched by the game's own <c>SteamAPI.RunCallbacks</c>. The status
    /// callback is global: it reports every connection in the process, so each transport
    /// only acts on the connections it opened itself or accepted on its own listen socket.
    /// An older connection (a previous attempt that wasn't closed yet) must never be taken
    /// for this one, or its closing ends a healthy session.
    /// </summary>
    internal sealed class SteamTransport : ITransport
    {
        private Callback<SteamNetConnectionStatusChangedCallback_t> _statusCallback;
        private HSteamListenSocket _listen = HSteamListenSocket.Invalid;
        private HSteamNetPollGroup _group = HSteamNetPollGroup.Invalid;
        private bool _isHost;
        private bool _running;

        private readonly Dictionary<uint, int> _idByHandle = new Dictionary<uint, int>();
        private readonly Dictionary<int, HSteamNetConnection> _connById = new Dictionary<int, HSteamNetConnection>();
        private readonly HashSet<int> _announced = new HashSet<int>();
        private int _nextId = 1;
        private int _maxConnections = 8;

        private readonly IntPtr[] _inbox = new IntPtr[128];
        private byte[] _receive = new byte[64 * 1024];

        public string Kind => "Steam";
        public bool IsRunning => _running;

        public event Action<int> Connected;
        public event Action<int, string> Disconnected;
        public event Action<int, byte[], int, int> Received;
        public event Action<string> Error;

        private static SteamNetworkingConfigValue_t[] Options()
        {
            return new[]
            {
                Int(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendBufferSize, 8 * 1024 * 1024),
                Int(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMin, 256 * 1024),
                Int(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax, 8 * 1024 * 1024),
                Int(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_TimeoutConnected, 20000),
                Int(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_TimeoutInitial, 15000),
            };
        }

        private static SteamNetworkingConfigValue_t Int(ESteamNetworkingConfigValue key, int value)
        {
            var v = new SteamNetworkingConfigValue_t
            {
                m_eValue = key,
                m_eDataType = ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32,
            };
            v.m_val.m_int32 = value;
            return v;
        }

        private void Begin()
        {
            SteamNetworkingUtils.InitRelayNetworkAccess();
            _statusCallback = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(OnStatusChanged);
            _group = SteamNetworkingSockets.CreatePollGroup();
            _running = true;
        }

        public void StartHost(int maxConnections)
        {
            _isHost = true;
            _maxConnections = maxConnections;
            Begin();
            var opts = Options();
            _listen = SteamNetworkingSockets.CreateListenSocketP2P(0, opts.Length, opts);
            if (_listen == HSteamListenSocket.Invalid) throw new InvalidOperationException("Steam refused to open a listen socket");
            Log.Info("[steam] listening as " + GameInfo.SteamId);
        }

        public void Connect(ulong hostSteamId)
        {
            _isHost = false;
            Begin();
            var identity = new SteamNetworkingIdentity();
            identity.SetSteamID64(hostSteamId);
            var opts = Options();
            var conn = SteamNetworkingSockets.ConnectP2P(ref identity, 0, opts.Length, opts);
            if (conn == HSteamNetConnection.Invalid) throw new InvalidOperationException("Steam could not start a connection");
            Track(conn);
            Log.Info("[steam] connecting to " + hostSteamId);
        }

        private int Track(HSteamNetConnection conn)
        {
            if (_idByHandle.TryGetValue(conn.m_HSteamNetConnection, out var id)) return id;
            id = _nextId++;
            _idByHandle[conn.m_HSteamNetConnection] = id;
            _connById[id] = conn;
            SteamNetworkingSockets.SetConnectionPollGroup(conn, _group);
            return id;
        }

        private void OnStatusChanged(SteamNetConnectionStatusChangedCallback_t cb)
        {
            if (!_running) return;
            var conn = cb.m_hConn;
            switch (cb.m_info.m_eState)
            {
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting:
                    if (_isHost && cb.m_info.m_hListenSocket == _listen)
                    {
                        if (_connById.Count >= _maxConnections)
                        {
                            SteamNetworkingSockets.CloseConnection(conn, 0, "session full", false);
                            return;
                        }
                        var r = SteamNetworkingSockets.AcceptConnection(conn);
                        if (r != EResult.k_EResultOK)
                        {
                            SteamNetworkingSockets.CloseConnection(conn, 0, "accept failed", false);
                            Error?.Invoke("could not accept a connection: " + r);
                            return;
                        }
                        int incoming = Track(conn);
                        Log.Info("[steam] incoming connection " + incoming + " from " + Remote(cb));
                    }
                    break;

                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected:
                {
                    if (!_idByHandle.ContainsKey(conn.m_HSteamNetConnection))
                    {
                        // Not ours: another attempt's connection, or one on someone else's socket.
                        if (!_isHost || cb.m_info.m_hListenSocket != _listen)
                        {
                            Log.Info("[steam] ignored a connection to " + Remote(cb) + " this session didn't open");
                            break;
                        }
                    }
                    int id = Track(conn);
                    if (_announced.Add(id))
                    {
                        if (!_isHost) Log.Info("[steam] connection " + id + " to the host is up");
                        Connected?.Invoke(id);
                    }
                    break;
                }

                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer:
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally:
                {
                    string reason = cb.m_info.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer
                        ? "closed by the other side"
                        : "connection problem (code " + cb.m_info.m_eEndReason + ")";
                    if (_idByHandle.TryGetValue(conn.m_HSteamNetConnection, out var id))
                    {
                        SteamNetworkingSockets.CloseConnection(conn, 0, "", false);
                        Log.Info("[steam] connection " + id + " with " + Remote(cb) + " closed: " + reason + " (end " + cb.m_info.m_eEndReason + ")");
                        _idByHandle.Remove(conn.m_HSteamNetConnection);
                        _connById.Remove(id);
                        bool wasUp = _announced.Remove(id);
                        Disconnected?.Invoke(id, wasUp ? reason : "could not connect: " + reason);
                    }
                    break;
                }
            }
        }

        private static string Remote(SteamNetConnectionStatusChangedCallback_t cb)
        {
            try { return cb.m_info.m_identityRemote.GetSteamID64().ToString(); }
            catch { return "?"; }
        }

        public void Poll()
        {
            if (!_running || _group == HSteamNetPollGroup.Invalid) return;
            for (int guard = 0; guard < 64; guard++)
            {
                int n = SteamNetworkingSockets.ReceiveMessagesOnPollGroup(_group, _inbox, _inbox.Length);
                if (n <= 0) break;
                for (int i = 0; i < n; i++)
                {
                    var ptr = _inbox[i];
                    try
                    {
                        var msg = SteamNetworkingMessage_t.FromIntPtr(ptr);
                        int size = msg.m_cbSize;
                        if (size > _receive.Length) _receive = new byte[Math.Max(size, _receive.Length * 2)];
                        Marshal.Copy(msg.m_pData, _receive, 0, size);
                        if (_idByHandle.TryGetValue(msg.m_conn.m_HSteamNetConnection, out var id))
                            Received?.Invoke(id, _receive, 0, size);
                    }
                    finally
                    {
                        SteamNetworkingMessage_t.Release(ptr);
                    }
                }
            }
        }

        public void Send(int connection, byte[] data, int offset, int count, Delivery delivery)
        {
            if (!_running || !_connById.TryGetValue(connection, out var conn)) return;
            int flags = delivery == Delivery.Reliable
                ? Constants.k_nSteamNetworkingSend_Reliable
                : Constants.k_nSteamNetworkingSend_UnreliableNoNagle;
            var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                var ptr = IntPtr.Add(handle.AddrOfPinnedObject(), offset);
                var r = SteamNetworkingSockets.SendMessageToConnection(conn, ptr, (uint)count, flags, out _);
                if (r != EResult.k_EResultOK && delivery == Delivery.Reliable)
                    Error?.Invoke("reliable send failed: " + r);
            }
            finally
            {
                handle.Free();
            }
        }

        public void Disconnect(int connection, string reason)
        {
            if (!_connById.TryGetValue(connection, out var conn)) return;
            SteamNetworkingSockets.CloseConnection(conn, 0, reason ?? "", true);
            _connById.Remove(connection);
            _idByHandle.Remove(conn.m_HSteamNetConnection);
            if (_announced.Remove(connection)) Disconnected?.Invoke(connection, reason ?? "disconnected");
        }

        public int RoundTripMs(int connection) => Status(connection, out var s) ? s.m_nPing : -1;

        public float LinkQuality(int connection) =>
            Status(connection, out var s) && s.m_flConnectionQualityLocal >= 0f ? s.m_flConnectionQualityLocal : -1f;

        private bool Status(int connection, out SteamNetConnectionRealTimeStatus_t status)
        {
            status = new SteamNetConnectionRealTimeStatus_t();
            if (!_connById.TryGetValue(connection, out var conn)) return false;
            try
            {
                var lanes = new SteamNetConnectionRealTimeLaneStatus_t();
                return SteamNetworkingSockets.GetConnectionRealTimeStatus(conn, ref status, 0, ref lanes) == EResult.k_EResultOK;
            }
            catch { return false; }
        }

        /// <summary>The peer's Steam id, as authenticated by Steam for this connection.</summary>
        public string RemoteAddress(int connection)
        {
            if (!_connById.TryGetValue(connection, out var conn)) return "";
            try
            {
                if (SteamNetworkingSockets.GetConnectionInfo(conn, out var info) && !info.m_identityRemote.IsInvalid())
                    return info.m_identityRemote.GetSteamID64().ToString();
            }
            catch { /* fall through */ }
            return "";
        }

        public void Shutdown()
        {
            if (!_running) return;
            _running = false;
            foreach (var conn in _connById.Values) SteamNetworkingSockets.CloseConnection(conn, 0, "shutdown", true);
            _connById.Clear();
            _idByHandle.Clear();
            _announced.Clear();
            if (_listen != HSteamListenSocket.Invalid) SteamNetworkingSockets.CloseListenSocket(_listen);
            if (_group != HSteamNetPollGroup.Invalid) SteamNetworkingSockets.DestroyPollGroup(_group);
            _listen = HSteamListenSocket.Invalid;
            _group = HSteamNetPollGroup.Invalid;
            _statusCallback?.Dispose();
            _statusCallback = null;
        }

        public void Dispose() => Shutdown();
    }
}
