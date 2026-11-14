using System;
using System.Collections.Generic;
using System.Diagnostics;
using CCoop.Net.Transport;
using CCoop.Net.Wire;

namespace CCoop.Net.Session
{
    public sealed class PeerInfo
    {
        public PeerInfo(byte id, string name)
        {
            Id = id;
            Name = name ?? "";
        }

        public byte Id { get; }
        public string Name { get; internal set; }

        /// <summary>Round trip to the host in ms (host-measured), or -1.</summary>
        public int RoundTripMs { get; internal set; } = -1;

        /// <summary>Link quality to the host, 0..1 (host-measured), or -1.</summary>
        public float Quality { get; internal set; } = -1f;

        /// <summary>Host only: the transport-verified address (Steam id or ip:port).</summary>
        public string Address { get; internal set; } = "";

        public bool IsHost => Id == NetSession.HostId;
        public override string ToString() => Name + "#" + Id;
    }

    public enum SessionState : byte
    {
        Connecting,
        Established,
        Ended,
    }

    /// <summary>What the host checks a joining client against.</summary>
    public sealed class SessionConfig
    {
        public string ModVersion = "";
        public string GameBuild = "";
        public string PlayerName = "Player";
        public int MaxPlayers = NetSession.MaxPeers;
        public string Password = "";

        /// <summary>
        /// Host only: decides whether a joiner may come in, given its hello and its
        /// transport-verified address. Returns a reason to refuse, or null to admit.
        /// </summary>
        public Func<Hello, string, string> ExtraAdmission;
    }

    /// <summary>
    /// A host or client session over any <see cref="ITransport"/>.
    ///
    /// Star topology: clients talk only to the host, which relays. Every packet carries
    /// its origin peer, which the host overwrites with the connection's real id, so a
    /// client cannot speak for anybody else.
    ///
    /// All handlers run inside <see cref="Poll"/>, on the caller's thread. Messages a
    /// peer sends to itself (host broadcasting with <c>includeSelf</c>, or the host
    /// calling <see cref="SendToHost{T}"/>) are queued and dispatched on the next poll
    /// rather than synchronously, so calling Send from inside a game hook never
    /// re-enters game code.
    /// </summary>
    public sealed class NetSession : IDisposable
    {
        public const byte HostId = 0;
        public const byte Everyone = 0xFF;
        public const int MaxPeers = 8;
        public const int MaxBlobTotal = 64 * 1024 * 1024;
        public const int BlobChunkBytes = 48 * 1024;
        private const int HeaderSize = 6;
        private const byte Magic0 = 0xCC, Magic1 = 0x02;

        private readonly ITransport _transport;
        private readonly MessageRegistry _registry;
        private readonly SessionConfig _config;
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        private readonly Dictionary<byte, PeerInfo> _peers = new Dictionary<byte, PeerInfo>();
        private readonly Dictionary<int, byte> _peerByConnection = new Dictionary<int, byte>();
        private readonly Dictionary<byte, int> _connectionByPeer = new Dictionary<byte, int>();
        private readonly Dictionary<int, double> _pendingSince = new Dictionary<int, double>();
        private readonly List<KeyValuePair<int, double>> _closing = new List<KeyValuePair<int, double>>();

        private readonly Dictionary<Type, List<Action<byte, IMessage>>> _handlers = new Dictionary<Type, List<Action<byte, IMessage>>>();
        private readonly Queue<KeyValuePair<byte, IMessage>> _selfQueue = new Queue<KeyValuePair<byte, IMessage>>();
        private readonly PacketWriter _writer = new PacketWriter(1024);
        private readonly Dictionary<long, BlobAssembly> _blobs = new Dictionary<long, BlobAssembly>();
        private uint _nextTransfer = 1;
        private double _nextPingTable;
        private int _hostConnection = -1;

        private NetSession(ITransport transport, MessageRegistry registry, SessionConfig config, bool host)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _config = config ?? new SessionConfig();
            IsHost = host;
            _transport.Connected += OnConnected;
            _transport.Disconnected += OnDisconnected;
            _transport.Received += OnReceived;
            _transport.Error += e => Log?.Invoke("transport: " + e);

            On<Hello>(HandleHello);
            On<Welcome>(HandleWelcome);
            On<Reject>(HandleReject);
            On<PeerJoined>(HandlePeerJoined);
            On<PeerLeft>(HandlePeerLeft);
            On<BlobChunk>(HandleBlobChunk);
            On<PingTable>(HandlePingTable);

            if (host)
            {
                LocalId = HostId;
                _peers[HostId] = new PeerInfo(HostId, _config.PlayerName);
                State = SessionState.Established;
            }
        }

        public static NetSession CreateHost(ITransport t, MessageRegistry r, SessionConfig c) => new NetSession(t, r, c, true);
        public static NetSession CreateClient(ITransport t, MessageRegistry r, SessionConfig c) => new NetSession(t, r, c, false);

        public bool IsHost { get; }
        public byte LocalId { get; private set; } = Everyone;
        public SessionState State { get; private set; } = SessionState.Connecting;
        public string EndReason { get; private set; } = "";
        public ITransport Transport => _transport;
        public double Time => _clock.Elapsed.TotalSeconds;

        public PeerInfo LocalPeer => _peers.TryGetValue(LocalId, out var p) ? p : null;

        /// <summary>Everyone in the session, including the local peer.</summary>
        public IEnumerable<PeerInfo> Peers => _peers.Values;
        public int PeerCount => _peers.Count;
        public bool TryGetPeer(byte id, out PeerInfo p) => _peers.TryGetValue(id, out p);

        public event Action Established;
        public event Action<string> Ended;
        public event Action<PeerInfo> PeerJoinedEvent;
        public event Action<PeerInfo, string> PeerLeftEvent;
        public event Action<string> Log;

        /// <summary>A reassembled blob: (origin, tag, data).</summary>
        public event Action<byte, ushort, byte[]> BlobReceived;

        /// <summary>Blob progress: (origin, tag, bytes so far, total bytes).</summary>
        public event Action<byte, ushort, int, int> BlobProgress;

        // ------------------------------------------------------------------ handlers

        public void On<T>(Action<byte, T> handler) where T : IMessage
        {
            if (!_handlers.TryGetValue(typeof(T), out var list))
                _handlers[typeof(T)] = list = new List<Action<byte, IMessage>>();
            list.Add((origin, m) => handler(origin, (T)m));
        }

        // ------------------------------------------------------------------ sending

        /// <summary>To every other peer. Clients go through the host.</summary>
        public void Broadcast<T>(T msg, bool includeSelf = false) where T : IMessage
        {
            if (State != SessionState.Established) return;
            Route(msg, Everyone);
            if (includeSelf) _selfQueue.Enqueue(new KeyValuePair<byte, IMessage>(LocalId, msg));
        }

        public void SendToHost<T>(T msg) where T : IMessage
        {
            if (State != SessionState.Established) return;
            if (IsHost) _selfQueue.Enqueue(new KeyValuePair<byte, IMessage>(LocalId, msg));
            else Route(msg, HostId);
        }

        public void SendTo<T>(byte peer, T msg) where T : IMessage
        {
            if (State != SessionState.Established) return;
            if (peer == LocalId) _selfQueue.Enqueue(new KeyValuePair<byte, IMessage>(LocalId, msg));
            else Route(msg, peer);
        }

        /// <summary>
        /// Host only: sends to one client as if <paramref name="origin"/> had sent it.
        /// Used for host-mediated relays, where the host decides per recipient whether a
        /// client's message should be passed on.
        /// </summary>
        public void RelayTo<T>(byte peer, byte origin, T msg) where T : IMessage
        {
            if (!IsHost || State != SessionState.Established) return;
            if (!_connectionByPeer.TryGetValue(peer, out var conn)) return;
            var entry = _registry.Get(typeof(T));
            Encode(entry.Id, origin, peer, msg);
            SendRaw(conn, entry.Delivery);
        }

        private void Route<T>(T msg, byte target) where T : IMessage
        {
            var entry = _registry.Get(typeof(T));
            Encode(entry.Id, LocalId, target, msg);
            if (IsHost)
            {
                if (target == Everyone)
                {
                    foreach (var kv in _connectionByPeer) SendRaw(kv.Value, entry.Delivery);
                }
                else if (_connectionByPeer.TryGetValue(target, out var conn))
                {
                    SendRaw(conn, entry.Delivery);
                }
            }
            else if (_hostConnection >= 0)
            {
                SendRaw(_hostConnection, entry.Delivery);
            }
        }

        private void Encode(ushort typeId, byte origin, byte target, IMessage msg)
        {
            _writer.Reset();
            _writer.WriteByte(Magic0);
            _writer.WriteByte(Magic1);
            _writer.WriteUInt16(typeId);
            _writer.WriteByte(origin);
            _writer.WriteByte(target);
            msg.Write(_writer);
        }

        private void SendRaw(int connection, Delivery d)
        {
            var buf = _writer.Buffer;
            Traffic.CountSent((ushort)(buf[2] | (buf[3] << 8)), _writer.Length);
            _transport.Send(connection, buf, 0, _writer.Length, d);
        }

        /// <summary>What this machine sends and receives, by message type.</summary>
        public TrafficStats Traffic { get; } = new TrafficStats();

        /// <summary>A message type's name, for traffic reports.</summary>
        public string TypeName(ushort id) => _registry.TryGet(id, out var e) ? e.Type.Name : "#" + id;

        /// <summary>Splits a large payload into reliable chunks. Returns the transfer id.</summary>
        public uint SendBlob(byte target, ushort tag, byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (data.Length > MaxBlobTotal) throw new ArgumentException("blob too large");
            uint id = _nextTransfer++;
            int count = Math.Max(1, (data.Length + BlobChunkBytes - 1) / BlobChunkBytes);
            for (int i = 0; i < count; i++)
            {
                int off = i * BlobChunkBytes;
                int len = Math.Min(BlobChunkBytes, data.Length - off);
                var piece = new byte[len];
                Buffer.BlockCopy(data, off, piece, 0, len);
                var chunk = new BlobChunk
                {
                    TransferId = id, Tag = tag, Index = (ushort)i, Count = (ushort)count,
                    TotalLength = data.Length, Data = piece,
                };
                if (target == Everyone) Broadcast(chunk);
                else SendTo(target, chunk);
            }
            return id;
        }

        // ------------------------------------------------------------------ admin

        public void Kick(byte peer, string reason)
        {
            if (!IsHost || peer == HostId) return;
            if (!_connectionByPeer.TryGetValue(peer, out var conn)) return;
            Encode(SessionMessages.RejectId, HostId, peer, new Reject { Reason = reason });
            SendRaw(conn, Delivery.Reliable);
            _closing.Add(new KeyValuePair<int, double>(conn, Time + 0.5));
        }

        public void Leave(string reason = "left the session")
        {
            End(reason);
        }

        private void End(string reason)
        {
            if (State == SessionState.Ended) return;
            State = SessionState.Ended;
            EndReason = reason;
            try { _transport.Shutdown(); } catch { /* best effort */ }
            Ended?.Invoke(reason);
        }

        public void Dispose() => End("closed");

        // ------------------------------------------------------------------ pump

        public void Poll()
        {
            if (State == SessionState.Ended) return;
            _transport.Poll();

            while (_selfQueue.Count > 0)
            {
                var kv = _selfQueue.Dequeue();
                Dispatch(kv.Key, kv.Value);
            }

            if (!IsHost) return;

            double now = Time;
            for (int i = _closing.Count - 1; i >= 0; i--)
            {
                if (now < _closing[i].Value) continue;
                _transport.Disconnect(_closing[i].Key, "closed by host");
                _closing.RemoveAt(i);
            }

            List<int> expired = null;
            foreach (var kv in _pendingSince)
                if (now - kv.Value > 15) (expired ?? (expired = new List<int>())).Add(kv.Key);
            if (expired != null)
                foreach (var c in expired)
                {
                    _pendingSince.Remove(c);
                    _transport.Disconnect(c, "handshake timed out");
                }

            if (now >= _nextPingTable && _connectionByPeer.Count > 0)
            {
                _nextPingTable = now + 2;
                var table = new PingTable();
                foreach (var kv in _connectionByPeer)
                {
                    int rtt = _transport.RoundTripMs(kv.Value);
                    float q = _transport.LinkQuality(kv.Value);
                    if (_peers.TryGetValue(kv.Key, out var p))
                    {
                        p.RoundTripMs = rtt;
                        p.Quality = q;
                    }
                    table.Entries.Add(new PingTable.Entry
                    {
                        Peer = kv.Key,
                        RoundTripMs = (ushort)Math.Max(0, Math.Min(rtt, 65000)),
                        Quality = q < 0f ? (byte)255 : (byte)Math.Round(Math.Min(1f, q) * 254f),
                    });
                }
                Broadcast(table);
            }
        }

        // ------------------------------------------------------------------ transport events

        private void OnConnected(int connection)
        {
            if (IsHost)
            {
                _pendingSince[connection] = Time;
                Log?.Invoke("connection " + connection + " opened; awaiting handshake");
                return;
            }
            _hostConnection = connection;
            var hello = new Hello
            {
                RegistryHash = _registry.Hash,
                ModVersion = _config.ModVersion,
                GameBuild = _config.GameBuild,
                PlayerName = _config.PlayerName,
                Password = _config.Password,
            };
            Encode(SessionMessages.HelloId, Everyone, HostId, hello);
            SendRaw(connection, Delivery.Reliable);
        }

        private void OnDisconnected(int connection, string reason)
        {
            if (!IsHost)
            {
                End(State == SessionState.Established ? "lost connection to the host: " + reason : reason);
                return;
            }
            _pendingSince.Remove(connection);
            if (!_peerByConnection.TryGetValue(connection, out var id)) return;
            _peerByConnection.Remove(connection);
            _connectionByPeer.Remove(id);
            if (_peers.TryGetValue(id, out var peer))
            {
                _peers.Remove(id);
                Broadcast(new PeerLeft { Id = id, Reason = reason });
                PeerLeftEvent?.Invoke(peer, reason);
            }
        }

        private void OnReceived(int connection, byte[] data, int offset, int count)
        {
            if (count < HeaderSize || data[offset] != Magic0 || data[offset + 1] != Magic1) return;
            ushort typeId = (ushort)(data[offset + 2] | (data[offset + 3] << 8));
            byte origin = data[offset + 4];
            byte target = data[offset + 5];

            if (!_registry.TryGet(typeId, out var entry)) return;
            Traffic.CountReceived(typeId, count);

            if (IsHost)
            {
                bool known = _peerByConnection.TryGetValue(connection, out var sender);
                if (!known)
                {
                    // Only a Hello may come from a connection that has not handshaken.
                    if (typeId != SessionMessages.HelloId) return;
                    var hello = Decode(entry, data, offset, count);
                    if (hello != null) AdmitOrReject(connection, (Hello)hello);
                    return;
                }
                origin = sender;  // never trust a client's claim about who it is

                if (target != HostId && target != Everyone)
                {
                    // Directed at another client: forward without decoding.
                    if (_connectionByPeer.TryGetValue(target, out var to)) Forward(to, data, offset, count, origin, entry.Delivery);
                    return;
                }

                var msg = Decode(entry, data, offset, count);
                if (msg == null) return;
                if (IsSessionOnly(typeId)) return;  // clients may not send session control

                if (target == Everyone)
                    foreach (var kv in _connectionByPeer)
                        if (kv.Key != sender) Forward(kv.Value, data, offset, count, origin, entry.Delivery);

                Dispatch(origin, msg);
                return;
            }

            var decoded = Decode(entry, data, offset, count);
            if (decoded == null) return;
            if (State != SessionState.Established && typeId != SessionMessages.WelcomeId && typeId != SessionMessages.RejectId)
                return;
            Dispatch(origin, decoded);
        }

        private static bool IsSessionOnly(ushort id) =>
            id == SessionMessages.HelloId || id == SessionMessages.WelcomeId || id == SessionMessages.RejectId ||
            id == SessionMessages.PeerJoinedId || id == SessionMessages.PeerLeftId || id == SessionMessages.PingTableId;

        private readonly byte[] _forward = new byte[WireLimits.MaxPacketBytes];

        private void Forward(int connection, byte[] data, int offset, int count, byte origin, Delivery d)
        {
            if (count > _forward.Length) return;
            Buffer.BlockCopy(data, offset, _forward, 0, count);
            _forward[4] = origin;
            Traffic.CountSent((ushort)(_forward[2] | (_forward[3] << 8)), count);
            _transport.Send(connection, _forward, 0, count, d);
        }

        private IMessage Decode(MessageRegistry.Entry entry, byte[] data, int offset, int count)
        {
            var msg = entry.Create();
            var r = new PacketReader(data, offset + HeaderSize, count - HeaderSize);
            try { msg.Read(ref r); }
            catch (Exception e)
            {
                Log?.Invoke("decoder for " + entry.Type.Name + " threw: " + e.Message);
                return null;
            }
            if (r.Failed || !r.AtEnd)
            {
                Log?.Invoke("dropped malformed " + entry.Type.Name + " (" + count + " B)");
                return null;
            }
            return msg;
        }

        private void Dispatch(byte origin, IMessage msg)
        {
            if (!_handlers.TryGetValue(msg.GetType(), out var list)) return;
            for (int i = 0; i < list.Count; i++)
            {
                try { list[i](origin, msg); }
                catch (Exception e) { Log?.Invoke("handler for " + msg.GetType().Name + " threw: " + e); }
            }
        }

        // ------------------------------------------------------------------ handshake

        private void AdmitOrReject(int connection, Hello hello)
        {
            _pendingSince.Remove(connection);
            string reason = null;
            if (hello.RegistryHash != _registry.Hash)
                reason = "Your co-op mod build does not match the host's (protocol " + hello.RegistryHash + " vs " + _registry.Hash + "). Install the same version.";
            else if (hello.ModVersion != _config.ModVersion)
                reason = "Mod version mismatch: you have " + hello.ModVersion + ", the host has " + _config.ModVersion + ".";
            else if (hello.GameBuild != _config.GameBuild)
                reason = "Your game version differs from the host's. Both players must be on the same Steam build.";
            else if (!string.IsNullOrEmpty(_config.Password) && hello.Password != _config.Password)
                reason = "Wrong password.";
            else if (_peers.Count >= Math.Min(_config.MaxPlayers, MaxPeers))
                reason = "The session is full.";
            else if (_config.ExtraAdmission != null)
                reason = _config.ExtraAdmission(hello, _transport.RemoteAddress(connection));

            byte id = 0;
            if (reason == null && !TryAllocate(out id)) reason = "The session is full.";

            if (reason != null)
            {
                Log?.Invoke("rejected connection " + connection + ": " + reason);
                Encode(SessionMessages.RejectId, HostId, Everyone, new Reject { Reason = reason });
                SendRaw(connection, Delivery.Reliable);
                _closing.Add(new KeyValuePair<int, double>(connection, Time + 0.5));
                return;
            }

            string name = Sanitize(hello.PlayerName, id);
            var peer = new PeerInfo(id, name) { Address = _transport.RemoteAddress(connection) ?? "" };

            var welcome = new Welcome { YourId = id };
            foreach (var p in _peers.Values) welcome.Peers.Add(p);
            Encode(SessionMessages.WelcomeId, HostId, id, welcome);
            SendRaw(connection, Delivery.Reliable);

            Broadcast(new PeerJoined { Id = id, Name = name });

            _peers[id] = peer;
            _peerByConnection[connection] = id;
            _connectionByPeer[id] = connection;
            Log?.Invoke(name + " joined as peer " + id);
            PeerJoinedEvent?.Invoke(peer);
        }

        private string Sanitize(string name, byte id)
        {
            name = (name ?? "").Trim();
            if (name.Length > 24) name = name.Substring(0, 24);
            if (name.Length == 0) name = "Player " + id;
            foreach (var p in _peers.Values)
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return name + " (" + id + ")";
            return name;
        }

        private bool TryAllocate(out byte id)
        {
            for (byte i = 1; i < MaxPeers; i++)
                if (!_peers.ContainsKey(i)) { id = i; return true; }
            id = 0;
            return false;
        }

        private void HandleHello(byte origin, Hello m) { /* host handles before dispatch */ }

        private void HandleWelcome(byte origin, Welcome m)
        {
            if (IsHost || State == SessionState.Established) return;
            LocalId = m.YourId;
            _peers.Clear();
            foreach (var p in m.Peers) _peers[p.Id] = p;
            _peers[LocalId] = new PeerInfo(LocalId, _config.PlayerName);
            State = SessionState.Established;
            Established?.Invoke();
            foreach (var p in m.Peers) PeerJoinedEvent?.Invoke(p);
        }

        private void HandleReject(byte origin, Reject m)
        {
            if (IsHost) return;
            End(m.Reason);
        }

        private void HandlePeerJoined(byte origin, PeerJoined m)
        {
            if (IsHost || m.Id == LocalId) return;
            var p = new PeerInfo(m.Id, m.Name);
            _peers[m.Id] = p;
            PeerJoinedEvent?.Invoke(p);
        }

        private void HandlePeerLeft(byte origin, PeerLeft m)
        {
            if (IsHost) return;
            if (_peers.TryGetValue(m.Id, out var p))
            {
                _peers.Remove(m.Id);
                PeerLeftEvent?.Invoke(p, m.Reason);
            }
        }

        private void HandlePingTable(byte origin, PingTable m)
        {
            if (IsHost) return;
            foreach (var e in m.Entries)
                if (_peers.TryGetValue(e.Peer, out var p))
                {
                    p.RoundTripMs = e.RoundTripMs;
                    p.Quality = e.Quality == 255 ? -1f : e.Quality / 254f;
                }
        }

        // ------------------------------------------------------------------ blobs

        private sealed class BlobAssembly
        {
            public byte[] Data;
            public bool[] Have;
            public int Received;
            public int Bytes;
        }

        private void HandleBlobChunk(byte origin, BlobChunk c)
        {
            long key = ((long)origin << 32) | c.TransferId;
            if (!_blobs.TryGetValue(key, out var a))
            {
                if (_blobs.Count > 16) _blobs.Clear();  // abandoned transfers; bound memory
                a = new BlobAssembly { Data = new byte[c.TotalLength], Have = new bool[c.Count] };
                _blobs[key] = a;
            }
            if (a.Have.Length != c.Count || a.Data.Length != c.TotalLength || a.Have[c.Index]) return;
            int off = c.Index * BlobChunkBytes;
            if (off + c.Data.Length > a.Data.Length) { _blobs.Remove(key); return; }
            Buffer.BlockCopy(c.Data, 0, a.Data, off, c.Data.Length);
            a.Have[c.Index] = true;
            a.Received++;
            a.Bytes += c.Data.Length;
            BlobProgress?.Invoke(origin, c.Tag, a.Bytes, c.TotalLength);
            if (a.Received < c.Count) return;
            _blobs.Remove(key);
            BlobReceived?.Invoke(origin, c.Tag, a.Data);
        }
    }
}
