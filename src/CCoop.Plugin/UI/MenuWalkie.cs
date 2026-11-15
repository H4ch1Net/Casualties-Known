using System;
using CCoop.Plugin.Runtime;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CCoop.Plugin.UI
{
    /// <summary>
    /// The way into co-op on the main menu: a walkie-talkie standing on the ground beside
    /// the sleeping bag, an object in the scene like the game's own (the character to play,
    /// the radio for the tutorial). It behaves as they do: an outline that brightens as
    /// the mouse comes near, a label under it when the mouse is on it, a click sound, and
    /// it sways with the scene's parallax layer. Clicking it opens the co-op window.
    ///
    /// The art is the mod's own (art/walkie.px, drawn for this scene, shown at 2x),
    /// embedded in the plugin. If the menu can't be read, the plain "Co-op (F1)" button
    /// stays instead.
    /// </summary>
    internal static class MenuWalkie
    {
        /// <summary>Where it stands, in the menu's 1920 x 1080 picture: centre of its base.</summary>
        private static readonly Vector2 Spot = new Vector2(1468f, 812f);
        private const float Scale = 2f;

        private static GameObject _root;
        private static int _scene = -1;
        private static float _retryAt;

        /// <summary>The walkie-talkie is in the menu (so the plain button isn't needed).</summary>
        internal static bool Shown => _root != null;

        /// <summary>Every frame: puts the walkie-talkie into the main menu when it appears.</summary>
        internal static void Ensure()
        {
            if (!GameInfo.OnMainMenu) { _root = null; return; }
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
            if (_root != null && _scene == scene) return;
            if (Time.unscaledTime < _retryAt) return;
            _retryAt = Time.unscaledTime + 1f;
            try { Build(scene); }
            catch (Exception e)
            {
                _root = null;
                _retryAt = Time.unscaledTime + 30f;
                Log.Warn("[ui] couldn't put the walkie-talkie in the menu: " + e.Message);
            }
        }

        private static void Build(int scene)
        {
            if (PreRunScript.instance == null) return;
            AdaptiveButton radio = null;
            foreach (var b in UnityEngine.Object.FindObjectsOfType<AdaptiveButton>())
                if (b != null && b.action == AdaptiveButton.MenuAction.Tutorial) { radio = b; break; }
            if (radio == null || radio.overlay == null || radio.text == null) return;

            var art = LoadArt();
            if (art == null) return;

            // In the radio's parallax layer, just above the radio's own pictures.
            var layerImage = radio.overlay.rectTransform;
            var parent = layerImage.parent;
            float unit = layerImage.rect.width > 1f ? layerImage.rect.width / 1920f : 1f;
            Vector2 centre = layerImage.localPosition;
            Vector2 Local(Vector2 menuPx) => centre + new Vector2(menuPx.x - 960f, 540f - menuPx.y) * unit;

            _root = new GameObject("CasualtiesCoop.Walkie", typeof(RectTransform));
            _root.transform.SetParent(parent, false);
            _root.transform.SetSiblingIndex(layerImage.GetSiblingIndex() + 1);
            var rt = (RectTransform)_root.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0f);
            rt.sizeDelta = new Vector2(art.width * Scale, art.height * Scale) * unit;
            rt.localPosition = Local(Spot);

            // A soft shadow on the ground, then the walkie-talkie, then its hover outline.
            Child(_root.transform, "Shadow", Shadow(), new Vector2(0.5f, 0.5f), new Vector2(0f, 2f * unit), new Vector2(84f, 18f) * unit, new Color(0f, 0f, 0f, 0.55f));
            Child(_root.transform, "Art", art, new Vector2(0.5f, 0f), Vector2.zero, rt.sizeDelta, Color.white);
            var outline = Child(_root.transform, "Overlay", Outline(art), new Vector2(0.5f, 0f), new Vector2(0f, -Scale * unit),
                                new Vector2(art.width + 2, art.height + 2) * Scale * unit, new Color(1f, 1f, 1f, 0.025f));

            // The label, a copy of the radio's ("Tutorial"), in the same font and place under it.
            var label = UnityEngine.Object.Instantiate(radio.text.gameObject, radio.text.transform.parent);
            label.name = "CasualtiesCoop.WalkieLabel";
            foreach (var mb in label.GetComponents<MonoBehaviour>())
                if (mb != null && !(mb is TextMeshProUGUI) && mb.GetType().Name.IndexOf("Local", StringComparison.OrdinalIgnoreCase) >= 0)
                    UnityEngine.Object.Destroy(mb);
            var tmp = label.GetComponent<TextMeshProUGUI>();
            tmp.text = Lang.T("Co-op");
            tmp.color = new Color(1f, 1f, 1f, 0f);
            label.transform.position = rt.TransformPoint(new Vector3(0f, -24f * unit, 0f));

            var item = _root.AddComponent<WalkieItem>();
            item.Overlay = outline;
            item.Label = tmp;
            item.Radio = radio;
            item.LabelRoot = label;
            _scene = scene;
            Log.Info("[ui] walkie-talkie in the main menu (layer " + parent.name + ")");
        }

        private static Image Child(Transform parent, string name, Texture2D tex, Vector2 pivot, Vector2 at, Vector2 size, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var r = (RectTransform)go.transform;
            r.anchorMin = r.anchorMax = new Vector2(0.5f, 0f);
            r.pivot = pivot;
            r.anchoredPosition = at;
            r.sizeDelta = size;
            var img = go.GetComponent<Image>();
            img.sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0f), 100f);
            img.color = color;
            img.raycastTarget = false;
            return img;
        }

        private static Texture2D LoadArt()
        {
            using (var s = typeof(MenuWalkie).Assembly.GetManifestResourceStream("CCoop.walkie.png"))
            {
                if (s == null) { Log.Warn("[ui] the walkie-talkie picture is missing from the plugin"); return null; }
                var bytes = new byte[s.Length];
                int read = 0;
                while (read < bytes.Length) { int n = s.Read(bytes, read, bytes.Length - read); if (n <= 0) break; read += n; }
                var tex = new Texture2D(2, 2, TextureFormat.ARGB32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
                tex.LoadImage(bytes);
                tex.filterMode = FilterMode.Point;
                return tex;
            }
        }

        /// <summary>A one-pixel light outline round the picture (as the menu's own objects get on hover).</summary>
        private static Texture2D Outline(Texture2D art)
        {
            int w = art.width + 2, h = art.height + 2;
            var t = new Texture2D(w, h, TextureFormat.ARGB32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
            var src = art.GetPixels32();
            bool Solid(int x, int y) => x >= 0 && y >= 0 && x < art.width && y < art.height && src[y * art.width + x].a > 128;
            var line = new Color32(232, 230, 246, 255);
            var clear = new Color32(0, 0, 0, 0);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int ax = x - 1, ay = y - 1;
                    bool edge = !Solid(ax, ay) && (Solid(ax - 1, ay) || Solid(ax + 1, ay) || Solid(ax, ay - 1) || Solid(ax, ay + 1));
                    t.SetPixel(x, y, edge ? line : clear);
                }
            t.Apply();
            return t;
        }

        /// <summary>A soft elliptical shadow (the menu's objects sit in soft shadows).</summary>
        private static Texture2D Shadow()
        {
            const int w = 64, h = 16;
            var t = new Texture2D(w, h, TextureFormat.ARGB32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float dx = (x + 0.5f - w / 2f) / (w / 2f), dy = (y + 0.5f - h / 2f) / (h / 2f);
                    float a = Mathf.Clamp01(1f - Mathf.Sqrt(dx * dx + dy * dy));
                    t.SetPixel(x, y, new Color(1f, 1f, 1f, a * a));
                }
            t.Apply();
            return t;
        }

        /// <summary>Hover and click, the game's way (AdaptiveButton): glow by distance, label inside, click inside.</summary>
        internal sealed class WalkieItem : MonoBehaviour
        {
            internal Image Overlay;
            internal TextMeshProUGUI Label;
            internal GameObject LabelRoot;
            internal AdaptiveButton Radio;
            private RectTransform _rect;

            private void Start() => _rect = (RectTransform)transform;

            private void OnDestroy()
            {
                if (LabelRoot != null) Destroy(LabelRoot);
            }

            private void Update()
            {
                if (_rect == null || Overlay == null || Label == null) return;
                float dist = Distance();
                bool blocked = CoopWindow.IsOpen || UiMouse.Over;
                try { blocked |= Radio != null && Radio.overlayActive; } catch { /* the menu is closing */ }
                if (blocked) dist = 99999f;
                float a = Mathf.Max(1f - dist * 0.005f, 0.025f);
                Overlay.color = Color.Lerp(Overlay.color, new Color(1f, 1f, 1f, a), Time.unscaledDeltaTime * 3f);
                Label.color = Color.Lerp(Label.color, new Color(1f, 1f, 1f, dist <= 0f ? 1f : 0f), Time.unscaledDeltaTime * 3f);
                if (Input.GetMouseButtonDown(0) && dist <= 0f)
                {
                    try { PlayerCamera.PlayUISound("click"); } catch { /* no sound */ }
                    CoopWindow.Show();
                    Log.Info("[ui] co-op menu opened from the walkie-talkie");
                }
            }

            private float Distance()
            {
                RectTransformUtility.ScreenPointToLocalPointInRectangle(_rect, Input.mousePosition, null, out var p);
                var r = _rect.rect;
                if (r.Contains(p)) return 0f;
                return Vector2.Distance(p, new Vector2(Mathf.Clamp(p.x, r.xMin, r.xMax), Mathf.Clamp(p.y, r.yMin, r.yMax)));
            }
        }
    }

    /// <summary>The game's menu objects ignore clicks that land on the co-op window.</summary>
    [HarmonyPatch(typeof(AdaptiveButton), "Clicked")]
    internal static class MenuClickThroughPatch
    {
        private static bool Prefix() => !UiMouse.Over;
    }
}
