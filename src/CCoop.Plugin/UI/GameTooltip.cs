using CCoop.Plugin.Runtime;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CCoop.Plugin.UI
{
    /// <summary>
    /// The game's hover label, for the mod's own screens. The game draws its label (a
    /// name, and a description under it, each on its dark band, beside the mouse) on its
    /// own canvas, which the overlay covers, so this draws the same label over the
    /// overlay: its font, sizes, colours, bands and offsets are read from the game's own
    /// label (GlobalDark), and it scales with the screen as the game's does.
    /// </summary>
    internal static class GameTooltip
    {
        private static string _name, _desc;
        private static int _frame = -10;

        private static bool _looked;
        private static float _nameSize = 22f, _descSize = 18f, _descMax = 420f, _nameH = 30f;
        private static Vector2 _nameAt = new Vector2(14f, -10f), _descAt = new Vector2(14f, -40f);
        private static Color _nameColor = Color.white, _descColor = new Color(0.75f, 0.75f, 0.75f);
        private static Color _nameBand = new Color(0f, 0f, 0f, 0.8f), _descBand = new Color(0f, 0f, 0f, 0.8f);
        private static GUIStyle _nameStyle, _descStyle;
        private static GameObject _layer;

        private static Vector2? _devAt;
        private static string _devName, _devDesc;

        /// <summary>Self-test screenshots: holds a label at a fixed point (null clears it).</summary>
        internal static void DevShow(string name, string desc, Vector2? at)
        {
            _devAt = at;
            _devName = name;
            _devDesc = desc;
            if (at.HasValue) Set(name, desc);
        }

        /// <summary>Shows the label this frame (call while the mouse is on the thing).</summary>
        internal static void Set(string name, string desc = null)
        {
            if (string.IsNullOrEmpty(name)) return;
            _name = name;
            _desc = desc;
            _frame = Time.frameCount;
            if (_layer == null)
            {
                _layer = new GameObject("CasualtiesCoop.Tooltip") { hideFlags = HideFlags.HideAndDontSave };
                Object.DontDestroyOnLoad(_layer);
                _layer.AddComponent<Layer>();
            }
        }

        /// <summary>Above every window of the overlay.</summary>
        private sealed class Layer : MonoBehaviour
        {
            private void OnGUI()
            {
                GUI.depth = -30000;
                if (Event.current.type == EventType.Repaint) Draw();
            }
        }

        private static void Draw()
        {
            if (_devAt.HasValue) { _name = _devName; _desc = _devDesc; _frame = Time.frameCount; }
            if (Time.frameCount - _frame > 1 || string.IsNullOrEmpty(_name)) return;
            Look();
            float s = Screen.height / 1080f;
            _nameStyle.fontSize = Mathf.Max(8, Mathf.RoundToInt(_nameSize * s));
            _descStyle.fontSize = Mathf.Max(8, Mathf.RoundToInt(_descSize * s));
            var m = _devAt ?? Event.current.mousePosition;
            var oldMatrix = GUI.matrix;
            GUI.matrix = Matrix4x4.identity;

            var nameStyle = UiSkin.Fit(_nameStyle, _name);
            var nameSize = nameStyle.CalcSize(new GUIContent(_name));
            var nameBox = new Rect(m.x + _nameAt.x * s, m.y - _nameAt.y * s, nameSize.x + 8f * s, Mathf.Max(_nameH * s, nameSize.y));
            Rect descBox = default;
            bool hasDesc = !string.IsNullOrEmpty(_desc);
            GUIStyle descStyle = null;
            if (hasDesc)
            {
                descStyle = UiSkin.Fit(_descStyle, _desc);
                float w = Mathf.Min(descStyle.CalcSize(new GUIContent(_desc)).x, _descMax * s);
                float h = descStyle.CalcHeight(new GUIContent(_desc), w);
                descBox = new Rect(m.x + _descAt.x * s, Mathf.Max(nameBox.yMax, m.y - _descAt.y * s), w + 8f * s, h + 4f * s);
            }

            // Kept on screen, as the game keeps its own.
            float right = Mathf.Max(nameBox.xMax, hasDesc ? descBox.xMax : 0f), bottom = hasDesc ? descBox.yMax : nameBox.yMax;
            float dx = Mathf.Min(0f, Screen.width - 30f * s - right), dy = Mathf.Min(0f, Screen.height - 30f * s - bottom);
            nameBox.x += dx; nameBox.y += dy;
            descBox.x += dx; descBox.y += dy;

            Band(nameBox, _nameBand);
            GUI.Label(new Rect(nameBox.x + 4f * s, nameBox.y, nameBox.width, nameBox.height), _name, nameStyle);
            if (hasDesc)
            {
                Band(descBox, _descBand);
                GUI.Label(new Rect(descBox.x + 4f * s, descBox.y + 2f * s, descBox.width - 4f * s, descBox.height), _desc, descStyle);
            }
            GUI.matrix = oldMatrix;
        }

        private static void Band(Rect r, Color c)
        {
            var old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = old;
        }

        /// <summary>Reads the game's label once: text sizes and colours, bands, and where they sit from the mouse.</summary>
        private static void Look()
        {
            if (_nameStyle == null)
            {
                _nameStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleLeft, wordWrap = false, richText = true, padding = new RectOffset(0, 0, 0, 0) };
                _descStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.UpperLeft, wordWrap = true, richText = true, padding = new RectOffset(0, 0, 0, 0) };
                if (GameLook.Pixel != null) { _nameStyle.font = GameLook.Pixel; _descStyle.font = GameLook.Pixel; }
                _nameStyle.normal.textColor = _nameColor;
                _descStyle.normal.textColor = _descColor;
            }
            if (_looked) return;
            var g = GlobalDark.main;
            if (g == null || g.tooltipRect == null) return;
            _looked = true;
            try
            {
                if (g.tooltipNameObj != null) { _nameSize = g.tooltipNameObj.fontSize; _nameColor = g.tooltipNameObj.color; }
                if (g.tooltipDescObj != null)
                {
                    _descSize = g.tooltipDescObj.fontSize;
                    _descColor = g.tooltipDescObj.color;
                    if (g.tooltipDescObj.rectTransform.sizeDelta.x > 10f) _descMax = g.tooltipDescObj.rectTransform.sizeDelta.x;
                }
                if (g.hoverNameBackground != null)
                {
                    _nameAt = TopLeft(g.tooltipRect, g.hoverNameBackground);
                    _nameH = g.hoverNameBackground.sizeDelta.y;
                    var img = g.hoverNameBackground.GetComponent<Image>();
                    if (img != null) _nameBand = img.color;
                }
                if (g.hoverDescriptionBackground != null)
                {
                    _descAt = TopLeft(g.tooltipRect, g.hoverDescriptionBackground);
                    var img = g.hoverDescriptionBackground.GetComponent<Image>();
                    if (img != null) _descBand = img.color;
                }
                Log.Info("[ui] game hover label: name " + _nameSize + " " + ColorUtility.ToHtmlStringRGBA(_nameColor) + " on " + ColorUtility.ToHtmlStringRGBA(_nameBand) +
                         " at " + _nameAt + ", desc " + _descSize + " " + ColorUtility.ToHtmlStringRGBA(_descColor) + " on " + ColorUtility.ToHtmlStringRGBA(_descBand) + " at " + _descAt + " max " + _descMax);
            }
            catch (System.Exception e) { Log.Debug("tooltip look: " + e.Message); }
            _nameStyle.normal.textColor = _nameColor;
            _descStyle.normal.textColor = _descColor;
        }

        /// <summary>A band's top-left corner in the label's own units (y up), from the mouse point.</summary>
        private static Vector2 TopLeft(RectTransform root, RectTransform band)
        {
            var c = new Vector3[4];
            band.GetWorldCorners(c);
            var tl = root.InverseTransformPoint(c[1]);
            var origin = root.InverseTransformPoint(root.position);
            return new Vector2(tl.x - origin.x, tl.y - origin.y);
        }
    }
}
