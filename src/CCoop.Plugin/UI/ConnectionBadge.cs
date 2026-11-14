using CCoop.Plugin.Runtime;
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

        internal static void Draw()
        {
            var s = Coop.Session;
            if (!Coop.IsClient || s.LocalPeer == null) return;
            int rtt = s.LocalPeer.RoundTripMs;
            float q = s.LocalPeer.Quality;
            bool slow = rtt >= 180;
            bool lossy = q >= 0f && q < 0.85f;
            if (!slow && !lossy) return;

            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.box) { fontSize = 12, alignment = TextAnchor.MiddleCenter };
                _style.normal.textColor = Color.white;
            }
            string text = "Connection to host: " + rtt + " ms" + (lossy ? ", " + Mathf.RoundToInt((1f - q) * 100f) + "% lost" : "");
            var old = GUI.color;
            GUI.color = CoopWindow.PingColor(rtt, q);
            GUI.Box(new Rect(Screen.width - 290, 8, 280, 24), text, _style);
            GUI.color = old;
        }
    }
}
