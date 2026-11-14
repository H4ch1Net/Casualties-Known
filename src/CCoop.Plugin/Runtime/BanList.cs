using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace CCoop.Plugin.Runtime
{
    /// <summary>
    /// Players this machine refuses when hosting, kept across sessions. Keys are what the
    /// transport verifies: a SteamID64 over Steam, an IP address over LAN/IP. Names are
    /// only for the list in the co-op window.
    /// </summary>
    internal static class BanList
    {
        internal struct Entry
        {
            public string Key;
            public string Name;
            public string When;
        }

        private static List<Entry> _entries;

        private static string FilePath => Path.Combine(Path.Combine(Application.persistentDataPath, "CasualtiesCoop"), "bans.txt");

        internal static IReadOnlyList<Entry> All
        {
            get
            {
                Load();
                return _entries;
            }
        }

        /// <summary>
        /// "7656..." (Steam) stays as is; LAN addresses always arrive as address + ":" +
        /// port, so "1.2.3.4:7777" becomes "1.2.3.4" and "::1:7777" becomes "::1".
        /// </summary>
        internal static string KeyFor(string address)
        {
            if (string.IsNullOrEmpty(address) || address.StartsWith("loopback")) return address ?? "";
            int colon = address.LastIndexOf(':');
            if (colon > 0 && int.TryParse(address.Substring(colon + 1), out _)) return address.Substring(0, colon);
            return address;
        }

        internal static bool IsBanned(string address)
        {
            string key = KeyFor(address);
            if (key.Length == 0) return false;
            Load();
            foreach (var e in _entries) if (e.Key == key) return true;
            return false;
        }

        internal static void Ban(string address, string name)
        {
            string key = KeyFor(address);
            if (key.Length == 0 || IsBanned(key)) return;
            _entries.Add(new Entry { Key = key, Name = Clean(name), When = DateTime.Now.ToString("yyyy-MM-dd") });
            Save();
            Log.Info("[bans] banned " + Clean(name) + " (" + key + ")");
        }

        internal static void Unban(string key)
        {
            Load();
            if (_entries.RemoveAll(e => e.Key == key) > 0) Save();
        }

        private static string Clean(string s) => (s ?? "").Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');

        private static void Load()
        {
            if (_entries != null) return;
            _entries = new List<Entry>();
            try
            {
                if (!File.Exists(FilePath)) return;
                foreach (var line in File.ReadAllLines(FilePath))
                {
                    if (line.Length == 0 || line[0] == '#') continue;
                    var parts = line.Split('\t');
                    _entries.Add(new Entry
                    {
                        Key = parts[0].Trim(),
                        Name = parts.Length > 1 ? parts[1] : "",
                        When = parts.Length > 2 ? parts[2] : "",
                    });
                }
            }
            catch (Exception e) { Log.Warn("could not read the ban list: " + e.Message); }
        }

        private static void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                var lines = new List<string> { "# CasualtiesCoop bans: key<TAB>name<TAB>date. Delete a line to unban." };
                foreach (var e in _entries) lines.Add(e.Key + "\t" + e.Name + "\t" + e.When);
                File.WriteAllLines(FilePath, lines);
            }
            catch (Exception e) { Log.Warn("could not save the ban list: " + e.Message); }
        }
    }
}
