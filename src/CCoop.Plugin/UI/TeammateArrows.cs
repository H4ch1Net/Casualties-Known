using CCoop.Plugin.Protocol;
using CCoop.Plugin.Runtime;
using CCoop.Plugin.Sync;
using UnityEngine;

namespace CCoop.Plugin.UI
{
    /// <summary>
    /// An arrow at the screen edge for each teammate who is off screen on this layer, with
    /// their name, distance and state. The host's difficulty can take them away, and each
    /// player can turn their own off.
    /// </summary>
    internal sealed class TeammateArrows : CoopModule
    {
        private static Texture2D _arrow;
        private static GUIStyle _label;

        internal override void Gui()
        {
            if (!Coop.WorldLive || !HostRules.Current.Arrows || CoopPlugin.Instance == null || !CoopPlugin.Instance.ShowArrows.Value || ConditionFx.Blind) return;
            var cam = Camera.main;
            var me = GameInfo.LocalBody;
            if (cam == null || me == null) return;
            if (_arrow == null) _arrow = MakeArrow(32);
            if (_label == null) _label = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.UpperCenter, fontSize = 12, fontStyle = FontStyle.Bold };

            Vector2 eye = LifeSync.Spectating ? (Coop.Get<LifeSync>()?.TargetPosition() ?? (Vector2)me.transform.position) : (Vector2)me.transform.position;
            var centre = new Vector2(Screen.width / 2f, Screen.height / 2f);
            foreach (var rec in Coop.Players.Values)
            {
                if (rec.Id == Coop.LocalId || !rec.HasPosition || rec.ReportedEpoch != Coop.Epoch) continue;
                Vector3 sp = cam.WorldToScreenPoint(rec.Position);
                var gui = new Vector2(sp.x, Screen.height - sp.y);
                bool onScreen = sp.z > 0 && gui.x > 40 && gui.x < Screen.width - 40 && gui.y > 40 && gui.y < Screen.height - 40;
                if (onScreen) continue;

                var dir = (gui - centre);
                if (sp.z < 0) dir = -dir;
                if (dir.sqrMagnitude < 1f) continue;
                dir.Normalize();
                // Where the ray from the centre leaves the screen (with a margin).
                float mx = Screen.width / 2f - 46f, my = Screen.height / 2f - 46f;
                float t = Mathf.Min(Mathf.Abs(mx / (dir.x == 0 ? 0.0001f : dir.x)), Mathf.Abs(my / (dir.y == 0 ? 0.0001f : dir.y)));
                var at = centre + dir * t;

                var color = !rec.Alive ? new Color(0.65f, 0.65f, 0.7f)
                    : !rec.Conscious ? new Color(1f, 0.45f, 0.4f)
                    : PingSync.ColorOf(rec.Id);
                float angle = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;
                var old = GUI.matrix;
                var oldColor = GUI.color;
                GUIUtility.RotateAroundPivot(angle, at);
                GUI.color = color;
                GUI.DrawTexture(new Rect(at.x - 14f, at.y - 14f, 28f, 28f), _arrow);
                GUI.matrix = old;
                GUI.color = oldColor;

                int dist = Mathf.RoundToInt(Vector2.Distance(eye, rec.Position));
                string state = !rec.Alive ? " (dead)" : !rec.Conscious ? " (down)" : "";
                var r = new Rect(at.x - 90f, at.y + 14f, 180f, 20f);
                _label.normal.textColor = new Color(0f, 0f, 0f, 0.8f);
                GUI.Label(new Rect(r.x + 1, r.y + 1, r.width, r.height), rec.Name + state + " · " + dist + " m", _label);
                _label.normal.textColor = color;
                GUI.Label(r, rec.Name + state + " · " + dist + " m", _label);
            }
        }

        /// <summary>A soft right-pointing triangle.</summary>
        private static Texture2D MakeArrow(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float u = x / (float)(size - 1), v = Mathf.Abs(y / (float)(size - 1) - 0.5f) * 2f;
                    float edge = 1f - u;  // half-width of the triangle at this x
                    float a = Mathf.Clamp01((edge - v) * size * 0.5f) * Mathf.Clamp01(u * size * 0.25f + 0.2f);
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                }
            tex.Apply();
            return tex;
        }
    }
}
