using CCoop.Net.Session;
using CCoop.Plugin.Protocol;
using CCoop.Plugin.Runtime;
using UnityEngine;

namespace CCoop.Plugin.Sync
{
    /// <summary>
    /// Shared danger: when a creature raises the threat music on one player's machine,
    /// teammates close by hear it too. Only threat raised locally is announced, so two
    /// players don't keep each other's music going forever.
    ///
    /// (Body warmth between teammates is a game <c>Heater</c> on each puppet; see
    /// <see cref="RemotePlayer"/>.)
    /// </summary>
    internal sealed class TeamSense : CoopModule
    {
        private const float HearThreatWithin = 40f;

        private float _prevThreat;
        private float _localThreatUntil;
        private byte _localTheme;
        private float _nextAnnounce;

        internal override void Attach(NetSession s) => s.On<ThreatPing>(OnThreat);

        internal override void Detach()
        {
            _prevThreat = 0f;
            _localThreatUntil = 0f;
        }

        internal override void Tick()
        {
            var cam = PlayerCamera.main;
            if (!Coop.WorldLive || cam == null) return;
            float now = Time.unscaledTime;
            float t = cam.threatMusicTime;
            if (t > _prevThreat + 0.05f)
            {
                // Raised on this machine since last frame (network raises update _prevThreat).
                _localThreatUntil = now + Mathf.Min(t, 10f);
                _localTheme = (byte)Mathf.Clamp(cam.currentThreatTheme, 0, 255);
            }
            _prevThreat = t;

            var body = GameInfo.LocalBody;
            if (now < _localThreatUntil && now >= _nextAnnounce && body != null && body.alive)
            {
                _nextAnnounce = now + 1f;
                Session.Broadcast(new ThreatPing { Theme = _localTheme, Seconds = Mathf.Min(5f, _localThreatUntil - now + 1f) });
            }
        }

        private void OnThreat(byte origin, ThreatPing m)
        {
            var cam = PlayerCamera.main;
            var body = GameInfo.LocalBody;
            if (!Coop.WorldLive || cam == null || body == null || !body.alive) return;
            var rec = Coop.Player(origin);
            if (!rec.HasPosition || rec.ReportedEpoch != Coop.Epoch) return;
            if (Vector2.Distance(rec.Position, body.transform.position) > HearThreatWithin) return;
            if (cam.threatMusicTime <= 0f) cam.currentThreatTheme = m.Theme;
            cam.threatMusicTime = Mathf.Max(cam.threatMusicTime, m.Seconds);
            _prevThreat = cam.threatMusicTime;
        }
    }
}
