using System.Collections.Generic;
using UnityEngine;

namespace CCoop.Plugin.UI
{
    /// <summary>
    /// The gesture wheel, drawn like the game's pause menu: round black buttons with a
    /// one-pixel white outline, each holding a white pixel icon, set around a thin circle
    /// with the gesture's name in it. The one under the mouse is inverted (white, with a
    /// black icon), as a chosen option is everywhere in the overlay, and its key is
    /// written beside each button. Everything is pixel art at the overlay's pixel size
    /// (two screen pixels per art pixel at 1080p), point filtered.
    /// </summary>
    internal static class GestureWheel
    {
        // Sizes in art pixels.
        private const int ButtonD = 37, Orbit = 80, InnerR = 52, BackR = 114;

        private static Texture2D _button, _buttonOn, _inner, _back;
        private static readonly Dictionary<string, Texture2D> Icons = new Dictionary<string, Texture2D>();
        private static GUIStyle _name, _key, _hint;

        private static float Px => PixelMarks.Px;

        /// <summary>How far the wheel reaches from its centre, in screen pixels (left/right/up, and down with the hint).</summary>
        internal static float Reach => (BackR + 2) * Px;
        internal static float ReachDown => (BackR + 2) * Px + 44f * CoopWindow.Scale;

        /// <summary>Within this distance of the centre (screen pixels) the mouse picks nothing.</summary>
        internal static float DeadZone => 22f * Px;

        /// <summary>
        /// Draws the wheel around <paramref name="centre"/> (GUI pixels, y down).
        /// <paramref name="shownFor"/> is how long it has been open (it fades in).
        /// </summary>
        internal static void Draw(Vector2 centre, string[] labels, string[] keys, string[] icons, int choice, bool sticky, float shownFor)
        {
            Build();
            float px = Px, k = CoopWindow.Scale;
            centre = new Vector2(Mathf.Round(centre.x), Mathf.Round(centre.y));
            float fade = Runtime.Coop.InSession && Sync.ConditionFx.Reduced ? 1f : Mathf.Clamp01(shownFor / 0.1f);
            var old = GUI.color;
            try
            {
                GUI.color = new Color(1f, 1f, 1f, fade);
                Sprite(_back, centre);
                Sprite(_inner, centre);
                int n = labels.Length;
                for (int i = 0; i < n; i++)
                {
                    float a = i * (360f / n) * Mathf.Deg2Rad;
                    var dir = new Vector2(Mathf.Sin(a), -Mathf.Cos(a));
                    var at = centre + dir * Orbit * px;
                    bool on = i == choice;
                    GUI.color = new Color(1f, 1f, 1f, fade);
                    Sprite(on ? _buttonOn : _button, at);
                    var icon = Icon(icons[i]);
                    if (icon != null)
                    {
                        GUI.color = on ? new Color(0f, 0f, 0f, fade) : new Color(1f, 1f, 1f, fade);
                        Sprite(icon, at);
                    }
                    // Its key, just outside the button.
                    Text(keys[i], centre + dir * (Orbit + ButtonD / 2f + 7f) * px, Key(), on ? 1f : 0.7f, fade);
                }

                // In the middle: the name of the one under the mouse (or what this is).
                string name = choice >= 0 ? labels[choice] : Lang.T("Gestures");
                Text(name, centre, Name(), choice >= 0 ? 1f : 0.55f, fade);

                string hint = sticky ? Lang.T("Click a gesture (or press its key). Esc to close")
                                     : Lang.T("Move the mouse onto a gesture and let go");
                Text(hint, centre + new Vector2(0f, (BackR + 4) * px + 14f * k), Hint(), 0.85f, fade);
            }
            finally { GUI.color = old; }
        }

        /// <summary>A texture at whole art pixels, centred on <paramref name="at"/> (GUI pixels).</summary>
        private static void Sprite(Texture2D t, Vector2 at)
        {
            float px = Px, w = t.width * px, h = t.height * px;
            GUI.DrawTexture(new Rect(Mathf.Round(at.x - w / 2f), Mathf.Round(at.y - h / 2f), w, h), t);
        }

        /// <summary>One line of text centred on <paramref name="at"/> (GUI pixels), in the overlay's scaled font.</summary>
        private static void Text(string text, Vector2 at, GUIStyle style, float bright, float fade)
        {
            float k = CoopWindow.Scale;
            var old = GUI.matrix;
            var oldColor = GUI.color;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(k, k, 1f));
            try
            {
                var st = UiSkin.Fit(style, text);
                var size = st.CalcSize(new GUIContent(text));
                var r = new Rect(Mathf.Round(at.x / k - size.x / 2f), Mathf.Round(at.y / k - size.y / 2f), size.x, size.y);
                GUI.color = new Color(0f, 0f, 0f, 0.8f * fade);
                GUI.Label(new Rect(r.x + 1f, r.y + 1f, r.width, r.height), text, st);
                GUI.color = new Color(bright, bright, bright, fade);
                GUI.Label(r, text, st);
            }
            finally
            {
                GUI.matrix = old;
                GUI.color = oldColor;
            }
        }

        private static GUIStyle Name() => _name ?? (_name = Style(UiSkin.Head));
        private static GUIStyle Key() => _key ?? (_key = Style(UiSkin.Small));
        private static GUIStyle Hint() => _hint ?? (_hint = Style(UiSkin.Small));

        private static GUIStyle Style(int size)
        {
            var s = new GUIStyle(GUI.skin.label) { fontSize = size, alignment = TextAnchor.MiddleCenter, wordWrap = false, clipping = TextClipping.Overflow };
            s.normal.textColor = Color.white;
            return s;
        }

        // ------------------------------------------------------------------ pixels

        private static void Build()
        {
            if (_button != null) return;
            var clear = new Color(0f, 0f, 0f, 0f);
            _button = Disc(ButtonD, new Color(0f, 0f, 0f, 0.88f), Color.white);
            _buttonOn = Disc(ButtonD, Color.white, Color.white);
            _inner = Disc(InnerR * 2 + 1, clear, new Color(1f, 1f, 1f, 0.9f));
            _back = Disc(BackR * 2 + 1, new Color(0f, 0f, 0f, 0.62f), clear);
        }

        /// <summary>A filled circle with a one-pixel outline (the midpoint circle: no doubled pixels).</summary>
        private static Texture2D Disc(int d, Color fill, Color line)
        {
            if (d % 2 == 0) d++;
            int r = d / 2;
            var t = New(d, d);
            var px = new Color[d * d];
            for (int y = -r; y <= r; y++)
                for (int x = -r; x <= r; x++)
                    px[(y + r) * d + x + r] = x * x + y * y <= r * r - r ? fill : new Color(0f, 0f, 0f, 0f);
            int cx = r, cy = 0, err = 1 - r;
            while (cx >= cy)
            {
                foreach (var (a, b) in new[] { (cx, cy), (cy, cx), (-cy, cx), (-cx, cy), (-cx, -cy), (-cy, -cx), (cy, -cx), (cx, -cy) })
                    px[(b + r) * d + a + r] = line;
                cy++;
                if (err < 0) err += 2 * cy + 1;
                else { cx--; err += 2 * (cy - cx) + 1; }
            }
            t.SetPixels(px);
            t.Apply();
            return t;
        }

        private static Texture2D Icon(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (Icons.TryGetValue(name, out var t)) return t;
            if (!Grids.TryGetValue(name, out var rows)) return Icons[name] = null;
            int h = rows.Length, w = rows[0].Length;
            t = New(w, h);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    t.SetPixel(x, h - 1 - y, rows[y][x] == '#' ? Color.white : new Color(0f, 0f, 0f, 0f));
            t.Apply();
            return Icons[name] = t;
        }

        private static Texture2D New(int w, int h) =>
            new Texture2D(w, h, TextureFormat.ARGB32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };

        /// <summary>The icons, 17 × 17, drawn for the mod in the pause menu's style (white, chunky).</summary>
        private static readonly Dictionary<string, string[]> Grids = new Dictionary<string, string[]>
        {
            ["wave"] = new[]
            {
                ".................",
                ".#....##.......#.",
                "#..##.##.##.....#",
                "#..##.##.##.##..#",
                ".#.##.##.##.##.#.",
                "...##.##.##.##...",
                "...##.##.##.##...",
                ".#.###########...",
                ".#############...",
                "..############...",
                "...###########...",
                "...###########...",
                "....#########....",
                ".....#######.....",
                ".....#######.....",
                ".................",
                ".................",
            },
            ["come_here"] = new[]
            {
                ".................",
                ".................",
                ".................",
                ".................",
                ".................",
                "..#...........#..",
                "..##.........##..",
                "..###..###..###..",
                "#######...#######",
                "#######...#######",
                "..###..###..###..",
                "..##.........##..",
                "..#...........#..",
                ".................",
                ".................",
                ".................",
                ".................",
            },
            ["follow_me"] = new[]
            {
                ".................",
                ".................",
                ".................",
                "..##.....##......",
                "..###....###.....",
                "...###....###....",
                "....###....###...",
                ".....###....###..",
                "......###....###.",
                ".....###....###..",
                "....###....###...",
                "...###....###....",
                "..###....###.....",
                "..##.....##......",
                ".................",
                ".................",
                ".................",
            },
            ["help"] = new[]
            {
                ".................",
                ".......###.......",
                "......#####......",
                "......#####......",
                "......#####......",
                "......#####......",
                ".......###.......",
                ".......###.......",
                ".......###.......",
                ".......###.......",
                ".................",
                ".................",
                ".......###.......",
                "......#####......",
                ".......###.......",
                ".................",
                ".................",
            },
            ["hug"] = new[]
            {
                ".................",
                ".................",
                ".................",
                "...####...####...",
                "..######.######..",
                ".###############.",
                ".###############.",
                ".###############.",
                "..#############..",
                "...###########...",
                "....#########....",
                ".....#######.....",
                "......#####......",
                ".......###.......",
                "........#........",
                ".................",
                ".................",
            },
            ["thumbs_up"] = new[]
            {
                ".................",
                ".......##........",
                "......###........",
                "......###........",
                ".....###.........",
                ".....###.........",
                "....#########....",
                "###.##########...",
                "###.##########...",
                "###..#########...",
                "###.##########...",
                "###.#########....",
                "###..########....",
                "###.#########....",
                ".................",
                ".................",
                ".................",
            },
            ["yes"] = new[]
            {
                ".................",
                ".................",
                ".................",
                "..............##.",
                ".............###.",
                "............###..",
                "...........###...",
                "..##......###....",
                "..###....###.....",
                "...###..###......",
                "....######.......",
                ".....####........",
                "......##.........",
                ".................",
                ".................",
                ".................",
                ".................",
            },
            ["no"] = new[]
            {
                ".................",
                ".................",
                ".................",
                "...##.......##...",
                "...###.....###...",
                "....###...###....",
                ".....###.###.....",
                "......#####......",
                ".......###.......",
                "......#####......",
                ".....###.###.....",
                "....###...###....",
                "...###.....###...",
                "...##.......##...",
                ".................",
                ".................",
                ".................",
            },
            ["shrug"] = new[]
            {
                ".................",
                "......#####......",
                ".....#######.....",
                "....###...###....",
                "....##.....##....",
                "...........##....",
                "..........###....",
                ".........###.....",
                "........###......",
                "........##.......",
                "........##.......",
                ".................",
                ".................",
                "........##.......",
                ".......####......",
                "........##.......",
                ".................",
            },
            ["wait"] = new[]
            {
                ".................",
                "...###########...",
                "...###########...",
                "....#.......#....",
                "....##.....##....",
                ".....##...##.....",
                "......##.##......",
                ".......###.......",
                ".......#.#.......",
                "......#...#......",
                ".....#..#..#.....",
                "....#..###..#....",
                "...#..#####..#...",
                "...###########...",
                "...###########...",
                ".................",
                ".................",
            },
        };
    }
}
