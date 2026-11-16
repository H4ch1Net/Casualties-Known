using UnityEngine;

namespace CCoop.Plugin.UI
{
    /// <summary>
    /// Two-line captions at the bottom centre of the screen (spectating, sleeping, carrying,
    /// waiting at the exit). Several at once stack up instead of covering each other, and
    /// none of them sits on the game's own alert line: the first goes under it, the rest
    /// above it.
    /// </summary>
    internal static class Overlay
    {
        private const float Gap = 8f, MinWidth = 560f, MaxWidth = 820f;

        private static GUIStyle _top, _bottom;
        private static int _pass = -1, _used;
        /// <summary>This pass: where the next caption's bottom edge goes.</summary>
        private static float _cursor;

        internal static void Bottom(string top, string bottom)
        {
            if (_top == null)
            {
                _top = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = UiSkin.Head, wordWrap = false };
                _top.normal.textColor = UiSkin.Ink;
                _bottom = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = UiSkin.Small, wordWrap = true };
                _bottom.normal.textColor = UiSkin.Dim;
            }
            // Each module draws its own caption, in the same order every pass: the n-th
            // caption of a pass takes the n-th place.
            int pass = Time.frameCount * 32 + (int)Event.current.type;
            if (pass != _pass) { _pass = pass; _used = 0; }
            int place = _used++;

            float k = CoopWindow.Scale;
            var old = GUI.matrix;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(k, k, 1f));
            try
            {
                float w = Screen.width / k, h = Screen.height / k;
                var topStyle = UiSkin.Fit(_top, top);
                var bottomStyle = string.IsNullOrEmpty(bottom) ? null : UiSkin.Fit(_bottom, bottom);
                // As wide as the words need (within reason); the second line wraps if it must.
                float width = topStyle.CalcSize(new GUIContent(top)).x + 32f;
                if (bottomStyle != null) width = Mathf.Max(width, bottomStyle.CalcSize(new GUIContent(bottom)).x + 32f);
                width = Mathf.Min(Mathf.Clamp(width, MinWidth, MaxWidth), w - 40f);
                float bottomH = bottomStyle != null ? Mathf.Max(20f, bottomStyle.CalcHeight(new GUIContent(bottom), width - 16f)) : 0f;
                float height = bottomStyle != null ? 36f + bottomH + 6f : 40f;

                var box = new Rect(w / 2f - width / 2f, Top(place, height, h, k), width, height);
                GUI.Box(box, GUIContent.none);
                GUI.Label(new Rect(box.x, box.y + 6f, box.width, 26f), top, topStyle);
                if (bottomStyle != null) GUI.Label(new Rect(box.x + 8f, box.y + 34f, box.width - 16f, bottomH), bottom, bottomStyle);
            }
            finally
            {
                GUI.matrix = old;
            }
        }

        /// <summary>
        /// The top of the n-th caption, in the overlay's scaled units: the first under the
        /// game's alert line if it fits there, every other one above it.
        /// </summary>
        private static float Top(int place, float height, float h, float k)
        {
            float alertTop = AlertTop() / k, alertBottom = AlertBottom() / k;
            float bottomEdge = place == 0 ? h - 34f : _cursor;
            if (place == 0 && bottomEdge - height < alertBottom + Gap) bottomEdge = Mathf.Min(bottomEdge, alertTop - Gap);
            float top = bottomEdge - height;
            _cursor = Mathf.Min(top - Gap, alertTop - Gap);
            return top;
        }

        // ---- where the game writes its alerts (PlayerCamera.DoAlert): centred, 400 canvas
        // units below the middle of a 1080-tall canvas.

        private static float _alertTop, _alertBottom;
        private static int _alertFor = -1;

        private static float AlertTop()
        {
            Measure();
            return _alertTop;
        }

        private static float AlertBottom()
        {
            Measure();
            return _alertBottom;
        }

        private static void Measure()
        {
            int key = Screen.width * 10000 + Screen.height;
            if (key == _alertFor) return;
            float u = Screen.height / 1080f;
            _alertTop = Screen.height / 2f + 371f * u;
            _alertBottom = Screen.height / 2f + 429f * u;
            try
            {
                var back = PlayerCamera.main != null ? PlayerCamera.main.alertBack : null;
                // Measured only where ordinary alerts go (an important one is mid-screen).
                if (back == null || back.rectTransform.anchoredPosition.y > -1f) return;
                var corners = new Vector3[4];
                back.rectTransform.GetWorldCorners(corners);
                var canvas = back.canvas != null ? back.canvas.rootCanvas : null;
                var cam = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
                float lo = RectTransformUtility.WorldToScreenPoint(cam, corners[0]).y;
                float hi = RectTransformUtility.WorldToScreenPoint(cam, corners[1]).y;
                if (hi <= lo) return;
                _alertTop = Screen.height - hi;
                _alertBottom = Screen.height - lo;
                _alertFor = key;
            }
            catch { /* keep the estimate */ }
        }
    }
}
