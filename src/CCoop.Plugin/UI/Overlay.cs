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
                _top = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 18, fontStyle = FontStyle.Bold };
                _top.normal.textColor = Color.white;
                _bottom = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 13 };
                _bottom.normal.textColor = new Color(0.85f, 0.85f, 0.85f);
            }
            float k = CoopWindow.Scale;
            var old = GUI.matrix;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(k, k, 1f));
            try
            {
                float w = Screen.width / k, h = Screen.height / k;
                var box = new Rect(w / 2f - 280f, h - 96f - row * 70f, 560f, 62f);
                var c = GUI.color;
                GUI.color = new Color(0f, 0f, 0f, 0.55f);
                GUI.DrawTexture(box, Texture2D.whiteTexture);
                GUI.color = c;
                GUI.Label(new Rect(box.x, box.y + 6f, box.width, 26f), top, _top);
                if (!string.IsNullOrEmpty(bottom)) GUI.Label(new Rect(box.x, box.y + 34f, box.width, 22f), bottom, _bottom);
            }
            finally
            {
                GUI.matrix = old;
            }
        }
    }
}
