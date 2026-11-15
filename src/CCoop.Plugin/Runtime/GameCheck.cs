using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace CCoop.Plugin.Runtime
{
    /// <summary>
    /// After a game update, the risky part of a mod is every game member it reaches by
    /// name: those fail silently, later, at the worst moment. This checks them all at
    /// start-up and the co-op window names what broke (patches that failed to bind are
    /// reported separately by <see cref="CoopPlugin"/>).
    /// </summary>
    internal static class GameCheck
    {
        private static readonly (Type type, string member, string feature)[] Members =
        {
            (typeof(Talker), "currentString", "chat and speech bubbles"),
            (typeof(SpiderHandler), "biteCooldown", "creatures biting teammates"),
            (typeof(GuardScript), "bld", "guard sync"),
            (typeof(GuardScript), "didDie", "guard sync"),
            (typeof(BuildingEntity), "_health", "object damage"),
            (typeof(BuildingEntity), "didDestroyRoutine", "object breaking"),
            (typeof(RadiationLine), "timeGone", "the radiation line"),
            (typeof(SteamManager), "initialized", "Steam detection"),
            (typeof(KeyBinds), "cachedBinds", "the T chat key"),
            (typeof(WorldGeneration), "worldBlocks", "terrain sync"),
            (typeof(WorldGeneration), "chunks", "terrain sync"),
            (typeof(WorldGeneration), "instantiatingWorld", "layer loading"),
            (typeof(WorldGeneration), "doingRegen", "descending"),
            (typeof(MinigameBase), "ActiveMinigameUpdate", "treating teammates"),
            (typeof(PlayerCamera), "HandleDeathScreen", "spectating"),
            (typeof(Body), "GetTotalEncumberance", "carrying"),
        };

        /// <summary>Features that may not work, e.g. "chat and speech bubbles (Talker.currentString)".</summary>
        internal static readonly List<string> Missing = new List<string>();

        internal static void Run()
        {
            Missing.Clear();
            foreach (var (type, member, feature) in Members)
            {
                bool found = AccessTools.Field(type, member) != null || AccessTools.Property(type, member) != null ||
                             AccessTools.Method(type, member) != null;
                if (!found) Missing.Add(feature + " (" + type.Name + "." + member + ")");
            }
            if (Missing.Count == 0) Log.Info("game check: all " + Members.Length + " game members found");
            else Log.Warn("game check: missing " + string.Join(", ", Missing.ToArray()));
        }

        // ------------------------------------------------------------------ bug reports

        /// <summary>
        /// Gathers this machine's co-op logs and the session facts into a folder on the
        /// desktop, ready to attach to a bug report. Nothing is sent anywhere.
        /// </summary>
        internal static string SaveReport()
        {
            // Under Proton the "desktop" is a folder inside the game's prefix that nobody
            // would find: use the real Linux desktop (or home) instead.
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string home = GameInfo.LinuxHome();
            if (home != null) desktop = Directory.Exists(Path.Combine(home, "Desktop")) ? Path.Combine(home, "Desktop") : home;
            string dir = Path.Combine(desktop, "CasualtiesCoop-report-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(dir);

            var info = new StringBuilder();
            info.AppendLine("CasualtiesCoop " + CoopPlugin.Version);
            info.AppendLine("Game build " + GameInfo.GameBuild + ", Steam build " + Net.SteamServices.AppBuildId);
            info.AppendLine("Unity " + Application.unityVersion + ", " + SystemInfo.operatingSystem + (GameInfo.UnderWine ? " (under Wine/Proton)" : ""));
            info.AppendLine("Failed patches: " + (CoopPlugin.FailedPatches.Length == 0 ? "none" : string.Join(", ", CoopPlugin.FailedPatches)));
            info.AppendLine("Missing game members: " + (Missing.Count == 0 ? "none" : string.Join(", ", Missing.ToArray())));
            var s = Coop.Session;
            if (s != null)
            {
                info.AppendLine("Session: " + (s.IsHost ? "host" : "client") + " over " + Coop.Runtime?.TransportKind + ", " + s.PeerCount + " players, epoch " + Coop.Epoch);
                info.AppendLine("Rules: " + Sync.HostRules.Current.Describe());
                foreach (var p in s.Peers) info.AppendLine("  " + p.Id + " " + p.Name + " rtt " + p.RoundTripMs + " ms");
            }
            var w = WorldGeneration.world;
            if (w != null) info.AppendLine("Layer " + (w.biomeDepth + 1) + ", terrain " + Dev.DevHarness.TerrainHash());
            File.WriteAllText(Path.Combine(dir, "info.txt"), info.ToString());

            // Today's co-op logs from this machine (both instances when testing locally).
            string logs = Path.GetDirectoryName(Log.FilePath ?? "");
            int copied = 0;
            if (!string.IsNullOrEmpty(logs) && Directory.Exists(logs))
                foreach (var f in Directory.GetFiles(logs, "coop-*.log"))
                    if (File.GetLastWriteTime(f) > DateTime.Now.AddHours(-6))
                    {
                        File.Copy(f, Path.Combine(dir, Path.GetFileName(f)), true);
                        copied++;
                    }
            string bepinex = Path.Combine(Paths.GameRoot, "BepInEx", "LogOutput.log");
            if (File.Exists(bepinex)) File.Copy(bepinex, Path.Combine(dir, "LogOutput.log"), true);
            Log.Info("bug report saved to " + dir + " (" + copied + " logs)");
            return dir;
        }

        private static class Paths
        {
            internal static string GameRoot => Path.GetDirectoryName(Application.dataPath) ?? "";
        }
    }
}
