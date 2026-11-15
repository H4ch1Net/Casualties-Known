using System.Collections.Generic;
using CCoop.Plugin.Runtime;
using UnityEngine;
using UnityEngine.UI;

namespace CCoop.Plugin.UI
{
    /// <summary>
    /// The game's run settings, in the lobby: its presets as the cards it shows (the same
    /// pictures and names), then every custom setting as the game lists them, with the
    /// game's own words. The host edits the run being set up (the game's
    /// PreRunScript.runSettings, so its own screen agrees); everyone else sees the host's,
    /// read only. In a run, the run's settings, read only.
    /// </summary>
    internal static class RunSetup
    {
        private const int Custom = 6;
        private static readonly Color Selected = new Color(1f, 0.69f, 0.12f);

        private static List<Sprite> _art;
        private static int _artScene = -1;
        private static GUIStyle _cardName;

        /// <summary>Host on the menu: edit. Otherwise shows <paramref name="values"/> read only.</summary>
        internal static void Draw(float width, bool editable, Dictionary<string, object> values)
        {
            if (values == null) { GUILayout.Label(Lang.T("Waiting for the host's run settings..."), GUI.skin.label); return; }
            var pre = PreRunScript.instance;
            if (editable && pre == null) editable = false;
            if (_cardName == null)
                _cardName = new GUIStyle(GUI.skin.label) { fontSize = UiSkin.Head, alignment = TextAnchor.MiddleCenter, wordWrap = false, clipping = TextClipping.Clip };

            int current = editable ? pre.currentPreset : Matching(values);
            Cards(width, editable, current);
            GUILayout.Space(6);

            bool was = GUI.enabled;
            GUI.enabled = was && editable;
            foreach (var def in RunSettings.settingTypes)
            {
                if (!string.IsNullOrEmpty(def.divider)) GameRows.Header(Game("rundiv" + def.divider, def.divider));
                if (!values.TryGetValue(def.name, out var raw)) continue;
                string label = Game("runset" + def.name, def.name);
                string tip = Game("runset" + def.name + "dsc", null);
                object next = raw;
                switch (def)
                {
                    case RunSettingBool _:
                        next = GameRows.Toggle(label, raw is bool b && b, tip);
                        break;
                    case RunSettingDropdown dd:
                        var names = new string[dd.choices.Length];
                        for (int i = 0; i < names.Length; i++) names[i] = Game("runset" + def.name + dd.choices[i], dd.choices[i]);
                        next = GameRows.Choice("run." + def.name, label, raw is int ix ? ix : 0, names, tip);
                        break;
                    case RunSettingFloat f:
                        float x = raw is float fl ? fl : 0f;
                        float y = GameRows.Slider(label, x, f.limits.min, f.limits.max, Shown(x) + f.postfix, tip);
                        // Only a moved slider changes the value (a value off the rounding grid stays as set).
                        if (!Mathf.Approximately(x, y)) next = f.wholeNum ? Mathf.Round(y) : Mathf.Round(y * 100f) / 100f;
                        break;
                }
                if (editable && !Equals(next, raw))
                {
                    var v = RunPresets.Coerce(def.name, next);
                    if (v != null)
                    {
                        pre.runSettings[def.name] = v;
                        pre.currentPreset = Custom;
                        try { pre.UpdateAllSettingDisplays(); } catch { /* the game's screen isn't built */ }
                    }
                }
            }
            GUI.enabled = was;
        }

        /// <summary>The game's number format for a setting's value.</summary>
        private static string Shown(float x) => x >= 10f || x < 0f ? x.ToString("0.#") : x.ToString("0.##");

        private static string Game(string key, string fallback)
        {
            try
            {
                string s = Locale.GetOther(key);
                if (!string.IsNullOrEmpty(s) && s != key) return s;
            }
            catch { /* no language loaded */ }
            return fallback;
        }

        /// <summary>The preset cards: the game's pictures, the selected one framed in amber.</summary>
        private static void Cards(float width, bool editable, int current)
        {
            var presets = RunSettings.presets;
            int n = presets.Count + 1;
            float gap = 6f;
            float cw = Mathf.Floor((width - gap * (n - 1)) / n);
            float ch = Mathf.Round(cw * 1.05f);
            var row = GUILayoutUtility.GetRect(width, ch, GUILayout.Width(width), GUILayout.Height(ch));
            Art();
            for (int i = 0; i < n; i++)
            {
                var r = new Rect(row.x + i * (cw + gap), row.y, cw, ch);
                bool custom = i == presets.Count;
                bool on = custom ? current == Custom || current < 0 : current == i;
                string name = custom ? Lang.T("Custom") : Game("preset" + presets[i].presetName, presets[i].presetName);
                if (Event.current.type == EventType.Repaint)
                {
                    Fill(r, new Color(1f, 1f, 1f, 0.05f));
                    if (!custom && _art != null && i < _art.Count && _art[i] != null) DrawSprite(new Rect(r.x + 4f, r.y + 4f, r.width - 8f, r.height - 34f), _art[i]);
                    else if (custom) GUI.Label(new Rect(r.x, r.y, r.width, r.height - 30f), "...", _cardName);
                    GUI.Label(new Rect(r.x + 2f, r.yMax - 32f, r.width - 4f, 30f), name, UiSkin.Fit(_cardName, name));
                    Border(r, on ? Selected : new Color(1f, 1f, 1f, 0.5f), on ? 3f : 1f);
                    if (editable && !custom && r.Contains(Event.current.mousePosition)) Fill(r, new Color(1f, 1f, 1f, 0.06f));
                }
                if (editable && !custom && GUI.Button(r, GUIContent.none, GUIStyle.none))
                {
                    try
                    {
                        PreRunScript.instance.ApplyPreset(presets[i].presetName);
                        PlayerCamera.PlayUISound("click");
                    }
                    catch (System.Exception e) { Log.Warn("[ui] preset: " + e.Message); }
                }
            }
        }

        /// <summary>Which preset these values are (over the normal one), or -1.</summary>
        private static int Matching(Dictionary<string, object> values)
        {
            try
            {
                var normal = RunSettings.GetPreset("normal").presetValues;
                for (int i = 0; i < RunSettings.presets.Count; i++)
                {
                    var p = RunSettings.presets[i].presetValues;
                    bool same = true;
                    foreach (var kv in normal)
                    {
                        var want = p.TryGetValue(kv.Key, out var o) ? o : kv.Value;
                        if (!values.TryGetValue(kv.Key, out var have) || !Same(want, have)) { same = false; break; }
                    }
                    if (same) return i;
                }
            }
            catch { /* the game's presets aren't readable */ }
            return -1;
        }

        private static bool Same(object a, object b)
        {
            if (a is float fa && b is float fb) return Mathf.Abs(fa - fb) < 0.001f;
            return Equals(a, b);
        }

        /// <summary>The preset pictures, from the game's run settings screen (one card per preset, in order).</summary>
        private static void Art()
        {
            var pre = PreRunScript.instance;
            int scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
            if (_art != null && _artScene == scene) return;
            if (pre == null || pre.presetRects == null) return;
            _artScene = scene;
            _art = new List<Sprite>();
            foreach (var rect in pre.presetRects)
            {
                Sprite best = null;
                float area = 0f;
                if (rect != null)
                    foreach (var img in rect.GetComponentsInChildren<Image>(true))
                    {
                        // The card's picture is its "Art" image; the frame around it is a sliced block.
                        if (img.sprite == null || img.type == Image.Type.Sliced) continue;
                        if (img.name == "Art") { best = img.sprite; break; }
                        var s = img.rectTransform.rect.size;
                        if (s.x * s.y > area) { area = s.x * s.y; best = img.sprite; }
                    }
                _art.Add(best);
            }
        }

        private static void DrawSprite(Rect r, Sprite sp)
        {
            var tex = sp.texture;
            if (tex == null) return;
            var tr = sp.textureRect;
            float k = Mathf.Min(r.width / tr.width, r.height / tr.height);
            var at = new Rect(r.center.x - tr.width * k / 2f, r.center.y - tr.height * k / 2f, tr.width * k, tr.height * k);
            GUI.DrawTextureWithTexCoords(at, tex, new Rect(tr.x / tex.width, tr.y / tex.height, tr.width / tex.width, tr.height / tex.height));
        }

        private static void Fill(Rect r, Color c)
        {
            var old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = old;
        }

        private static void Border(Rect r, Color c, float w) =>
            GUI.DrawTexture(r, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f, c, w, 0f);
    }
}
