using UnityEngine;

namespace CCoop.Plugin.UI
{
    /// <summary>A two-line caption at the bottom centre of the screen (spectating, sleeping).</summary>
    internal static class Overlay
    {
        private static GUIStyle _top, _bottom;

        /// <summary>Row 0 sits at the bottom; higher rows stack above it.</summary>
        internal static void Bottom(string top, string bottom, int row = 0)
        {
            if (_top == null)
            {
                _top = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = UiSkin.Head, wordWrap = false };
                _top.normal.textColor = UiSkin.Ink;
                _bottom = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = UiSkin.Small, wordWrap = true };
                _bottom.normal.textColor = UiSkin.Dim;
            }
            float k = CoopWindow.Scale;
            var old = GUI.matrix;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(k, k, 1f));
            try
            {
                float w = Screen.width / k, h = Screen.height / k;
                var box = new Rect(w / 2f - 280f, h - 96f - row * 70f, 560f, 62f);
                GUI.Box(box, GUIContent.none);
                GUI.Label(new Rect(box.x, box.y + 6f, box.width, 26f), top, UiSkin.Fit(_top, top));
                if (!string.IsNullOrEmpty(bottom)) GUI.Label(new Rect(box.x + 8f, box.y + 33f, box.width - 16f, 24f), bottom, UiSkin.Fit(_bottom, bottom));
            }
            finally
            {
                GUI.matrix = old;
            }
        }
    }
}
