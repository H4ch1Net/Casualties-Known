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

        /// <summary>Other copies of a co-op mod in the plugins folder (an old version, the first mod), as paths.</summary>
        internal static readonly List<string> OtherCopies = new List<string>();

        /// <summary>
        /// Another CCoop.Plugin.dll anywhere under BepInEx/plugins: the first co-op mod (v1,
        /// "ccoop.plugin") or a second copy of this one. Both are the same assembly name, and
        /// two of them break each other. The installer removes them; this says so in game.
        /// </summary>
        internal static void FindOtherCopies()
        {
            OtherCopies.Clear();
            try
            {
                string mine = Path.GetFullPath(typeof(GameCheck).Assembly.Location);
                string plugins = BepInEx.Paths.PluginPath;
                foreach (var dll in Directory.GetFiles(plugins, "CCoop.Plugin.dll", SearchOption.AllDirectories))
                {
                    if (string.Equals(Path.GetFullPath(dll), mine, StringComparison.OrdinalIgnoreCase)) continue;
                    OtherCopies.Add(Relative(plugins, dll));
                }
                foreach (var tool in new[] { "CCoop.Gates.dll", "CCoop.Probe.dll" })
                    foreach (var dll in Directory.GetFiles(plugins, tool, SearchOption.AllDirectories))
                        OtherCopies.Add(Relative(plugins, dll));
            }
            catch (Exception e) { Log.Debug("other copies: " + e.Message); }
            if (OtherCopies.Count > 0) Log.Warn("another co-op mod is installed too: " + string.Join(", ", OtherCopies.ToArray()) + ". Run the installer again to remove it.");
        }

        /// <summary>"plugins/CCoop/CCoop.Plugin.dll" for a file under the plugins folder.</summary>
        private static string Relative(string plugins, string file)
        {
            string parent = Path.GetDirectoryName(plugins) ?? "";
            string rel = file.Length > parent.Length ? file.Substring(parent.Length) : file;
            return rel.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace(Path.DirectorySeparatorChar, '/');
        }

        internal static void Run()
        {
            Missing.Clear();
            foreach (var (type, member, feature) in Members)
            {
                // Plain reflection: Harmony's lookups log a warning for each kind they don't find.
                bool found = type.GetMember(member, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static |
                                                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic).Length > 0;
                if (!found) Missing.Add(feature + " (" + type.Name + "." + member + ")");
            }
            if (Missing.Count == 0) Log.Info("game check: all " + Members.Length + " game members found");
            else Log.Warn("game check: missing " + string.Join(", ", Missing.ToArray()));
            FindOtherCopies();
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
