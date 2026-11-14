using CCoop.Plugin.Runtime;
using UnityEngine;

namespace CCoop.Plugin.UI
{
    /// <summary>
    /// Steam names come in every script. The co-op overlay (IMGUI, used only by the mod)
    /// draws with an operating-system font plus a fallback chain covering Latin, Cyrillic,
    /// Greek, Chinese, Japanese and Korean, so a name like "李明" or "ゆき" shows instead
    /// of empty boxes, on Windows and under Proton alike. The game's own UI is untouched.
    /// </summary>
    internal static class Fonts
    {
        private static readonly string[] Chain =
        {
            "Segoe UI", "Arial", "Microsoft YaHei", "Yu Gothic UI", "Malgun Gothic", "SimSun", "MS Gothic",
            "Noto Sans", "Noto Sans CJK SC", "Noto Sans CJK JP", "Noto Sans CJK KR", "DejaVu Sans", "Liberation Sans",
        };

        private static Font _font;
        private static bool _tried;

        /// <summary>Call at the start of every OnGUI that draws mod UI.</summary>
        internal static void Apply()
        {
            if (!_tried)
            {
                _tried = true;
                try
                {
                    _font = Font.CreateDynamicFontFromOSFont(Chain, 13);
                    Log.Info("[ui] overlay font: " + (_font != null ? string.Join(", ", _font.fontNames) : "built-in"));
                }
                catch (System.Exception e) { Log.Warn("[ui] no OS font: " + e.Message); }
            }
            if (_font != null && GUI.skin.font != _font) GUI.skin.font = _font;
        }
    }
}
