using System.Collections.Generic;
using UnityEngine;

namespace CCoop.Plugin.UI
{
    /// <summary>
    /// Where the co-op overlay takes clicks. A click on one of its buttons ("Take it", the
    /// teammate panel, the co-op window) must not also reach the game, which would swing
    /// whatever the player holds (at a teammate, with friendly fire on). Rects are
    /// collected while drawing and used by the game's next frame of input.
    /// </summary>
    internal static class UiMouse
    {
        private static List<Rect> _drawing = new List<Rect>();
        private static List<Rect> _last = new List<Rect>();

        /// <summary>While drawing: this rect (in the current GUI.matrix) takes clicks.</summary>
        internal static void Claim(Rect r)
        {
            if (Event.current == null || Event.current.type != EventType.Repaint) return;
            var m = GUI.matrix;
            Vector3 a = m.MultiplyPoint3x4(new Vector3(r.xMin, r.yMin, 0f));
            Vector3 b = m.MultiplyPoint3x4(new Vector3(r.xMax, r.yMax, 0f));
            _drawing.Add(Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y)));
        }

        /// <summary>After everything is drawn for a frame (on Repaint).</summary>
        internal static void EndFrame()
        {
            if (Event.current == null || Event.current.type != EventType.Repaint) return;
            var t = _last;
            _last = _drawing;
            _drawing = t;
            _drawing.Clear();
        }

        /// <summary>The mouse is over something of ours that takes clicks.</summary>
        internal static bool Over
        {
            get
            {
                if (_last.Count == 0) return false;
                Vector2 m = Input.mousePosition;
                var g = new Vector2(m.x, Screen.height - m.y);
                foreach (var r in _last)
                    if (r.Contains(g)) return true;
                return false;
            }
        }
    }
}
