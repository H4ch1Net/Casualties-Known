using System.Collections.Generic;
using UnityEngine;

namespace CCoop.Plugin.UI
{
    /// <summary>
    /// An item's picture, as the game shows it in the world: the sprite of its prefab
    /// (items are prefabs in Resources, named by their id). Drawn into IMGUI rects.
    /// </summary>
    internal static class ItemIcons
    {
        private static readonly Dictionary<string, Sprite> Cache = new Dictionary<string, Sprite>();

        internal static Sprite Get(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            string key = id.Replace("(Clone)", "").Trim();
            if (Cache.TryGetValue(key, out var have)) return have;
            Sprite sprite = null;
            try
            {
                var prefab = Resources.Load<GameObject>(key);
                var sr = prefab != null ? prefab.GetComponentInChildren<SpriteRenderer>(true) : null;
                sprite = sr != null ? sr.sprite : null;
            }
            catch { /* not an item */ }
            Cache[key] = sprite;
            return sprite;
        }

        /// <summary>Draws the sprite as large as fits in <paramref name="r"/>, keeping its shape; false if there's none.</summary>
        internal static bool Draw(Rect r, string id, Color? tint = null)
        {
            var sp = Get(id);
            if (sp == null || sp.texture == null) return false;
            if (Event.current.type != EventType.Repaint) return true;
            var tr = sp.textureRect;
            float k = Mathf.Min(r.width / tr.width, r.height / tr.height);
            var at = new Rect(r.center.x - tr.width * k / 2f, r.center.y - tr.height * k / 2f, tr.width * k, tr.height * k);
            var tex = sp.texture;
            var old = GUI.color;
            if (tint.HasValue) GUI.color = tint.Value;
            GUI.DrawTextureWithTexCoords(at, tex, new Rect(tr.x / tex.width, tr.y / tex.height, tr.width / tex.width, tr.height / tex.height));
            GUI.color = old;
            return true;
        }
    }
}
