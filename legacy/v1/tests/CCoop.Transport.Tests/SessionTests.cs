using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using CCoop.Core;
using CCoop.Protocol;
using CCoop.Protocol.Messages;
using CCoop.Transport.LiteNet;
using Xunit;
using Xunit.Abstractions;

namespace CCoop.Transport.Tests
{
    /// <summary>
    /// Session-layer tests over real loopback UDP. These cover the M3a pass condition —
    /// two peers establishing a session and seeing each other move — without needing a
    /// running game, because <see cref="CoopSession"/> holds no Unity references.
    /// </summary>
    public sealed class SessionTests : IDisposable
    {
        private readonly ITestOutputHelper _log;
        private readonly List<IDisposable> _owned = new List<IDisposable>();

        public SessionTests(ITestOutputHelper log) => _log = log;

        public void Dispose()
        {
            for (int i = _owned.Count - 1; i >= 0; i--)
            {
                try { _owned[i].Dispose(); } catch { /* teardown */ }
            }
        }

        private const string BuildHash =
            "fd0ad2851f6bae4ab2589634643b92e16696c0e36f090bdd3f4c41e8f1c97095";

        private static HostRequirements Requirements() => new HostRequirements
        {
            ModVersion = "0.1.0",
            GameAppId = 4576510,
            AssemblyHash = BuildHash,
            PluginManifest = "ccoop.plugin@0.1.0",
            MaxPlayers = 4,
            AcceptingNewPlayers = true,
        };

        private static HelloMessage Hello(string name, string hash = BuildHash) => new HelloMessage
        {
            ModVersion = "0.1.0",
            GameAppId = 4576510,
            AssemblyHash = hash,
            UnityVersion = "2022.3.62f3",
            PluginManifest = "ccoop.plugin@0.1.0",
            PlayerName = name,
        };

        private (HostSession host, LiteNetTransport transport, int port) StartHost()
        {
            var t = new LiteNetTransport();
            t.StartHost(0, 4);
            var s = new HostSession(t, Requirements(), "hostplayer");
            s.Diagnostic += m => _log.WriteLine("[host] " + m);
            _owned.Add(s);
            return (s, t, t.LocalPort);
        }

        private ClientSession StartClient(int port, HelloMessage hello, string tag = "client")
        {
            var t = new LiteNetTransport();
            var s = new ClientSession(t, hello);
            s.Diagnostic += m => _log.WriteLine("[" + tag + "] " + m);
            t.StartClient("127.0.0.1", (ushort)port);
            _owned.Add(s);
            return s;
        }

        private static bool Pump(Func<bool> until, int timeoutMs, params CoopSession[] sessions)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                foreach (var s in sessions) s.Poll();
                if (until()) return true;
                Thread.Sleep(5);
            }
            foreach (var s in sessions) s.Poll();
            return until();
        }

        private static PlayerTransformMessage Move(float x, float y)
        {
            var limbs = new LimbPose[PlayerTransformMessage.ExpectedLimbCount];
            for (int i = 0; i < limbs.Length; i++)
            {
                limbs[i].LocalX = i * 0.1f;
                limbs[i].LocalY = i * -0.2f;
                limbs[i].Rotation = i * 3f;
            }
            return new PlayerTransformMessage
            {
                PositionX = x, PositionY = y,
                VelocityX = 1.5f, VelocityY = -0.25f,
                FacingRight = true,
                StateFlags = 0b0000_0011,
                Limbs = limbs,
            };
        }

        // ---------------------------------------------------------------- tests

        [Fact]
        public void Client_joins_and_both_sides_see_each_other()
        {
            var (host, _, port) = StartHost();
            var client = StartClient(port, Hello("guest"));

            Assert.True(Pump(() => client.Status == SessionStatus.Established
                                && host.RemotePlayerCount == 1, 8000, host, client),
                        "session never established (client status " + client.Status + ")");

            // Host sees the guest.
            Assert.Equal(1, host.RemotePlayerCount);
            Assert.Equal("guest", host.RemotePlayers.First().Name);

            // The client must be told about the host, or it would see an empty world
            // until the host happened to move.
            Assert.True(Pump(() => client.RemotePlayerCount == 1, 4000, host, client),
                        "client never learned about the host");
            Assert.Equal(ProtocolConstants.HostPeerId, client.RemotePlayers.First().PeerId);

            Assert.NotEqual(ProtocolConstants.HostPeerId, client.LocalPeerId);
            Assert.Equal(host.SessionId, client.SessionId);
        }

        [Fact]
        public void Mismatched_build_is_rejected_with_a_usable_message()
        {
            var (host, _, port) = StartHost();
            var client = StartClient(port, Hello("guest", new string('b', 64)));

            Assert.True(Pump(() => client.Status == SessionStatus.Rejected, 8000, host, client),
                        "client was not rejected (status " + client.Status + ")");

            Assert.NotNull(client.Rejection);
            Assert.Equal(RejectReason.GameBuildMismatch, client.Rejection.Reason);
            _log.WriteLine("shown to player: " + client.Rejection.Describe());

            // A rejected client must never become a player.
            Assert.Equal(0, host.RemotePlayerCount);
        }

        [Fact]
        public void Client_movement_reaches_the_host()
        {
            var (host, _, port) = StartHost();
            var client = StartClient(port, Hello("guest"));
            Assert.True(Pump(() => client.Status == SessionStatus.Established, 8000, host, client),
                        "no session");

            var moved = new List<RemotePlayer>();
            host.RemotePlayerMoved += p => moved.Add(p);

            for (int i = 0; i < 20; i++)
            {
                client.SendLocalTransform(Move(10f + i, 20f));
                Pump(() => false, 25, host, client);
            }

            Assert.True(Pump(() => moved.Count > 0, 5000, host, client),
                        "host never received movement");

            var latest = host.RemotePlayers.First().LatestTransform;
            Assert.NotNull(latest);
            Assert.Equal(20f, latest.PositionY, 3);
            Assert.True(latest.FacingRight);
            Assert.Equal(PlayerTransformMessage.ExpectedLimbCount, latest.Limbs.Length);
            // Full pose must survive the round trip, not just root position. Limb local
            // POSITION is the part that was missing originally and made remote players
            // float with limbs spinning in place.
            Assert.Equal(3f, latest.Limbs[1].Rotation, 3);
            Assert.Equal(0.1f, latest.Limbs[1].LocalX, 3);
            Assert.Equal(-0.2f, latest.Limbs[1].LocalY, 3);
        }

        [Fact]
        public void Host_movement_reaches_the_client()
        {
            var (host, _, port) = StartHost();
            var client = StartClient(port, Hello("guest"));
            Assert.True(Pump(() => client.Status == SessionStatus.Established
                                && client.RemotePlayerCount == 1, 8000, host, client), "no session");

            for (int i = 0; i < 20; i++)
            {
                host.SendLocalTransform(Move(-5f - i, 7f));
                Pump(() => false, 25, host, client);
            }

            Assert.True(Pump(() => client.RemotePlayers.First().HasTransform, 5000, host, client),
                        "client never saw the host move");

            var t = client.RemotePlayers.First().LatestTransform;
            Assert.Equal(7f, t.PositionY, 3);
        }

        [Fact]
        public void Two_clients_see_each_other_through_the_host()
        {
            // The relay path: A -> host -> B, without echoing back to A.
            var (host, _, port) = StartHost();
            var a = StartClient(port, Hello("alice"), "alice");
            var b = StartClient(port, Hello("bob"), "bob");

            Assert.True(Pump(() => a.Status == SessionStatus.Established
                                && b.Status == SessionStatus.Established
                                && host.RemotePlayerCount == 2, 10000, host, a, b),
                        "both clients did not join");

            // Each client should know about the host plus the other client.
            Assert.True(Pump(() => a.RemotePlayerCount == 2 && b.RemotePlayerCount == 2,
                             6000, host, a, b),
                        "clients did not learn about each other (a=" + a.RemotePlayerCount +
                        " b=" + b.RemotePlayerCount + ")");

            for (int i = 0; i < 30; i++)
            {
                a.SendLocalTransform(Move(42f, 13f));
                Pump(() => false, 20, host, a, b);
            }

            Assert.True(Pump(() => b.RemotePlayers.Any(p => p.PeerId == a.LocalPeerId && p.HasTransform),
                             6000, host, a, b),
                        "bob never saw alice move");

            var seen = b.RemotePlayers.First(p => p.PeerId == a.LocalPeerId).LatestTransform;
            Assert.Equal(42f, seen.PositionX, 3);

            // Alice must not receive her own movement back as a remote player.
            Assert.DoesNotContain(a.RemotePlayers, p => p.PeerId == a.LocalPeerId);
        }

        [Fact]
        public void Disconnecting_client_is_despawned_for_everyone()
        {
            var (host, _, port) = StartHost();
            var a = StartClient(port, Hello("alice"), "alice");
            var b = StartClient(port, Hello("bob"), "bob");

            Assert.True(Pump(() => host.RemotePlayerCount == 2
                                && a.RemotePlayerCount == 2 && b.RemotePlayerCount == 2,
                             10000, host, a, b), "both clients did not join");

            byte aliceId = a.LocalPeerId;
            a.Dispose();

            Assert.True(Pump(() => host.RemotePlayerCount == 1, 8000, host, b),
                        "host did not drop alice");
            Assert.True(Pump(() => !b.RemotePlayers.Any(p => p.PeerId == aliceId), 8000, host, b),
                        "bob still sees alice");
        }

        [Fact]
        public void Stale_out_of_order_movement_is_discarded()
        {
            // Movement is unreliable-sequenced, so reordering is normal. Applying a late
            // frame would visibly jerk the character backwards.
            var (host, _, port) = StartHost();
            var client = StartClient(port, Hello("guest"));
            Assert.True(Pump(() => client.Status == SessionStatus.Established
                                && client.RemotePlayerCount == 1, 8000, host, client), "no session");

            var player = client.RemotePlayers.First();
            player.LatestTransform = Move(100f, 100f);
            player.LastTransformSequence = 500;

            // Simulate a delayed older packet arriving after a newer one.
            Assert.False(CoopSession.IsNewer(499, 500), "499 must not be newer than 500");
            Assert.True(CoopSession.IsNewer(501, 500), "501 must be newer than 500");
        }

        [Theory]
        // Normal ordering.
        [InlineData(2u, 1u, true)]
        [InlineData(1u, 2u, false)]
        // Wrap-around: after ~4 billion frames the counter rolls over, and a naive
        // "a > b" comparison would reject every subsequent packet forever.
        [InlineData(0u, uint.MaxValue, true)]
        [InlineData(uint.MaxValue, 0u, false)]
        [InlineData(5u, uint.MaxValue - 5u, true)]
        public void Sequence_comparison_handles_wraparound(uint a, uint b, bool expected)
        {
            Assert.Equal(expected, CoopSession.IsNewer(a, b));
        }

        [Fact]
        public void Session_survives_sustained_movement_under_loss_and_latency()
        {
            var (host, hostTransport, port) = StartHost();
            var client = StartClient(port, Hello("guest"));
            Assert.True(Pump(() => client.Status == SessionStatus.Established, 8000, host, client),
                        "no session");

            hostTransport.SimulateConditions(5, 25, 60);

            int updates = 0;
            host.RemotePlayerMoved += _ => updates++;

            for (int i = 0; i < 150; i++)
            {
                client.SendLocalTransform(Move(i * 0.5f, 3f));
                Pump(() => false, 15, host, client);
            }
            Pump(() => false, 1500, host, client);

            // Snapshots are droppable by design, so demand a healthy majority rather
            // than every frame — the point is that the stream keeps flowing.
            _log.WriteLine("applied " + updates + "/150 movement updates under 5% loss");
            Assert.True(updates > 100, "only " + updates + "/150 updates applied");
            Assert.Equal(SessionStatus.Established, client.Status);
            Assert.Equal(1, host.RemotePlayerCount);
        }

        /// <summary>
        /// The M3b pass condition: a realistically sized world crosses the wire intact.
        /// Exercises fragmentation, ordering and verification through the real transport
        /// rather than in isolation.
        /// </summary>
        [Fact]
        public void Host_streams_a_full_world_to_the_client()
        {
            var (host, _, port) = StartHost();
            var client = StartClient(port, Hello("guest"));
            Assert.True(Pump(() => client.Status == SessionStatus.Established
                                && host.RemotePlayerCount == 1, 8000, host, client), "no session");

            // 1024x1024 blocks, as measured in game.
            const int chunkW = 16, chunkH = 16, chunkSize = 64;
            int width = chunkW * chunkSize, height = chunkH * chunkSize;
            var rng = new Random(17);
            var blocks = new ushort[width * height];
            ushort cur = 0; int run = 0;
            for (int i = 0; i < blocks.Length; i++)
            {
                if (run == 0) { cur = (ushort)rng.Next(0, 17); run = rng.Next(20, 400); }
                blocks[i] = cur; run--;
            }
            var raw = new byte[blocks.Length * sizeof(ushort)];
            Buffer.BlockCopy(blocks, 0, raw, 0, raw.Length);

            LayerPrepareMessage gotPrepare = null;
            byte[] gotRaw = null;
            EntityManifest gotEntities = null;
            float lastProgress = 0f;
            client.WorldSnapshotReady += snap => { gotPrepare = snap.Prepare; gotRaw = snap.Terrain; gotEntities = snap.Entities; };
            client.WorldSnapshotProgress += p => lastProgress = p;

            bool hostSawReady = false, hostSawSuccess = false;
            host.PeerLayerReady += (peer, ok, detail) => { hostSawReady = true; hostSawSuccess = ok; };

            // Include an entity manifest: terrain and contents travel together, and a
            // client that got one without the other would be silently unplayable.
            var manifest = new EntityManifest();
            for (int i = 0; i < 500; i++)
                manifest.Add(i % 2 == 0 ? "glowplant" : "medcrate", i * 1.5f, i * -0.5f, i % 360);

            uint transferId = host.BroadcastWorldSnapshot(
                raw, chunkW, chunkH, chunkSize, biomeDepth: 0, spawnX: 12.5f, spawnY: -3.25f,
                entities: manifest);
            Assert.NotEqual(0u, transferId);

            Assert.True(Pump(() => gotRaw != null, 30000, host, client),
                        "world never arrived (progress " + lastProgress.ToString("P0") + ")");

            // Byte-for-byte, or the client's terrain silently differs from the host's.
            Assert.Equal(raw, gotRaw);

            // Entities must survive the same transfer.
            Assert.NotNull(gotEntities);
            Assert.Equal(500, gotEntities.Entities.Count);
            Assert.Equal(2, gotEntities.Keys.Count);
            Assert.Equal("glowplant", gotEntities.KeyFor(gotEntities.Entities[0]));
            Assert.Equal("medcrate", gotEntities.KeyFor(gotEntities.Entities[1]));

            Assert.Equal(transferId, gotPrepare.TransferId);
            Assert.Equal(chunkW, gotPrepare.ChunkWidth);
            Assert.Equal(12.5f, gotPrepare.SpawnX, 3);
            Assert.Equal(-3.25f, gotPrepare.SpawnY, 3);
            Assert.Equal(1f, lastProgress, 2);

            _log.WriteLine("transferred " + raw.Length + " B raw in " + gotPrepare.ChunkCount +
                           " chunks (" + gotPrepare.CompressedLength + " B compressed)");

            // The client acknowledges so the host knows when everyone is ready.
            client.SendLayerReady(transferId, true);
            Assert.True(Pump(() => hostSawReady, 6000, host, client), "host never saw LayerReady");
            Assert.True(hostSawSuccess);
        }

        [Fact]
        public void World_transfer_survives_packet_loss()
        {
            // Bulk is reliable-ordered, so loss should cost time, not correctness.
            var (host, hostTransport, port) = StartHost();
            var client = StartClient(port, Hello("guest"));
            Assert.True(Pump(() => client.Status == SessionStatus.Established
                                && host.RemotePlayerCount == 1, 8000, host, client), "no session");

            hostTransport.SimulateConditions(packetLossPercent: 8, minLatencyMs: 20, maxLatencyMs: 50);

            const int chunkW = 8, chunkH = 8, chunkSize = 64;
            int width = chunkW * chunkSize, height = chunkH * chunkSize;
            var blocks = new ushort[width * height];
            for (int i = 0; i < blocks.Length; i++) blocks[i] = (ushort)((i / 512) % 17);
            var raw = new byte[blocks.Length * sizeof(ushort)];
            Buffer.BlockCopy(blocks, 0, raw, 0, raw.Length);

            byte[] gotRaw = null;
            client.WorldSnapshotReady += snap => gotRaw = snap.Terrain;

            host.BroadcastWorldSnapshot(raw, chunkW, chunkH, chunkSize, 0, 0f, 0f);

            Assert.True(Pump(() => gotRaw != null, 40000, host, client),
                        "world never completed under 8% loss");
            Assert.Equal(raw, gotRaw);
        }

        [Fact]
        public void Client_reports_failure_when_the_transfer_is_corrupt()
        {
            var (host, _, port) = StartHost();
            var client = StartClient(port, Hello("guest"));
            Assert.True(Pump(() => client.Status == SessionStatus.Established, 8000, host, client),
                        "no session");

            // Announce a transfer whose hash cannot match the data we then send.
            var prepare = new LayerPrepareMessage
            {
                TransferId = 55,
                ChunkWidth = 4, ChunkHeight = 4, ChunkSize = 64,
                BiomeDepth = 0, SpawnX = 0f, SpawnY = 0f,
                CompressedLength = 16,
                ChunkCount = 1,
                CompressedHash = new string('a', 64),
                UncompressedLength = 4 * 64 * 4 * 64 * sizeof(ushort),
                // Must agree with the declared dimensions or the client rejects the
                // announcement outright and never reaches the hash check being tested.
                TerrainLength = 4 * 64 * 4 * 64 * sizeof(ushort),
            };

            bool failureReported = false;
            string detail = null;
            host.PeerLayerReady += (peer, ok, d) => { if (!ok) { failureReported = true; detail = d; } };

            byte[] gotRaw = null;
            client.WorldSnapshotReady += snap => gotRaw = snap.Terrain;

            // Reach into the host transport directly: HostSession would compute a correct
            // hash, and the point here is to prove the client rejects a bad one.
            var prepareBytes = prepare.Encode(host.SessionId, 900, 0);
            var peerId = new CCoop.Transport.PeerId(client.LocalPeerId == 0 ? (byte)1 : client.LocalPeerId);
            hostTransportSend(host, peerId, prepareBytes, DeliveryClass.Control);

            var chunkBytes = new WorldSnapshotChunkMessage
            {
                TransferId = 55, ChunkIndex = 0, Data = new byte[16],
            }.Encode(host.SessionId, 901, 0);
            hostTransportSend(host, peerId, chunkBytes, DeliveryClass.Bulk);

            Assert.True(Pump(() => failureReported, 10000, host, client),
                        "client did not report the corrupt transfer");
            Assert.Null(gotRaw); // must never apply unverified terrain
            _log.WriteLine("client reported: " + detail);
        }

        /// <summary>Sends a raw packet through a host session's transport.</summary>
        private static void hostTransportSend(HostSession host, CCoop.Transport.PeerId peer,
                                              byte[] bytes, DeliveryClass delivery)
        {
            var field = typeof(CoopSession).GetField("Transport",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var transport = (CCoop.Transport.INetworkTransport)field.GetValue(host);
            transport.Send(peer, bytes, 0, bytes.Length, delivery);
        }

        [Fact]
        public void Session_is_full_when_capacity_is_reached()
        {
            var t = new LiteNetTransport();
            t.StartHost(0, 4);
            var req = Requirements();
            req.MaxPlayers = 2; // host + 1
            var host = new HostSession(t, req, "hostplayer");
            _owned.Add(host);

            var a = StartClient(t.LocalPort, Hello("alice"), "alice");
            Assert.True(Pump(() => a.Status == SessionStatus.Established, 8000, host, a),
                        "first client did not join");

            var b = StartClient(t.LocalPort, Hello("bob"), "bob");
            Assert.True(Pump(() => b.Status == SessionStatus.Rejected, 8000, host, a, b),
                        "second client should have been refused (status " + b.Status + ")");

            Assert.Equal(RejectReason.SessionFull, b.Rejection.Reason);
            Assert.Equal(1, host.RemotePlayerCount);
        }
    }
}
