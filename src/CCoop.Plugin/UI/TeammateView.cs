using System;
using System.Collections.Generic;
using CCoop.Plugin.Protocol;
using CCoop.Plugin.Runtime;
using UnityEngine;
using UnityEngine.UI;

namespace CCoop.Plugin.UI
{
    /// <summary>
    /// A teammate's body in the game's own health view. The figure is a copy of the
    /// game's real one (the WoundView's limb images, same sprites, same layout, same
    /// place and size on screen), on a canvas of the mod's own with the game's canvas
    /// scaling. Every frame it is coloured by the game's own rules (WoundView.UpdateView),
    /// fed from the teammate's synced body instead of the local one:
    ///
    ///  * outline white -> red with skin (muscle on a prosthetic), the fill black -> red
    ///    with muscle, blinking under 10; a prosthetic grey at half alpha; a lost limb gone;
    ///  * the top-to-bottom wipe as it opens, the shake with pain, the flash on hover;
    ///  * the icons beside each limb, rebuilt every 0.1 s in the game's order, spreading
    ///    out for the limb you look at; splint and tourniquet marks on the limb.
    ///
    /// Left out on purpose: the green read-outs (the chip's, and only its owner's) and
    /// anything the game itself doesn't show for a body.
    /// </summary>
    internal sealed class TeammateView : MonoBehaviour
    {
        private static TeammateView _view;
        private static float _retryAt;

        private RectTransform _root;
        private Image[] _limbs;
        private Image[] _fills;
        private Vector2[] _orig;
        private Sprite[] _icons, _heads;
        private Sprite _head0, _headFill0, _splint, _tourniquet;
        private Gradient _bleed;
        private Transform _iconParent;
        private int _iconSibling;
        private readonly List<GameObject> _iconObjs = new List<GameObject>();
        private readonly Dictionary<int, GameObject> _marks = new Dictionary<int, GameObject>();
        private float[] _flash, _lerp;
        private float _timeOpen, _checkTime;
        private int _shownCharacter = -1;

        internal static byte Teammate;
        internal static int Selected = -1;
        internal static int Hovered = -1;

        /// <summary>The figure is up.</summary>
        internal static bool Showing => _view != null && _view.gameObject.activeSelf;

        /// <summary>Opens (or keeps open) the view for this teammate. False if the game's view can't be borrowed.</summary>
        internal static bool Show(byte id)
        {
            if (_view == null && !Build()) return false;
            if (!_view.gameObject.activeSelf || Teammate != id)
            {
                Teammate = id;
                Selected = -1;
                _view._timeOpen = 0f;
                _view.gameObject.SetActive(true);
            }
            return true;
        }

        internal static void Hide()
        {
            if (_view != null && _view.gameObject.activeSelf) _view.gameObject.SetActive(false);
            Hovered = -1;
        }

        /// <summary>Screen rectangle (GUI coordinates) the figure covers, for the click guard.</summary>
        internal static Rect ScreenArea { get; private set; }

        private static bool Build()
        {
            if (Time.unscaledTime < _retryAt) return false;
            _retryAt = Time.unscaledTime + 3f;
            try
            {
                WoundView wv = WoundView.view;
                if (wv == null && PlayerCamera.main != null && PlayerCamera.main.woundView != null) wv = PlayerCamera.main.woundView.GetComponentInChildren<WoundView>(true);
                if (wv == null || wv.limbImages == null || wv.limbImages.Length == 0 || wv.limbImages[0] == null) return false;
                var gameCanvas = wv.GetComponentInParent<Canvas>(true);
                if (gameCanvas != null) gameCanvas = gameCanvas.rootCanvas;

                // Our own canvas, scaled as the game's is.
                var cgo = new GameObject("CasualtiesCoop.TeammateView", typeof(RectTransform));
                var canvas = cgo.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = (gameCanvas != null ? gameCanvas.sortingOrder : 0) + 5;
                var scaler = cgo.AddComponent<CanvasScaler>();
                var gameScaler = gameCanvas != null ? gameCanvas.GetComponent<CanvasScaler>() : null;
                if (gameScaler != null)
                {
                    scaler.uiScaleMode = gameScaler.uiScaleMode;
                    scaler.referenceResolution = gameScaler.referenceResolution;
                    scaler.screenMatchMode = gameScaler.screenMatchMode;
                    scaler.matchWidthOrHeight = gameScaler.matchWidthOrHeight;
                    scaler.scaleFactor = gameScaler.scaleFactor;
                    scaler.referencePixelsPerUnit = gameScaler.referencePixelsPerUnit;
                }

                // Copy the game's view while it can't wake up (under an inactive holder), so
                // none of its scripts run; then keep only the body.
                var holder = new GameObject("CasualtiesCoop.Holder");
                holder.SetActive(false);
                var clone = Instantiate(wv.gameObject, holder.transform);
                var root = clone.transform;
                var paths = new List<int[]>();
                foreach (var img in wv.limbImages) paths.Add(img != null ? PathFrom(wv.transform, img.transform) : null);
                var containerPath = PathFrom(wv.transform, wv.limbImages[0].transform.parent);

                foreach (var mb in clone.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (mb == null) continue;
                    bool game = mb.GetType().Assembly == typeof(WoundView).Assembly;
                    if (game || mb is Selectable || mb is UnityEngine.EventSystems.EventTrigger) DestroyImmediate(mb);
                }

                var limbs = new Image[wv.limbImages.Length];
                var fills = new Image[limbs.Length];
                for (int k = 0; k < limbs.Length; k++)
                {
                    if (paths[k] == null) continue;
                    var t = Resolve(root, paths[k]);
                    limbs[k] = t != null ? t.GetComponent<Image>() : null;
                    if (limbs[k] == null) continue;
                    limbs[k].raycastTarget = false;
                    // Keep the fill (child 0); drop the local player's own marks (splints...).
                    for (int c = limbs[k].transform.childCount - 1; c >= 1; c--) DestroyImmediate(limbs[k].transform.GetChild(c).gameObject);
                    if (limbs[k].transform.childCount > 0)
                    {
                        var f = limbs[k].transform.GetChild(0);
                        fills[k] = f.GetComponent<Image>();
                        for (int c = f.childCount - 1; c >= 0; c--) f.GetChild(c).gameObject.SetActive(false);
                        if (fills[k] != null) fills[k].raycastTarget = false;
                    }
                }

                // Only the path down to the body stays: panels, read-outs and buttons go.
                var container = Resolve(root, containerPath);
                for (var t = container; t != null && t != holder.transform; t = t.parent)
                {
                    var parent = t.parent;
                    if (parent == null || parent == holder.transform) break;
                    for (int c = 0; c < parent.childCount; c++)
                        if (parent.GetChild(c) != t) parent.GetChild(c).gameObject.SetActive(false);
                    foreach (var g in parent.GetComponents<Graphic>()) g.enabled = false;
                }
                for (int c = 0; c < container.childCount; c++)
                {
                    var ch = container.GetChild(c);
                    if (Array.IndexOf(limbs, ch.GetComponent<Image>()) < 0) ch.gameObject.SetActive(false);
                }
                foreach (var g in container.GetComponents<Graphic>()) g.enabled = false;
                foreach (var cg in clone.GetComponentsInChildren<CanvasGroup>(true)) cg.alpha = 1f;
                for (var t = container; t != null && t != holder.transform; t = t.parent) t.localScale = Vector3.one;

                clone.transform.SetParent(cgo.transform, false);
                clone.SetActive(true);  // copied from the game's view while it was shut
                for (var t = container; t != null && t != clone.transform.parent; t = t.parent) t.gameObject.SetActive(true);
                Destroy(holder);

                var view = cgo.AddComponent<TeammateView>();
                view._root = (RectTransform)clone.transform;
                view._limbs = limbs;
                view._fills = fills;
                view._orig = wv.origPositions != null && wv.origPositions.Length == limbs.Length ? (Vector2[])wv.origPositions.Clone() : null;
                view._icons = wv.icons;
                view._heads = wv.headCharSprites;
                view._bleed = wv.bleedGradient;
                view._splint = Resources.Load<Sprite>("Special/splintIcon");
                view._tourniquet = Resources.Load<Sprite>("Special/tourniquetIcon");
                view._iconParent = clone.transform;
                view._iconSibling = container.GetSiblingIndex() + 1;
                view._flash = new float[limbs.Length];
                view._lerp = new float[limbs.Length];
                // The default head (the game swaps it in Start for the other two characters).
                int localChar = GameInfo.LocalBody != null ? GameInfo.LocalBody.charType : 0;
                view._head0 = localChar == 0 || view._heads == null || view._heads.Length < 2 ? limbs[0]?.sprite : view._heads[0];
                view._headFill0 = localChar == 0 || view._heads == null || view._heads.Length < 2 ? fills[0]?.sprite : view._heads[1];
                cgo.SetActive(false);
                _view = view;
                Log.Info("[ui] teammate view from the game's health view: " + limbs.Length + " limbs, canvas " + (gameCanvas != null ? gameCanvas.name : "?") +
                         (gameScaler != null ? " " + gameScaler.uiScaleMode + " " + gameScaler.referenceResolution : ""));
                return true;
            }
            catch (Exception e)
            {
                Log.Warn("[ui] couldn't copy the game's health view: " + e.Message);
                return false;
            }
        }

        private static int[] PathFrom(Transform root, Transform t)
        {
            var path = new List<int>();
            for (var x = t; x != null && x != root; x = x.parent) path.Insert(0, x.GetSiblingIndex());
            return path.ToArray();
        }

        private static Transform Resolve(Transform root, int[] path)
        {
            var t = root;
            foreach (int i in path)
            {
                if (t == null || i >= t.childCount) return null;
                t = t.GetChild(i);
            }
            return t;
        }

        private void OnDestroy()
        {
            if (_view == this) _view = null;
        }

        private void Update()
        {
            var v = Coop.Get<Sync.VitalsSync>()?.Of(Teammate);
            if (v == null || !Coop.WorldLive) { gameObject.SetActive(false); return; }
            SetHead(Coop.Player(Teammate).Character);
            _timeOpen += Time.unscaledDeltaTime;
            Hover(v);

            // WoundView.UpdateView, for the teammate.
            for (int i = 0; i < _lerp.Length; i++)
            {
                _lerp[i] = Mathf.Lerp(_lerp[i], (Selected == i || Hovered == i) ? 1f : 0f, Time.unscaledDeltaTime * 7f);
                _flash[i] = Mathf.Lerp(_flash[i], 0f, Time.unscaledDeltaTime * 4f);
            }
            var area = new Rect();
            bool first = true;
            for (int k = 0; k < _limbs.Length && k < v.Limbs.Count; k++)
            {
                var img = _limbs[k];
                if (img == null) continue;
                var l = v.Limbs[k];
                bool prosthetic = (l.Signs & LimbSigns.Prosthetic) != 0;
                bool gone = (l.Signs & LimbSigns.Missing) != 0;
                var baseColor = prosthetic ? new Color(0.5f, 0.5f, 0.5f, 0.5f) : new Color(0f, 0f, 0f, 1f);
                float num4 = _flash[k] * 0.6f;
                float value = Mathf.Clamp01(((_timeOpen + 0.15f) * 12f - img.transform.position.y / Screen.height * 1080f * 0.01f) * 0.25f);
                if (gone) value = 0f;
                value *= baseColor.a;
                float h = prosthetic ? l.Muscle : l.Skin;
                img.color = new Color(1f - num4, h * 0.01f - num4, h * 0.01f - num4, value);
                if (_fills[k] != null)
                {
                    _fills[k].color = new Color(Mathf.Lerp(baseColor.r, 1f, 1f - l.Muscle * 0.01f), baseColor.g, baseColor.b, value);
                    if (l.Muscle < 10) _fills[k].color = new Color(Mathf.PingPong(Time.unscaledTime * 4f, 1f), 0f, 0f, value);
                }
                if (_orig != null)
                    img.rectTransform.anchoredPosition = _orig[k] + new Vector2(UnityEngine.Random.Range(-l.Pain * 0.02f, l.Pain * 0.02f), UnityEngine.Random.Range(-l.Pain * 0.02f, l.Pain * 0.02f));
                Marks(k, img, l, gone);
                if (!gone)
                {
                    var r = ScreenRect(img.rectTransform);
                    area = first ? r : Rect.MinMaxRect(Mathf.Min(area.xMin, r.xMin), Mathf.Min(area.yMin, r.yMin), Mathf.Max(area.xMax, r.xMax), Mathf.Max(area.yMax, r.yMax));
                    first = false;
                }
            }
            ScreenArea = area;

            _checkTime += Time.unscaledDeltaTime;
            if (_checkTime > 0.1f)
            {
                _checkTime = 0f;
                Icons(v);
            }
        }

        /// <summary>The limb under the mouse (the game flashes a limb as you move onto it).</summary>
        private void Hover(Vitals v)
        {
            int hover = -1;
            float best = float.MaxValue;
            if (!UiMouse.Over)
                for (int k = 0; k < _limbs.Length && k < v.Limbs.Count; k++)
                {
                    var img = _limbs[k];
                    if (img == null || (v.Limbs[k].Signs & LimbSigns.Missing) != 0) continue;
                    if (!RectTransformUtility.RectangleContainsScreenPoint(img.rectTransform, Input.mousePosition, null)) continue;
                    var size = img.rectTransform.rect.size;
                    float a = size.x * size.y;
                    if (a < best) { best = a; hover = k; }
                }
            if (hover >= 0 && hover != Hovered) _flash[hover] = 1f;
            Hovered = hover;
            if (hover >= 0 && Input.GetMouseButtonDown(0)) Selected = Selected == hover ? -1 : hover;
        }

        private void SetHead(int character)
        {
            if (character == _shownCharacter || _limbs[0] == null) return;
            _shownCharacter = character;
            Sprite head = _head0, fill = _headFill0;
            if (_heads != null && character >= 1 && character <= 2 && _heads.Length >= character * 2 + 2)
            {
                head = _heads[character * 2];
                fill = _heads[character * 2 + 1];
            }
            if (head != null) { _limbs[0].sprite = head; _limbs[0].SetNativeSize(); }
            if (fill != null && _fills[0] != null) { _fills[0].sprite = fill; _fills[0].SetNativeSize(); }
        }

        /// <summary>A splint or tourniquet drawn on the limb, as the game adds them (AddImageToLimb).</summary>
        private void Marks(int k, Image limb, LimbVitals l, bool gone)
        {
            Mark(k * 2, limb, _splint, !gone && (l.Signs & LimbSigns.Splinted) != 0, Color.white);
            Mark(k * 2 + 1, limb, _tourniquet, !gone && (l.Signs & LimbSigns.Tourniquet) != 0, Color.white);
        }

        private void Mark(int key, Image limb, Sprite sprite, bool on, Color color)
        {
            _marks.TryGetValue(key, out var go);
            if (!on || sprite == null) { if (go != null) go.SetActive(false); return; }
            if (go == null)
            {
                go = new GameObject(sprite.name, typeof(RectTransform), typeof(Image));
                go.transform.SetParent(limb.rectTransform, false);
                var rt = (RectTransform)go.transform;
                rt.anchoredPosition = Vector2.zero;
                var img = go.GetComponent<Image>();
                img.sprite = sprite;
                img.raycastTarget = false;
                img.SetNativeSize();
                _marks[key] = go;
            }
            go.SetActive(true);
            go.GetComponent<Image>().color = new Color(color.r, color.g, color.b, limb.color.a);
        }

        /// <summary>The icons beside each limb, as WoundView.UpdateView makes them.</summary>
        private void Icons(Vitals v)
        {
            foreach (var o in _iconObjs) if (o != null) Destroy(o);
            _iconObjs.Clear();
            if (_icons == null || _icons.Length < 16) return;
            for (int l = 0; l < _limbs.Length && l < v.Limbs.Count; l++)
            {
                var lv = v.Limbs[l];
                if (_limbs[l] == null || (lv.Signs & LimbSigns.Missing) != 0) continue;
                bool prosthetic = (lv.Signs & LimbSigns.Prosthetic) != 0;
                float bleed = (lv.Signs & LimbSigns.BleedBlocked) != 0 ? 0f : lv.Bleed / 10f;
                var list = new List<(Sprite, Color)>();
                if (bleed > 1f)
                {
                    bool bandaged = (lv.Signs & LimbSigns.Bandaged) != 0;
                    var sp = bandaged ? (bleed > 20f ? _icons[15] : bleed > 8f ? _icons[14] : bleed > 3.75f ? _icons[13] : _icons[12])
                                      : (bleed > 20f ? _icons[8] : bleed > 8f ? _icons[0] : bleed > 3.75f ? _icons[9] : _icons[7]);
                    list.Add((sp, _bleed != null ? _bleed.Evaluate(bleed * 0.01f) : Color.red));
                }
                if (lv.Infection >= 25) list.Add((_icons[10], Color.Lerp(Color.white, new Color32(163, 0, 182, 255), lv.Infection * 0.01f)));
                if ((lv.Signs & LimbSigns.Broken) != 0) list.Add((prosthetic && _icons.Length > 17 ? _icons[17] : _icons[2], Color.white));
                if ((lv.Signs & LimbSigns.Dislocated) != 0) list.Add((prosthetic && _icons.Length > 16 ? _icons[16] : _icons[1], Color.white));
                if ((lv.Signs & LimbSigns.Shrapnel) != 0) list.Add((_icons[5], new Color32(173, 106, 54, 255)));
                for (int m = 0; m < list.Count; m++)
                {
                    float num6 = (m * 50f - (list.Count - 1) * 25f) * (_lerp[l] * 0.8f + 0.2f);
                    var vector = new Vector2(num6, num6 * 0.2f);
                    var go = new GameObject("icon", typeof(RectTransform), typeof(Image));
                    var img = go.GetComponent<Image>();
                    img.raycastTarget = false;
                    go.transform.SetParent(_iconParent);
                    go.GetComponent<RectTransform>().position = vector + (Vector2)_limbs[l].transform.position;
                    go.transform.localScale = Vector3.one;
                    go.transform.localRotation = Quaternion.identity;
                    img.sprite = list[m].Item1;
                    img.SetNativeSize();
                    img.color = list[m].Item2;
                    go.transform.SetSiblingIndex(Mathf.Min(_iconSibling, _iconParent.childCount - 1));
                    _iconObjs.Add(go);
                }
            }
        }

        private static Rect ScreenRect(RectTransform rt)
        {
            var c = new Vector3[4];
            rt.GetWorldCorners(c);
            float xMin = Mathf.Min(c[0].x, c[2].x), xMax = Mathf.Max(c[0].x, c[2].x);
            float yMin = Mathf.Min(c[0].y, c[2].y), yMax = Mathf.Max(c[0].y, c[2].y);
            return Rect.MinMaxRect(xMin, Screen.height - yMax, xMax, Screen.height - yMin);
        }
    }
}
