using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    /// World-mutation traffic over real loopback UDP.
    ///
    /// Terrain edits and item spawns are the one class of message that travels
    /// peer-to-peer rather than being arbitrated by the host, so the routing rules —
    /// relayed to the others, never echoed to the sender, never accepted before the
    /// handshake — are the whole correctness story and are what these cover.
    /// </summary>
    public sealed class WorldMutationSessionTests : IDisposable
    {
        private readonly ITestOutputHelper _log;
        private readonly List<IDisposable> _ownedSessions = new List<IDisposable>();
        private readonly List<LiteNetTransport> _ownedTransports = new List<LiteNetTransport>();

        public WorldMutationSessionTests(ITestOutputHelper log) => _log = log;

        public void Dispose()
        {
            for (int i = _ownedSessions.Count - 1; i >= 0; i--)
            {
                try { _ownedSessions[i].Dispose(); } catch { /* teardown */ }
            }
            foreach (var t in _ownedTransports)
            {
                try { t.Stop(); } catch { /* teardown */ }
            }
        }

        private const string BuildHash =
            "fd0ad2851f6bae4ab2589634643b92e16696c0e36f090bdd3f4c41e8f1c97095";

        private static HostRequirements Requirements() => new HostRequirements
        {
            ModVersion = "0.2.0",
            GameAppId = 4576510,
            AssemblyHash = BuildHash,
            PluginManifest = "ccoop.plugin@0.2.0",
            MaxPlayers = 4,
            AcceptingNewPlayers = true,
        };

        private static HelloMessage Hello(string name) => new HelloMessage
        {
            ModVersion = "0.2.0",
            GameAppId = 4576510,
            AssemblyHash = BuildHash,
            UnityVersion = "2022.3.62f3",
            PluginManifest = "ccoop.plugin@0.2.0",
            PlayerName = name,
        };

        private (HostSession host, int port) StartHost()
        {
            var t = new LiteNetTransport();
            t.StartHost(0, 4);
            var s = new HostSession(t, Requirements(), "hostplayer");
            s.Diagnostic += m => _log.WriteLine("[host] " + m);
            _ownedSessions.Add(s);
            return (s, t.LocalPort);
        }

        private ClientSession StartClient(int port, string tag)
        {
            var t = new LiteNetTransport();
            var s = new ClientSession(t, Hello(tag));
            s.Diagnostic += m => _log.WriteLine("[" + tag + "] " + m);
            t.StartClient("127.0.0.1", (ushort)port);
            _ownedSessions.Add(s);
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

        private static BlockDeltaMessage Edits(params (ushort x, ushort y, ushort block)[] cells)
        {
            var m = new BlockDeltaMessage();
            foreach (var c in cells) m.Edits.Add(new BlockEdit(c.x, c.y, c.block));
            return m;
        }

        // ---------------------------------------------------------------- tests

        [Fact]
        public void A_clients_terrain_edits_reach_the_host()
        {
            var (host, port) = StartHost();
            var client = StartClient(port, "guest");
            Assert.True(Pump(() => client.Status == SessionStatus.Established, 8000, host, client),
                        "no session");

            BlockDeltaMessage seen = null;
            byte seenFrom = 255;
            host.BlockDeltasReceived += (peer, m) => { seenFrom = peer; seen = m; };

            client.SendBlockDeltas(Edits((100, 200, 0), (101, 200, 3)));

            Assert.True(Pump(() => seen != null, 4000, host, client), "host never saw the edits");
            Assert.Equal(client.LocalPeerId, seenFrom);
            Assert.Equal(2, seen.Edits.Count);
            Assert.Equal(100, seen.Edits[0].X);
            Assert.Equal(0, seen.Edits[0].Block);
            Assert.Equal(3, seen.Edits[1].Block);
        }

        [Fact]
        public void The_hosts_terrain_edits_reach_a_client()
        {
            var (host, port) = StartHost();
            var client = StartClient(port, "guest");
            Assert.True(Pump(() => client.Status == SessionStatus.Established
                                && host.RemotePlayerCount == 1, 8000, host, client), "no session");

            BlockDeltaMessage seen = null;
            byte seenFrom = 255;
            client.BlockDeltasReceived += (peer, m) => { seenFrom = peer; seen = m; };

            host.SendBlockDeltas(Edits((7, 8, 21)));

            Assert.True(Pump(() => seen != null, 4000, host, client), "client never saw the edits");
            Assert.Equal(ProtocolConstants.HostPeerId, seenFrom);
            Assert.Equal(21, seen.Edits[0].Block);
        }

        [Fact]
        public void Edits_from_one_client_reach_the_other_but_not_their_author()
        {
            // The relay is what makes three-way terrain work at all, and echoing an edit
            // back to its author is how a peer ends up re-broadcasting forever.
            var (host, port) = StartHost();
            var a = StartClient(port, "a");
            var b = StartClient(port, "b");

            Assert.True(Pump(() => host.RemotePlayerCount == 2
                                && a.Status == SessionStatus.Established
                                && b.Status == SessionStatus.Established, 8000, host, a, b),
                        "both clients never joined");

            int seenByA = 0;
            BlockDeltaMessage seenByB = null;
            a.BlockDeltasReceived += (_, __) => seenByA++;
            b.BlockDeltasReceived += (_, m) => seenByB = m;

            a.SendBlockDeltas(Edits((512, 512, 0)));

            Assert.True(Pump(() => seenByB != null, 4000, host, a, b), "the edit was not relayed");
            Assert.Equal(512, seenByB.Edits[0].X);

            // Give any stray echo a chance to arrive before concluding there was none.
            Pump(() => false, 300, host, a, b);
            Assert.Equal(0, seenByA);
        }

        [Fact]
        public void A_clients_item_spawn_reaches_the_host_and_the_other_client()
        {
            var (host, port) = StartHost();
            var a = StartClient(port, "a");
            var b = StartClient(port, "b");
            Assert.True(Pump(() => host.RemotePlayerCount == 2
                                && a.Status == SessionStatus.Established
                                && b.Status == SessionStatus.Established, 8000, host, a, b),
                        "both clients never joined");

            EntitySpawnMessage atHost = null, atB = null;
            host.EntitySpawned += (_, m) => atHost = m;
            b.EntitySpawned += (_, m) => atB = m;

            a.SendEntitySpawn(new EntitySpawnMessage
            {
                EntityId = 0x0120_0003,
                ItemId = "scrapmetal",
                PositionX = 12f,
                PositionY = -4f,
                Condition = 0.15f,
                VelocityX = 2f,
                VelocityY = 5f,
            });

            Assert.True(Pump(() => atHost != null && atB != null, 4000, host, a, b),
                        "spawn did not reach both peers");

            // The id is the spawner's, unchanged in flight: it is what every peer will
            // use to refer to this item from now on.
            Assert.Equal(0x0120_0003u, atHost.EntityId);
            Assert.Equal(0x0120_0003u, atB.EntityId);
            Assert.Equal("scrapmetal", atB.ItemId);
            Assert.Equal(0.15f, atB.Condition);
        }

        [Fact]
        public void A_clients_entity_destruction_reaches_the_host_and_the_other_client()
        {
            // The case this exists for: a client chops a crate. Nobody else's copy has
            // taken a scratch, so without this message it stands on their screens forever
            // and they can break it again for a second set of loot.
            var (host, port) = StartHost();
            var a = StartClient(port, "a");
            var b = StartClient(port, "b");
            Assert.True(Pump(() => host.RemotePlayerCount == 2
                                && a.Status == SessionStatus.Established
                                && b.Status == SessionStatus.Established, 8000, host, a, b),
                        "both clients never joined");

            EntityDespawnMessage atHost = null, atB = null;
            int seenByA = 0;
            host.EntityDespawned += (_, m) => atHost = m;
            b.EntityDespawned += (_, m) => atB = m;
            a.EntityDespawned += (_, __) => seenByA++;

            a.SendEntityDespawn(new EntityDespawnMessage { EntityId = 1417 });

            Assert.True(Pump(() => atHost != null && atB != null, 4000, host, a, b),
                        "despawn did not reach both peers");
            Assert.Equal(1417u, atHost.EntityId);
            Assert.Equal(1417u, atB.EntityId);

            Pump(() => false, 300, host, a, b);
            Assert.Equal(0, seenByA);
        }

        [Fact]
        public void A_destruction_and_its_loot_arrive_in_that_order()
        {
            // Both ride the entity-lifecycle channel precisely so a crate cannot appear
            // to spawn its contents before it breaks.
            var (host, port) = StartHost();
            var client = StartClient(port, "guest");
            Assert.True(Pump(() => client.Status == SessionStatus.Established
                                && host.RemotePlayerCount == 1, 8000, host, client), "no session");

            var order = new List<string>();
            client.EntityDespawned += (_, __) => order.Add("despawn");
            client.EntitySpawned += (_, __) => order.Add("spawn");

            host.SendEntityDespawn(new EntityDespawnMessage { EntityId = 42 });
            for (int i = 0; i < 3; i++)
            {
                host.SendEntitySpawn(new EntitySpawnMessage
                {
                    EntityId = (uint)(NetRuntimeIdBase + i),
                    ItemId = "woodscraps",
                    Condition = 1f,
                });
            }

            Assert.True(Pump(() => order.Count == 4, 4000, host, client),
                        "expected a despawn and three spawns, got " + order.Count);
            Assert.Equal(new[] { "despawn", "spawn", "spawn", "spawn" }, order);
        }

        /// <summary>Mirrors <c>NetEntityRegistry.RuntimeIdBase</c>, which lives in the plugin.</summary>
        private const uint NetRuntimeIdBase = 0x0100_0000;

        [Fact]
        public void Damage_from_both_clients_reaches_the_host_and_stacks()
        {
            // The whole point of sending an amount rather than a health: two players
            // chopping the same crate must add up, not overwrite each other.
            var (host, port) = StartHost();
            var a = StartClient(port, "a");
            var b = StartClient(port, "b");
            Assert.True(Pump(() => host.RemotePlayerCount == 2
                                && a.Status == SessionStatus.Established
                                && b.Status == SessionStatus.Established, 8000, host, a, b),
                        "both clients never joined");

            float total = 0f;
            host.EntityDamaged += (_, m) =>
            {
                foreach (var d in m.Damages) if (d.EntityId == 77) total += d.Amount;
            };

            var fromA = new EntityDamageMessage();
            fromA.Damages.Add(new EntityDamage(77, 60f));
            a.SendEntityDamage(fromA);

            var fromB = new EntityDamageMessage();
            fromB.Damages.Add(new EntityDamage(77, 50f));
            b.SendEntityDamage(fromB);

            Assert.True(Pump(() => total >= 110f, 4000, host, a, b),
                        "host saw " + total + " damage, expected 110");
            Assert.Equal(110f, total);
        }

        [Fact]
        public void Damage_and_the_despawn_it_causes_arrive_in_order()
        {
            // Both on the entity-lifecycle channel precisely so the receiving peer cannot
            // see its copy die and claim the kill before it learns who dealt the blow.
            var (host, port) = StartHost();
            var client = StartClient(port, "guest");
            Assert.True(Pump(() => client.Status == SessionStatus.Established
                                && host.RemotePlayerCount == 1, 8000, host, client), "no session");

            var order = new List<string>();
            client.EntityDamaged += (_, __) => order.Add("damage");
            client.EntityDespawned += (_, __) => order.Add("despawn");

            var blow = new EntityDamageMessage();
            blow.Damages.Add(new EntityDamage(5, 25f));
            host.SendEntityDamage(blow);
            host.SendEntityDespawn(new EntityDespawnMessage { EntityId = 5 });

            Assert.True(Pump(() => order.Count == 2, 4000, host, client),
                        "expected damage then despawn, got " + order.Count + " messages");
            Assert.Equal(new[] { "damage", "despawn" }, order);
        }

        [Fact]
        public void A_clients_speed_request_reaches_the_host_as_a_request_only()
        {
            // Deliberately NOT relayed like terrain: one client must not be able to set
            // the speed for a session whose host disallowed it.
            var (host, port) = StartHost();
            var a = StartClient(port, "a");
            var b = StartClient(port, "b");
            Assert.True(Pump(() => host.RemotePlayerCount == 2
                                && a.Status == SessionStatus.Established
                                && b.Status == SessionStatus.Established, 8000, host, a, b),
                        "both clients never joined");

            TimeScaleMessage asked = null;
            byte askedBy = 255;
            int seenByB = 0;
            host.TimeScaleRequested += (peer, m) => { askedBy = peer; asked = m; };
            b.TimeScaleChanged += _ => seenByB++;

            a.SendTimeScale(new TimeScaleMessage { Speed = SessionSpeed.SuperFast });

            Assert.True(Pump(() => asked != null, 4000, host, a, b), "host never saw the request");
            Assert.Equal(a.LocalPeerId, askedBy);
            Assert.Equal(SessionSpeed.SuperFast, asked.Speed);

            // Nothing reaches the other client until the host answers.
            Pump(() => false, 300, host, a, b);
            Assert.Equal(0, seenByB);
        }

        [Fact]
        public void The_hosts_answer_reaches_every_client()
        {
            var (host, port) = StartHost();
            var a = StartClient(port, "a");
            var b = StartClient(port, "b");
            Assert.True(Pump(() => host.RemotePlayerCount == 2
                                && a.Status == SessionStatus.Established
                                && b.Status == SessionStatus.Established, 8000, host, a, b),
                        "both clients never joined");

            SessionSpeed? atA = null, atB = null, atHost = null;
            a.TimeScaleChanged += m => atA = m.Speed;
            b.TimeScaleChanged += m => atB = m.Speed;
            host.TimeScaleChanged += m => atHost = m.Speed;

            host.SendTimeScale(new TimeScaleMessage { Speed = SessionSpeed.Fast });

            Assert.True(Pump(() => atA != null && atB != null, 4000, host, a, b),
                        "the answer did not reach both clients");
            Assert.Equal(SessionSpeed.Fast, atA);
            Assert.Equal(SessionSpeed.Fast, atB);

            // The host raises it on itself too, so it runs at exactly what it published
            // rather than at whatever it happened to set locally.
            Assert.Equal(SessionSpeed.Fast, atHost);
        }

        [Fact]
        public void A_speed_that_belongs_to_one_screen_is_never_sent()
        {
            var (host, port) = StartHost();
            var client = StartClient(port, "guest");
            Assert.True(Pump(() => client.Status == SessionStatus.Established
                                && host.RemotePlayerCount == 1, 8000, host, client), "no session");

            int seen = 0;
            client.TimeScaleChanged += _ => seen++;

            host.SendTimeScale(new TimeScaleMessage { Speed = SessionSpeed.Paused });
            host.SendTimeScale(new TimeScaleMessage { Speed = SessionSpeed.UnconsciousFast });

            Pump(() => false, 500, host, client);
            Assert.Equal(0, seen);
        }

        [Fact]
        public void Terrain_edits_from_an_unhandshaked_peer_are_ignored()
        {
            // A raw socket that never said Hello can still reach the port. Letting it
            // rewrite terrain would hand the world to anyone who found the port.
            var (host, port) = StartHost();

            var raw = new LiteNetTransport();
            _ownedTransports.Add(raw);
            raw.StartClient("127.0.0.1", (ushort)port);

            bool connected = false;
            raw.PeerConnected += _ => connected = true;

            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 8000 && !connected)
            {
                host.Poll();
                raw.Poll();
                Thread.Sleep(5);
            }
            Assert.True(connected, "raw peer never connected at the transport level");

            bool applied = false;
            host.BlockDeltasReceived += (_, __) => applied = true;

            var bytes = Edits((1, 1, 9)).Encode(1, host.SessionId, 1, 1);
            raw.Send(PeerId.Host, bytes, 0, bytes.Length, DeliveryClass.Transactions);

            sw.Restart();
            while (sw.ElapsedMilliseconds < 1000)
            {
                host.Poll();
                raw.Poll();
                Thread.Sleep(5);
            }

            Assert.False(applied, "an unhandshaked peer was allowed to edit the world");
        }
    }
}
