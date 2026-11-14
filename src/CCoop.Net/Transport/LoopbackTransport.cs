using System;
using System.Collections.Generic;

namespace CCoop.Net.Transport
{
    /// <summary>
    /// In-process transport for tests: a hub with one host endpoint and any number of
    /// client endpoints, delivering in order on Poll.
    /// </summary>
    public sealed class LoopbackHub
    {
        internal readonly LoopbackTransport HostEnd;
        private readonly List<LoopbackTransport> _clients = new List<LoopbackTransport>();
        private int _nextConnection = 1;

        public LoopbackHub()
        {
            HostEnd = new LoopbackTransport(this, true);
        }

        public LoopbackTransport Host => HostEnd;

        /// <summary>Probability (0..1) of dropping an unreliable packet.</summary>
        public double UnreliableLoss { get; set; }

        private readonly Random _rng = new Random(1234);

        public LoopbackTransport Connect()
        {
            var c = new LoopbackTransport(this, false) { ConnectionIdOnHost = _nextConnection++ };
            _clients.Add(c);
            HostEnd.Enqueue(() => HostEnd.RaiseConnected(c.ConnectionIdOnHost));
            c.Enqueue(() => c.RaiseConnected(0));
            return c;
        }

        internal LoopbackTransport ClientFor(int connection)
        {
            foreach (var c in _clients) if (c.ConnectionIdOnHost == connection && c.IsRunning) return c;
            return null;
        }

        internal void Deliver(LoopbackTransport from, int connection, byte[] data, Delivery delivery)
        {
            if (delivery == Delivery.Unreliable && UnreliableLoss > 0 && _rng.NextDouble() < UnreliableLoss) return;
            if (from.IsHostEnd)
            {
                var c = ClientFor(connection);
                c?.Enqueue(() => c.RaiseReceived(0, data));
            }
            else
            {
                int id = from.ConnectionIdOnHost;
                HostEnd.Enqueue(() => HostEnd.RaiseReceived(id, data));
            }
        }

        internal void Drop(LoopbackTransport from, int connection, string reason)
        {
            if (from.IsHostEnd)
            {
                var c = ClientFor(connection);
                if (c == null) return;
                c.Enqueue(() => c.RaiseDisconnected(0, reason));
                HostEnd.Enqueue(() => HostEnd.RaiseDisconnected(connection, reason));
            }
            else
            {
                int id = from.ConnectionIdOnHost;
                HostEnd.Enqueue(() => HostEnd.RaiseDisconnected(id, reason));
                from.Enqueue(() => from.RaiseDisconnected(0, reason));
            }
        }
    }

    public sealed class LoopbackTransport : ITransport
    {
        private readonly LoopbackHub _hub;
        private readonly Queue<Action> _inbox = new Queue<Action>();
        private bool _running = true;

        internal LoopbackTransport(LoopbackHub hub, bool isHost)
        {
            _hub = hub;
            IsHostEnd = isHost;
        }

        internal bool IsHostEnd { get; }
        internal int ConnectionIdOnHost { get; set; }

        public string Kind => "Loopback";
        public bool IsRunning => _running;

        public event Action<int> Connected;
        public event Action<int, string> Disconnected;
        public event Action<int, byte[], int, int> Received;
        public event Action<string> Error { add { } remove { } }

        internal void Enqueue(Action a) { lock (_inbox) _inbox.Enqueue(a); }
        internal void RaiseConnected(int c) => Connected?.Invoke(c);
        internal void RaiseDisconnected(int c, string reason) => Disconnected?.Invoke(c, reason);
        internal void RaiseReceived(int c, byte[] data) => Received?.Invoke(c, data, 0, data.Length);

        public void Poll()
        {
            while (true)
            {
                Action a;
                lock (_inbox)
                {
                    if (_inbox.Count == 0) return;
                    a = _inbox.Dequeue();
                }
                if (_running) a();
            }
        }

        public void Send(int connection, byte[] data, int offset, int count, Delivery delivery)
        {
            if (!_running) return;
            var copy = new byte[count];
            Buffer.BlockCopy(data, offset, copy, 0, count);
            _hub.Deliver(this, connection, copy, delivery);
        }

        public void Disconnect(int connection, string reason) => _hub.Drop(this, connection, reason);
        public int RoundTripMs(int connection) => 0;
        public float LinkQuality(int connection) => 1f;
        public string RemoteAddress(int connection) => "loopback:" + connection;

        public void Shutdown()
        {
            if (!_running) return;
            if (!IsHostEnd) _hub.Drop(this, 0, "shutdown");
            _running = false;
        }

        public void Dispose() => Shutdown();
    }
}
