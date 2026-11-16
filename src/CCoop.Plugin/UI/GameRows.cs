using System.Collections.Generic;
using UnityEngine;

namespace CCoop.Plugin.UI
{
    /// <summary>
    /// Settings rows in the look of the game's own "Custom settings" panel: a label on the
    /// left, the control on the right (an outlined box to tick, an outlined slider with its
    /// value, a dropdown box with a "v"), thin lines between rows and a darker band for
    /// each section's heading. Explanations aren't printed: hovering a row puts its
    /// explanation in the game's own hover label (see GameTooltip).
    /// </summary>
    internal static class GameRows
    {
        internal const float RowH = 42f;

        private static GUIStyle _label, _header, _value, _dropText, _option, _track, _thumb;
        private static string _openDrop;

        internal static void DevOpen(string key) => _openDrop = key;

        /// <summary>
        /// The part of a scrolled list that's on screen, in its content's coordinates (null:
        /// all of it). Rows scrolled out of sight mustn't take the mouse: they'd answer
        /// hovers and clicks meant for whatever is drawn where they would be.
        /// </summary>
        internal static Rect? Clip;

        private static bool InView => !Clip.HasValue || Clip.Value.Contains(Event.current.mousePosition);

        private static bool Over(Rect r) => InView && r.Contains(Event.current.mousePosition);

        /// <summary>An invisible button over the whole row (nowhere, when the mouse is outside the list).</summary>
        private static bool RowButton(Rect r) => GUI.Button(InView ? r : new Rect(0f, 0f, 0f, 0f), GUIContent.none, GUIStyle.none);

        private static void Styles()
        {
            if (_label != null) return;
            _label = new GUIStyle(GUI.skin.label) { fontSize = UiSkin.Head, alignment = TextAnchor.MiddleLeft, wordWrap = false, clipping = TextClipping.Clip };
            _header = new GUIStyle(GUI.skin.label) { fontSize = UiSkin.Big, alignment = TextAnchor.MiddleLeft, wordWrap = false };
            _value = new GUIStyle(GUI.skin.label) { fontSize = UiSkin.Body, alignment = TextAnchor.MiddleRight, wordWrap = false };
            _dropText = new GUIStyle(GUI.skin.label) { fontSize = UiSkin.Body, alignment = TextAnchor.MiddleLeft, wordWrap = false, clipping = TextClipping.Clip };
            _option = new GUIStyle(GUI.skin.button) { fontSize = UiSkin.Body, alignment = TextAnchor.MiddleLeft };
            _option.padding = new RectOffset(10, 8, _option.padding.top, _option.padding.bottom);

            var outline = Outline(16, 2);
            _track = new GUIStyle { border = new RectOffset(3, 3, 3, 3), fixedHeight = 18f };
            _track.normal.background = outline;
            _thumb = new GUIStyle { fixedWidth = 12f, fixedHeight = 22f, margin = new RectOffset(0, 0, -2, 0) };
            _thumb.normal.background = Texture2D.whiteTexture;
            _thumb.hover.background = Texture2D.whiteTexture;
            _thumb.active.background = Texture2D.whiteTexture;
        }

        private static Texture2D Outline(int n, int w)
        {
            var t = new Texture2D(n, n, TextureFormat.ARGB32, false) { filterMode = FilterMode.Point, hideFlags = HideFlags.HideAndDontSave };
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                    t.SetPixel(x, y, x < w || y < w || x >= n - w || y >= n - w ? Color.white : new Color(0f, 0f, 0f, 0f));
            t.Apply();
            return t;
        }

        private static void Box(Rect r, Color c, float width = 2f) =>
            GUI.DrawTexture(r, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f, c, width, 0f);

        private static void Fill(Rect r, Color c)
        {
            var old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = old;
        }

        /// <summary>A section heading on a darker band.</summary>
        internal static void Header(string title)
        {
            Styles();
            var r = GUILayoutUtility.GetRect(10f, RowH + 6f, GUILayout.ExpandWidth(true));
            if (Event.current.type != EventType.Repaint) return;
            Fill(r, new Color(1f, 1f, 1f, 0.09f));
            GUI.Label(new Rect(r.x + 10f, r.y, r.width - 20f, r.height), title, UiSkin.Fit(_header, title));
            Fill(new Rect(r.x, r.yMax - 1f, r.width, 1f), new Color(1f, 1f, 1f, 0.35f));
        }

        /// <summary>A row: the label on the left; returns the row's rect. Hovering it shows <paramref name="tip"/>.</summary>
        private static Rect Row(string label, string tip, float height = RowH)
        {
            Styles();
            var r = GUILayoutUtility.GetRect(10f, height, GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint)
            {
                GUI.Label(new Rect(r.x + 10f, r.y, r.width * 0.62f, Mathf.Min(height, RowH)), label, UiSkin.Fit(_label, label));
                Fill(new Rect(r.x, r.yMax - 1f, r.width, 1f), new Color(1f, 1f, 1f, 0.22f));
                if (!string.IsNullOrEmpty(tip) && Over(r)) GameTooltip.Set(label, tip);
            }
            return r;
        }

        /// <summary>An outlined box on the right to tick; a click anywhere on the row flips it.</summary>
        internal static bool Toggle(string label, bool value, string tip = null)
        {
            var r = Row(label, tip);
            var box = new Rect(r.xMax - 40f, r.center.y - 13f, 26f, 26f);
            bool enabled = GUI.enabled;
            if (Event.current.type == EventType.Repaint)
            {
                var c = enabled ? Color.white : new Color(1f, 1f, 1f, 0.4f);
                Box(box, c);
                if (value) Fill(new Rect(box.x + 6f, box.y + 6f, box.width - 12f, box.height - 12f), c);
                if (enabled && Over(r)) Fill(r, new Color(1f, 1f, 1f, 0.04f));
            }
            if (RowButton(r)) { value = !value; Click(); }
            return value;
        }

        /// <summary>
        /// A dropdown, as the game's: an outlined box with the choice and a "v"; a click lists
        /// the choices under it. Returns the index chosen.
        /// </summary>
        internal static int Choice(string key, string label, int index, string[] options, string tip = null)
        {
            bool open = _openDrop == key && GUI.enabled;
            var r = Row(label, tip);
            float w = 0f;
            Styles();
            foreach (var o in options) w = Mathf.Max(w, _dropText.CalcSize(new GUIContent(o)).x);
            // (Not from r: while IMGUI lays out, r is a placeholder.)
            w = Mathf.Clamp(w + 44f, 120f, 380f);
            var box = new Rect(r.xMax - w - 10f, r.center.y - 15f, w, 30f);
            string shown = index >= 0 && index < options.Length ? options[index] : "-";
            if (Event.current.type == EventType.Repaint)
            {
                var c = GUI.enabled ? Color.white : new Color(1f, 1f, 1f, 0.4f);
                Box(box, c);
                var old = GUI.color;
                GUI.color = c;
                GUI.Label(new Rect(box.x + 8f, box.y, box.width - 30f, box.height), shown, UiSkin.Fit(_dropText, shown));
                GUI.Label(new Rect(box.xMax - 22f, box.y, 18f, box.height), open ? "^" : "v", _dropText);
                GUI.color = old;
                if (GUI.enabled && Over(r)) Fill(r, new Color(1f, 1f, 1f, 0.04f));
            }
            if (RowButton(r)) { _openDrop = open ? null : key; Click(); }
            if (!open) return index;
            for (int i = 0; i < options.Length; i++)
            {
                GUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                bool picked = GUILayout.Toggle(i == index, options[i], _option, GUILayout.Width(w), GUILayout.Height(28f));
                GUILayout.Space(10f);
                GUILayout.EndHorizontal();
                if (picked && i != index) { index = i; _openDrop = null; Click(); }
            }
            return index;
        }

        /// <summary>A slider as the game's: the label above, an outlined bar with a white handle, the value at the right.</summary>
        internal static float Slider(string label, float value, float min, float max, string shown, string tip = null)
        {
            var r = Row(label, tip, RowH + 26f);
            var bar = new Rect(r.x + 10f, r.y + RowH - 6f, r.width - 90f, 18f);
            float v = GUI.HorizontalSlider(InView || GUIUtility.hotControl != 0 ? bar : new Rect(bar.x, bar.y, 0f, 0f), value, min, max, _track, _thumb);
            if (Event.current.type == EventType.Repaint)
                GUI.Label(new Rect(bar.xMax + 6f, bar.y - 6f, 70f, 30f), shown, _value);
            return v;
        }

        /// <summary>A row with a label; returns the rect on its right for the caller's own control (a text field, say).</summary>
        internal static Rect Slot(string label, string tip, float width = 320f)
        {
            var r = Row(label, tip);
            float w = Mathf.Min(width, r.width * 0.5f);
            return new Rect(r.xMax - w - 10f, r.center.y - 14f, w, 28f);
        }

        /// <summary>A read-only line: the label, and a value at the right.</summary>
        internal static void Info(string label, string value, string tip = null, Color? color = null)
        {
            var r = Row(label, tip);
            if (Event.current.type != EventType.Repaint) return;
            var old = GUI.color;
            if (color.HasValue) GUI.color = color.Value;
            GUI.Label(new Rect(r.x + r.width * 0.5f, r.y, r.width * 0.5f - 12f, r.height), value, _value);
            GUI.color = old;
        }

        private static void Click()
        {
            try { PlayerCamera.PlayUISound("click"); } catch { /* no sound */ }
        }
    }
}
