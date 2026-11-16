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
        /// <summary>Screenshots: teammates drawn as if this far from where they are.</summary>
        internal static Vector2 DevOffset;

        internal override void Gui()
        {
            if (!Coop.WorldLive || !HostRules.Current.Arrows || !CoopPlugin.Ready || !CoopPlugin.Instance.ShowArrows.Value || ConditionFx.Blind) return;
            var cam = Camera.main;
            var me = GameInfo.LocalBody;
            if (cam == null || me == null) return;
            var arrow = PixelMarks.Arrow;
            var size = PixelMarks.Size(arrow, true);

            Vector2 eye = LifeSync.Spectating ? (Coop.Get<LifeSync>()?.TargetPosition() ?? (Vector2)me.transform.position) : (Vector2)me.transform.position;
            var centre = new Vector2(Screen.width / 2f, Screen.height / 2f);
            foreach (var rec in Coop.Players.Values)
            {
                if (rec.Id == Coop.LocalId || !rec.HasPosition || rec.ReportedEpoch != Coop.Epoch) continue;
                var pos = rec.Position + DevOffset;
                Vector3 sp = cam.WorldToScreenPoint(pos);
                var gui = new Vector2(sp.x, Screen.height - sp.y);
                bool onScreen = sp.z > 0 && gui.x > 0f && gui.x < Screen.width && gui.y > 0f && gui.y < Screen.height;
                if (onScreen) continue;

                var dir = (gui - centre);
                if (sp.z < 0) dir = -dir;
                if (dir.sqrMagnitude < 1f) continue;
                dir.Normalize();
                // Where the ray from the centre leaves the screen, kept clear of the game's HUD.
                float mx = Screen.width / 2f - (dir.x > 0f ? PixelMarks.EdgeRight : PixelMarks.EdgeLeft) - size.x / 2f;
                float my = Screen.height / 2f - (dir.y > 0f ? PixelMarks.EdgeBottom : PixelMarks.EdgeTop) - size.y / 2f;
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
                GUI.DrawTexture(new Rect(Mathf.Round(at.x - size.x / 2f), Mathf.Round(at.y - size.y / 2f), size.x, size.y), arrow);
                GUI.matrix = old;
                GUI.color = oldColor;

                int dist = Mathf.RoundToInt(Vector2.Distance(eye, pos));
                string state = !rec.Alive ? " (" + Lang.T("dead") + ")" : !rec.Conscious ? " (" + Lang.T("down") + ")" : "";
                PixelMarks.Label(new Vector2(at.x, at.y + size.y / 2f + 2f), rec.Name + state + " · " + dist + " m", color);
            }
        }
    }
}
