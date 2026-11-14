using CCoop.Plugin.Runtime;
using CCoop.Plugin.Sync;
using UnityEngine;

namespace CCoop.Plugin.UI
{
    /// <summary>
    /// A small warning in the corner while a client's link to the host is poor, so lag
    /// is explained instead of mysterious. Hidden while the connection is fine.
    /// </summary>
    internal static class ConnectionBadge
    {
        private static GUIStyle _style;

        /// <summary>Below this the host's game is struggling, and everyone's world with it.</summary>
        internal const int SlowFps = 20;

        internal static void Draw()
        {
            var s = Coop.Session;
            if (s == null || s.LocalPeer == null) return;
            string text = null;
            Color tint = Color.white;
            if (Coop.IsClient)
            {
                int rtt = s.LocalPeer.RoundTripMs;
                float q = s.LocalPeer.Quality;
                bool slow = rtt >= 180;
                bool lossy = q >= 0f && q < 0.85f;
                if (slow || lossy)
                {
                    text = lossy ? Lang.F("Connection to host: {0} ms, {1}% lost", rtt, Mathf.RoundToInt((1f - q) * 100f)) : Lang.F("Connection to host: {0} ms", rtt);
                    tint = CoopWindow.PingColor(rtt, q);
                }
                else
                {
                    int hostFps = Coop.Player(CCoop.Net.Session.NetSession.HostId).Fps;
                    if (hostFps > 0 && hostFps < SlowFps)
                    {
                        text = Lang.F("The host's game is running slowly ({0} fps)", hostFps);
                        tint = new Color(1f, 0.7f, 0.4f);
                    }
                }
            }
            else if (Coop.WorldLive && s.PeerCount > 1 && PlayerSync.LocalFps > 0f && PlayerSync.LocalFps < SlowFps)
            {
                text = Lang.F("Your game is running slowly ({0} fps); everyone's world goes through yours", Mathf.RoundToInt(PlayerSync.LocalFps));
                tint = new Color(1f, 0.7f, 0.4f);
            }
            if (text == null) return;

            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.box) { fontSize = 12, alignment = TextAnchor.MiddleCenter, wordWrap = true };
                _style.normal.textColor = Color.white;
            }
            var old = GUI.color;
            GUI.color = tint;
            GUI.Box(new Rect(Screen.width - 330, 8, 320, 24), text, _style);
            GUI.color = old;
        }
    }
}
