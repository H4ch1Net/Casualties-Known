using System;
using System.Collections.Generic;
using CCoop.Net.Session;
using CCoop.Plugin.Net;
using CCoop.Plugin.Runtime;
using Steamworks;

namespace CCoop.Plugin.Sync
{
    /// <summary>
    /// Steam game recording: one timeline phase per co-op run, a marker per layer, and
    /// markers when players join, leave or die, so a clip of "the time we all died on
    /// layer 6" is a click away. Nothing here is replicated; each player marks their own
    /// recording.
    /// </summary>
    internal sealed class SteamMoments : CoopModule
    {
        private bool _phase;
        private int _depth = -1;
        private ETimelineGameMode _mode = ETimelineGameMode.k_ETimelineGameMode_Invalid;
        private readonly Dictionary<byte, bool> _alive = new Dictionary<byte, bool>();

        private static bool Enabled =>
            CoopPlugin.Instance != null && CoopPlugin.Instance.SteamTimeline.Value && SteamServices.Ready;

        internal override void Detach()
        {
            EndPhase();
            _alive.Clear();
            _mode = ETimelineGameMode.k_ETimelineGameMode_Invalid;
        }

        private void EndPhase()
        {
            if (_phase) SteamServices.TimelineEndRun();
            _phase = false;
            _depth = -1;
        }

        internal override void OnPeerJoined(PeerInfo peer)
        {
            if (Enabled) SteamServices.TimelineEvent(peer.Name + " joined", "Co-op", "steam_group");
        }

        internal override void OnPeerLeft(PeerInfo peer)
        {
            if (Enabled) SteamServices.TimelineEvent(peer.Name + " left", "Co-op", "steam_x");
        }

        internal override void OnWorldLive()
        {
            var w = WorldGeneration.world;
            if (!Enabled || w == null) return;
            if (!_phase)
            {
                _phase = true;
                SteamServices.TimelineStartRun("ccoop-" + DateTime.UtcNow.Ticks);
            }
            if (w.biomeDepth == _depth) return;
            _depth = w.biomeDepth;
            string title = "Layer " + (_depth + 1);
            SteamServices.TimelineLayer(title, (uint)(_depth + 1));
            int n = Session != null ? Session.PeerCount : 1;
            SteamServices.TimelineEvent(title, "Co-op with " + n + (n == 1 ? " player" : " players"), "steam_flag");
        }

        internal override void Tick()
        {
            if (!Enabled) return;

            var mode = Coop.WorldLive ? ETimelineGameMode.k_ETimelineGameMode_Playing
                : GameInfo.InRunScene ? ETimelineGameMode.k_ETimelineGameMode_LoadingScreen
                : ETimelineGameMode.k_ETimelineGameMode_Menus;
            if (mode != _mode)
            {
                _mode = mode;
                SteamServices.TimelineMode(mode);
            }

            if (_phase && GameInfo.OnMainMenu) EndPhase();
            if (!Coop.WorldLive) return;

            foreach (var rec in Coop.Players.Values)
            {
                bool local = rec.Id == Coop.LocalId;
                var body = local ? GameInfo.LocalBody : null;
                bool alive = local ? body == null || body.alive : !rec.HasPosition || rec.Alive;
                if (_alive.TryGetValue(rec.Id, out var was) && was && !alive)
                    SteamServices.TimelineEvent((local ? "You" : rec.Name) + " died", "Layer " + (_depth + 1), "steam_death", local);
                _alive[rec.Id] = alive;
            }
        }
    }
}
