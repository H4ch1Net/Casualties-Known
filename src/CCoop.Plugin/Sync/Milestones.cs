using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using CCoop.Plugin.Runtime;
using UnityEngine;
using CCoop.Plugin.UI;

namespace CCoop.Plugin.Sync
{
    /// <summary>
    /// Co-op milestones: small things done for each other, counted on each player's own
    /// machine and kept in a file beside the co-op saves. They are the mod's own and never
    /// Steam achievements. A self-test never touches the player's file.
    /// </summary>
    internal sealed class Milestones : CoopModule
    {
        internal sealed class Def
        {
            public string Key, Title, Text;
            public float Goal;
            public string Unit;
        }

        internal static readonly Def[] All =
        {
            new Def { Key = "carry", Goal = 100f, Unit = " m", Title = Lang.N("Pack mule"), Text = Lang.N("Carry teammates 100 m in all.") },
            new Def { Key = "catch", Goal = 1f, Title = Lang.N("Safe hands"), Text = Lang.N("Catch a falling teammate.") },
            new Def { Key = "legup", Goal = 5f, Title = Lang.N("Stepping stone"), Text = Lang.N("Give 5 leg-ups.") },
            new Def { Key = "hug", Goal = 10f, Title = Lang.N("Warm company"), Text = Lang.N("Hug teammates 10 times.") },
            new Def { Key = "treat", Goal = 10f, Title = Lang.N("Field medic"), Text = Lang.N("Treat teammates' wounds 10 times.") },
            new Def { Key = "revived", Goal = 1f, Title = Lang.N("Second chance"), Text = Lang.N("Be brought back from death by a teammate.") },
            new Def { Key = "layer", Goal = 10f, Title = Lang.N("Deeper together"), Text = Lang.N("Reach 10 new layers with a teammate alive beside you.") },
        };

        private static Dictionary<string, float> _counts;
        private static bool _dirty;
        private static float _nextSave;
        private static Vector2? _lastCarry;
        private ushort _countedEpoch;

        private static string FilePath => Path.Combine(Path.Combine(Application.persistentDataPath, "CasualtiesCoop"), "milestones.txt");

        /// <summary>Self-tests share the player's data folder: their milestones stay in memory.</summary>
        private static bool Persist => !Dev.DevHarness.SelfTestActive;

        private static Dictionary<string, float> Counts
        {
            get
            {
                if (_counts != null) return _counts;
                _counts = new Dictionary<string, float>();
                if (!Persist) return _counts;
                try
                {
                    if (File.Exists(FilePath))
                        foreach (var line in File.ReadAllLines(FilePath))
                        {
                            int eq = line.IndexOf('=');
                            if (eq > 0 && float.TryParse(line.Substring(eq + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                                _counts[line.Substring(0, eq)] = v;
                        }
                }
                catch (Exception e) { Log.Warn("[milestones] could not read: " + e.Message); }
                return _counts;
            }
        }

        internal static float Count(string key) => Counts.TryGetValue(key, out var v) ? v : 0f;

        internal static int Reached
        {
            get
            {
                int n = 0;
                foreach (var d in All) if (Count(d.Key) >= d.Goal) n++;
                return n;
            }
        }

        /// <summary>Counts something done; announces a milestone the moment it's reached.</summary>
        internal static void Add(string key, float amount = 1f)
        {
            if (!Coop.InSession || amount <= 0f) return;
            float before = Count(key);
            float after = before + amount;
            Counts[key] = after;
            _dirty = true;
            foreach (var d in All)
                if (d.Key == key && before < d.Goal && after >= d.Goal)
                {
                    CoopRuntime.Notice(Lang.F("Co-op milestone: {0}", Lang.T(d.Title)));
                    Log.Info("[milestones] reached " + d.Title + " (" + d.Text + ")");
                    Save();
                }
        }

        /// <summary>While carrying a teammate: the distance walked counts.</summary>
        internal static void Carrying(Vector2 at)
        {
            if (_lastCarry.HasValue)
            {
                float d = Vector2.Distance(_lastCarry.Value, at);
                if (d < 3f) Add("carry", d);  // a jump in position is a teleport, not a walk
            }
            _lastCarry = at;
        }

        internal static void NotCarrying() => _lastCarry = null;

        private static void Save()
        {
            _dirty = false;
            if (!Persist) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                var lines = new List<string>();
                foreach (var kv in Counts) lines.Add(kv.Key + "=" + kv.Value.ToString("0.##", CultureInfo.InvariantCulture));
                File.WriteAllLines(FilePath, lines.ToArray());
            }
            catch (Exception e) { Log.Warn("[milestones] could not save: " + e.Message); }
        }

        private float _layerCheckAt = -1f;

        internal override void OnWorldLive()
        {
            // A new layer reached with a living teammate. Checked a little later: the
            // others are still loading when this machine's layer goes live.
            if (Coop.Epoch == _countedEpoch) return;
            _countedEpoch = Coop.Epoch;
            _layerCheckAt = Time.unscaledTime + 10f;
        }

        private void CheckLayer()
        {
            _layerCheckAt = -1f;
            var body = GameInfo.LocalBody;
            if (!Coop.WorldLive || body == null || !body.alive) return;
            foreach (var rec in Coop.Players.Values)
                if (rec.Id != Coop.LocalId && rec.Alive && rec.HasPosition && rec.ReportedEpoch == Coop.Epoch) { Add("layer"); return; }
        }

        internal override void Tick()
        {
            if (_layerCheckAt > 0f && Time.unscaledTime >= _layerCheckAt) CheckLayer();
            if (_dirty && Time.unscaledTime >= _nextSave)
            {
                _nextSave = Time.unscaledTime + 5f;
                Save();
            }
        }

        internal override void Detach()
        {
            if (_dirty) Save();
            _lastCarry = null;
            _countedEpoch = 0;
        }
    }
}
