using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using CCoop.Net;
using CCoop.Net.Session;
using CCoop.Net.Transport;
using CCoop.Net.Wire;
using Xunit;

namespace CCoop.Net.Tests
{
    public sealed class Chat : IMessage
    {
        public string Text = "";
        public void Write(PacketWriter w) => w.WriteString(Text);
        public void Read(ref PacketReader r) => Text = r.ReadString();
    }

    public sealed class Pos : IMessage
    {
        public float X, Y;
        public void Write(PacketWriter w) { w.WriteSingle(X); w.WriteSingle(Y); }
        public void Read(ref PacketReader r) { X = r.ReadSingle(); Y = r.ReadSingle(); }
    }

    public class SessionTests
    {
        private static MessageRegistry Registry(bool extra = false)
        {
            var r = new MessageRegistry()
                .Add<Chat>(MessageRegistry.FirstUserId, Delivery.Reliable)
                .Add<Pos>(MessageRegistry.FirstUserId + 1, Delivery.Unreliable);
            if (extra) r.Add<Extra>(MessageRegistry.FirstUserId + 2, Delivery.Reliable);
            return r;
        }

        public sealed class Extra : IMessage
        {
            public void Write(PacketWriter w) { }
            public void Read(ref PacketReader r) { }
        }

        private static SessionConfig Cfg(string name, string ver = "1", string build = "b") =>
            new SessionConfig { PlayerName = name, ModVersion = ver, GameBuild = build };

        private static void Pump(params NetSession[] s)
        {
            for (int i = 0; i < 20; i++) foreach (var x in s) x.Poll();
        }

        [Fact]
        public void Handshake_assigns_ids_and_announces_peers()
        {
            var hub = new LoopbackHub();
            var host = NetSession.CreateHost(hub.Host, Registry(), Cfg("Host"));
            var a = NetSession.CreateClient(hub.Connect(), Registry(), Cfg("Alice"));
            Pump(host, a);
            var b = NetSession.CreateClient(hub.Connect(), Registry(), Cfg("Bob"));
            Pump(host, a, b);

            Assert.Equal(SessionState.Established, a.State);
            Assert.Equal(SessionState.Established, b.State);
            Assert.Equal(1, a.LocalId);
            Assert.Equal(2, b.LocalId);
            Assert.Equal(3, host.PeerCount);
            Assert.Equal(3, a.PeerCount);
            Assert.Equal(3, b.PeerCount);
            Assert.True(a.TryGetPeer(2, out var bob) && bob.Name == "Bob");
        }

        [Fact]
        public void Mismatched_registry_is_rejected_with_reason()
        {
            var hub = new LoopbackHub();
            var host = NetSession.CreateHost(hub.Host, Registry(), Cfg("Host"));
            var a = NetSession.CreateClient(hub.Connect(), Registry(extra: true), Cfg("Alice"));
            string reason = null;
            a.Ended += r => reason = r;
            Pump(host, a);
            Assert.Equal(SessionState.Ended, a.State);
            Assert.Contains("does not match", reason);
            Assert.Equal(1, host.PeerCount);
        }

        [Fact]
        public void Mismatched_game_build_is_rejected()
        {
            var hub = new LoopbackHub();
            var host = NetSession.CreateHost(hub.Host, Registry(), Cfg("Host", build: "x"));
            var a = NetSession.CreateClient(hub.Connect(), Registry(), Cfg("Alice", build: "y"));
            Pump(host, a);
            Assert.Equal(SessionState.Ended, a.State);
            Assert.Contains("game version", a.EndReason);
        }

        [Fact]
        public void Client_broadcast_is_relayed_with_true_origin()
        {
            var hub = new LoopbackHub();
            var host = NetSession.CreateHost(hub.Host, Registry(), Cfg("Host"));
            var a = NetSession.CreateClient(hub.Connect(), Registry(), Cfg("Alice"));
            var b = NetSession.CreateClient(hub.Connect(), Registry(), Cfg("Bob"));
            Pump(host, a, b);

            var got = new List<string>();
            host.On<Chat>((o, m) => got.Add("host<-" + o + ":" + m.Text));
            a.On<Chat>((o, m) => got.Add("a<-" + o + ":" + m.Text));
            b.On<Chat>((o, m) => got.Add("b<-" + o + ":" + m.Text));

            a.Broadcast(new Chat { Text = "hi" });
            Pump(host, a, b);

            Assert.Contains("host<-1:hi", got);
            Assert.Contains("b<-1:hi", got);
            Assert.DoesNotContain(got, s => s.StartsWith("a<-"));
        }

        [Fact]
        public void Traffic_is_counted_per_type_and_reset_by_take()
        {
            var hub = new LoopbackHub();
            var host = NetSession.CreateHost(hub.Host, Registry(), Cfg("Host"));
            var a = NetSession.CreateClient(hub.Connect(), Registry(), Cfg("Alice"));
            var b = NetSession.CreateClient(hub.Connect(), Registry(), Cfg("Bob"));
            Pump(host, a, b);
            host.Traffic.Take(null);
            a.Traffic.Take(null);
            b.Traffic.Take(null);

            a.Broadcast(new Chat { Text = "hello" });
            Pump(host, a, b);

            var sent = a.Traffic.Take(a.TypeName);
            var chat = sent.Find(l => l.Name == nameof(Chat));
            Assert.Equal(1, chat.Sent);
            Assert.True(chat.SentBytes > 0);
            // The host receives it once and forwards it once (to Bob).
            var relayed = host.Traffic.Take(host.TypeName).Find(l => l.Name == nameof(Chat));
            Assert.Equal(1, relayed.Received);
            Assert.Equal(1, relayed.Sent);
            Assert.Equal(1, b.Traffic.Take(b.TypeName).Find(l => l.Name == nameof(Chat)).Received);
            Assert.Empty(a.Traffic.Take(null).FindAll(l => l.Name == nameof(Chat)));
            Assert.Contains("KB/s", TrafficStats.Describe(sent, 1f));
        }

        [Fact]
        public void Simulated_drop_ends_the_client_as_a_lost_connection()
        {
            var hub = new LoopbackHub();
            var host = NetSession.CreateHost(hub.Host, Registry(), Cfg("Host"));
            var a = NetSession.CreateClient(hub.Connect(), Registry(), Cfg("Alice"));
            Pump(host, a);
            string reason = null;
            a.Ended += r => reason = r;
            var left = new List<byte>();
            host.PeerLeftEvent += (p, r) => left.Add(p.Id);

            a.SimulateDrop();
            Pump(host, a);

            Assert.NotNull(reason);
            Assert.StartsWith("lost connection to the host", reason);
            Assert.Contains((byte)1, left);
        }

        [Fact]
        public void Directed_message_reaches_only_its_target()
        {
            var hub = new LoopbackHub();
            var host = NetSession.CreateHost(hub.Host, Registry(), Cfg("Host"));
            var a = NetSession.CreateClient(hub.Connect(), Registry(), Cfg("Alice"));
            var b = NetSession.CreateClient(hub.Connect(), Registry(), Cfg("Bob"));
            var c = NetSession.CreateClient(hub.Connect(), Registry(), Cfg("Cid"));
            Pump(host, a, b, c);

            int hostGot = 0, bGot = 0, cGot = 0;
            host.On<Chat>((o, m) => hostGot++);
            b.On<Chat>((o, m) => { if (o == 1) bGot++; });
            c.On<Chat>((o, m) => cGot++);

            a.SendTo(2, new Chat { Text = "psst" });
            Pump(host, a, b, c);

            Assert.Equal(0, hostGot);
            Assert.Equal(1, bGot);
            Assert.Equal(0, cGot);
        }

        [Fact]
        public void Host_self_send_is_deferred_to_next_poll()
        {
            var hub = new LoopbackHub();
            var host = NetSession.CreateHost(hub.Host, Registry(), Cfg("Host"));
            int n = 0;
            host.On<Chat>((o, m) => n++);
            host.SendToHost(new Chat { Text = "self" });
            Assert.Equal(0, n);
            host.Poll();
            Assert.Equal(1, n);
        }

        [Fact]
        public void Blob_reassembles_across_chunks()
        {
            var hub = new LoopbackHub();
            var host = NetSession.CreateHost(hub.Host, Registry(), Cfg("Host"));
            var a = NetSession.CreateClient(hub.Connect(), Registry(), Cfg("Alice"));
            Pump(host, a);

            var data = new byte[NetSession.BlobChunkBytes * 3 + 123];
            new Random(5).NextBytes(data);
            byte[] got = null;
            ushort tag = 0;
            a.BlobReceived += (o, t, d) => { got = d; tag = t; };
            host.SendBlob(1, 77, data);
            Pump(host, a);

            Assert.NotNull(got);
            Assert.Equal(77, tag);
            Assert.True(got.SequenceEqual(data));
        }

        [Fact]
        public void Peer_leaving_is_announced()
        {
            var hub = new LoopbackHub();
            var host = NetSession.CreateHost(hub.Host, Registry(), Cfg("Host"));
            var a = NetSession.CreateClient(hub.Connect(), Registry(), Cfg("Alice"));
            var b = NetSession.CreateClient(hub.Connect(), Registry(), Cfg("Bob"));
            Pump(host, a, b);

            byte left = 0;
            b.PeerLeftEvent += (p, r) => left = p.Id;
            a.Leave();
            Pump(host, b);
            Assert.Equal(1, left);
            Assert.Equal(2, host.PeerCount);
        }

        [Fact]
        public void Duplicate_names_are_disambiguated()
        {
            var hub = new LoopbackHub();
            var host = NetSession.CreateHost(hub.Host, Registry(), Cfg("Sam"));
            var a = NetSession.CreateClient(hub.Connect(), Registry(), Cfg("sam"));
            Pump(host, a);
            Assert.True(host.TryGetPeer(1, out var p));
            Assert.NotEqual("sam", p.Name);
        }

        [Fact]
        public void Wrong_password_is_rejected_and_right_one_admitted()
        {
            var hub = new LoopbackHub();
            var hostCfg = Cfg("Host");
            hostCfg.Password = "secret";
            var host = NetSession.CreateHost(hub.Host, Registry(), hostCfg);

            var wrong = Cfg("Eve");
            wrong.Password = "guess";
            var a = NetSession.CreateClient(hub.Connect(), Registry(), wrong);
            Pump(host, a);
            Assert.Equal(SessionState.Ended, a.State);
            Assert.Contains("password", a.EndReason, StringComparison.OrdinalIgnoreCase);

            var right = Cfg("Bob");
            right.Password = "secret";
            var b = NetSession.CreateClient(hub.Connect(), Registry(), right);
            Pump(host, b);
            Assert.Equal(SessionState.Established, b.State);
        }

        [Fact]
        public void Admission_hook_sees_verified_address_and_can_refuse()
        {
            var hub = new LoopbackHub();
            var hostCfg = Cfg("Host");
            string seen = null;
            hostCfg.ExtraAdmission = (hello, address) =>
            {
                seen = address;
                return hello.PlayerName == "Banned" ? "You are banned from this session." : null;
            };
            var host = NetSession.CreateHost(hub.Host, Registry(), hostCfg);
            var a = NetSession.CreateClient(hub.Connect(), Registry(), Cfg("Banned"));
            Pump(host, a);
            Assert.Equal(SessionState.Ended, a.State);
            Assert.Contains("banned", a.EndReason);
            Assert.StartsWith("loopback:", seen);

            var b = NetSession.CreateClient(hub.Connect(), Registry(), Cfg("Fine"));
            Pump(host, b);
            Assert.Equal(SessionState.Established, b.State);
            Assert.True(host.TryGetPeer(b.LocalId, out var peer));
            Assert.StartsWith("loopback:", peer.Address);
        }

        [Fact]
        public void Ping_table_carries_round_trip_and_quality_to_clients()
        {
            var hub = new LoopbackHub();
            var host = NetSession.CreateHost(hub.Host, Registry(), Cfg("Host"));
            var a = NetSession.CreateClient(hub.Connect(), Registry(), Cfg("Alice"));
            Pump(host, a);
            var deadline = DateTime.UtcNow.AddSeconds(4);
            PeerInfo me = null;
            while (DateTime.UtcNow < deadline)
            {
                Pump(host, a);
                if (a.TryGetPeer(a.LocalId, out me) && me.Quality >= 0f) break;
                Thread.Sleep(50);
            }
            Assert.NotNull(me);
            Assert.Equal(1f, me.Quality, 2);
            Assert.Equal(0, me.RoundTripMs);
        }

        [Fact]
        public void Reader_rejects_truncated_and_nan()
        {
            var w = new PacketWriter();
            w.WriteSingle(float.NaN);
            var r = new PacketReader(w.ToArray(), 0, w.Length);
            r.ReadSingle();
            Assert.True(r.Failed);

            var r2 = new PacketReader(new byte[] { 5, 0, 65 }, 0, 3);
            r2.ReadString();
            Assert.True(r2.Failed);
        }

        [Fact]
        public void Quantized_values_round_trip_closely()
        {
            var w = new PacketWriter();
            w.WriteQuantized(3.21f, 8f);
            w.WriteAngle(-90f);
            w.WriteUnit(0.5f);
            var r = new PacketReader(w.ToArray(), 0, w.Length);
            Assert.InRange(r.ReadQuantized(8f), 3.2f, 3.22f);
            Assert.InRange(r.ReadAngle(), 269.9f, 270.1f);
            Assert.InRange(r.ReadUnit(), 0.49f, 0.51f);
        }

        [Fact]
        public void LiteNet_end_to_end_over_udp()
        {
            var registry = Registry();
            var ht = new LiteNetTransport();
            ht.StartHost(0, 4);
            var host = NetSession.CreateHost(ht, registry, Cfg("Host"));
            var ct = new LiteNetTransport();
            ct.StartClient("127.0.0.1", ht.LocalPort);
            var client = NetSession.CreateClient(ct, Registry(), Cfg("Alice"));

            string got = null;
            host.On<Chat>((o, m) => got = m.Text);
            var deadline = DateTime.UtcNow.AddSeconds(5);
            bool sent = false;
            while (DateTime.UtcNow < deadline && got == null)
            {
                host.Poll();
                client.Poll();
                if (!sent && client.State == SessionState.Established)
                {
                    client.SendToHost(new Chat { Text = "over udp" });
                    sent = true;
                }
                Thread.Sleep(5);
            }
            client.Leave();
            host.Leave();
            Assert.Equal("over udp", got);
        }
    }
}
