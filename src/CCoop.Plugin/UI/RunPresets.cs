using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using CCoop.Plugin.Runtime;
using UnityEngine;

namespace CCoop.Plugin.UI
{
    /// <summary>
    /// Co-op run presets: named sets of the game's own run settings, picked from the co-op
    /// window on the main menu. They write into the game's run-settings screen (as its
    /// "custom" choice does), so the host can still check and change everything there.
    /// A few come with the mod; players can save their own and share them as a line of
    /// text. Nothing here adds a mechanic: every value is one of the game's settings,
    /// clamped to the game's own limits.
    /// </summary>
    internal static class RunPresets
    {
        internal sealed class Preset
        {
            public string Name = "";
            public string About = "";
            public Dictionary<string, object> Values = new Dictionary<string, object>();
            public bool Saved;
        }

        private const string SharePrefix = "ccoop-run:";

        internal static readonly Preset[] Builtin =
        {
            Make(Lang.N("Cold snap"), Lang.N("Twenty degrees colder, and clothes hold less heat. Stay close: teammates warm each other."),
                 "temperatureoffset", -20f, "insulationmultiplier", 0.8f, "metabolismrate", 1.2f),
            Make(Lang.N("Glass bones"), Lang.N("Breaks hurt more, wounds bleed faster and heal slowly. Bring a medic."),
                 "fracturepain", 1.5f, "bleedrate", 1.4f, "healingrate", 0.6f, "fibrillationrate", 1.5f),
            Make(Lang.N("Field hospital"), Lang.N("Infections take hold faster, but everyone starts fully supplied and heals quicker."),
                 "infectionchance", 2f, "infectionspeed", 1.5f, "healingrate", 1.3f, "startingsupplies", 2),
            Make(Lang.N("Lean pickings"), Lang.N("Half the loot and more traders: share what you find and trade together."),
                 "baselootdensity", 0.5f, "lootmultiplier", 0.6f, "traderchance", 66f, "traderitemamount", 1.5f),
            Make(Lang.N("Long haul"), Lang.N("Twice the time per layer, slower hunger and slower rot, for long sessions."),
                 "timelimit", 120f, "metabolismrate", 0.8f, "itemdecayrate", 0.6f),
        };

        private static Preset Make(string name, string about, params object[] kv)
        {
            var p = new Preset { Name = name, About = about };
            for (int i = 0; i + 1 < kv.Length; i += 2) p.Values[(string)kv[i]] = kv[i + 1];
            return p;
        }

        /// <summary>The pre-run screen is up: its settings are the next run's.</summary>
        internal static bool CanApply => GameInfo.OnMainMenu && PreRunScript.instance != null && PreRunScript.instance.runSettings != null;

        // ------------------------------------------------------------------ applying

        internal static bool Apply(Preset p)
        {
            if (!CanApply || p == null) return false;
            var screen = PreRunScript.instance;
            try
            {
                screen.ApplyPreset("normal");
                foreach (var kv in p.Values)
                {
                    var v = Coerce(kv.Key, kv.Value);
                    if (v != null) screen.runSettings[kv.Key] = v;
                }
                screen.currentPreset = CustomIndex;
                screen.UpdateAllSettingDisplays();
            }
            catch (Exception e)
            {
                Log.Warn("[presets] could not apply " + p.Name + ": " + e.Message);
                return false;
            }
            CoopRuntime.Notice(Lang.F("Run settings: {0}", p.Saved ? p.Name : Lang.T(p.Name)));
            Log.Info("[presets] applied " + p.Name + " (" + p.Values.Count + " settings)");
            return true;
        }

        /// <summary>The game's "custom" button on its preset list (after its six presets).</summary>
        private static int CustomIndex => RunSettings.presets.Count;

        /// <summary>A value checked against the game's own setting: right type, within its limits. Null if unknown.</summary>
        internal static object Coerce(string key, object value)
        {
            RunSetting def = null;
            foreach (var s in RunSettings.settingTypes) if (s.name == key) { def = s; break; }
            if (def == null || value == null) return null;
            try
            {
                switch (def)
                {
                    case RunSettingBool _:
                        if (value is bool b) return b;
                        var str = value.ToString().Trim().ToLowerInvariant();
                        return str == "true" || str == "1" || str == "on";
                    case RunSettingDropdown dd:
                        int i = Convert.ToInt32(value, CultureInfo.InvariantCulture);
                        return Mathf.Clamp(i, 0, (dd.choices?.Length ?? 1) - 1);
                    case RunSettingFloat f:
                        float x = Convert.ToSingle(value, CultureInfo.InvariantCulture);
                        if (float.IsNaN(x) || float.IsInfinity(x)) return null;
                        x = Mathf.Clamp(x, f.limits.min, f.limits.max);
                        if (f.wholeNum) x = Mathf.Round(x);
                        return x;
                }
            }
            catch { /* not a number */ }
            return null;
        }

        /// <summary>Which preset the next run's settings match, if any.</summary>
        internal static string Matching()
        {
            if (!CanApply) return null;
            var cur = PreRunScript.instance.runSettings;
            foreach (var p in All())
                if (Matches(p, cur)) return p.Name;
            return null;
        }

        private static bool Matches(Preset p, Dictionary<string, object> cur)
        {
            var normal = RunSettings.GetPreset("normal").presetValues;
            foreach (var kv in normal)
            {
                object want = p.Values.TryGetValue(kv.Key, out var pv) ? Coerce(kv.Key, pv) : kv.Value;
                if (!cur.TryGetValue(kv.Key, out var have) || !Same(want, have)) return false;
            }
            return true;
        }

        private static bool Same(object a, object b)
        {
            if (a == null || b == null) return a == b;
            if (a is bool ab && b is bool bb) return ab == bb;
            try { return Math.Abs(Convert.ToDouble(a, CultureInfo.InvariantCulture) - Convert.ToDouble(b, CultureInfo.InvariantCulture)) < 1e-4; }
            catch { return a.ToString() == b.ToString(); }
        }

        // ------------------------------------------------------------------ saving and sharing

        private static List<Preset> _saved;

        private static string FilePath => Path.Combine(Path.Combine(Application.persistentDataPath, "CasualtiesCoop"), "run-presets.txt");

        internal static List<Preset> SavedPresets
        {
            get
            {
                if (_saved != null) return _saved;
                _saved = new List<Preset>();
                try
                {
                    if (File.Exists(FilePath))
                        foreach (var line in File.ReadAllLines(FilePath))
                        {
                            var p = Decode(line);
                            if (p != null) { p.Saved = true; _saved.Add(p); }
                        }
                }
                catch (Exception e) { Log.Warn("[presets] could not read: " + e.Message); }
                return _saved;
            }
        }

        internal static IEnumerable<Preset> All()
        {
            foreach (var p in Builtin) yield return p;
            foreach (var p in SavedPresets) yield return p;
        }

        /// <summary>The next run's settings, as what differs from normal.</summary>
        internal static Preset Current(string name)
        {
            if (!CanApply) return null;
            var p = new Preset { Name = string.IsNullOrEmpty(name) ? "Shared" : name };
            var normal = RunSettings.GetPreset("normal").presetValues;
            foreach (var kv in PreRunScript.instance.runSettings)
                if (!normal.TryGetValue(kv.Key, out var d) || !Same(d, kv.Value)) p.Values[kv.Key] = kv.Value;
            return p;
        }

        internal static string Encode(Preset p)
        {
            var sb = new StringBuilder(SharePrefix).Append(Clean(p.Name)).Append('|');
            bool first = true;
            foreach (var kv in p.Values)
            {
                if (!first) sb.Append(';');
                first = false;
                sb.Append(kv.Key).Append('=').Append(kv.Value is IFormattable f ? f.ToString(null, CultureInfo.InvariantCulture) : kv.Value.ToString().ToLowerInvariant());
            }
            return sb.ToString();
        }

        /// <summary>A shared preset line; unknown settings are dropped and values clamped. Null if it isn't one.</summary>
        internal static Preset Decode(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            text = text.Trim();
            if (!text.StartsWith(SharePrefix, StringComparison.OrdinalIgnoreCase)) return null;
            text = text.Substring(SharePrefix.Length);
            int bar = text.IndexOf('|');
            if (bar < 0) return null;
            var p = new Preset { Name = Clean(text.Substring(0, bar)) };
            if (p.Name.Length == 0) p.Name = "Shared";
            foreach (var part in text.Substring(bar + 1).Split(';'))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                string key = part.Substring(0, eq).Trim();
                var v = Coerce(key, part.Substring(eq + 1).Trim());
                if (v != null) p.Values[key] = v;
            }
            return p;
        }

        private static string Clean(string name)
        {
            var sb = new StringBuilder();
            foreach (char c in name ?? "")
                if (c != '|' && c != ';' && c != '\n' && c != '\r' && c != '\t' && sb.Length < 32) sb.Append(c);
            return sb.ToString().Trim();
        }

        internal static bool Save(string name)
        {
            var p = Current(name);
            if (p == null || p.Name.Length == 0) return false;
            p.Name = Clean(p.Name);
            p.Saved = true;
            SavedPresets.RemoveAll(x => string.Equals(x.Name, p.Name, StringComparison.OrdinalIgnoreCase));
            SavedPresets.Add(p);
            Write();
            CoopRuntime.Notice(Lang.F("Saved run preset: {0}", p.Name));
            return true;
        }

        internal static void Delete(Preset p)
        {
            if (SavedPresets.Remove(p)) Write();
        }

        private static void Write()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                var lines = new List<string>();
                foreach (var p in SavedPresets) lines.Add(Encode(p));
                File.WriteAllLines(FilePath, lines.ToArray());
            }
            catch (Exception e) { Log.Warn("[presets] could not save: " + e.Message); }
        }

        // ------------------------------------------------------------------ window

        private static string _name = "";

        /// <summary>
        /// The co-op presets, in the lobby's rows: each one's line says what it is on hover,
        /// with Use (and Delete for your own); then saving the current settings under a name,
        /// and sharing them as a line of text.
        /// </summary>
        internal static void Section()
        {
            if (!CanApply) return;
            string match = Matching();
            GameRows.Header(Lang.T("Co-op run presets"));
            Preset remove = null;
            foreach (var p in All())
            {
                string name = p.Saved ? p.Name : Lang.T(p.Name);
                var at = GameRows.Slot(name, p.Saved ? Summary(p) : Lang.T(p.About), p.Saved ? 220f : 110f);
                float bw = p.Saved ? (at.width - 6f) / 2f : at.width;
                bool inUse = p.Name == match;
                bool was = GUI.enabled;
                GUI.enabled = was && !inUse;
                if (GUI.Button(new Rect(at.x, at.y, bw, at.height), inUse ? Lang.T("In use") : Lang.T("Use"))) Apply(p);
                GUI.enabled = was;
                if (p.Saved && GUI.Button(new Rect(at.x + bw + 6f, at.y, bw, at.height), Lang.T("Delete"))) remove = p;
            }
            if (remove != null) Delete(remove);

            var nameAt = GameRows.Slot(Lang.T("Save these settings"), Lang.T("Keeps the run settings above under a name, to use again or share."), 330f);
            _name = GUI.TextField(new Rect(nameAt.x, nameAt.y, nameAt.width - 96f, nameAt.height), _name ?? "", 32);
            if (GUI.Button(new Rect(nameAt.xMax - 90f, nameAt.y, 90f, nameAt.height), Lang.T("Save")) && Clean(_name).Length > 0) Save(_name);

            var shareAt = GameRows.Slot(Lang.T("Share"), Lang.F("Copy: these settings as a line of text for a friend. Paste: use a line a friend sent you (it starts with {0}).", SharePrefix), 220f);
            float hw = (shareAt.width - 6f) / 2f;
            if (GUI.Button(new Rect(shareAt.x, shareAt.y, hw, shareAt.height), Lang.T("Copy")))
            {
                var cur = Current(match ?? (Clean(_name).Length > 0 ? _name : "Shared"));
                if (cur != null)
                {
                    GUIUtility.systemCopyBuffer = Encode(cur);
                    CoopRuntime.Notice(Lang.T("Copied the run settings: paste them to a friend"));
                }
            }
            if (GUI.Button(new Rect(shareAt.x + hw + 6f, shareAt.y, hw, shareAt.height), Lang.T("Paste")))
            {
                var p = Decode(GUIUtility.systemCopyBuffer);
                if (p == null) CoopRuntime.Notice(Lang.F("The clipboard has no co-op run preset (it starts with {0})", SharePrefix));
                else Apply(p);
            }
        }

        private static string Summary(Preset p)
        {
            if (p.Values.Count == 0) return Lang.T("The game's normal settings.");
            var parts = new List<string>();
            foreach (var kv in p.Values)
            {
                string label = Locale.GetOther("runset" + kv.Key);
                if (string.IsNullOrEmpty(label) || label.StartsWith("runset", StringComparison.Ordinal)) label = kv.Key;
                parts.Add(label);
                if (parts.Count == 4) break;
            }
            string list = string.Join(", ", parts.ToArray());
            return p.Values.Count > 4 ? Lang.F("Changes {0} and {1} more.", list, p.Values.Count - 4) : Lang.F("Changes {0}.", list);
        }
    }
}
