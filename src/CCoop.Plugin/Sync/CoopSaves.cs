using System;
using System.Collections.Generic;
using System.IO;
using CCoop.Net.Session;
using CCoop.Plugin.Runtime;
using HarmonyLib;
using UnityEngine;
using CCoop.Plugin.UI;

namespace CCoop.Plugin.Sync
{
    /// <summary>
    /// Co-op saves. The game's save is just the player — body, wounds, items, recipes,
    /// character, layer number and run settings — and loading it regenerates the layer.
    /// That fits co-op: each player saves their own character under the run's id, and a
    /// continued run regenerates the layer for everyone.
    ///
    /// The game's own SaveGame/TryLoadGame do the work, so the format stays the game's.
    /// Its single save file (save.sv) is moved aside first and always put back, even
    /// after a crash (a marker file triggers the restore on the next launch).
    /// Saves happen when each new layer goes live and when leaving a session.
    /// </summary>
    internal sealed class CoopSaves : CoopModule
    {
        private static string Root => Path.Combine(Path.Combine(Application.persistentDataPath, "CasualtiesCoop"), "saves");
        private static string GameSave => Path.Combine(Application.persistentDataPath, "save.sv");
        private static string Backup => GameSave + ".ccoop-singleplayer";
        private static string Marker => Path.Combine(Path.Combine(Application.persistentDataPath, "CasualtiesCoop"), "save-swap-in-progress");

        /// <summary>The id of the run being played (the host's, sent with RunInfo).</summary>
        internal static string RunId = "";

        /// <summary>Host: a saved run to continue once hosting.</summary>
        internal static string PendingContinue;

        /// <summary>True while the mod itself calls SaveGame (the in-session save guard lets it through).</summary>
        internal static bool Saving;

        private static bool _restoreAfterLoad;
        private static bool _continuing;
        private ushort _savedEpoch;

        // ------------------------------------------------------------------ the single-player save, always safe

        /// <summary>At start-up: if a swap was interrupted (crash), put the single-player save back.</summary>
        internal static void RecoverFromCrash()
        {
            try
            {
                if (!File.Exists(Marker)) return;
                if (File.Exists(Backup))
                {
                    if (File.Exists(GameSave)) File.Delete(GameSave);
                    File.Move(Backup, GameSave);
                }
                File.Delete(Marker);
                Log.Warn("[save] restored the single-player save after an interrupted co-op save");
            }
            catch (Exception e) { Log.Error("[save] crash recovery failed: " + e.Message); }
        }

        private static void StashSinglePlayer()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Marker));
            File.WriteAllText(Marker, DateTime.Now.ToString("o"));
            if (File.Exists(Backup)) File.Delete(Backup);
            if (File.Exists(GameSave)) File.Move(GameSave, Backup);
        }

        private static void RestoreSinglePlayer()
        {
            if (File.Exists(GameSave)) File.Delete(GameSave);
            if (File.Exists(Backup)) File.Move(Backup, GameSave);
            if (File.Exists(Marker)) File.Delete(Marker);
        }

        // ------------------------------------------------------------------ identity and listing

        private static string Me
        {
            get
            {
                ulong steam = GameInfo.SteamId;
                if (steam != 0) return "steam" + steam;
                var name = GameInfo.PlayerName ?? "player";
                foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
                return "name-" + name;
            }
        }

        private static string FileFor(string runId) => Path.Combine(Path.Combine(Root, runId), Me + ".sv");

        internal sealed class SavedRun
        {
            public string Id;
            public int Layer;
            public string Players;
            public DateTime When;
        }

        /// <summary>Runs this player has a saved character in, newest first.</summary>
        internal static List<SavedRun> List()
        {
            var list = new List<SavedRun>();
            try
            {
                if (!Directory.Exists(Root)) return list;
                foreach (var dir in Directory.GetDirectories(Root))
                {
                    string id = Path.GetFileName(dir);
                    string mine = FileFor(id);
                    if (!File.Exists(mine)) continue;
                    var run = new SavedRun { Id = id, When = File.GetLastWriteTime(mine), Players = "", Layer = 0 };
                    string meta = Path.Combine(dir, "run.txt");
                    if (File.Exists(meta))
                        foreach (var line in File.ReadAllLines(meta))
                        {
                            if (line.StartsWith("layer=")) int.TryParse(line.Substring(6), out run.Layer);
                            else if (line.StartsWith("players=")) run.Players = line.Substring(8);
                        }
                    list.Add(run);
                }
            }
            catch (Exception e) { Log.Warn("[save] listing failed: " + e.Message); }
            list.Sort((a, b) => b.When.CompareTo(a.When));
            return list;
        }

        /// <summary>The run is over (everyone died): its save goes, like the game's own.</summary>
        internal static void DeleteCurrent()
        {
            if (string.IsNullOrEmpty(RunId)) return;
            Delete(RunId);
            Log.Info("[save] run " + RunId + " ended in a wipe; save deleted");
        }

        internal static void Delete(string runId)
        {
            try
            {
                string mine = FileFor(runId);
                if (File.Exists(mine)) File.Delete(mine);
            }
            catch (Exception e) { Log.Warn("[save] delete failed: " + e.Message); }
        }

        // ------------------------------------------------------------------ saving

        /// <summary>Saves the local character into the current run. Quietly does nothing outside a run.</summary>
        internal static bool SaveNow(string why)
        {
            var w = WorldGeneration.world;
            var body = GameInfo.LocalBody;
            if (string.IsNullOrEmpty(RunId) || w == null || body == null || !body.alive || !Coop.WorldLive) return false;
            if (w.biomeDepth >= 9) return false;  // the game refuses saves from WHERE WE MUST REACH on
            try
            {
                StashSinglePlayer();
                Saving = true;
                try { SaveSystem.SaveGame(); }
                finally { Saving = false; }
                string target = FileFor(RunId);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                if (File.Exists(target)) File.Delete(target);
                if (File.Exists(GameSave)) File.Move(GameSave, target);
                if (Coop.IsHost) WriteMeta(w);
                Log.Info("[save] saved our character for layer " + (w.biomeDepth + 1) + " (" + why + ")");
                return true;
            }
            catch (Exception e)
            {
                Log.Error("[save] co-op save failed: " + e.Message);
                return false;
            }
            finally
            {
                try { RestoreSinglePlayer(); }
                catch (Exception e) { Log.Error("[save] could not restore the single-player save: " + e.Message); }
            }
        }

        private static void WriteMeta(WorldGeneration w)
        {
            var names = new List<string>();
            foreach (var r in Coop.Players.Values) names.Add(r.Name);
            File.WriteAllLines(Path.Combine(Path.Combine(Root, RunId), "run.txt"), new[]
            {
                "layer=" + (w.biomeDepth + 1),
                "players=" + string.Join(", ", names.ToArray()),
                "saved=" + DateTime.Now.ToString("o"),
            });
        }

        // ------------------------------------------------------------------ loading

        /// <summary>
        /// Puts this player's saved character for <paramref name="runId"/> where the game
        /// loads from, and tells the game to load it on the next run start. Returns the
        /// saved character type, or -1 if there's nothing to load.
        /// </summary>
        internal static int PrepareLoad(string runId)
        {
            string mine = FileFor(runId);
            if (!File.Exists(mine)) return -1;
            try
            {
                StashSinglePlayer();
                File.Copy(mine, GameSave, true);
                _restoreAfterLoad = true;
                SaveSystem.loadedRun = true;
                int character = ReadCharacter(GameSave);
                Log.Info("[save] loading our character from co-op run " + runId);
                return character;
            }
            catch (Exception e)
            {
                Log.Error("[save] could not prepare the load: " + e.Message);
                try { RestoreSinglePlayer(); } catch { /* already logged */ }
                return -1;
            }
        }

        private static int ReadCharacter(string path)
        {
            try
            {
                var json = Newtonsoft.Json.Linq.JObject.Parse(SaveSystem.Unzip(File.ReadAllBytes(path)));
                var token = json["character"];
                return token != null ? (int)token : -1;
            }
            catch { return -1; }
        }

        /// <summary>After the game has loaded (and deleted) save.sv, bring the single-player save back.</summary>
        internal static void AfterGameLoaded()
        {
            if (!_restoreAfterLoad) return;
            _restoreAfterLoad = false;
            try { RestoreSinglePlayer(); }
            catch (Exception e) { Log.Error("[save] could not restore the single-player save: " + e.Message); }
        }

        /// <summary>Host: start the saved run (from the menu, once hosting).</summary>
        internal static bool StartContinue()
        {
            var pre = PreRunScript.instance;
            if (pre == null || string.IsNullOrEmpty(PendingContinue)) return false;
            int character = PrepareLoad(PendingContinue);
            if (character < 0) { CoopRuntime.Notice(Lang.T("That save is gone.")); PendingContinue = null; return false; }
            RunId = PendingContinue;
            PendingContinue = null;
            _continuing = true;
            PreRunScript.characterSelected = character;
            Traverse.Create(pre).Field("startedLoading").SetValue(true);
            pre.StartCoroutine(pre.WaitLoad());
            Log.Info("[save] continuing co-op run " + RunId);
            return true;
        }

        // ------------------------------------------------------------------ module

        internal override void Detach()
        {
            if (Coop.WorldLive) SaveNow("leaving the session");
            _savedEpoch = 0;
        }

        internal override void OnWorldLive()
        {
            _continuing = false;
            if (Coop.IsHost && string.IsNullOrEmpty(RunId)) RunId = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            if (Coop.Epoch == _savedEpoch) return;
            _savedEpoch = Coop.Epoch;
            // Let the layer settle a moment (spawn placement, revivals) before saving.
            Coop.Runtime.StartCoroutine(SaveSoon());
        }

        private System.Collections.IEnumerator SaveSoon()
        {
            yield return new WaitForSecondsRealtime(3f);
            SaveNow("new layer");
        }

        internal override void Tick()
        {
            // Back in the menu, the host's next run is a new one (unless it's continuing a save).
            if (GameInfo.OnMainMenu && Coop.IsHost && !_continuing) RunId = "";
        }
    }

    /// <summary>The in-session save guard lets the mod's own co-op saves through.</summary>
    internal static class SaveGate
    {
        internal static bool Allowed => CoopSaves.Saving;
    }

    [HarmonyPatch(typeof(SaveSystem), nameof(SaveSystem.TryLoadGame))]
    internal static class LoadRestorePatch
    {
        private static void Postfix() => CoopSaves.AfterGameLoaded();
    }
}
