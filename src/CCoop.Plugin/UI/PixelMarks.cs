using UnityEngine;

namespace CCoop.Plugin.UI
{
    /// <summary>
    /// Small marks drawn over the world (teammate arrows, ping rings) as pixel art like the
    /// rest of the game: hard edges, point filtering, a dark rim so they read on any
    /// background, and always drawn at a whole multiple of their pixels. White parts take
    /// the colour GUI.color gives them; the rim stays dark.
    /// </summary>
    internal static class PixelMarks
    {
        /// <summary>Screen pixels per art pixel at the overlay's scale.</summary>
        internal static float Px => Mathf.Max(2f, Mathf.Round(2f * CoopWindow.Scale));

        /// <summary>
        /// How far from the screen's edges marks stay: clear of the game's depth meter on
        /// the right and its hotbar along the bottom.
        /// </summary>
        internal static float EdgeLeft => 40f * CoopWindow.Scale;
        internal static float EdgeRight => 90f * CoopWindow.Scale;
        internal static float EdgeTop => 40f * CoopWindow.Scale;
        internal static float EdgeBottom => 110f * CoopWindow.Scale;

        private static readonly Color Rim = new Color(0f, 0f, 0f, 0.85f);
        private static Texture2D _arrow, _ring, _dot;
        private static GUIStyle _label;

        /// <summary>A right-pointing triangle, 11 × 13.</summary>
        internal static Texture2D Arrow
        {
            get
            {
                if (_arrow != null) return _arrow;
                const int w = 11, h = 13;
                var inside = new bool[w, h];
                for (int x = 1; x < w - 1; x++)
                    for (int y = 1; y < h - 1; y++)
                    {
                        float half = (h / 2 - 1) * (1f - (x - 1) / (float)(w - 3));
                        inside[x, y] = Mathf.Abs(y - h / 2) <= half + 0.01f;
                    }
                return _arrow = Build(w, h, inside);
            }
        }

        /// <summary>A ring, 15 × 15, two pixels thick.</summary>
        internal static Texture2D Ring
        {
            get
            {
                if (_ring != null) return _ring;
                const int n = 15;
                var inside = new bool[n, n];
                float c = (n - 1) / 2f;
                for (int x = 0; x < n; x++)
                    for (int y = 0; y < n; y++)
                    {
                        float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
                        inside[x, y] = d <= 5.9f && d >= 3.9f;
                    }
                return _ring = Build(n, n, inside);
            }
        }

        /// <summary>A 3 × 3 dot (2 × 2 pixels with its rim), for dotted lines.</summary>
        internal static Texture2D Dot
        {
            get
            {
                if (_dot != null) return _dot;
                var inside = new bool[4, 4];
                inside[1, 1] = inside[1, 2] = inside[2, 1] = inside[2, 2] = true;
                return _dot = Build(4, 4, inside);
            }
        }

        /// <summary>Fill where <paramref name="inside"/>, a dark rim around it, clear elsewhere.</summary>
        private static Texture2D Build(int w, int h, bool[,] inside)
        {
            var t = new Texture2D(w, h, TextureFormat.ARGB32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
            for (int x = 0; x < w; x++)
                for (int y = 0; y < h; y++)
                {
                    if (inside[x, y]) { t.SetPixel(x, y, Color.white); continue; }
                    bool rim = false;
                    for (int dx = -1; dx <= 1 && !rim; dx++)
                        for (int dy = -1; dy <= 1 && !rim; dy++)
                        {
                            int nx = x + dx, ny = y + dy;
                            rim = nx >= 0 && ny >= 0 && nx < w && ny < h && inside[nx, ny];
                        }
                    t.SetPixel(x, y, rim ? Rim : new Color(0f, 0f, 0f, 0f));
                }
            t.Apply();
            return t;
        }

        /// <summary>The mark's size on screen, in whole art pixels (<paramref name="big"/>: half as big again).</summary>
        internal static Vector2 Size(Texture2D t, bool big = false) => new Vector2(t.width, t.height) * (big ? Mathf.Round(Px * 1.5f) : Px);

        /// <summary>A small label with a dark shadow, centred under <paramref name="at"/> (screen pixels).</summary>
        internal static void Label(Vector2 at, string text, Color color, float alpha = 1f)
        {
            if (_label == null) _label = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.UpperCenter, fontSize = UiSkin.Body, wordWrap = false, clipping = TextClipping.Overflow };
            float k = CoopWindow.Scale;
            var old = GUI.matrix;
            var oldColor = GUI.color;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(k, k, 1f));
            try
            {
                var style = UiSkin.Fit(_label, text);
                // Kept whole on screen when the mark is at an edge.
                float half = style.CalcSize(new GUIContent(text)).x / 2f + 4f;
                float cx = Mathf.Clamp(at.x / k, half, Mathf.Max(half, Screen.width / k - half));
                var r = new Rect(cx - 150f, Mathf.Min(at.y / k, Screen.height / k - 24f), 300f, 22f);
                GUI.color = Color.white;
                style.normal.textColor = new Color(0f, 0f, 0f, alpha * 0.85f);
                GUI.Label(new Rect(r.x + 1f, r.y + 1f, r.width, r.height), text, style);
                style.normal.textColor = new Color(color.r, color.g, color.b, alpha);
                GUI.Label(r, text, style);
            }
            finally
            {
                GUI.matrix = old;
                GUI.color = oldColor;
            }
        }
    }
}
