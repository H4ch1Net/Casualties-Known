using System;
using System.Collections.Generic;
using CCoop.Net;
using CCoop.Net.Session;

namespace CCoop.Plugin.Runtime
{
    /// <summary>
    /// The channel for changes to the shared layer (terrain, entities, items).
    ///
    /// Every such change goes through the host, which applies it and then relays it only
    /// to peers that hold this layer's snapshot. That one rule gives ordering for free:
    /// a peer receives, on one reliable stream, the snapshot and then every change made
    /// after it was captured — never a change already baked into the snapshot (which
    /// would apply twice) and never a change from a layer it has left.
    ///
    /// Clients still applying their snapshot buffer what arrives and replay it once the
    /// layer is live.
    /// </summary>
    internal static class WorldNet
    {
        private const int MaxQueued = 50000;
        private static readonly Queue<Action> Pending = new Queue<Action>();
        private static bool _overflowed;

        /// <summary>
        /// Registers a world-scoped message. <paramref name="apply"/> runs with the remote
        /// guard raised so the hooks it triggers do not publish the change back out.
        /// </summary>
        internal static void Register<T>(NetSession session, Action<byte, T> apply, bool queueWhilePending = true, bool relay = true)
            where T : IMessage
        {
            session.On<T>((origin, msg) =>
            {
                if (Coop.IsHost)
                {
                    if (!Coop.WorldLive) return;
                    if (origin != NetSession.HostId)
                    {
                        // A client still on an older layer (or not in the run at all).
                        if (!Coop.Players.TryGetValue(origin, out var rec) || !rec.InWorld || rec.WorldEpoch != Coop.Epoch) return;
                        Run(apply, origin, msg);
                    }
                    if (relay) RelayToWorld(origin, msg);
                    return;
                }

                if (Coop.WorldLive) Run(apply, origin, msg);
                else if (queueWhilePending && AwaitingWorld)
                {
                    if (Pending.Count >= MaxQueued)
                    {
                        if (!_overflowed) Log.Warn("[world] too many changes queued while loading; some were dropped");
                        _overflowed = true;
                        return;
                    }
                    Pending.Enqueue(() => Run(apply, origin, msg));
                }
            });
        }

        private static void Run<T>(Action<byte, T> apply, byte origin, T msg)
        {
            Coop.ApplyingRemote++;
            try { apply(origin, msg); }
            catch (Exception e) { Log.Warn("[world] applying " + typeof(T).Name + " failed: " + e.Message); }
            finally { Coop.ApplyingRemote--; }
        }

        /// <summary>Publishes a local change to everyone in the layer.</summary>
        internal static void Publish<T>(T msg) where T : IMessage
        {
            if (!Coop.InSession || !Coop.WorldLive) return;
            if (Coop.IsHost) RelayToWorld(NetSession.HostId, msg);
            else Coop.Session.SendToHost(msg);
        }

        /// <summary>Host: sends to one peer if it is in the current layer.</summary>
        internal static void SendToWorldPeer<T>(byte peer, T msg) where T : IMessage
        {
            if (!Coop.IsHost) return;
            if (Coop.Players.TryGetValue(peer, out var rec) && rec.WorldEpoch == Coop.Epoch && rec.WorldEpoch != 0)
                Coop.Session.RelayTo(peer, NetSession.HostId, msg);
        }

        private static void RelayToWorld<T>(byte origin, T msg) where T : IMessage
        {
            foreach (var rec in Coop.Players.Values)
            {
                if (rec.Id == NetSession.HostId || rec.Id == origin) continue;
                if (rec.WorldEpoch != Coop.Epoch || rec.WorldEpoch == 0) continue;
                Coop.Session.RelayTo(rec.Id, origin, msg);
            }
        }

        // ---- client-side buffering ----

        /// <summary>Client: a snapshot for the current epoch is on its way or being applied.</summary>
        internal static bool AwaitingWorld;

        internal static void BeginAwaiting()
        {
            Pending.Clear();
            _overflowed = false;
            AwaitingWorld = true;
        }

        /// <summary>Client: the snapshot is applied; replay what arrived meanwhile.</summary>
        internal static int Drain()
        {
            AwaitingWorld = false;
            int n = Pending.Count;
            while (Pending.Count > 0) Pending.Dequeue()();
            return n;
        }

        internal static void Clear()
        {
            Pending.Clear();
            AwaitingWorld = false;
            _overflowed = false;
        }
    }
}
