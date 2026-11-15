using System.Collections.Generic;
using CCoop.Plugin.Protocol;
using CCoop.Plugin.Runtime;
using UnityEngine;
using UnityEngine.UI;

namespace CCoop.Plugin.UI
{
    /// <summary>
    /// A teammate's body drawn as the game draws your own in its health view (the R key):
    /// the same limb shapes, a white outline that reddens as the skin is hurt, a black
    /// fill that reddens as the muscle is, missing limbs gone, and the game's own icons
    /// beside a limb for bleeding (bandaged or not), infection, a broken bone, a
    /// dislocation and shrapnel, plus its splint and tourniquet marks. Everything is
    /// borrowed from the game's health view at runtime; nothing of it is shipped.
    ///
    /// Only the body: the green read-outs beside it in the game come from the
    /// character's implanted chip, and nobody can read a teammate's chip.
    /// </summary>
    internal static class BodyFigure
    {
        private sealed class Part
        {
            public Sprite Outline, Fill;
            public Rect OutRect, FillRect;   // figure space: y grows downward
        }

        private static Part[] _parts;
        private static Sprite[] _icons, _heads;
        private static Gradient _bleed;
        private static Sprite _splint, _tourniquet;
        private static Rect _bounds;
        private static float _nativeToFigure = 1f;
        private static float _retryAt;

        /// <summary>The game's health view has been found (only in a run).</summary>
        internal static bool Ready => Capture();

        private static bool Capture()
        {
            if (_parts != null) return true;
            if (Time.unscaledTime < _retryAt) return false;
            _retryAt = Time.unscaledTime + 3f;
            try
            {
                WoundView wv = WoundView.view;
                if (wv == null && PlayerCamera.main != null && PlayerCamera.main.woundView != null) wv = PlayerCamera.main.woundView.GetComponentInChildren<WoundView>(true);
                if (wv == null)
                    foreach (var w in Resources.FindObjectsOfTypeAll<WoundView>())
                        if (w != null && w.gameObject.scene.IsValid()) { wv = w; break; }
                if (wv == null || wv.limbImages == null || wv.limbImages.Length == 0 || wv.limbImages[0] == null) return false;

                var root = wv.limbImages[0].transform.parent;
                var parts = new Part[wv.limbImages.Length];
                bool first = true;
                for (int k = 0; k < parts.Length; k++)
                {
                    var img = wv.limbImages[k];
                    if (img == null) continue;
                    var p = new Part { Outline = img.sprite, OutRect = RectIn(root, img.rectTransform) };
                    var child = img.rectTransform.childCount > 0 ? img.rectTransform.GetChild(0).GetComponent<Image>() : null;
                    if (child != null) { p.Fill = child.sprite; p.FillRect = RectIn(root, child.rectTransform); }
                    parts[k] = p;
                    _bounds = first ? p.OutRect : Union(_bounds, p.OutRect);
                    first = false;
                }
                // Native sprite size (as the game's SetNativeSize gives it) to figure units.
                var torso = parts.Length > 1 ? parts[1] : parts[0];
                if (torso?.Outline != null && torso.Outline.rect.width > 0f)
                    _nativeToFigure = torso.OutRect.width / (torso.Outline.rect.width * 100f / torso.Outline.pixelsPerUnit);
                _icons = wv.icons;
                _heads = wv.headCharSprites;
                _bleed = wv.bleedGradient;
                _splint = Resources.Load<Sprite>("Special/splintIcon");
                _tourniquet = Resources.Load<Sprite>("Special/tourniquetIcon");
                _parts = parts;
                Log.Info("[ui] body figure from the game's health view: " + parts.Length + " limbs, " + (_icons?.Length ?? 0) + " icons");
                return true;
            }
            catch (System.Exception e)
            {
                Log.Warn("[ui] couldn't read the game's health view: " + e.Message);
                return false;
            }
        }

        private static Rect RectIn(Transform root, RectTransform rt)
        {
            var c = new Vector3[4];
            rt.GetWorldCorners(c);
            Vector3 a = root.InverseTransformPoint(c[0]), b = root.InverseTransformPoint(c[2]);
            float xMin = Mathf.Min(a.x, b.x), xMax = Mathf.Max(a.x, b.x), yMin = Mathf.Min(a.y, b.y), yMax = Mathf.Max(a.y, b.y);
            return Rect.MinMaxRect(xMin, -yMax, xMax, -yMin);
        }

        private static Rect Union(Rect a, Rect b) =>
            Rect.MinMaxRect(Mathf.Min(a.xMin, b.xMin), Mathf.Min(a.yMin, b.yMin), Mathf.Max(a.xMax, b.xMax), Mathf.Max(a.yMax, b.yMax));

        /// <summary>The size a figure drawn into <paramref name="width"/> needs.</summary>
        internal static float HeightFor(float width) => Capture() && _bounds.width > 0f ? width * _bounds.height / _bounds.width : width * 1.4f;

        /// <summary>
        /// Draws the body into <paramref name="area"/>; returns the limb under the mouse, or -1.
        /// <paramref name="character"/> picks the head (the game's three characters differ).
        /// </summary>
        internal static int Draw(Rect area, Vitals v, int character, int selected)
        {
            if (!Capture() || v == null) return -1;
            float s = Mathf.Min(area.width / _bounds.width, area.height / _bounds.height);
            var origin = new Vector2(area.x + (area.width - _bounds.width * s) / 2f - _bounds.x * s,
                                     area.y + (area.height - _bounds.height * s) / 2f - _bounds.y * s);
            Rect Place(Rect r) => new Rect(origin.x + r.x * s, origin.y + r.y * s, r.width * s, r.height * s);

            var mouse = Event.current.mousePosition;
            int hover = -1;
            float hoverArea = float.MaxValue;
            for (int k = 0; k < _parts.Length && k < v.Limbs.Count; k++)
            {
                if (_parts[k] == null || (v.Limbs[k].Signs & LimbSigns.Missing) != 0) continue;
                var r = Place(_parts[k].OutRect);
                float a = r.width * r.height;
                if (r.Contains(mouse) && a < hoverArea) { hover = k; hoverArea = a; }
            }

            var old = GUI.color;
            float t = Time.unscaledTime;
            // Body first, then the marks on top (the game's icons sit above every limb).
            for (int k = 0; k < _parts.Length && k < v.Limbs.Count; k++)
            {
                var p = _parts[k];
                var l = v.Limbs[k];
                if (p == null || (l.Signs & LimbSigns.Missing) != 0) continue;
                bool prosthetic = (l.Signs & LimbSigns.Prosthetic) != 0;
                float alpha = prosthetic ? 0.5f : 1f;
                float flash = k == hover ? 0.45f : 0f;
                float h = (prosthetic ? l.Muscle : l.Skin) * 0.01f;
                var outline = p.Outline;
                var fill = p.Fill;
                if (k == 0 && _heads != null && _heads.Length >= 6 && character >= 0 && character <= 2 && _heads[character * 2] != null)
                {
                    outline = _heads[character * 2];
                    fill = _heads[character * 2 + 1] ?? fill;
                }
                // The game's colours: skin on the outline (white -> red), muscle in the fill (black -> red).
                GUI.color = k == selected ? new Color(UiSkin.Amber.r, UiSkin.Amber.g * Mathf.Clamp01(h + 0.2f), UiSkin.Amber.b * h, alpha)
                                          : new Color(1f - flash, Mathf.Clamp01(h - flash), Mathf.Clamp01(h - flash), alpha);
                DrawSprite(Place(k == 0 && outline != p.Outline ? Native(p.OutRect, outline) : p.OutRect), outline);
                if (fill != null)
                {
                    float m = l.Muscle * 0.01f;
                    var c = new Color(Mathf.Lerp(prosthetic ? 0.5f : 0f, 1f, 1f - Mathf.Clamp01(m)), prosthetic ? 0.5f : 0f, prosthetic ? 0.5f : 0f, alpha);
                    if (l.Muscle < 10) c = new Color(Mathf.PingPong(t * 4f, 1f), 0f, 0f, alpha);
                    GUI.color = c;
                    DrawSprite(Place(k == 0 && fill != p.Fill ? Native(p.FillRect, fill) : p.FillRect), fill);
                }
                if ((l.Signs & LimbSigns.Splinted) != 0 && _splint != null) { GUI.color = Color.white; DrawSprite(Place(Native(p.OutRect, _splint)), _splint); }
                if ((l.Signs & LimbSigns.Tourniquet) != 0 && _tourniquet != null) { GUI.color = Color.white; DrawSprite(Place(Native(p.OutRect, _tourniquet)), _tourniquet); }
            }
            for (int k = 0; k < _parts.Length && k < v.Limbs.Count; k++)
            {
                var p = _parts[k];
                var l = v.Limbs[k];
                if (p == null || (l.Signs & LimbSigns.Missing) != 0) continue;
                var marks = Marks(l);
                float spread = k == selected || k == hover ? 1f : 0.2f;
                var centre = new Vector2(p.OutRect.center.x, p.OutRect.center.y);
                for (int m = 0; m < marks.Count; m++)
                {
                    float x = (m * 50f - (marks.Count - 1) * 25f) * spread;
                    var (sprite, color) = marks[m];
                    if (sprite == null) continue;
                    float w = sprite.rect.width * 100f / sprite.pixelsPerUnit * _nativeToFigure, hh = sprite.rect.height * 100f / sprite.pixelsPerUnit * _nativeToFigure;
                    var r = new Rect(centre.x + x - w / 2f, centre.y - x * 0.2f - hh / 2f, w, hh);
                    GUI.color = color;
                    DrawSprite(Place(r), sprite);
                }
            }
            GUI.color = old;
            return hover;
        }

        /// <summary>A sprite at its native size, centred where the game's limb image is.</summary>
        private static Rect Native(Rect at, Sprite sp)
        {
            float w = sp.rect.width * 100f / sp.pixelsPerUnit * _nativeToFigure, h = sp.rect.height * 100f / sp.pixelsPerUnit * _nativeToFigure;
            return new Rect(at.center.x - w / 2f, at.center.y - h / 2f, w, h);
        }

        /// <summary>The game's icons for this limb, in its order: bleed, infection, broken, dislocated, shrapnel.</summary>
        private static List<(Sprite, Color)> Marks(LimbVitals l)
        {
            var list = new List<(Sprite, Color)>();
            if (_icons == null || _icons.Length < 16) return list;
            float bleed = (l.Signs & LimbSigns.BleedBlocked) != 0 ? 0f : l.Bleed / 10f;
            if (bleed > 1f)
            {
                bool bandaged = (l.Signs & LimbSigns.Bandaged) != 0;
                var sp = bandaged ? (bleed > 20f ? _icons[15] : bleed > 8f ? _icons[14] : bleed > 3.75f ? _icons[13] : _icons[12])
                                  : (bleed > 20f ? _icons[8] : bleed > 8f ? _icons[0] : bleed > 3.75f ? _icons[9] : _icons[7]);
                list.Add((sp, _bleed != null ? _bleed.Evaluate(bleed * 0.01f) : new Color(0.8f, 0.1f, 0.1f)));
            }
            if (l.Infection >= 25 || (l.Signs & LimbSigns.Infected) != 0)
                list.Add((_icons[10], Color.Lerp(Color.white, new Color32(163, 0, 182, 255), l.Infection * 0.01f)));
            bool prosthetic = (l.Signs & LimbSigns.Prosthetic) != 0;
            if ((l.Signs & LimbSigns.Broken) != 0) list.Add((prosthetic ? _icons[17 < _icons.Length ? 17 : 2] : _icons[2], Color.white));
            if ((l.Signs & LimbSigns.Dislocated) != 0) list.Add((prosthetic ? _icons[16 < _icons.Length ? 16 : 1] : _icons[1], Color.white));
            if ((l.Signs & LimbSigns.Shrapnel) != 0) list.Add((_icons[5], new Color32(173, 106, 54, 255)));
            return list;
        }

        private static void DrawSprite(Rect r, Sprite sp)
        {
            if (sp == null || sp.texture == null) return;
            var tr = sp.textureRect;
            var tex = sp.texture;
            GUI.DrawTextureWithTexCoords(r, tex, new Rect(tr.x / tex.width, tr.y / tex.height, tr.width / tex.width, tr.height / tex.height));
        }
    }
}
