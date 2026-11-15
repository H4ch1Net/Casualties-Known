using System.Collections.Generic;
using CCoop.Net.Session;
using CCoop.Plugin.Protocol;
using CCoop.Plugin.Runtime;
using UnityEngine;
using CCoop.Plugin.UI;

namespace CCoop.Plugin.Sync
{
    /// <summary>
    /// Everyone starts a layer together. The host's game builds the layer first and a
    /// client may take a while to load it (a slow disk, a big layer), so without this the
    /// host would be off exploring, and the client would arrive wherever the host had got
    /// to. Instead, whoever has arrived waits at the arrival point, held still, until the
    /// whole team is in; then everyone is let go at once. A player who doesn't come
    /// (left, failed, stayed in the menu) isn't waited for, nor anyone for more than
    /// <see cref="Timeout"/> seconds, and the host can start early with the jump key.
    /// Only ordinary layers: the surface and the End lab have their own openings.
    /// </summary>
    internal sealed class ArrivalGate : CoopModule
    {
        internal const float Timeout = 75f;
        private const float NotComing = 25f;

        // Host
        private readonly HashSet<byte> _expected = new HashSet<byte>();
        private ushort _epoch;
        private float _since = -1f;
        private bool _open = true;
        private float _sentAt;

        // Everyone: the gate as the host last told us (for our current epoch).
        private ushort _shownEpoch;
        private bool _shownOpen = true;
        private string _waitingFor = "";
        private float _shownLeft;
        private bool _holding;

        internal static int Opened, TimedOut, Skipped;

        /// <summary>The local player is being held at the arrival point.</summary>
        internal static bool Holding => Coop.Get<ArrivalGate>()?._holding == true;

        internal override void Attach(NetSession s) => s.On<ArrivalStatus>(OnStatus);

        internal override void Detach()
        {
            Let("session over");
            _expected.Clear();
            _open = true;
            _since = -1f;
            _shownOpen = true;
        }

        internal override void OnPeerLeft(PeerInfo peer) => _expected.Remove(peer.Id);

        private static bool Applies(WorldGeneration w) =>
            w != null && w.biomeDepth <= 8 && w.biomeOverride == WorldGeneration.OverrideSceneType.None && !Dev.DevHarness.NoArrivalGate;

        // ------------------------------------------------------------------ host

        /// <summary>Host: a new layer is being built (epoch already advanced).</summary>
        internal void HostLayerStarting()
        {
            _expected.Clear();
            foreach (var rec in Coop.Players.Values)
                if (rec.Id != NetSession.HostId && rec.Status != "failed") _expected.Add(rec.Id);
            _epoch = Coop.Epoch;
            _open = _expected.Count == 0;
            _since = -1f;
        }

        internal override void OnWorldLive()
        {
            if (!Coop.IsHost) return;
            if (_epoch != Coop.Epoch || !Applies(WorldGeneration.world)) { _open = true; return; }
            if (_open) return;
            _since = Time.unscaledTime;
            Log.Info("[gate] layer ready; waiting for " + Names(_expected) + " before anyone sets off");
        }

        internal override void Tick()
        {
            if (Coop.IsHost) HostTick();
            Hold();
        }

        private void HostTick()
        {
            if (_open || _since < 0f || !Coop.WorldLive) return;
            float now = Time.unscaledTime;
            float waited = now - _since;
            var still = new List<byte>();
            foreach (var id in _expected)
            {
                var rec = Coop.Player(id);
                if (rec.InWorld && rec.WorldEpoch == _epoch) continue;            // arrived
                if (rec.Status == "failed") continue;                              // not coming
                if (rec.Status == "menu" && waited > NotComing) continue;          // stayed in the menu
                still.Add(id);
            }
            string why = null;
            if (still.Count == 0) why = "everyone is in";
            else if (waited > Timeout) { why = "waited " + (int)Timeout + " s"; TimedOut++; }
            else if (waited > 3f && JumpPressed()) { why = "the host went ahead"; Skipped++; }
            if (why != null)
            {
                _open = true;
                Opened++;
                Session.Broadcast(new ArrivalStatus { Epoch = _epoch, Open = true });
                Show(_epoch, true, "", 0f);
                Log.Info("[gate] everyone sets off (" + why + ", after " + waited.ToString("F0") + " s)");
                if (still.Count > 0) CoopRuntime.Notice(Lang.F("Setting off without {0}", Names(still)));
                return;
            }
            if (now - _sentAt > 0.5f)
            {
                _sentAt = now;
                string names = Names(still);
                float left = Timeout - waited;
                Session.Broadcast(new ArrivalStatus { Epoch = _epoch, Open = false, Waiting = names, Left = left });
                Show(_epoch, false, names, left);
            }
        }

        internal override void OnPeerJoined(PeerInfo peer)
        {
            if (Coop.IsHost && _since >= 0f) Session.SendTo(peer.Id, new ArrivalStatus { Epoch = _epoch, Open = _open });
        }

        private static bool JumpPressed()
        {
            if (InputBlock.Blocking) return false;
            KeyCode jump = KeyCode.Space;
            try { jump = KeyBinds.GetBind("jump"); } catch { /* default */ }
            if (jump == KeyCode.None) jump = KeyCode.Space;
            return Input.GetKeyDown(jump);
        }

        private static string Names(IEnumerable<byte> ids)
        {
            var names = new List<string>();
            foreach (var id in ids) names.Add(Coop.Player(id).Name);
            return string.Join(", ", names.ToArray());
        }

        // ------------------------------------------------------------------ everyone

        private void OnStatus(byte origin, ArrivalStatus m)
        {
            if (Coop.IsHost) return;
            Show(m.Epoch, m.Open, m.Waiting, m.Left);
        }

        private void Show(ushort epoch, bool open, string waiting, float left)
        {
            if (epoch < _shownEpoch) return;
            _shownEpoch = epoch;
            _shownOpen = open;
            _waitingFor = waiting ?? "";
            _shownLeft = left;
        }

        /// <summary>Held at the arrival point while the gate for our layer is shut.</summary>
        private void Hold()
        {
            bool hold = Coop.WorldLive && !_shownOpen && _shownEpoch == Coop.Epoch && GameInfo.LocalBody != null;
            if (hold)
            {
                if (!_holding) Log.Info("[gate] holding at the arrival point until " + (_waitingFor.Length > 0 ? _waitingFor : "the team") + " arrives");
                _holding = true;
                BodyFreeze.Set(true);
            }
            else Let(null);
        }

        private void Let(string why)
        {
            if (!_holding) return;
            _holding = false;
            BodyFreeze.Set(false);
            Log.Info("[gate] let go" + (why != null ? " (" + why + ")" : ""));
        }

        internal override void OnWorldLost() => Let("layer over");

        internal override void Gui()
        {
            if (!_holding) return;
            string top = _waitingFor.Length > 0 ? Lang.F("Waiting for {0} to arrive", _waitingFor) : Lang.T("Waiting for the team to arrive");
            string bottom;
            if (Coop.IsHost)
            {
                string key = "Space";
                try { key = KeyBinds.GetBind("jump").ToString(); } catch { /* default */ }
                bottom = Lang.F("Everyone sets off together. Up to {0} s more · {1} to go ahead without them", Mathf.CeilToInt(Mathf.Max(0f, _shownLeft)), key);
            }
            else bottom = Lang.T("Everyone sets off together, as soon as the whole team is in.");
            Overlay.Bottom(top, bottom, 1);
        }
    }
}
