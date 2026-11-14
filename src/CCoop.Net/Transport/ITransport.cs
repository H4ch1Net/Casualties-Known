using System;

namespace CCoop.Net.Transport
{
    /// <summary>
    /// A connection-oriented packet pipe. Connections are identified by transport-local
    /// integers; the session maps them to peer ids once the handshake completes.
    ///
    /// Threading: all events are raised from <see cref="Poll"/>, on the caller's thread.
    /// </summary>
    public interface ITransport : IDisposable
    {
        /// <summary>Human-readable kind, e.g. "Steam" or "LAN/IP".</summary>
        string Kind { get; }

        bool IsRunning { get; }

        void Poll();

        /// <summary>Sends to one connection. The buffer may be reused once this returns.</summary>
        void Send(int connection, byte[] data, int offset, int count, Delivery delivery);

        void Disconnect(int connection, string reason);

        void Shutdown();

        /// <summary>Round trip in milliseconds, or -1 if unknown.</summary>
        int RoundTripMs(int connection);

        /// <summary>Share of packets arriving intact, 0..1, or -1 if unknown.</summary>
        float LinkQuality(int connection);

        /// <summary>
        /// Who is on the other end, as established by the transport rather than claimed
        /// by the peer: a Steam id for Steam, "ip:port" for UDP. Used for bans.
        /// </summary>
        string RemoteAddress(int connection);

        /// <summary>
        /// A connection was established. On a client this fires once, for the host.
        /// </summary>
        event Action<int> Connected;

        event Action<int, string> Disconnected;

        /// <summary>A packet arrived. The array is only valid during the callback.</summary>
        event Action<int, byte[], int, int> Received;

        event Action<string> Error;
    }
}
