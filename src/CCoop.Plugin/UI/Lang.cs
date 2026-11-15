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
    /// The mod's text in other languages. The English text itself is the key: every
    /// player-facing string goes through <see cref="T"/> (fixed text) or <see cref="F"/>
    /// (text with names or numbers in it, as {0}, {1}...). Without a translation the
    /// English shows.
    ///
    /// The language follows the game's own (its language code, such as zh-CN), unless
    /// the Language setting picks one. Translations are plain text files named after the
    /// code, in <c>lang/</c> beside the mod, or in <c>CasualtiesCoop/lang/</c> in the
    /// game's data folder (which wins, so players can add one without touching the mod):
    /// <code>
    /// Host with Steam
    /// = 用 Steam 主持
    /// </code>
    /// An English line, then its translation on the next line after "= ". Lines starting
    /// with # are comments; \n stands for a line break. tools/extract-strings.py writes
    /// the full list of English lines (lang/template.txt) from the source.
    /// </summary>
    internal static class Lang
    {
        private static Dictionary<string, string> _table;
        private static string _loadedFor;
        private static float _nextCheck;

        /// <summary>The language in use: the setting, else the game's (EN when unknown).</summary>
        internal static string Code
        {
            get
            {
                if (!string.IsNullOrEmpty(Dev.DevHarness.LangOverride)) return Dev.DevHarness.LangOverride;
                string set = CoopPlugin.Ready ? CoopPlugin.Instance.Language.Value : "";
                if (!string.IsNullOrEmpty(set) && !string.Equals(set, "auto", StringComparison.OrdinalIgnoreCase)) return set.Trim();
                string game = null;
                try { game = Locale.currentLangName; }
                catch { /* the game's locale isn't loaded yet */ }
                return string.IsNullOrEmpty(game) ? "EN" : game;
            }
        }

        /// <summary>Fixed text, translated.</summary>
        internal static string T(string english)
        {
            if (string.IsNullOrEmpty(english)) return english;
            var t = Table;
            return t != null && t.TryGetValue(english, out var tr) ? tr : english;
        }

        /// <summary>Text with values in it ({0}, {1}...), translated, then filled in.</summary>
        internal static string F(string english, params object[] args)
        {
            string pattern = T(english);
            try { return string.Format(CultureInfo.InvariantCulture, pattern, args); }
            catch (FormatException)
            {
                // A translation that broke its placeholders: show the English rather than nothing.
                try { return string.Format(CultureInfo.InvariantCulture, english, args); }
                catch (FormatException) { return english; }
            }
        }

        private const char Packed = '\u0002', Sep = '\u001f';

        /// <summary>
        /// Text for another player's screen: the English pattern and its values travel
        /// together, and <see cref="Unpack"/> translates on the machine that shows it.
        /// </summary>
        internal static string Pack(string english, params object[] args)
        {
            if (args == null || args.Length == 0) return english;
            var sb = new StringBuilder().Append(Packed).Append(english);
            foreach (var a in args) sb.Append(Sep).Append(Convert.ToString(a, CultureInfo.InvariantCulture)?.Replace(Sep, ' '));
            return sb.ToString();
        }

        /// <summary>Shows text from another player: a packed pattern, or fixed English, in this player's language.</summary>
        internal static string Unpack(string wire)
        {
            if (string.IsNullOrEmpty(wire)) return wire;
            if (wire[0] != Packed) return T(wire);
            var parts = wire.Substring(1).Split(Sep);
            var args = new object[parts.Length - 1];
            for (int i = 1; i < parts.Length; i++) args[i - 1] = parts[i];
            return F(parts[0], args);
        }

        /// <summary>Packed text in English, for logs and bug reports.</summary>
        internal static string Plain(string wire)
        {
            if (string.IsNullOrEmpty(wire) || wire[0] != Packed) return wire;
            var parts = wire.Substring(1).Split(Sep);
            var args = new object[parts.Length - 1];
            for (int i = 1; i < parts.Length; i++) args[i - 1] = parts[i];
            try { return string.Format(CultureInfo.InvariantCulture, parts[0], args); }
            catch (FormatException) { return parts[0]; }
        }

        /// <summary>Marks fixed English text that is translated later, where it is shown (the extractor finds it).</summary>
        internal static string N(string english) => english;

        /// <summary>A list of fixed texts, translated (toolbars).</summary>
        internal static string[] All(string[] english)
        {
            if (english == null || Table == null) return english;
            var o = new string[english.Length];
            for (int i = 0; i < english.Length; i++) o[i] = T(english[i]);
            return o;
        }

        internal static int Count => Table?.Count ?? 0;

        private static Dictionary<string, string> Table
        {
            get
            {
                // The language can change in the game's settings; look again now and then.
                float now = Time.unscaledTime;
                if (_loadedFor != null && now < _nextCheck) return _table;
                _nextCheck = now + 2f;
                string code = Code;
                if (code == _loadedFor) return _table;
                _loadedFor = code;
                _table = Load(code);
                return _table;
            }
        }

        private static IEnumerable<string> Candidates(string code)
        {
            if (!string.IsNullOrEmpty(Dev.DevHarness.LangFileOverride)) yield return Dev.DevHarness.LangFileOverride;
            string file = code + ".txt";
            yield return Path.Combine(Path.Combine(Path.Combine(Application.persistentDataPath, "CasualtiesCoop"), "lang"), file);
            string here = null;
            try { here = Path.GetDirectoryName(typeof(Lang).Assembly.Location); }
            catch { /* loaded from memory */ }
            if (!string.IsNullOrEmpty(here)) yield return Path.Combine(Path.Combine(here, "lang"), file);
        }

        private static Dictionary<string, string> Load(string code)
        {
            if (string.Equals(code, "EN", StringComparison.OrdinalIgnoreCase)) return null;
            foreach (var path in Candidates(code))
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    var table = Parse(File.ReadAllLines(path, Encoding.UTF8));
                    Log.Info("[lang] " + code + ": " + table.Count + " lines from " + path);
                    return table;
                }
                catch (Exception e) { Log.Warn("[lang] could not read " + path + ": " + e.Message); }
            }
            Log.Info("[lang] no " + code + " translation; showing English");
            return null;
        }

        internal static Dictionary<string, string> Parse(IEnumerable<string> lines)
        {
            var table = new Dictionary<string, string>(StringComparer.Ordinal);
            string english = null;
            foreach (var raw in lines)
            {
                var line = raw.TrimEnd('\r');
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                if (line.StartsWith("= ", StringComparison.Ordinal) || line == "=")
                {
                    if (english == null) continue;
                    string tr = line.Length > 2 ? Unescape(line.Substring(2)) : "";
                    if (tr.Length > 0) table[english] = tr;
                    english = null;
                    continue;
                }
                english = Unescape(line);
            }
            return table;
        }

        private static string Unescape(string s) => s.Replace("\\n", "\n");
    }
}
