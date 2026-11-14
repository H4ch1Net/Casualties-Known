using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CCoop.Probe
{
    /// <summary>
    /// M0 deliverable. Read-only reconnaissance plugin: records the build
    /// fingerprint, verifies the game assembly hash, traces lifecycle events, and
    /// measures the world-snapshot payload that the co-op design depends on.
    ///
    /// This plugin must never change game behaviour. Every hook is a logging-only
    /// postfix, every reflection access is guarded, and any failure degrades to a
    /// warning rather than an exception escaping into game code.
    /// </summary>
    [BepInPlugin(PluginGuid, "CCoop Probe", "0.1.0")]
    public sealed class ProbePlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "ccoop.probe";

        /// <summary>
        /// Assembly-CSharp.dll SHA-256 for Steam App 4576510, Build 23560597.
        /// The whole API map is pinned to this build; a mismatch means every
        /// documented patch target is unverified.
        /// </summary>
        private const string KnownAssemblyHash =
            "fd0ad2851f6bae4ab2589634643b92e16696c0e36f090bdd3f4c41e8f1c97095";

        internal static ManualLogSource Log;

        private Harmony _harmony;

        /// <summary>
        /// Per-process log file. BepInEx's shared LogOutput.log is truncated by each
        /// launch and cannot be written by a second concurrent instance, which makes
        /// it useless both for post-hoc forensics and for the two-instance testing
        /// M1 requires. This writes an unambiguous, PID-stamped file instead.
        /// </summary>
        private static StreamWriter _file;
        private static readonly object FileLock = new object();

        internal static string LogFilePath { get; private set; }

        private static void OpenLogFile()
        {
            try
            {
                var dir = Path.Combine(Application.persistentDataPath, "CCoop");
                Directory.CreateDirectory(dir);
                int pid = System.Diagnostics.Process.GetCurrentProcess().Id;
                LogFilePath = Path.Combine(dir,
                    string.Format("probe-{0:yyyyMMdd-HHmmss}-pid{1}.log", DateTime.Now, pid));
                _file = new StreamWriter(new FileStream(
                    LogFilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
                {
                    AutoFlush = true // never lose the tail to a crash or hard exit
                };
            }
            catch
            {
                _file = null; // fall back to BepInEx logging only
            }
        }

        /// <summary>Logs to both the BepInEx logger and our per-process file.</summary>
        private static void Say(string msg)
        {
            Log?.LogInfo(msg);
            Write("INFO ", msg);
        }

        private static void Warn(string msg)
        {
            Log?.LogWarning(msg);
            Write("WARN ", msg);
        }

        /// <summary>Logging entry points for sibling probe modules.</summary>
        internal static void Announce(string msg) => Say(msg);

        internal static void AnnounceWarning(string msg) => Warn(msg);

        private static void Write(string level, string msg)
        {
            if (_file == null) return;
            try
            {
                lock (FileLock)
                    _file.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + " " + level + msg);
            }
            catch { /* logging must never throw into game code */ }
        }

        /// <summary>Persistent host object for <see cref="ProbeWatcher"/>.</summary>
        private static GameObject _watcherHost;

        private void Awake()
        {
            Log = Logger;
            try
            {
                OpenLogFile();
                Log.LogInfo("Probe log file: " + (LogFilePath ?? "<unavailable>"));
                SceneManager.activeSceneChanged += (from, to) =>
                    Say("[scene] '" + from.name + "' -> '" + to.name + "'");
                LogFingerprint();
                _harmony = new Harmony(PluginGuid);
                ApplyTracePatches();
                StartWatcher();
                Say("Probe ready. Watching for world generation.");
            }
            catch (Exception e)
            {
                Log.LogError("Probe init failed (game unaffected): " + e);
            }
        }

        private void OnDestroy()
        {
            try { _harmony?.UnpatchSelf(); } catch { /* shutting down */ }

            // Deliberately do NOT dispose the log file here. This component is
            // destroyed on scene load, but the process (and the watcher) lives on;
            // disposing would silently truncate all further logging. AutoFlush plus
            // process exit is sufficient.
            try { lock (FileLock) _file?.Flush(); } catch { /* shutting down */ }
        }

        // ---------------------------------------------------------------- fingerprint

        private void LogFingerprint()
        {
            var managed = Path.Combine(Application.dataPath, "Managed");
            var asmPath = Path.Combine(managed, "Assembly-CSharp.dll");

            string hash = "<unreadable>";
            long size = -1;
            try
            {
                if (File.Exists(asmPath))
                {
                    size = new FileInfo(asmPath).Length;
                    using (var sha = SHA256.Create())
                    using (var fs = File.OpenRead(asmPath))
                        hash = BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
                }
            }
            catch (Exception e)
            {
                Warn("Could not hash Assembly-CSharp.dll: " + e.Message);
            }

            var sb = new StringBuilder();
            sb.AppendLine("================ CCoop build fingerprint ================");
            sb.AppendLine("  productName        : " + Application.productName);
            sb.AppendLine("  companyName        : " + Application.companyName);
            sb.AppendLine("  version            : " + Application.version);
            sb.AppendLine("  unityVersion       : " + Application.unityVersion);
            sb.AppendLine("  platform           : " + Application.platform);
            sb.AppendLine("  is64BitProcess     : " + (IntPtr.Size == 8));
            sb.AppendLine("  dataPath           : " + Application.dataPath);
            sb.AppendLine("  persistentDataPath : " + Application.persistentDataPath);
            sb.AppendLine("  clrVersion         : " + Environment.Version);
            sb.AppendLine("  bepinexVersion     : " + typeof(BaseUnityPlugin).Assembly.GetName().Version);
            sb.AppendLine("  asmCSharp size     : " + size + " bytes");
            sb.AppendLine("  asmCSharp sha256   : " + hash);
            sb.AppendLine("  hash matches pin   : " + (hash == KnownAssemblyHash ? "YES" : "NO  <-- UNVERIFIED BUILD"));
            sb.AppendLine("  activeScene        : " + SceneManager.GetActiveScene().name);
            sb.AppendLine("  sceneCountInBuild  : " + SceneManager.sceneCountInBuildSettings);
            sb.Append("========================================================");
            Say(sb.ToString());

            if (hash != KnownAssemblyHash)
            {
                Warn(
                    "Assembly hash does not match the pinned build (" + KnownAssemblyHash + "). " +
                    "The documented API map was derived from that build; re-verify patch targets before trusting them.");
            }

            LogLoadedPluginManifest();
        }

        private static void LogLoadedPluginManifest()
        {
            try
            {
                // The compatibility handshake will need a stable, ordered plugin
                // manifest; log it now so we know what a vanilla+probe run looks like.
                var plugins = Chainloader.PluginInfos.Values
                    .Select(p => p.Metadata.GUID + "@" + p.Metadata.Version)
                    .OrderBy(s => s, StringComparer.Ordinal)
                    .ToArray();
                Say("Loaded BepInEx plugins (" + plugins.Length + "): " + string.Join(", ", plugins));
            }
            catch (Exception e)
            {
                Warn("Could not enumerate plugins: " + e.Message);
            }
        }

        // ---------------------------------------------------------------- trace patches

        private void ApplyTracePatches()
        {
            // Logging-only postfixes. Each is applied independently so one bad
            // signature cannot take down the rest of the probe.
            TryPatch(typeof(SaveSystem), "SaveGame",
                postfix: nameof(Trace_SaveGame));
            TryPatch(typeof(SaveSystem), "TryLoadGame",
                postfix: nameof(Trace_TryLoadGame));
            TryPatch(typeof(PauseHandler), "TogglePause",
                postfix: nameof(Trace_TogglePause));
            TryPatch(typeof(PreRunScript), "StartRun",
                postfix: nameof(Trace_StartRun));
        }

        private void TryPatch(Type target, string methodName, string postfix)
        {
            try
            {
                var original = AccessTools.Method(target, methodName);
                if (original == null)
                {
                    Warn("Trace target not found: " + target.Name + "." + methodName +
                                   " (game build may differ from the pinned one)");
                    return;
                }

                var post = AccessTools.Method(typeof(ProbePlugin), postfix);
                _harmony.Patch(original, postfix: new HarmonyMethod(post));
                Say("Tracing " + target.Name + "." + methodName);
            }
            catch (Exception e)
            {
                Warn("Failed to trace " + target.Name + "." + methodName + ": " + e.Message);
            }
        }

        private static void Trace_SaveGame()
        {
            // Confirms the plan's finding that saves are player-only: watch that no
            // world/terrain data is written here.
            Say("[lifecycle] SaveSystem.SaveGame completed. " + DescribeWorldBriefly());
        }

        private static void Trace_TryLoadGame()
        {
            // Returns void in this build; loadedRun is the actual outcome signal.
            Say("[lifecycle] SaveSystem.TryLoadGame completed" +
                        " (loadedRun=" + SaveSystem.loadedRun + ", savedRunTime=" + SaveSystem.savedRunTime + ")");
        }

        private static void Trace_TogglePause()
        {
            // Verifies the finding that pause is a bool + UI only: Time.timeScale
            // should be unchanged across this call.
            Say("[lifecycle] PauseHandler.TogglePause -> isPaused=" +
                        SafeGet(() => PauseHandler.main != null ? PauseHandler.main.isPaused.ToString() : "<no main>") +
                        ", Time.timeScale=" + Time.timeScale);
        }

        private static void Trace_StartRun()
        {
            Say("[lifecycle] PreRunScript.StartRun -> run starting. " + DescribeRunSettings());
        }

        private static void StartWatcher()
        {
            if (_watcherHost != null) return;
            _watcherHost = new GameObject("CCoop.Probe.Watcher");
            UnityEngine.Object.DontDestroyOnLoad(_watcherHost);
            _watcherHost.hideFlags = HideFlags.HideAndDontSave;
            _watcherHost.AddComponent<ProbeWatcher>();
        }

        // ---------------------------------------------------------------- generation watcher

        /// <summary>
        /// Polls world-generation state from its own <c>DontDestroyOnLoad</c> object.
        ///
        /// This must NOT live on the plugin itself: BepInEx's plugin component is
        /// destroyed when the game changes scene (verified on this build — the plugin's
        /// Update stops after the first PreGen -> SampleScene transition), so a watcher
        /// hosted there never observes a single layer. The co-op session manager will
        /// need exactly this treatment (plan M1 gate 6).
        /// </summary>
        private sealed class ProbeWatcher : MonoBehaviour
        {
            private bool _wasGenerating;
            private bool _sawWorld;

            private void Update()
            {
                try
                {
                    var world = WorldGeneration.world;
                    if (world == null)
                    {
                        _wasGenerating = false;
                        return;
                    }

                    if (!_sawWorld)
                    {
                        // Proves the watcher is alive; its absence in a log means the
                        // watcher never reached the world, not that nothing happened.
                        _sawWorld = true;
                        Say("[worldgen] WorldGeneration.world detected (watcher live).");
                    }

                    bool generating = world.generatingWorld;

                    if (generating && !_wasGenerating)
                        Say("[worldgen] generation started (biomeDepth=" + world.biomeDepth +
                            "/" + world.amountOfLayers + "). " + DescribeRunSettings());

                    if (!generating && _wasGenerating)
                    {
                        Say("[worldgen] generation finished (biomeDepth=" + world.biomeDepth + ")");
                        MeasureWorldSnapshot(world);
                        BodyReport.WriteOnce(world);
                        WorldEntityReport.WriteOnce(world);
                    }

                    _wasGenerating = generating;
                }
                catch (Exception e)
                {
                    Warn("Generation watcher error (disabling): " + e.Message);
                    enabled = false;
                }
            }
        }

        /// <summary>
        /// Measures the payload the host would have to stream to clients, and hashes
        /// the terrain so successive runs can be compared. Two runs with identical
        /// run settings producing different hashes is the empirical proof that no
        /// world seed exists and clients cannot regenerate the host's world.
        /// </summary>
        private static void MeasureWorldSnapshot(WorldGeneration world)
        {
            try
            {
                var blocks = AccessTools.Field(typeof(WorldGeneration), "worldBlocks")?.GetValue(world) as Array;
                if (blocks == null)
                {
                    Warn("[snapshot] worldBlocks not readable; cannot size the payload.");
                    return;
                }

                int w = blocks.GetLength(0);
                int h = blocks.GetLength(1);
                int count = w * h;

                var raw = new byte[count * sizeof(ushort)];
                Buffer.BlockCopy(blocks, 0, raw, 0, raw.Length);

                string sha;
                using (var s = SHA256.Create())
                    sha = BitConverter.ToString(s.ComputeHash(raw)).Replace("-", "").ToLowerInvariant();

                int deflated = Deflate(raw).Length;
                int rleThenDeflated = Deflate(RunLengthEncode(raw)).Length;

                var distinct = new HashSet<ushort>();
                for (int i = 0; i + 1 < raw.Length; i += 2)
                    distinct.Add((ushort)(raw[i] | (raw[i + 1] << 8)));

                var sb = new StringBuilder();
                sb.AppendLine("---------------- world snapshot measurement ----------------");
                sb.AppendLine("  layer (biomeDepth) : " + world.biomeDepth + " / " + world.amountOfLayers);
                sb.AppendLine("  block grid         : " + w + " x " + h + " (" + count.ToString("N0") + " blocks)");
                sb.AppendLine("  CHUNKSIZE          : " + WorldGeneration.CHUNKSIZE);
                sb.AppendLine("  distinct block ids : " + distinct.Count);
                sb.AppendLine("  raw terrain        : " + Kb(raw.Length));
                sb.AppendLine("  deflate            : " + Kb(deflated));
                sb.AppendLine("  RLE + deflate      : " + Kb(rleThenDeflated));
                sb.AppendLine("  terrain sha256     : " + sha);
                sb.AppendLine("  blockDamages       : " + SafeGet(() => world.blockDamages.Count.ToString()));
                sb.AppendLine("  realTimeElapsed    : " + world.realTimeElapsed.ToString("F1") + "s");
                sb.Append("  NOTE: compare terrain sha256 across runs with identical run settings.");
                Say(sb.ToString());
            }
            catch (Exception e)
            {
                Warn("[snapshot] measurement failed: " + e.Message);
            }
        }

        // ---------------------------------------------------------------- helpers

        private static string Kb(int bytes)
        {
            return bytes.ToString("N0") + " B (" + (bytes / 1024.0).ToString("F1") + " KiB)";
        }

        private static byte[] Deflate(byte[] data)
        {
            using (var ms = new MemoryStream())
            {
                using (var ds = new DeflateStream(ms, CompressionMode.Compress, true))
                    ds.Write(data, 0, data.Length);
                return ms.ToArray();
            }
        }

        /// <summary>
        /// Run-length encodes 16-bit blocks as (value, count) pairs. Terrain is
        /// large contiguous runs of the same block, so this is a realistic estimate
        /// of what a purpose-built snapshot encoder would achieve.
        /// </summary>
        private static byte[] RunLengthEncode(byte[] raw)
        {
            var outBuf = new List<byte>(raw.Length / 4);
            int i = 0;
            while (i + 1 < raw.Length)
            {
                ushort value = (ushort)(raw[i] | (raw[i + 1] << 8));
                ushort run = 1;
                int j = i + 2;
                while (j + 1 < raw.Length && run < ushort.MaxValue)
                {
                    ushort next = (ushort)(raw[j] | (raw[j + 1] << 8));
                    if (next != value) break;
                    run++;
                    j += 2;
                }

                outBuf.Add((byte)(value & 0xFF));
                outBuf.Add((byte)(value >> 8));
                outBuf.Add((byte)(run & 0xFF));
                outBuf.Add((byte)(run >> 8));
                i = j;
            }

            return outBuf.ToArray();
        }

        private static string DescribeWorldBriefly()
        {
            return SafeGet(() =>
            {
                var w = WorldGeneration.world;
                if (w == null) return "world=<null>";
                return "world: biomeDepth=" + w.biomeDepth + ", generating=" + w.generatingWorld;
            });
        }

        private static string DescribeRunSettings()
        {
            return SafeGet(() =>
            {
                var names = new[]
                {
                    "baselootdensity", "lootmultiplier", "basetrapdensity", "trapincrease",
                    "timelimit", "startingsupplies", "xpgain", "metabolismrate",
                    "healingrate", "fracturepain", "bleedrate", "infectionspeed",
                    "ambientlight", "unchipped"
                };

                var parts = new List<string>(names.Length);
                foreach (var n in names)
                {
                    try { parts.Add(n + "=" + WorldGeneration.GetRunSettingFloat(n)); }
                    catch { /* not all settings are floats; skip */ }
                }
                return "runSettings[" + string.Join(" ", parts.ToArray()) + "]";
            });
        }

        private static string SafeGet(Func<string> f)
        {
            try { return f(); }
            catch (Exception e) { return "<err:" + e.GetType().Name + ">"; }
        }
    }
}
