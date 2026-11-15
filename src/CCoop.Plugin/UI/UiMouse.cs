using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

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
            Shield();
        }

        private static GameObject _shield;
        private static readonly List<RectTransform> _blocks = new List<RectTransform>();

        /// <summary>
        /// The game's own buttons (uGUI, through its EventSystem) know nothing of the overlay:
        /// a click on the co-op window would also press whatever game button lies under it.
        /// So each rect we claim gets an invisible block on top of the game's canvases.
        /// </summary>
        private static void Shield()
        {
            try
            {
                if (_shield == null)
                {
                    _blocks.Clear();
                    _shield = new GameObject("CasualtiesCoop.ClickShield", typeof(RectTransform));
                    Object.DontDestroyOnLoad(_shield);
                    var canvas = _shield.AddComponent<Canvas>();
                    canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                    canvas.sortingOrder = 32000;
                    _shield.AddComponent<GraphicRaycaster>();
                }
                while (_blocks.Count < _last.Count)
                {
                    var go = new GameObject("Block", typeof(RectTransform), typeof(Image));
                    go.transform.SetParent(_shield.transform, false);
                    var img = go.GetComponent<Image>();
                    img.color = new Color(0f, 0f, 0f, 0f);
                    img.raycastTarget = true;
                    var rt = (RectTransform)go.transform;
                    rt.anchorMin = rt.anchorMax = Vector2.zero;
                    rt.pivot = Vector2.zero;
                    _blocks.Add(rt);
                }
                var root = (RectTransform)_shield.transform;
                float scale = root.lossyScale.x > 0f ? root.lossyScale.x : 1f;
                for (int i = 0; i < _blocks.Count; i++)
                {
                    bool on = i < _last.Count;
                    if (_blocks[i].gameObject.activeSelf != on) _blocks[i].gameObject.SetActive(on);
                    if (!on) continue;
                    var r = _last[i];  // screen pixels, from the top
                    _blocks[i].anchoredPosition = new Vector2(r.xMin, Screen.height - r.yMax) / scale;
                    _blocks[i].sizeDelta = new Vector2(r.width, r.height) / scale;
                }
            }
            catch { _shield = null; }
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
