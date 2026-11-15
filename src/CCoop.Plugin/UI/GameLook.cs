using System.Collections.Generic;
using CCoop.Plugin.Runtime;
using UnityEngine;

namespace CCoop.Plugin.UI
{
    /// <summary>
    /// The game's own look, borrowed at runtime (nothing of the game's is shipped with the
    /// mod): its pixel fonts and its 9-slice UI blocks. Retro Gaming is the font of the
    /// game's menus and HUD; Unifont, which the game also carries, covers almost every
    /// script and stands in wherever Retro Gaming has no glyph (Chinese, Japanese,
    /// Korean...). The blocks are copied out of the game's sprite atlas into textures
    /// IMGUI can draw.
    /// </summary>
    internal static class GameLook
    {
        private static bool _looked;
        private static Font _pixel, _uni;
        private static readonly Dictionary<string, Texture2D> Copies = new Dictionary<string, Texture2D>();
        private static readonly Dictionary<string, Sprite> Sprites = new Dictionary<string, Sprite>();

        /// <summary>Retro Gaming (the game's UI font), or null if the game no longer has it.</summary>
        internal static Font Pixel { get { Look(); return _pixel; } }

        /// <summary>Unifont (the game's wide-coverage pixel font), or null.</summary>
        internal static Font Uni { get { Look(); return _uni; } }

        private static void Look()
        {
            if (_looked) return;
            _looked = true;
            try
            {
                foreach (var f in Resources.FindObjectsOfTypeAll<Font>())
                {
                    if (f == null) continue;
                    if (_pixel == null && f.name.StartsWith("RetroGaming", System.StringComparison.OrdinalIgnoreCase)) _pixel = f;
                    else if (_uni == null && f.name.StartsWith("unifont", System.StringComparison.OrdinalIgnoreCase)) _uni = f;
                }
                foreach (var s in Resources.FindObjectsOfTypeAll<Sprite>())
                    if (s != null && !Sprites.ContainsKey(s.name)) Sprites[s.name] = s;
                Log.Info("[ui] game look: font " + (_pixel != null ? _pixel.name : "none") + ", wide font " + (_uni != null ? _uni.name : "none") +
                         ", " + Sprites.Count + " sprites");
            }
            catch (System.Exception e) { Log.Warn("[ui] couldn't borrow the game's look: " + e.Message); }
        }

        /// <summary>The 9-slice border the game gives one of its sprites (left, bottom, right, top), or zero.</summary>
        internal static Vector4 Border(string sprite)
        {
            Look();
            return Sprites.TryGetValue(sprite, out var s) && s != null ? s.border : Vector4.zero;
        }

        /// <summary>
        /// One of the game's sprites as a texture of its own (point-filtered, as the game
        /// draws it), copied on the GPU since the atlas isn't readable. Null if missing.
        /// </summary>
        internal static Texture2D Texture(string sprite)
        {
            Look();
            if (Copies.TryGetValue(sprite, out var have)) return have;
            Texture2D tex = null;
            if (Sprites.TryGetValue(sprite, out var s) && s != null && s.texture != null)
            {
                RenderTexture rt = null, prev = RenderTexture.active;
                try
                {
                    var r = s.textureRect;
                    int w = Mathf.RoundToInt(r.width), h = Mathf.RoundToInt(r.height);
                    var src = s.texture;
                    rt = RenderTexture.GetTemporary(src.width, src.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                    Graphics.Blit(src, rt);
                    RenderTexture.active = rt;
                    tex = new Texture2D(w, h, TextureFormat.ARGB32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
                    tex.ReadPixels(new Rect(r.x, src.height - r.y - h, w, h), 0, 0);
                    tex.Apply();
                }
                catch (System.Exception e)
                {
                    Log.Warn("[ui] couldn't copy the game's " + sprite + ": " + e.Message);
                    tex = null;
                }
                finally
                {
                    RenderTexture.active = prev;
                    if (rt != null) RenderTexture.ReleaseTemporary(rt);
                }
            }
            Copies[sprite] = tex;
            return tex;
        }

        /// <summary>A plain texture of one colour (for fills and rules).</summary>
        internal static Texture2D Solid(Color c)
        {
            string key = "#" + ColorUtility.ToHtmlStringRGBA(c);
            if (Copies.TryGetValue(key, out var have) && have != null) return have;
            var t = new Texture2D(1, 1, TextureFormat.ARGB32, false) { filterMode = FilterMode.Point, hideFlags = HideFlags.HideAndDontSave };
            t.SetPixel(0, 0, c);
            t.Apply();
            Copies[key] = t;
            return t;
        }

        /// <summary>A small framed block drawn in code: a fill inside a 1-pixel line (and a darker outer pixel).</summary>
        internal static Texture2D Frame(Color fill, Color line, int size = 8, int lineWidth = 1)
        {
            string key = "frame" + size + "/" + lineWidth + ColorUtility.ToHtmlStringRGBA(fill) + ColorUtility.ToHtmlStringRGBA(line);
            if (Copies.TryGetValue(key, out var have) && have != null) return have;
            var t = new Texture2D(size, size, TextureFormat.ARGB32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    bool edge = x < lineWidth || y < lineWidth || x >= size - lineWidth || y >= size - lineWidth;
                    t.SetPixel(x, y, edge ? line : fill);
                }
            t.Apply();
            Copies[key] = t;
            return t;
        }

        /// <summary>Dev: writes the borrowed sprites to PNG files (to look at while styling).</summary>
        internal static void DevExport(string dir, IEnumerable<string> names)
        {
            foreach (var n in names)
            {
                var t = Texture(n);
                if (t == null) { Log.Info("[uidump] no sprite " + n); continue; }
                System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, "sprite-" + n + ".png"), t.EncodeToPNG());
                Log.Info("[uidump] sprite " + n + " " + t.width + "x" + t.height + " border " + Border(n));
            }
        }
    }
}
