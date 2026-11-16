using System.Collections.Generic;
using CCoop.Plugin.Runtime;
using UnityEngine;

namespace CCoop.Plugin.UI
{
    /// <summary>
    /// The co-op overlay in the game's own style: its Retro Gaming pixel font, white text,
    /// black panels and dark-grey buttons framed by a one-pixel white line with notched
    /// pixel corners (the look of the game's uiBlock sprites, its craft and health buttons
    /// and its inventory ring). Selected choices are inverted, white with black text.
    ///
    /// Everything is drawn in code at startup; only the fonts are the game's (found at
    /// runtime, nothing shipped). Text Retro Gaming has no glyphs for (Chinese, Japanese,
    /// Korean, Cyrillic, an unusual Steam name) uses Unifont, the game's other pixel font,
    /// which covers nearly every script: the whole overlay when the language needs it,
    /// otherwise just that line (<see cref="Fit"/>). If the game's fonts are ever gone the
    /// overlay falls back to the operating-system font and Unity's default look.
    /// </summary>
    internal static class UiSkin
    {
        // The type scale. Retro Gaming is crisp at these sizes; Unifont (for languages
        // Retro Gaming can't write) only at 16 and up.
        internal static int Small => _wide ? 16 : 13;
        internal static int Body => _wide ? 16 : 15;
        internal static int Head => _wide ? 16 : 18;
        internal static int Big => _wide ? 24 : 22;

        internal static readonly Color Ink = Color.white;
        internal static readonly Color Dim = new Color(0.68f, 0.68f, 0.68f);
        /// <summary>The game's own speech colour (an amber), for warnings and calls to action.</summary>
        internal static readonly Color Amber = new Color(1f, 0.69f, 0.12f);
        internal static readonly Color Bad = new Color(1f, 0.45f, 0.36f);
        internal static readonly Color Good = new Color(0.56f, 0.95f, 0.56f);

        private static readonly Color Line = Color.white;
        private static readonly Color LineDim = new Color(0.66f, 0.66f, 0.66f);
        private static readonly Color ButtonFill = new Color32(0x1E, 0x1E, 0x1E, 0xFF);
        private static readonly Color ButtonShade = new Color32(0x12, 0x12, 0x12, 0xFF);
        private static readonly Color ButtonHover = new Color32(0x36, 0x36, 0x36, 0xFF);
        private static readonly Color Panel = new Color(0f, 0f, 0f, 0.86f);
        private static readonly Color BoxFill = new Color(0f, 0f, 0f, 0.78f);
        private static readonly Color Clear = new Color(0f, 0f, 0f, 0f);

        internal static GUISkin Skin { get; private set; }
        private static bool _built;
        private static string _builtFor;
        private static bool _wide;
        private static readonly Dictionary<GUIStyle, GUIStyle> WideTwins = new Dictionary<GUIStyle, GUIStyle>();
        private static readonly Dictionary<char, bool> Covered = new Dictionary<char, bool>();
        private static TMPro.TMP_FontAsset _oracle;
        private static bool _oracleLooked;

        /// <summary>The overlay is drawn in the game's style (its fonts were found).</summary>

        /// <summary>Call at the start of every OnGUI that draws mod UI.</summary>
        internal static void Apply()
        {
            if (!_built || _builtFor != Lang.Code) Build();
            if (Skin != null) GUI.skin = Skin;
            else Fonts.Apply();
        }

        /// <summary>
        /// The style to draw <paramref name="text"/> with: <paramref name="s"/> itself, or its
        /// Unifont twin when the text has characters Retro Gaming can't draw (a name in
        /// another script, say).
        /// </summary>
        internal static GUIStyle Fit(GUIStyle s, string text)
        {
            if (s == null || _wide || Skin == null || string.IsNullOrEmpty(text) || PixelCovers(text)) return s;
            if (!WideTwins.TryGetValue(s, out var w))
            {
                w = new GUIStyle(s) { font = GameLook.Uni ?? s.font, fontSize = Mathf.Max(16, s.fontSize) };
                WideTwins[s] = w;
            }
            return w;
        }

        /// <summary>Whether Retro Gaming has a glyph for every character of <paramref name="text"/>.</summary>
        internal static bool PixelCovers(string text)
        {
            foreach (char c in text)
            {
                if (c < 0x7F) continue;
                if (!Covered.TryGetValue(c, out bool ok))
                {
                    ok = HasGlyph(c);
                    Covered[c] = ok;
                }
                if (!ok) return false;
            }
            return true;
        }

        private static bool HasGlyph(char c)
        {
            if (char.IsWhiteSpace(c)) return true;
            // The game's dynamic Retro Gaming font asset reads the font file itself, so it
            // knows exactly which glyphs exist (Unity's own Font answers yes to everything).
            if (!_oracleLooked)
            {
                _oracleLooked = true;
                try
                {
                    foreach (var f in Resources.FindObjectsOfTypeAll<TMPro.TMP_FontAsset>())
                        if (f != null && f.atlasPopulationMode == TMPro.AtlasPopulationMode.Dynamic && f.name.StartsWith("RetroGaming", System.StringComparison.OrdinalIgnoreCase)) { _oracle = f; break; }
                }
                catch { /* no TextMeshPro: guess */ }
            }
            if (_oracle != null)
            {
                try { return _oracle.HasCharacter(c, false, true); }
                catch { /* guess below */ }
            }
            return c < 0x180;  // Latin-1 and Latin Extended-A
        }

        /// <summary>
        /// A yes / no prompt centred at the top of the screen, as wide as its question:
        /// returns 1 for yes, 2 for no, 0 while unanswered. <paramref name="y"/> moves past it.
        /// </summary>
        internal static int Prompt(GUIStyle box, float screenW, ref float y, string question, string yes, string no)
        {
            var style = Fit(box, question);
            float w = Mathf.Clamp(style.CalcSize(new GUIContent(question)).x + 32f, 380f, screenW - 40f);
            var r = new Rect(screenW / 2f - w / 2f, y, w, 68f);
            UiMouse.Claim(r);
            GUI.Box(r, question, style);
            int answer = 0;
            if (GUI.Button(new Rect(r.center.x - 150f, r.y + 34f, 140f, 26f), yes)) answer = 1;
            if (GUI.Button(new Rect(r.center.x + 10f, r.y + 34f, 140f, 26f), no)) answer = 2;
            y += 76f;
            return answer;
        }

        // ------------------------------------------------------------------ building

        private static void Build()
        {
            _built = true;
            _builtFor = Lang.Code;
            WideTwins.Clear();
            var pixel = GameLook.Pixel;
            if (pixel == null) { Skin = null; return; }
            try
            {
                // The language's own words decide the overlay font.
                string sample = Lang.T("Host") + Lang.T("Players") + Lang.T("When you die") + Lang.T("Rules (change them any time)") + Lang.T("Leave session");
                _wide = !PixelCovers(sample) && GameLook.Uni != null;
                var font = _wide ? GameLook.Uni : pixel;
                Crisp(font);

                var basis = GUI.skin;
                var s = Object.Instantiate(basis);
                s.hideFlags = HideFlags.HideAndDontSave;
                s.font = font;
                int body = _wide ? 16 : Body;

                Text(s.label, body, Ink);
                s.label.padding = new RectOffset(2, 2, 2, 2);

                // Buttons, and the toolbar's left / middle / right pieces.
                Button(s.button, body);
                foreach (var cs in s.customStyles)
                    if (cs != null && cs.name != null && cs.name.StartsWith("button", System.StringComparison.OrdinalIgnoreCase)) Button(cs, body);

                // Boxes: prompts, speech bubbles, badges.
                Plain(s.box, Frame(BoxFill, Line), body, Ink);
                s.box.border = new RectOffset(3, 3, 3, 3);
                s.box.padding = new RectOffset(8, 8, 5, 6);

                // Windows: a title band over a rule, then the body.
                var win = WindowTexture();
                foreach (var st in new[] { s.window.normal, s.window.hover, s.window.active, s.window.focused, s.window.onNormal, s.window.onHover, s.window.onActive, s.window.onFocused })
                {
                    st.background = win;
                    st.textColor = Ink;
                }
                s.window.font = font;
                s.window.fontSize = body;
                s.window.fontStyle = FontStyle.Normal;
                s.window.border = new RectOffset(3, 3, 29, 3);
                s.window.padding = new RectOffset(12, 12, 34, 12);
                s.window.contentOffset = new Vector2(0f, -28f);
                s.window.alignment = TextAnchor.UpperLeft;
                s.window.overflow = new RectOffset(0, 0, 0, 0);

                // Text fields.
                foreach (var tf in new[] { s.textField, s.textArea })
                {
                    tf.normal.background = Frame(new Color(0f, 0f, 0f, 0.9f), LineDim);
                    tf.hover.background = Frame(new Color(0f, 0f, 0f, 0.9f), new Color(0.85f, 0.85f, 0.85f));
                    tf.focused.background = tf.onFocused.background = Frame(new Color(0.04f, 0.04f, 0.04f, 0.95f), Line);
                    tf.active.background = tf.focused.background;
                    foreach (var st in new[] { tf.normal, tf.hover, tf.focused, tf.active, tf.onNormal, tf.onFocused }) st.textColor = Ink;
                    tf.border = new RectOffset(3, 3, 3, 3);
                    tf.padding = new RectOffset(6, 6, 4, 4);
                    tf.fontSize = body;
                    tf.fontStyle = FontStyle.Normal;
                    tf.overflow = new RectOffset(0, 0, 0, 0);
                }

                Toggle(s.toggle, body);

                // Slider: a thin line and a white block.
                s.horizontalSlider.normal.background = SliderTrack();
                s.horizontalSlider.border = new RectOffset(3, 3, 0, 0);
                s.horizontalSlider.fixedHeight = 12f;
                s.horizontalSlider.overflow = new RectOffset(0, 0, 0, 0);
                s.horizontalSlider.padding = new RectOffset(0, 0, 0, 0);
                var thumb = Frame(Color.white, Color.white);
                foreach (var st in new[] { s.horizontalSliderThumb.normal, s.horizontalSliderThumb.hover, s.horizontalSliderThumb.active, s.horizontalSliderThumb.focused })
                    st.background = thumb;
                s.horizontalSliderThumb.active.background = Frame(new Color(0.75f, 0.75f, 0.75f), Color.white);
                s.horizontalSliderThumb.border = new RectOffset(3, 3, 3, 3);
                s.horizontalSliderThumb.fixedWidth = 8f;
                s.horizontalSliderThumb.fixedHeight = 12f;
                s.horizontalSliderThumb.overflow = new RectOffset(0, 0, 0, 0);

                // Scroll bars: a faint rail and a framed thumb, no arrow buttons.
                Scrollbar(s.verticalScrollbar, s.verticalScrollbarThumb, true);
                Scrollbar(s.horizontalScrollbar, s.horizontalScrollbarThumb, false);
                foreach (var b in new[] { s.verticalScrollbarUpButton, s.verticalScrollbarDownButton, s.horizontalScrollbarLeftButton, s.horizontalScrollbarRightButton })
                {
                    b.fixedHeight = 0f;
                    b.fixedWidth = 0f;
                    b.normal.background = null;
                }
                s.scrollView.normal.background = null;

                s.settings.cursorColor = Color.white;
                s.settings.selectionColor = new Color(1f, 1f, 1f, 0.35f);
                s.settings.doubleClickSelectsWord = true;

                Skin = s;
                Log.Info("[ui] game-styled overlay with " + font.name + (_wide ? " (the language needs its wide coverage)" : ""));
            }
            catch (System.Exception e)
            {
                Skin = null;
                Log.Warn("[ui] couldn't build the game-styled overlay: " + e.Message);
            }
        }

        /// <summary>Pixel fonts stay sharp with point filtering (the glyph atlas is rebuilt as text appears).</summary>
        private static void Crisp(Font f)
        {
            if (f == null) return;
            void Point(Font g)
            {
                if (g != f || g.material == null || g.material.mainTexture == null) return;
                g.material.mainTexture.filterMode = FilterMode.Point;
            }
            Point(f);
            Font.textureRebuilt -= Rebuilt;
            Font.textureRebuilt += Rebuilt;
        }

        private static void Rebuilt(Font f)
        {
            if (f == null || f.material == null || f.material.mainTexture == null) return;
            if (f == GameLook.Pixel || f == GameLook.Uni) f.material.mainTexture.filterMode = FilterMode.Point;
        }

        private static void Text(GUIStyle st, int size, Color c)
        {
            st.fontSize = size;
            st.fontStyle = FontStyle.Normal;
            foreach (var s in new[] { st.normal, st.hover, st.active, st.focused, st.onNormal, st.onHover, st.onActive, st.onFocused }) s.textColor = c;
        }

        private static void Plain(GUIStyle st, Texture2D bg, int size, Color text)
        {
            foreach (var s in new[] { st.normal, st.hover, st.active, st.focused, st.onNormal, st.onHover, st.onActive, st.onFocused })
            {
                s.background = bg;
                s.textColor = text;
            }
            st.fontSize = size;
            st.fontStyle = FontStyle.Normal;
            st.overflow = new RectOffset(0, 0, 0, 0);
        }

        private static void Button(GUIStyle b, int size)
        {
            b.normal.background = Frame(ButtonFill, Line, ButtonShade);
            b.hover.background = b.focused.background = Frame(ButtonHover, Line, ButtonFill);
            b.active.background = Frame(Color.black, LineDim);
            b.onNormal.background = b.onFocused.background = Frame(new Color(0.91f, 0.91f, 0.91f), Line);
            b.onHover.background = Frame(Color.white, Line);
            b.onActive.background = Frame(new Color(0.66f, 0.66f, 0.66f), LineDim);
            b.normal.textColor = b.hover.textColor = b.focused.textColor = Ink;
            b.active.textColor = new Color(0.8f, 0.8f, 0.8f);
            b.onNormal.textColor = b.onHover.textColor = b.onActive.textColor = b.onFocused.textColor = Color.black;
            b.border = new RectOffset(3, 3, 3, 3);
            b.padding = new RectOffset(8, 8, 4, 5);
            b.overflow = new RectOffset(0, 0, 0, 0);
            b.fontSize = size;
            b.fontStyle = FontStyle.Normal;
            b.alignment = TextAnchor.MiddleCenter;
        }

        private static void Toggle(GUIStyle t, int size)
        {
            t.normal.background = ToggleTexture(false, ButtonFill, Line);
            t.hover.background = t.focused.background = ToggleTexture(false, ButtonHover, Line);
            t.active.background = ToggleTexture(false, Color.black, LineDim);
            t.onNormal.background = t.onFocused.background = ToggleTexture(true, ButtonFill, Line);
            t.onHover.background = ToggleTexture(true, ButtonHover, Line);
            t.onActive.background = ToggleTexture(true, Color.black, LineDim);
            foreach (var s in new[] { t.normal, t.hover, t.active, t.focused, t.onNormal, t.onHover, t.onActive, t.onFocused }) s.textColor = Ink;
            t.border = new RectOffset(18, 1, 17, 4);
            t.padding = new RectOffset(20, 4, 3, 3);
            t.margin = new RectOffset(4, 4, 3, 3);
            t.overflow = new RectOffset(0, 0, 0, 0);
            t.contentOffset = Vector2.zero;
            t.fixedHeight = 0f;
            t.fixedWidth = 0f;
            t.fontSize = size;
            t.fontStyle = FontStyle.Normal;
            t.alignment = TextAnchor.UpperLeft;
            t.imagePosition = ImagePosition.ImageLeft;
            t.wordWrap = true;
        }

        private static void Scrollbar(GUIStyle bar, GUIStyle thumb, bool vertical)
        {
            var rail = new Texture2D(vertical ? 10 : 8, vertical ? 8 : 10, TextureFormat.ARGB32, false) { filterMode = FilterMode.Point, hideFlags = HideFlags.HideAndDontSave };
            var faint = new Color(1f, 1f, 1f, 0.25f);
            for (int y = 0; y < rail.height; y++)
                for (int x = 0; x < rail.width; x++)
                    rail.SetPixel(x, y, (vertical ? x == 4 || x == 5 : y == 4 || y == 5) ? faint : Clear);
            rail.Apply();
            bar.normal.background = rail;
            bar.border = vertical ? new RectOffset(0, 0, 3, 3) : new RectOffset(3, 3, 0, 0);
            bar.padding = new RectOffset(0, 0, 0, 0);
            bar.margin = new RectOffset(2, 2, 2, 2);
            if (vertical) bar.fixedWidth = 10f; else bar.fixedHeight = 10f;
            thumb.normal.background = Frame(ButtonFill, Line);
            thumb.hover.background = Frame(ButtonHover, Line);
            thumb.active.background = Frame(Color.white, Line);
            thumb.border = new RectOffset(3, 3, 3, 3);
            if (vertical) thumb.fixedWidth = 10f; else thumb.fixedHeight = 10f;
        }

        // ------------------------------------------------------------------ textures

        private static readonly Dictionary<string, Texture2D> Made = new Dictionary<string, Texture2D>();

        private static Texture2D New(int w, int h)
        {
            var t = new Texture2D(w, h, TextureFormat.ARGB32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
            var clear = new Color[w * h];
            t.SetPixels(clear);
            return t;
        }

        /// <summary>
        /// An 8×8 block: a fill inside a one-pixel line whose corner pixels are cut, like the
        /// game's uiBlock sprites; <paramref name="shade"/> darkens the bottom inner row.
        /// </summary>
        internal static Texture2D Frame(Color fill, Color line, Color? shade = null)
        {
            string key = "f" + ColorUtility.ToHtmlStringRGBA(fill) + ColorUtility.ToHtmlStringRGBA(line) + (shade.HasValue ? ColorUtility.ToHtmlStringRGBA(shade.Value) : "");
            if (Made.TryGetValue(key, out var have) && have != null) return have;
            const int n = 8;
            var t = New(n, n);
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    bool edgeX = x == 0 || x == n - 1, edgeY = y == 0 || y == n - 1;
                    if (edgeX && edgeY) continue;  // the notched corner
                    Color c = edgeX || edgeY ? line : (shade.HasValue && y == 1 ? shade.Value : fill);
                    t.SetPixel(x, y, c);
                }
            t.Apply();
            Made[key] = t;
            return t;
        }

        /// <summary>The window: a 16×40 block with a rule under the title band (rows count from the top).</summary>
        private static Texture2D WindowTexture()
        {
            const int w = 16, h = 40, rule = 26;
            var t = New(w, h);
            var ruleColor = new Color(1f, 1f, 1f, 0.45f);
            for (int row = 0; row < h; row++)
            {
                int y = h - 1 - row;  // textures count rows from the bottom
                for (int x = 0; x < w; x++)
                {
                    bool edgeX = x == 0 || x == w - 1, edgeY = row == 0 || row == h - 1;
                    if (edgeX && edgeY) continue;
                    Color c = edgeX || edgeY ? Line : row == rule && x > 2 && x < w - 3 ? ruleColor : Panel;
                    t.SetPixel(x, y, c);
                }
            }
            t.Apply();
            return t;
        }

        /// <summary>The check box: a 12×12 framed square at the left of a 20×22 image, the rest clear.</summary>
        private static Texture2D ToggleTexture(bool on, Color fill, Color line)
        {
            const int w = 20, h = 22, top = 4, size = 12;
            var t = New(w, h);
            for (int row = top; row < top + size; row++)
            {
                int y = h - 1 - row;
                for (int x = 0; x < size; x++)
                {
                    bool edgeX = x == 0 || x == size - 1, edgeY = row == top || row == top + size - 1;
                    if (edgeX && edgeY) continue;
                    bool mark = on && x >= 3 && x <= 8 && row >= top + 3 && row <= top + 8;
                    t.SetPixel(x, y, edgeX || edgeY ? line : mark ? Color.white : fill);
                }
            }
            t.Apply();
            return t;
        }

        private static Texture2D SliderTrack()
        {
            var t = New(8, 12);
            var c = new Color(1f, 1f, 1f, 0.6f);
            for (int x = 0; x < 8; x++) { t.SetPixel(x, 5, c); t.SetPixel(x, 6, c); }
            t.Apply();
            return t;
        }
    }
}
