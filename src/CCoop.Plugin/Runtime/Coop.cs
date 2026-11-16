using System;
using System.Collections.Generic;
using CCoop.Net;
using CCoop.Net.Session;
using UnityEngine;

namespace CCoop.Plugin.Runtime
{
    /// <summary>What the host knows about each player in the session.</summary>
    internal sealed class PlayerRecord
    {
        public byte Id;
        public string Name = "";
        public int Character;

        /// <summary>Their intelligence skill, or -1 when unknown (not in a layer).</summary>
        public int Intellect = -1;
        public ulong SteamId;

        /// <summary>Epoch of the layer snapshot this peer was sent (host-side), 0 = none.</summary>
        public ushort WorldEpoch;

        /// <summary>The peer has applied its snapshot and is playing in the shared layer.</summary>
        public bool InWorld;

        /// <summary>Waiting at an empty world scaffold for a snapshot (host-side).</summary>
        public ushort WaitingForEpoch;

        public ushort ReportedEpoch;
        public Vector2 Position;
        public bool HasPosition;
        public bool Alive = true;
        public bool Conscious = true;
        public bool Sleeping;
        public bool Dying;
        public float LastStateTime;
        public string Status = "";

        /// <summary>Their frame rate as last reported (0 = not known yet).</summary>
        public int Fps;

        /// <summary>They reached an ending and are on the end screen.</summary>
        public bool Finished;
    }

    /// <summary>Static entry points for Harmony patches, which cannot be handed instances.</summary>
    internal static class Coop
    {
        internal static CoopRuntime Runtime;
        internal static NetSession Session;

        internal static bool InSession => Session != null && Session.State == SessionState.Established;
        internal static bool IsHost => InSession && Session.IsHost;
        internal static bool IsClient => InSession && !Session.IsHost;
        internal static byte LocalId => Session != null ? Session.LocalId : NetSession.HostId;

        /// <summary>Generation counter for layers. Bumped by the host each time a layer is built.</summary>
        internal static ushort Epoch;

        /// <summary>
        /// The local world is the shared layer and changes to it should be replicated.
        /// False in menus, while generating, and while a client is applying a snapshot.
        /// </summary>
        internal static bool WorldLive { get; private set; }

        /// <summary>
        /// Set while this peer is applying a change that came from the network, so the
        /// hooks that would normally publish that change stay quiet.
        /// </summary>
        internal static int ApplyingRemote;

        internal static bool Applying => ApplyingRemote > 0;

        internal static readonly Dictionary<byte, PlayerRecord> Players = new Dictionary<byte, PlayerRecord>();

        /// <summary>
        /// The player is playing in this machine's layer and has a position. The host
        /// tracks <see cref="PlayerRecord.InWorld"/> from layer status; clients only see
        /// teammates' states, so there it is their reported layer.
        /// </summary>
        internal static bool InLayer(PlayerRecord rec) =>
            rec != null && rec.HasPosition && (IsHost ? rec.InWorld : rec.ReportedEpoch == Epoch);

        internal static PlayerRecord Player(byte id)
        {
            if (!Players.TryGetValue(id, out var r))
            {
                r = new PlayerRecord { Id = id };
                Players[id] = r;
            }
            return r;
        }

        internal static bool IsLocalBody(Body b) => b != null && b == GameInfo.LocalBody;

        internal static T Get<T>() where T : CoopModule => Runtime != null ? Runtime.Get<T>() : null;

        internal static void SetWorldLive(bool live)
        {
            if (WorldLive == live) return;
            WorldLive = live;
            Log.Info(live ? "[world] layer " + Epoch + " is live" : "[world] layer offline");
            Runtime?.NotifyWorld(live);
        }

        internal static void Reset()
        {
            WorldLive = false;
            Epoch = 0;
            ApplyingRemote = 0;
            Players.Clear();
            Session = null;
        }

        /// <summary>Runs <paramref name="a"/> with the remote-apply guard raised.</summary>
        internal static void AsRemote(Action a)
        {
            ApplyingRemote++;
            try { a(); }
            finally { ApplyingRemote--; }
        }
    }

    /// <summary>A unit of synchronisation. Lifetime follows the session.</summary>
    internal abstract class CoopModule
    {
        protected NetSession Session => Coop.Session;

        /// <summary>Session created: register message handlers here.</summary>
        internal virtual void Attach(NetSession session) { }

        /// <summary>Session ended: drop all state.</summary>
        internal virtual void Detach() { }

        internal virtual void OnPeerJoined(PeerInfo peer) { }
        internal virtual void OnPeerLeft(PeerInfo peer) { }

        /// <summary>The shared layer became live on this peer.</summary>
        internal virtual void OnWorldLive() { }

        /// <summary>The shared layer is going away (new layer, menu, disconnect).</summary>
        internal virtual void OnWorldLost() { }

        internal virtual void Tick() { }
        internal virtual void LateTick() { }
        internal virtual void Gui() { }
    }
}
