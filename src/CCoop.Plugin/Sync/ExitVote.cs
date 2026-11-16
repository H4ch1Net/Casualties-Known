using System.Collections.Generic;
using CCoop.Net.Session;
using CCoop.Plugin.Protocol;
using CCoop.Plugin.Runtime;
using CCoop.Plugin.UI;
using CCoop.Plugin.World;
using HarmonyLib;
using UnityEngine;

namespace CCoop.Plugin.Sync
{
    /// <summary>
    /// Descending as a team. Whenever the game would start the next layer (Continue at
    /// the bottom exit, a finished drill pod, the cargo pod, a cutscene), that becomes a
    /// vote. The host descends once the share of living players set by the host rule have
    /// voted; a drill pod needs every living player inside it. Players who didn't vote are
    /// brought along, never left behind.
    /// </summary>
    internal sealed class ExitVote : CoopModule
    {
        private enum Vote { None, Exit, Pod }

        // ---- local
        private Vote _local;
        private bool _votedAtExit;  // the vote was cast standing in the exit zone
        private float _nextStatus;

        // ---- host
        private readonly Dictionary<byte, bool> _votes = new Dictionary<byte, bool>();  // peer -> pod
        private bool _hadVotes;

        // ---- everyone
        private ExitStatus _status;

        /// <summary>Host: the next RegenerateWorld is ours and must run.</summary>
        internal static bool HostDescending;

        /// <summary>The local player is waiting at the exit: keep the save panel shut.</summary>
        internal static bool WaitingAtExit => Coop.Get<ExitVote>()?._local == Vote.Exit;

        internal override void Attach(NetSession s)
        {
            s.On<ExitStatus>((o, m) => { if (!Coop.IsHost) _status = m.Voters.Count > 0 ? m : null; });
        }

        internal override void Detach()
        {
            _local = Vote.None;
            _votes.Clear();
            _status = null;
            HostDescending = false;
        }

        private float _liveSince;
        private const float LoadingGrace = 60f;

        internal override void OnWorldLive()
        {
            _liveSince = Time.unscaledTime;
            ClearExitPanel();
        }

        /// <summary>
        /// The game's descend panel slows the player to a walk (no jumping) until its own
        /// Continue button is pressed. In co-op the team goes down together, so a player
        /// brought along never presses it: on a new layer it goes away, and so does the walk.
        /// The End lab and the tutorial keep theirs (they walk the player on purpose).
        /// </summary>
        private static void ClearExitPanel()
        {
            try
            {
                var w = WorldGeneration.world;
                if (w != null && w.savePanel != null && w.savePanel.activeSelf) w.savePanel.SetActive(false);
                var b = GameInfo.LocalBody;
                if (b == null || !b.forceWalk || EndLabScript.instance != null) return;
                if (UnityEngine.Object.FindObjectOfType<TutorialHandler>() != null) return;
                b.forceWalk = false;
                Log.Info("[exit] the new layer lets us run and jump again");
            }
            catch (System.Exception e) { Log.Debug("[exit] clearing the panel: " + e.Message); }
        }

        internal override void OnWorldLost()
        {
            _local = Vote.None;
            _votes.Clear();
            _status = null;
            _hadVotes = false;
        }

        internal override void OnPeerLeft(PeerInfo peer) => _votes.Remove(peer.Id);

        // ------------------------------------------------------------------ local

        private static bool InExitZone(Body body)
        {
            var w = WorldGeneration.world;
            return w != null && body != null && body.transform.position.y < -w.halfHeight + 3.1f + 1.5f;
        }

        /// <summary>The game wants to descend. Returns whether to let it run now.</summary>
        internal bool LocalWantsToDescend(bool twice)
        {
            var body = GameInfo.LocalBody;
            if (body != null && !body.alive) return false;
            bool fresh = _local == Vote.None;
            _local = twice ? Vote.Pod : Vote.Exit;
            _votedAtExit = !twice && InExitZone(body);
            if (Coop.IsHost)
            {
                _votes[NetSession.HostId] = twice;
                return HostEvaluate(true);
            }
            Session.SendToHost(new LayerRequest { Twice = twice });
            if (fresh) CoopRuntime.Notice(twice ? Lang.T("Waiting for everyone to get into the drill pod") : Lang.T("Waiting for the team at the exit"));
            return false;
        }

        private void CancelLocal(string why)
        {
            if (_local == Vote.None) return;
            if (_local == Vote.Pod && WorldGeneration.world != null) WorldGeneration.world.doPod = false;
            _local = Vote.None;
            Log.Info("[exit] vote withdrawn: " + why);
            if (Coop.IsHost) _votes.Remove(NetSession.HostId);
            else Session.SendToHost(new LayerRequest { Cancel = true });
        }

        internal override void Tick()
        {
            if (!Coop.WorldLive) return;
            var body = GameInfo.LocalBody;
            if (_local != Vote.None)
            {
                if (body == null || !body.alive) CancelLocal("died");
                else if (_local == Vote.Exit && _votedAtExit && !InExitZone(body)) CancelLocal("left the exit");
            }
            if (Coop.IsHost) HostTick();
        }

        // ------------------------------------------------------------------ host

        /// <summary>Host: a client's vote (or its withdrawal).</summary>
        internal void HostVote(byte peer, LayerRequest m)
        {
            if (!Coop.IsHost) return;
            if (m.Cancel)
            {
                _votes.Remove(peer);
                return;
            }
            if (!Coop.WorldLive || WorldAccess.Regenerating) return;
            if (!Coop.Players.TryGetValue(peer, out var rec) || !rec.Alive) return;
            bool fresh = !_votes.ContainsKey(peer);
            _votes[peer] = m.Twice;
            if (fresh) Log.Info("[exit] " + rec.Name + " votes to descend" + (m.Twice ? " by drill pod" : ""));
            HostEvaluate(false);
        }

        private float _nextEval;
        private float _nextLoadingLog;

        private void HostTick()
        {
            if (WorldAccess.Regenerating || Time.unscaledTime < _nextEval) return;
            _nextEval = Time.unscaledTime + 0.25f;
            // Votes from the dead or departed don't count.
            var drop = new List<byte>();
            foreach (var id in _votes.Keys)
            {
                if (id == NetSession.HostId) continue;
                if (!Coop.Players.TryGetValue(id, out var r) || !r.Alive || !r.InWorld) drop.Add(id);
            }
            foreach (var id in drop) _votes.Remove(id);
            HostEvaluate(false);
        }

        private struct Count
        {
            public int Living, Needed, Inside;
            public bool Pod;
            public string Loading;
        }

        private Count Tally()
        {
            var c = new Count();
            var positions = new List<Vector2>();
            var body = GameInfo.LocalBody;
            if (body != null && body.alive) positions.Add(body.transform.position);
            foreach (var rec in Coop.Players.Values)
                if (rec.Id != NetSession.HostId && rec.InWorld && rec.HasPosition && rec.Alive) positions.Add(rec.Position);
            c.Living = positions.Count;
            foreach (var pod in _votes.Values) c.Pod |= pod;

            // Someone still loading this layer would be dragged straight on to the next
            // one (and isn't counted as alive here yet): wait for them, within reason.
            if (Time.unscaledTime - _liveSince < LoadingGrace)
                foreach (var rec in Coop.Players.Values)
                    if (rec.Id != NetSession.HostId && !rec.InWorld && rec.Status == "loading")
                        c.Loading = c.Loading == null ? rec.Name : c.Loading + ", " + rec.Name;

            if (c.Pod)
            {
                // Everyone alive must be in the pod a voter is in.
                var pods = Object.FindObjectsOfType<DrillPod>();
                Vector2? podAt = null;
                foreach (var kv in _votes)
                {
                    if (!kv.Value) continue;
                    Vector2 voter = kv.Key == NetSession.HostId
                        ? (body != null ? (Vector2)body.transform.position : Vector2.zero)
                        : Coop.Player(kv.Key).Position;
                    float best = float.MaxValue;
                    foreach (var p in pods)
                    {
                        float d = Vector2.Distance(voter, p.transform.position);
                        if (d < best) { best = d; podAt = p.transform.position; }
                    }
                    break;
                }
                foreach (var pos in positions)
                    if (podAt.HasValue && Mathf.Abs(pos.x - podAt.Value.x) < 1.5f && Mathf.Abs(pos.y - podAt.Value.y) < 5f) c.Inside++;
                if (!podAt.HasValue) c.Inside = c.Living;  // no pod object found (cargo pod etc.): don't block
                c.Needed = c.Living;
            }
            else
            {
                int share = HostRules.Current.ExitShare;
                c.Needed = share <= 0 ? 1 : Mathf.CeilToInt(c.Living * share / 100f);
                c.Needed = Mathf.Clamp(c.Needed, 1, Mathf.Max(1, c.Living));
            }
            return c;
        }

        /// <summary>Host: descend if enough have voted. When <paramref name="fromLocalCall"/>, the caller runs the descent itself.</summary>
        private bool HostEvaluate(bool fromLocalCall)
        {
            if (_votes.Count == 0)
            {
                if (_hadVotes) Session.Broadcast(new ExitStatus());
                _hadVotes = false;
                _status = null;
                return false;
            }
            _hadVotes = true;
            var c = Tally();
            bool go = (c.Pod ? c.Inside >= c.Needed : _votes.Count >= c.Needed) && c.Loading == null;
            if (c.Loading != null && Time.unscaledTime >= _nextLoadingLog)
            {
                _nextLoadingLog = Time.unscaledTime + 5f;
                Log.Info("[exit] holding the descent: " + c.Loading + " still loading");
            }

            var status = new ExitStatus { Needed = (byte)c.Needed, Living = (byte)c.Living, Pod = c.Pod, Inside = (byte)c.Inside, Loading = c.Loading ?? "" };
            status.Voters.AddRange(_votes.Keys);
            _status = status;

            if (!go)
            {
                if (Time.unscaledTime >= _nextStatus)
                {
                    _nextStatus = Time.unscaledTime + 0.5f;
                    Session.Broadcast(status);
                }
                return false;
            }

            bool twice = c.Pod;
            Log.Info("[exit] descending" + (twice ? " by drill pod" : "") + ": " + _votes.Count + " voted, " + c.Needed + " needed of " + c.Living + " alive");
            _votes.Clear();
            _hadVotes = false;
            _status = null;
            Session.Broadcast(new ExitStatus());
            PodDescent = twice;
            if (twice && WorldGeneration.world != null) WorldGeneration.world.doPod = true;
            if (fromLocalCall) return true;
            var w = WorldGeneration.world;
            HostDescending = true;
            try { w.StartCoroutine(w.RegenerateWorld(twice)); }
            finally { HostDescending = false; }
            return false;
        }

        /// <summary>Host: the descent now starting is by drill pod (sent with LayerBegin).</summary>
        internal static bool PodDescent;

        // ------------------------------------------------------------------ overlay

        /// <summary>Dev: "voters/needed" as this machine sees it.</summary>
        internal static ExitStatus DevFake;

        internal string Describe() => _status == null ? "none" : _status.Voters.Count + "/" + _status.Needed + (_status.Pod ? " pod" : "");

        internal override void Gui()
        {
            var st = DevFake ?? _status;
            if (st == null || st.Voters.Count == 0 || !Coop.WorldLive) return;
            bool mine = _local != Vote.None;
            string top, bottom;
            if (st.Pod)
            {
                top = mine ? Lang.F("In the drill pod: {0} of {1} inside", st.Inside, st.Living) : Lang.F("{0} wants to drill down", Names(st.Voters));
                bottom = Lang.T("Everyone alive must crouch in the drill pod.");
            }
            else
            {
                top = mine ? Lang.F("Waiting at the exit: {0} of {1} needed", st.Voters.Count, st.Needed)
                           : st.Voters.Count == 1 ? Lang.F("{0} is waiting at the exit ({1} of {2} needed)", Names(st.Voters), st.Voters.Count, st.Needed)
                           : Lang.F("{0} are waiting at the exit ({1} of {2} needed)", Names(st.Voters), st.Voters.Count, st.Needed);
                bottom = mine ? Lang.T("Walk away from the exit to cancel.") : Lang.T("Join them at the exit. Once enough are there, everyone goes down.");
            }
            if (!string.IsNullOrEmpty(st.Loading)) bottom = Lang.F("Waiting for {0} to finish loading this layer.", st.Loading);
            Overlay.Bottom(top, bottom);
        }

        private static string Names(List<byte> ids)
        {
            var names = new List<string>();
            foreach (var id in ids) names.Add(id == Coop.LocalId ? Lang.T("You") : Coop.Player(id).Name);
            return string.Join(", ", names.ToArray());
        }
    }

    /// <summary>While the local player waits at the exit, the game's save panel stays shut.</summary>
    [HarmonyPatch(typeof(WorldGeneration), "Update")]
    internal static class ExitPanelPatch
    {
        private static void Postfix(WorldGeneration __instance)
        {
            if (!ExitVote.WaitingAtExit || __instance.savePanel == null || !__instance.savePanel.activeSelf) return;
            __instance.savePanel.SetActive(false);
            var body = GameInfo.LocalBody;
            if (body != null) body.forceWalk = false;
        }
    }
}
