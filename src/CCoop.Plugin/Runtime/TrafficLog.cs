using CCoop.Net.Session;
using UnityEngine;

namespace CCoop.Plugin.Runtime
{
    /// <summary>
    /// Every 30 s in a session, one log line with this machine's upload and download and
    /// the message types behind them. It's the data for the 4–8 player bandwidth budget,
    /// and a quick check in a bug report.
    /// </summary>
    internal sealed class TrafficLog : CoopModule
    {
        private const float Interval = 30f;
        private float _since, _next;

        /// <summary>The latest summary, for the co-op window.</summary>
        internal static string Last = "";

        internal override void Attach(NetSession s)
        {
            _since = Time.unscaledTime;
            _next = _since + Interval;
            s.Traffic.Take(null);
            Last = "";
        }

        internal override void Detach() => Last = "";

        internal override void Tick()
        {
            var s = Session;
            if (s == null) return;
            float now = Time.unscaledTime;
            if (now < _next) return;
            var lines = s.Traffic.Take(s.TypeName);
            Last = TrafficStats.Describe(lines, now - _since);
            Log.Info("[net] traffic over " + (now - _since).ToString("F0") + " s with " + Coop.Players.Count + " players: " + Last);
            _since = now;
            _next = now + Interval;
        }
    }
}
