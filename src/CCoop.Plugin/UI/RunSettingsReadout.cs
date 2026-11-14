using System;
using System.Collections.Generic;
using System.Globalization;
using CCoop.Plugin.Runtime;

namespace CCoop.Plugin.UI
{
    /// <summary>
    /// The host's run settings in words, for players who can't open the host's
    /// run-settings screen: which of the game's presets they match, or what differs from
    /// "normal", using the game's own setting names.
    /// </summary>
    internal static class RunSettingsReadout
    {
        private static Dictionary<string, object> _cachedFor;
        private static List<string> _cached;

        internal static List<string> Lines(Dictionary<string, object> s)
        {
            if (s == null) return new List<string>();
            if (ReferenceEquals(s, _cachedFor) && _cached != null) return _cached;
            var lines = new List<string>();
            try
            {
                string match = null;
                foreach (var p in RunSettings.presets)
                    if (p?.presetValues != null && Same(p.presetValues, s)) { match = p.presetName; break; }
                if (match != null)
                {
                    lines.Add(Lang.F("The game's {0} settings.", Name("preset" + match, match)));
                }
                else
                {
                    var normal = RunSettings.GetPreset("normal")?.presetValues;
                    foreach (var kv in s)
                    {
                        if (normal != null && normal.TryGetValue(kv.Key, out var d) && Equal(d, kv.Value)) continue;
                        lines.Add(Name("runset" + kv.Key, kv.Key) + ": " + Value(kv.Key, kv.Value));
                    }
                    if (lines.Count == 0) lines.Add(Lang.T("The game's normal settings."));
                    else lines.Insert(0, Lang.T("Changed from normal:"));
                }
            }
            catch (Exception e)
            {
                Log.Debug("run settings readout: " + e.Message);
            }
            _cachedFor = s;
            _cached = lines;
            return lines;
        }

        private static bool Same(Dictionary<string, object> preset, Dictionary<string, object> s)
        {
            foreach (var kv in preset)
                if (!s.TryGetValue(kv.Key, out var v) || !Equal(kv.Value, v)) return false;
            return true;
        }

        private static bool Equal(object a, object b)
        {
            if (a == null || b == null) return a == b;
            if (a is bool ab && b is bool bb) return ab == bb;
            try { return Math.Abs(Convert.ToDouble(a, CultureInfo.InvariantCulture) - Convert.ToDouble(b, CultureInfo.InvariantCulture)) < 1e-4; }
            catch { return a.ToString() == b.ToString(); }
        }

        private static string Value(string key, object v)
        {
            if (v is bool b) return b ? Lang.T("on") : Lang.T("off");
            try
            {
                if (RunSettings.GetSetting(key) is RunSettingDropdown dd && dd.choices != null)
                {
                    int i = Convert.ToInt32(v, CultureInfo.InvariantCulture);
                    if (i >= 0 && i < dd.choices.Length) return Name("runset" + key + dd.choices[i], dd.choices[i]);
                }
                if (RunSettings.GetSetting(key) is RunSettingFloat f)
                {
                    double d = Convert.ToDouble(v, CultureInfo.InvariantCulture);
                    return (f.wholeNum ? d.ToString("F0", CultureInfo.InvariantCulture) : d.ToString("0.##", CultureInfo.InvariantCulture)) + (f.postfix ?? "");
                }
            }
            catch { /* a setting this build doesn't know */ }
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        /// <summary>The game's own text for a key, or the key itself if it has none.</summary>
        private static string Name(string key, string fallback)
        {
            try
            {
                var s = Locale.GetOther(key);
                if (!string.IsNullOrEmpty(s) && s != key) return s;
            }
            catch { /* no locale entry */ }
            return fallback;
        }
    }
}
