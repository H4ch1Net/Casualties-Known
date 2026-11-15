using CCoop.Plugin.Runtime;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CCoop.Plugin.UI
{
    /// <summary>
    /// The game's own speech text: the amber, textured lettering that appears above a
    /// character who talks (TextMeshPro, the game's "ExpSpeak" font and its material).
    /// The mod copies the local character's own speech object, so teammates' words,
    /// name tags and the chat log are drawn exactly as the game draws speech.
    /// </summary>
    internal static class GameText
    {
        private static TextMeshPro _template;
        private static float _lookAt;
        private static Canvas _canvas;

        /// <summary>The local character's speech text (only exists in a run), or null.</summary>
        internal static TextMeshPro Template
        {
            get
            {
                if (_template != null) return _template;
                if (Time.unscaledTime < _lookAt) return null;
                _lookAt = Time.unscaledTime + 2f;
                try
                {
                    var b = GameInfo.LocalBody;
                    if (b != null && b.talker != null && b.talker.text != null) _template = b.talker.text;
                    if (_template == null)
                        foreach (var t in Object.FindObjectsOfType<Talker>())
                            if (t != null && t.text != null && t.GetComponent<Body>() != null) { _template = t.text; break; }
                }
                catch { /* not in a run */ }
                if (_template != null)
                    Log.Info("[ui] speech text: " + (_template.font != null ? _template.font.name : "?") + ", size " + _template.fontSize +
                             ", colour #" + ColorUtility.ToHtmlStringRGBA(_template.color));
                return _template;
            }
        }

        /// <summary>The game's speech colour (its amber).</summary>
        internal static Color Colour => Template != null ? Template.color : UiSkin.Amber;

        /// <summary>A world-space copy of the game's speech text, empty, ready to place. Null outside a run.</summary>
        internal static TextMeshPro World(string name)
        {
            var t = Template;
            if (t == null) return null;
            var go = new GameObject("CasualtiesCoop." + name);
            var tmp = go.AddComponent<TextMeshPro>();
            Copy(t, tmp);
            tmp.rectTransform.sizeDelta = t.rectTransform.sizeDelta;
            go.transform.localScale = t.transform.lossyScale;
            var fr = t.GetComponent<Renderer>();
            var tr = tmp.GetComponent<Renderer>();
            if (fr != null && tr != null) { tr.sortingLayerID = fr.sortingLayerID; tr.sortingOrder = fr.sortingOrder; }
            tmp.text = "";
            return tmp;
        }

        /// <summary>A screen-space copy (for the chat log), under the mod's own canvas. Null outside a run.</summary>
        internal static TextMeshProUGUI Screen(string name)
        {
            var t = Template;
            if (t == null) return null;
            if (_canvas == null)
            {
                var cgo = new GameObject("CasualtiesCoop.Canvas");
                Object.DontDestroyOnLoad(cgo);
                _canvas = cgo.AddComponent<Canvas>();
                _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                _canvas.sortingOrder = 30;
                var scaler = cgo.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920f, 1080f);
                scaler.matchWidthOrHeight = 1f;
            }
            var go = new GameObject("CasualtiesCoop." + name, typeof(RectTransform));
            go.transform.SetParent(_canvas.transform, false);
            var tmp = go.AddComponent<TextMeshProUGUI>();
            Copy(t, tmp);
            tmp.raycastTarget = false;
            tmp.text = "";
            return tmp;
        }

        private static void Copy(TMP_Text from, TMP_Text to)
        {
            to.font = from.font;
            to.fontSharedMaterial = from.fontSharedMaterial;
            to.color = from.color;
            to.fontSize = from.fontSize;
            to.fontStyle = from.fontStyle;
            to.characterSpacing = from.characterSpacing;
            to.alignment = TextAlignmentOptions.Center;
            to.enableWordWrapping = true;
            to.richText = false;
        }

        /// <summary>The game's sound for one spoken letter, by character (the same the game plays).</summary>
        internal static string LetterSound(int character) =>
            character == 1 ? "milspeech" : character == 2 ? "speechdune" : "speech";
    }
}
