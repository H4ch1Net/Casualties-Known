using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using CCoop.Protocol;
using CCoop.Protocol.Messages;
using CCoop.Transport.LiteNet;
using Xunit;
using Xunit.Abstractions;

namespace CCoop.Transport.Tests
{
    /// <summary>
    /// End-to-end tests over real loopback UDP — the M2 pass condition: a host plus
    /// synthetic clients that connect, reject mismatches, and disconnect cleanly.
    ///
    /// These use the actual socket stack rather than an in-memory fake, because the
    /// failures worth catching here (channel misconfiguration, buffer lifetime,
    /// fragmentation, event ordering) only exist in the real transport.
    /// </summary>
    public sealed class LoopbackHarnessTests : IDisposable
    {
        private readonly ITestOutputHelper _log;
        private readonly List<LiteNetTransport> _owned = new List<LiteNetTransport>();

        public LoopbackHarnessTests(ITestOutputHelper log) => _log = log;

        public void Dispose()
        {
            foreach (var t in _owned)
            {
                try { t.Stop(); } catch { /* teardown */ }
            }
        }

        private LiteNetTransport NewTransport()
        {
            var t = new LiteNetTransport();
            _owned.Add(t);
            return t;
        }

        /// <summary>Pumps every transport until <paramref name="until"/> or timeout.</summary>
        private static bool Pump(Func<bool> until, int timeoutMs, params LiteNetTransport[] transports)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                foreach (var t in transports) t.Poll();
                if (until()) return true;
                Thread.Sleep(5);
            }
            // One last pump so a result that landed on the final tick still counts.
            foreach (var t in transports) t.Poll();
            return until();
        }

        private const string HostHash =
            "fd0ad2851f6bae4ab2589634643b92e16696c0e36f090bdd3f4c41e8f1c97095";

        private static HostRequirements Requirements() => new HostRequirements
        {
            ModVersion = "0.1.0",
            GameAppId = 4576510,
            AssemblyHash = HostHash,
            PluginManifest = "ccoop.plugin@0.1.0",
            MaxPlayers = 4,
            AcceptingNewPlayers = true,
        };

        private static HelloMessage MatchingHello(string name) => new HelloMessage
        {
            ModVersion = "0.1.0",
            GameAppId = 4576510,
            AssemblyHash = HostHash,
            UnityVersion = "2022.3.62f3",
            PluginManifest = "ccoop.plugin@0.1.0",
            PlayerName = name,
        };

        // ---------------------------------------------------------------- tests

        [Fact]
        public void Client_connects_and_completes_a_matching_handshake()
        {
            var host = NewTransport();
            var client = NewTransport();

            host.StartHost(0, 4);
            int port = host.LocalPort;
            Assert.True(port > 0, "host did not bind a port");

            var req = Requirements();
            var hostSawPeers = new List<PeerId>();
            WelcomeMessage welcome = null;
            RejectMessage reject = null;

            // --- host side: validate Hello, answer Welcome or Reject ---
            host.PeerConnected += p => hostSawPeers.Add(p);
            host.PacketReceived += (peer, buf, off, len) =>
            {
                Assert.Equal(DecodeResult.Ok, PacketHeader.TryRead(buf, off, len, out var h));
                if (h.Type != MessageType.Hello) return;

                var r = PacketHeader.PayloadReader(buf, off, len);
                if (!HelloMessage.TryDecode(ref r, out var hello))
                {
                    var deny = new RejectMessage { Reason = RejectReason.MalformedHandshake };
                    var bytes = deny.Encode(1);
                    host.Send(peer, bytes, 0, bytes.Length, DeliveryClass.Control);
                    return;
                }

                var verdict = HandshakeValidator.Validate(hello, req, host.ConnectedPeerCount);
                if (verdict.Accepted)
                {
                    var w = new WelcomeMessage
                    {
                        SessionId = 0xC0FFEE,
                        AssignedPeerId = peer.Value,
                        HostPeerCount = (byte)(host.ConnectedPeerCount + 1),
                        ServerTick = 100,
                        HostPlayerName = "host",
                    };
                    var bytes = w.Encode(1);
                    host.Send(peer, bytes, 0, bytes.Length, DeliveryClass.Control);
                }
                else
                {
                    var deny = new RejectMessage { Reason = verdict.Reason, Detail = verdict.Detail };
                    var bytes = deny.Encode(1);
                    host.Send(peer, bytes, 0, bytes.Length, DeliveryClass.Control);
                }
            };

            // --- client side: send Hello on connect, then read the answer ---
            client.PeerConnected += _ =>
            {
                var bytes = MatchingHello("guest").Encode(1);
                client.Send(PeerId.Host, bytes, 0, bytes.Length, DeliveryClass.Control);
            };
            client.PacketReceived += (peer, buf, off, len) =>
            {
                Assert.Equal(DecodeResult.Ok, PacketHeader.TryRead(buf, off, len, out var h));
                var r = PacketHeader.PayloadReader(buf, off, len);

                if (h.Type == MessageType.Welcome)
                    WelcomeMessage.TryDecode(ref r, h, out welcome);
                else if (h.Type == MessageType.Reject)
                    RejectMessage.TryDecode(ref r, out reject);
            };

            client.StartClient("127.0.0.1", (ushort)port);

            Assert.True(Pump(() => welcome != null || reject != null, 6000, host, client),
                        "handshake did not complete");

            Assert.Null(reject);
            Assert.NotNull(welcome);
            Assert.Equal(0xC0FFEEu, welcome.SessionId);
            Assert.NotEqual(ProtocolConstants.HostPeerId, welcome.AssignedPeerId);
            Assert.Single(hostSawPeers);
        }

        [Fact]
        public void Mismatched_game_build_is_rejected_with_a_clear_reason()
        {
            var host = NewTransport();
            var client = NewTransport();

            host.StartHost(0, 4);
            var req = Requirements();
            RejectMessage reject = null;
            WelcomeMessage welcome = null;

            host.PacketReceived += (peer, buf, off, len) =>
            {
                PacketHeader.TryRead(buf, off, len, out var h);
                if (h.Type != MessageType.Hello) return;

                var r = PacketHeader.PayloadReader(buf, off, len);
                HelloMessage.TryDecode(ref r, out var hello);

                var verdict = HandshakeValidator.Validate(hello, req, host.ConnectedPeerCount);
                var reply = verdict.Accepted
                    ? (byte[])new WelcomeMessage { AssignedPeerId = peer.Value, HostPeerCount = 2 }.Encode(1)
                    : new RejectMessage { Reason = verdict.Reason, Detail = verdict.Detail }.Encode(1);
                host.Send(peer, reply, 0, reply.Length, DeliveryClass.Control);
            };

            client.PeerConnected += _ =>
            {
                var hello = MatchingHello("guest");
                // A different game build — the case that must never be allowed through,
                // since clients take their world wholesale from the host.
                hello.AssemblyHash = new string('a', 64);
                var bytes = hello.Encode(1);
                client.Send(PeerId.Host, bytes, 0, bytes.Length, DeliveryClass.Control);
            };
            client.PacketReceived += (peer, buf, off, len) =>
            {
                PacketHeader.TryRead(buf, off, len, out var h);
                var r = PacketHeader.PayloadReader(buf, off, len);
                if (h.Type == MessageType.Reject) RejectMessage.TryDecode(ref r, out reject);
                else if (h.Type == MessageType.Welcome) WelcomeMessage.TryDecode(ref r, h, out welcome);
            };

            client.StartClient("127.0.0.1", (ushort)host.LocalPort);

            Assert.True(Pump(() => reject != null || welcome != null, 6000, host, client),
                        "host never answered the handshake");

            Assert.Null(welcome);
            Assert.NotNull(reject);
            Assert.Equal(RejectReason.GameBuildMismatch, reject.Reason);
            _log.WriteLine("player-facing text: " + reject.Describe());
            Assert.Contains("game build", reject.Describe(), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Four_clients_connect_and_receive_distinct_peer_ids()
        {
            var host = NewTransport();
            host.StartHost(0, ProtocolConstants.MaxPeers);

            var assigned = new List<byte>();
            host.PeerConnected += p => assigned.Add(p.Value);

            var clients = new List<LiteNetTransport>();
            for (int i = 0; i < 4; i++)
            {
                var c = NewTransport();
                c.StartClient("127.0.0.1", (ushort)host.LocalPort);
                clients.Add(c);
            }

            var all = new List<LiteNetTransport> { host };
            all.AddRange(clients);

            Assert.True(Pump(() => assigned.Count == 4, 8000, all.ToArray()),
                        "only " + assigned.Count + "/4 clients connected");

            // Distinct, non-host ids: peer ids index the session's peer table.
            Assert.Equal(4, assigned.Distinct().Count());
            Assert.DoesNotContain(ProtocolConstants.HostPeerId, assigned);
            Assert.All(assigned, id => Assert.True(id < ProtocolConstants.MaxPeers));
        }

        [Fact]
        public void Transport_refuses_connections_beyond_max_peers()
        {
            var host = NewTransport();
            host.StartHost(0, 2); // host + 2 clients

            int connected = 0;
            host.PeerConnected += _ => connected++;

            var clients = new List<LiteNetTransport>();
            for (int i = 0; i < 4; i++)
            {
                var c = NewTransport();
                c.StartClient("127.0.0.1", (ushort)host.LocalPort);
                clients.Add(c);
            }

            var all = new List<LiteNetTransport> { host };
            all.AddRange(clients);

            Pump(() => false, 3000, all.ToArray());

            Assert.True(connected <= 2, "accepted " + connected + " peers with a cap of 2");
            Assert.True(connected >= 1, "no peers connected at all");
        }

        [Fact]
        public void Graceful_disconnect_is_reported_to_the_host()
        {
            var host = NewTransport();
            var client = NewTransport();

            host.StartHost(0, 4);
            PeerId? joined = null;
            var left = new List<(PeerId, DisconnectReason)>();
            host.PeerConnected += p => joined = p;
            host.PeerDisconnected += (p, r) => left.Add((p, r));

            client.StartClient("127.0.0.1", (ushort)host.LocalPort);
            Assert.True(Pump(() => joined != null, 6000, host, client), "client never connected");

            client.Disconnect(PeerId.Host, DisconnectReason.LocalRequest);

            Assert.True(Pump(() => left.Count > 0, 6000, host, client),
                        "host never observed the disconnect");
            Assert.Equal(joined.Value.Value, left[0].Item1.Value);
            Assert.Equal(DisconnectReason.RemoteRequest, left[0].Item2);
        }

        [Fact]
        public void Repeated_connect_disconnect_cycles_leak_no_peer_state()
        {
            // The plan's release gate is 100 cycles; 25 keeps the suite quick while still
            // catching a peer table that never empties.
            var host = NewTransport();
            host.StartHost(0, 4);

            int joins = 0, leaves = 0;
            host.PeerConnected += _ => joins++;
            host.PeerDisconnected += (_, __) => leaves++;

            for (int i = 0; i < 25; i++)
            {
                var c = new LiteNetTransport();
                try
                {
                    c.StartClient("127.0.0.1", (ushort)host.LocalPort);
                    int target = i + 1;
                    Assert.True(Pump(() => joins == target, 4000, host, c),
                                "cycle " + i + ": connect did not land");

                    c.Disconnect(PeerId.Host, DisconnectReason.LocalRequest);
                    Assert.True(Pump(() => leaves == target, 4000, host, c),
                                "cycle " + i + ": disconnect did not land");
                }
                finally { c.Stop(); }
            }

            Assert.Equal(25, joins);
            Assert.Equal(25, leaves);
            Assert.Equal(0, host.ConnectedPeerCount);
        }

        [Fact]
        public void Reliable_messages_survive_simulated_loss_and_latency()
        {
            var host = NewTransport();
            var client = NewTransport();

            host.StartHost(0, 4);
            client.StartClient("127.0.0.1", (ushort)host.LocalPort);

            PeerId? joined = null;
            host.PeerConnected += p => joined = p;
            Assert.True(Pump(() => joined != null, 6000, host, client), "no connection");

            // Well beyond the plan's 2% / 100 ms soak target.
            host.SimulateConditions(packetLossPercent: 10, minLatencyMs: 30, maxLatencyMs: 80);
            client.SimulateConditions(packetLossPercent: 10, minLatencyMs: 30, maxLatencyMs: 80);

            const int total = 200;
            var received = new List<uint>();
            host.PacketReceived += (peer, buf, off, len) =>
            {
                if (PacketHeader.TryRead(buf, off, len, out var h) != DecodeResult.Ok) return;
                if (h.Type == MessageType.Ping) received.Add(h.Sequence);
            };

            for (uint i = 0; i < total; i++)
            {
                var w = new PacketWriter(64);
                int lp = PacketHeader.Write(w, MessageType.Ping, 1, 1, i, i);
                w.WriteUInt32(i);
                PacketHeader.PatchPayloadLength(w, lp);
                var bytes = w.ToArray();
                client.Send(PeerId.Host, bytes, 0, bytes.Length, DeliveryClass.Control);
            }

            Assert.True(Pump(() => received.Count == total, 20000, host, client),
                        "only " + received.Count + "/" + total + " reliable messages arrived");

            // Control is ReliableOrdered: every message, exactly once, in order.
            Assert.Equal(total, received.Count);
            Assert.Equal(received.OrderBy(x => x).ToList(), received);
            Assert.Equal(total, received.Distinct().Count());
        }

        [Fact]
        public void Large_payloads_fragment_and_reassemble_intact()
        {
            // A world snapshot is ~145 KiB and will be chunked by the session layer, but
            // each chunk still has to survive the transport intact.
            var host = NewTransport();
            var client = NewTransport();

            host.StartHost(0, 4);
            client.StartClient("127.0.0.1", (ushort)host.LocalPort);

            PeerId? joined = null;
            host.PeerConnected += p => joined = p;
            Assert.True(Pump(() => joined != null, 6000, host, client), "no connection");

            var blob = new byte[ProtocolConstants.MaxBlobBytes];
            new Random(7).NextBytes(blob);

            byte[] got = null;
            host.PacketReceived += (peer, buf, off, len) =>
            {
                if (PacketHeader.TryRead(buf, off, len, out var h) != DecodeResult.Ok) return;
                if (h.Type != MessageType.WorldSnapshot) return;
                var r = PacketHeader.PayloadReader(buf, off, len);
                var payload = r.ReadBlob();
                if (!r.Failed && r.FullyConsumed) got = payload;
            };

            var w = new PacketWriter(ProtocolConstants.MaxPacketBytes);
            int lp = PacketHeader.Write(w, MessageType.WorldSnapshot, 1, 1, 1, 1);
            w.WriteBlob(blob);
            PacketHeader.PatchPayloadLength(w, lp);
            var packet = w.ToArray();

            client.Send(PeerId.Host, packet, 0, packet.Length, DeliveryClass.Bulk);

            Assert.True(Pump(() => got != null, 15000, host, client), "fragmented payload never arrived");
            Assert.Equal(blob, got);
        }

        [Fact]
        public void Garbage_udp_traffic_does_not_disturb_a_live_session()
        {
            var host = NewTransport();
            var client = NewTransport();

            host.StartHost(0, 4);
            client.StartClient("127.0.0.1", (ushort)host.LocalPort);

            PeerId? joined = null;
            var dropped = new List<DisconnectReason>();
            host.PeerConnected += p => joined = p;
            host.PeerDisconnected += (_, r) => dropped.Add(r);
            Assert.True(Pump(() => joined != null, 6000, host, client), "no connection");

            int decoded = 0, rejected = 0;
            host.PacketReceived += (peer, buf, off, len) =>
            {
                if (PacketHeader.TryRead(buf, off, len, out var h) == DecodeResult.Ok) decoded++;
                else rejected++;
            };

            // Random payloads inside otherwise valid transport frames: the decoder is the
            // only thing standing between a hostile peer and session state.
            var rng = new Random(3);
            for (int i = 0; i < 300; i++)
            {
                var junk = new byte[rng.Next(1, 200)];
                rng.NextBytes(junk);
                client.Send(PeerId.Host, junk, 0, junk.Length, DeliveryClass.Control);
            }

            // Then a well-formed message, which must still get through.
            var w = new PacketWriter(64);
            int lp = PacketHeader.Write(w, MessageType.Ping, 1, 1, 42, 0);
            PacketHeader.PatchPayloadLength(w, lp);
            var good = w.ToArray();
            client.Send(PeerId.Host, good, 0, good.Length, DeliveryClass.Control);

            Assert.True(Pump(() => decoded >= 1, 10000, host, client),
                        "the valid message never arrived after the garbage");

            Assert.True(rejected > 250, "expected most garbage rejected, got " + rejected);
            Assert.Empty(dropped); // the session must survive
            _log.WriteLine("rejected " + rejected + " junk packets, decoded " + decoded + " valid");
        }
    }
}
