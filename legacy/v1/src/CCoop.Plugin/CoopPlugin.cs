using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using CCoop.Core;
using CCoop.Protocol;
using CCoop.Protocol.Messages;
using CCoop.Transport.LiteNet;
using HarmonyLib;
using UnityEngine;

namespace CCoop.Plugin
{
    /// <summary>
    /// M3a: host or join a session and see other players move in the world.
    ///
    /// World state is NOT synchronised yet, so each peer generates its own terrain.
    /// Players will appear at the correct coordinates but in different terrain until
    /// M3b streams the host's world — the game has no seed, so clients cannot
    /// reproduce it.
    /// </summary>
    [BepInPlugin(Guid, "CasualtiesCoop", Version)]
    public sealed class CoopPlugin : BaseUnityPlugin
    {
        public const string Guid = "ccoop.plugin";
        public const string Version = "0.9.0";

        /// <summary>Steam App ID of the supported build.</summary>
        public const uint SupportedAppId = 4576510;

        private static CoopPlugin _self;
        internal static ManualLogSource Log => _self?.Logger;

        internal ConfigEntry<string> JoinAddress;
        internal ConfigEntry<int> Port;
        internal ConfigEntry<string> PlayerName;
        internal ConfigEntry<bool> RequireIdenticalPlugins;
        internal ConfigEntry<bool> AllowSpeedUp;
        internal ConfigEntry<int> SendRateHz;
        internal ConfigEntry<KeyboardShortcut> HostKey;
        internal ConfigEntry<KeyboardShortcut> JoinKey;
        internal ConfigEntry<KeyboardShortcut> LeaveKey;
        internal ConfigEntry<KeyboardShortcut> VerifyKey;
        internal ConfigEntry<bool> ShowOverlay;

        private void Awake()
        {
            _self = this;
            OpenLogFile();

            Port = Config.Bind("Network", "Port", 7777, "UDP port to host on / connect to.");
            JoinAddress = Config.Bind("Network", "JoinAddress", "127.0.0.1",
                "Host address to join. 127.0.0.1 for two instances on this machine.");
            PlayerName = Config.Bind("Network", "PlayerName", Environment.UserName,
                "Name shown to other players.");
            SendRateHz = Config.Bind("Network", "SendRateHz", 20,
                "Movement snapshots per second. Must stay below RemoteProxy.InterpolationDelaySeconds.");
            RequireIdenticalPlugins = Config.Bind("Network", "RequireIdenticalPlugins", true,
                "Require joining clients to have exactly the host's plugin set. " +
                "Other gameplay mods change items, world and body behaviour, and because " +
                "clients take world state from the host, a mismatch surfaces as unexplained desync.");

            AllowSpeedUp = Config.Bind("Session", "AllowSpeedUp", true,
                "Whether players may fast-forward time. Read on the HOST only — the speed " +
                "is shared, so it is the host's session setting that decides. Turn it off " +
                "if being dragged to 20x by a partner is more disruptive than useful.");

            HostKey = Config.Bind("Controls", "HostKey", new KeyboardShortcut(KeyCode.F6), "Start hosting.");
            JoinKey = Config.Bind("Controls", "JoinKey", new KeyboardShortcut(KeyCode.F7), "Join JoinAddress.");
            LeaveKey = Config.Bind("Controls", "LeaveKey", new KeyboardShortcut(KeyCode.F8), "Leave the session.");
            VerifyKey = Config.Bind("Controls", "VerifyKey", new KeyboardShortcut(KeyCode.F9),
                "Log a hash of the live terrain and entity registry. Press on both peers and " +
                "diff the two logs: equal hashes are proof the worlds still agree.");
            ShowOverlay = Config.Bind("UI", "ShowOverlay", true, "Show the session status overlay.");

            // Must be our own persistent object: BepInEx plugin components are destroyed
            // on scene load in this game, so anything hosted here would stop updating
            // after the first PreGen -> SampleScene transition and take the session with it.
            var host = new GameObject("CCoop.Runner") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(host);
            host.AddComponent<CoopRunner>();

            SaveGuard.Install(Logger);

            try
            {
                // Patches WorldGeneration.InstantiateWorld so clients can skip local
                // generation. Without it a client builds a different world from the host.
                var harmony = new Harmony(Guid);
                harmony.PatchAll(typeof(InstantiateWorldPatch));
                harmony.PatchAll(typeof(PickUpItemPatch));
                harmony.PatchAll(typeof(DropItemPatch));
                harmony.PatchAll(typeof(SetBlockPatch));
                harmony.PatchAll(typeof(DamageBlockPatch));
                harmony.PatchAll(typeof(UtilsCreatePatch));
                harmony.PatchAll(typeof(CheckSeatingPatch));
                harmony.PatchAll(typeof(BuildingEntityUpdatePatch));
                harmony.PatchAll(typeof(SetTimeScalePatch));
                harmony.PatchAll(typeof(AttackPatch));
                harmony.PatchAll(typeof(GenerateEntityAtPosPatch));
                harmony.PatchAll(typeof(ThrowItemPatch));
                harmony.PatchAll(typeof(LifepodButtonPatch));

                // Report what actually got patched. A patch class that is written but
                // never registered fails completely silently — DropItemPatch was missing
                // from this list and drops simply never synced.
                int patched = 0;
                foreach (var m in harmony.GetPatchedMethods()) patched++;
                Say("patches installed: " + patched + " methods hooked");
            }
            catch (Exception e)
            {
                Logger.LogError("could not patch InstantiateWorld; world sync will not work: " + e);
            }

            Say("CasualtiesCoop " + Version + " ready. " +
                HostKey.Value + " host, " + JoinKey.Value + " join, " + LeaveKey.Value + " leave.");
            Say("per-process log: " + (LogFilePath ?? "<unavailable>"));
        }

        internal static CoopPlugin Instance => _self;

        // ---------------------------------------------------------------- logging

        /// <summary>
        /// Per-process log file.
        ///
        /// Two instances on one machine share <c>BepInEx\LogOutput.log</c> and contend
        /// for it — the second instance may write nothing at all. Co-op diagnostics are
        /// worthless if one side of a session is invisible, so every line also goes to a
        /// PID-stamped file. This is not optional polish: relying on the shared log has
        /// already produced one wrong conclusion during development.
        /// </summary>
        private static StreamWriter _file;
        private static readonly object FileLock = new object();

        internal static string LogFilePath { get; private set; }

        internal static void OpenLogFile()
        {
            if (_file != null) return;
            try
            {
                var dir = Path.Combine(Application.persistentDataPath, "CCoop");
                Directory.CreateDirectory(dir);
                int pid = System.Diagnostics.Process.GetCurrentProcess().Id;
                LogFilePath = Path.Combine(dir,
                    string.Format("coop-{0:yyyyMMdd-HHmmss}-pid{1}.log", DateTime.Now, pid));
                _file = new StreamWriter(new FileStream(
                    LogFilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
                {
                    AutoFlush = true,
                };
            }
            catch { _file = null; }
        }

        internal static void Say(string s)
        {
            Log?.LogInfo(s);
            WriteLine("INFO ", s);
        }

        internal static void Warn(string s)
        {
            Log?.LogWarning(s);
            WriteLine("WARN ", s);
        }

        private static void WriteLine(string level, string msg)
        {
            if (_file == null) return;
            try
            {
                lock (FileLock)
                    _file.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + " " + level + msg);
            }
            catch { /* logging must never throw into game code */ }
        }

        // ---------------------------------------------------------------- fingerprint

        /// <summary>
        /// Builds the compatibility fingerprint. The assembly hash is the load-bearing
        /// field: clients receive the host's world wholesale, so a differing game build
        /// cannot be "mostly fine" — it desyncs in ways that look like gameplay bugs.
        /// </summary>
        internal static HelloMessage BuildHello(string playerName)
        {
            return new HelloMessage
            {
                ModVersion = Version,
                GameAppId = SupportedAppId,
                AssemblyHash = ComputeAssemblyHash(),
                UnityVersion = Application.unityVersion,
                PluginManifest = BuildPluginManifest(),
                PlayerName = playerName ?? string.Empty,
            };
        }

        internal static HostRequirements BuildRequirements(int maxPlayers, bool strictPlugins)
        {
            return new HostRequirements
            {
                ModVersion = Version,
                GameAppId = SupportedAppId,
                AssemblyHash = ComputeAssemblyHash(),
                PluginManifest = BuildPluginManifest(),
                RequireIdenticalPlugins = strictPlugins,
                MaxPlayers = maxPlayers,
                AcceptingNewPlayers = true,
            };
        }

        private static string _cachedHash;

        private static string ComputeAssemblyHash()
        {
            if (_cachedHash != null) return _cachedHash;
            try
            {
                var path = Path.Combine(Path.Combine(Application.dataPath, "Managed"), "Assembly-CSharp.dll");
                using (var sha = SHA256.Create())
                using (var fs = File.OpenRead(path))
                    _cachedHash = BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
            }
            catch (Exception e)
            {
                Warn("could not hash game assembly: " + e.Message);
                // A sentinel of the right shape: it will never match a real host, so the
                // handshake fails loudly instead of admitting an unverifiable client.
                _cachedHash = new string('0', ProtocolConstants.Sha256HexLength);
            }
            return _cachedHash;
        }

        private static string BuildPluginManifest()
        {
            try
            {
                var ids = Chainloader.PluginInfos.Values
                    .Select(p => p.Metadata.GUID + "@" + p.Metadata.Version)
                    .OrderBy(s => s, StringComparer.Ordinal);
                return string.Join(",", ids.ToArray());
            }
            catch { return string.Empty; }
        }
    }

    /// <summary>
    /// Persistent driver: owns the session, pumps it on the Unity main thread, sends the
    /// local player's movement, and keeps a proxy per remote player.
    /// </summary>
    public sealed class CoopRunner : MonoBehaviour
    {
        private CoopSession _session;
        private LiteNetTransport _transport;
        private readonly Dictionary<byte, RemoteProxy> _proxies = new Dictionary<byte, RemoteProxy>();
        private readonly List<string> _recentLog = new List<string>();

        private float _nextSendTime;
        private string _statusLine = "offline";

        // Auto-start support, for repeatable two-instance testing.
        private bool _autoStartDone;
        private float _autoStartAt = -1f;

        private CoopPlugin P => CoopPlugin.Instance;

        /// <summary>The live runner, for Harmony patches that cannot be given one.</summary>
        internal static CoopRunner Active { get; private set; }

        internal bool InSession => _session != null && _session.Status == SessionStatus.Established;
        internal bool IsHost => _session is HostSession;

        private void Awake() => Active = this;

        /// <summary>
        /// Reads the per-process auto-start mode.
        ///
        /// An environment variable rather than config, deliberately: two instances on
        /// one machine share <c>BepInEx\config</c> (Unity derives persistentDataPath
        /// from company/product, not install path), so a config setting could not tell
        /// the host instance from the client one. Env vars are per-process.
        ///
        ///   set CCOOP_AUTOSTART=host   -- host on startup
        ///   set CCOOP_AUTOSTART=join   -- join JoinAddress on startup
        /// </summary>
        private static string AutoStartMode()
        {
            try { return (Environment.GetEnvironmentVariable("CCOOP_AUTOSTART") ?? "").Trim().ToLowerInvariant(); }
            catch { return string.Empty; }
        }

        private void Start()
        {
            var mode = AutoStartMode();
            if (mode == "host" || mode == "join")
            {
                // Delay so the transport starts after the game has finished its own
                // startup work; hosting mid-load makes failures hard to attribute.
                _autoStartAt = Time.time + 3f;
                Note("auto-start armed: " + mode);
            }
        }

        // ---------------------------------------------------------------- lifecycle

        private void Update()
        {
            try
            {
                HandleAutoStart();
                HandleAutoRun();
                HandleInput();
                HandleVerifyTrigger();
                HandleKeyProbe();
                WindowTitle.Set(_session == null ? "OFFLINE" : (IsHost ? "HOST" : "CLIENT"));

                if (_session != null)
                {
                    // Raises every session event on this thread, so handlers may touch
                    // Unity objects safely.
                    _session.Poll();
                    SendLocalTransform();
                    SendLocalAppearance();
                    SendLocalEquipment();
                    ReconcileProxies();
                    HandleHostWorldBroadcast();
                    HandleClientWorldApply();
                    HandleWorldMutations();
                    HandleWorldEvents();
                    HandleBlockSyncSelfTest();
                    HandlePoseDiagnostics();
                }
            }
            catch (Exception e)
            {
                CoopPlugin.Warn("runner error: " + e);
                Note("error: " + e.Message);
            }
        }

        private void HandleAutoStart()
        {
            if (_autoStartDone || _autoStartAt < 0f || Time.time < _autoStartAt) return;
            _autoStartDone = true;

            var mode = AutoStartMode();
            if (mode == "host") StartHosting();
            else if (mode == "join") StartJoining();
        }

        // ------------------------------------------------------------ test hooks
        //
        // Both are opt-in via environment variable and do nothing in normal play. They
        // exist so a two-instance session can be verified end-to-end without a human
        // driving two windows, which is otherwise the only way to test proxies.

        private bool _autoRunTried;
        private float _nextPoseDiag;

        private static bool EnvFlag(string name)
        {
            try
            {
                var v = Environment.GetEnvironmentVariable(name);
                return !string.IsNullOrEmpty(v) && v != "0";
            }
            catch { return false; }
        }

        /// <summary>
        /// CCOOP_AUTORUN=1 — enters a run without menu input.
        ///
        /// Note this calls <c>PreRunScript.StartRun</c> directly. The normal
        /// descend/settings flow does NOT go through it (observed during M0), so this
        /// is a shortcut for automation, not a reproduction of the real entry path.
        /// </summary>
        private void HandleAutoRun()
        {
            if (_autoRunTried || !EnvFlag("CCOOP_AUTORUN")) return;

            // A client must be in an established session BEFORE entering a run, or
            // InstantiateWorld runs while SuppressGeneration is still false and the
            // client generates a throwaway world first. That is also the correct
            // real-world order: join, then descend.
            if (AutoStartMode() == "join" &&
                (_session == null || _session.Status != SessionStatus.Established)) return;

            // Only from the menu, and only once the world is definitely not loaded.
            if (WorldGeneration.world != null) return;
            if (PreRunScript.instance == null) return;

            _autoRunTried = true;
            try
            {
                PreRunScript.instance.StartRun();
                Note("auto-run: StartRun() invoked");
            }
            catch (Exception e)
            {
                Note("auto-run failed: " + e.Message);
            }
        }

        /// <summary>
        /// CCOOP_POSEDIAG=1 — periodically logs the local body's limb poses and each
        /// proxy's applied poses, so host and client logs can be diffed to check pose
        /// fidelity objectively rather than by eye.
        /// </summary>
        private void HandlePoseDiagnostics()
        {
            if (!EnvFlag("CCOOP_POSEDIAG")) return;
            if (Time.time < _nextPoseDiag) return;
            _nextPoseDiag = Time.time + 2f;

            var body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
            if (body != null && body.limbs != null)
                Note("LOCALPOSE " + DescribeLimbs(body.limbs));

            foreach (var kv in _proxies)
            {
                var proxy = kv.Value;
                if (proxy != null) Note("PROXYPOSE peer=" + kv.Key + " " + proxy.DescribePose());
            }
        }

        private static string DescribeLimbs(Limb[] limbs)
        {
            var sb = new StringBuilder();
            sb.Append("n=").Append(limbs.Length).Append(' ');
            // Head, UpTorso and a hand are enough to tell a real pose from a rest pose.
            foreach (int i in new[] { 0, 1, 5 })
            {
                if (i >= limbs.Length || limbs[i] == null) continue;
                var t = limbs[i].transform;
                sb.Append(limbs[i].name).Append('(')
                  .Append(t.localPosition.x.ToString("F3")).Append(',')
                  .Append(t.localPosition.y.ToString("F3")).Append(',')
                  .Append(t.localEulerAngles.z.ToString("F1")).Append(") ");
            }
            return sb.ToString();
        }

        private float _selfTestAt = -1f;
        private int _selfTestStage;

        /// <summary>
        /// CCOOP_BLOCKTEST=1 — proves terrain sync end to end without a human mining.
        ///
        /// The host writes a fixed patch of blocks a few seconds after the world settles;
        /// both peers then log their live terrain hash. Equal hashes are proof the edit
        /// crossed the wire and landed in the same cells, which is the only claim that
        /// matters and the one a structural check cannot make. Deliberately far from
        /// either player and a solid block rather than air, so nothing falls and no prop
        /// loses its footing while the test runs.
        /// </summary>
        private void HandleBlockSyncSelfTest()
        {
            if (!EnvFlag("CCOOP_BLOCKTEST")) return;

            if (_selfTestStage == 0)
            {
                if (!WorldMutationsLive) return;
                _selfTestStage = 1;
                _selfTestAt = Time.time + 8f;   // let both peers finish settling
                Note("BLOCKTEST armed; entities " + _worldSync.Entities.Fingerprint() +
                     " health " + EntityHealthSum() + " items " + WorldItemCount());
                return;
            }

            if (_selfTestStage > 3 || Time.time < _selfTestAt) return;

            if (_selfTestStage == 1)
            {
                _selfTestStage = 2;
                _selfTestAt = Time.time + 4f;   // time for the round trip

                var world = WorldGeneration.world;
                if (IsHost && world != null)
                {
                    int written = 0;
                    for (int x = 100; x < 105; x++)
                        for (int y = 100; y < 105; y++)
                        {
                            world.SetBlock(new Vector2Int(x, y), 1);
                            written++;
                        }
                    Note("BLOCKTEST host wrote " + written + " cells at (100..104, 100..104)");

                    uint destroyed = DestroyOneNetworkedBuilding();
                    DamageOneNetworkedBuilding(destroyed);

                    // Through the game's own method, so the patch sees it exactly as it
                    // would see a player pressing the speed key.
                    if (PlayerCamera.main != null)
                    {
                        PlayerCamera.main.SetTimeScale(PlayerCamera.SpeedType.Fast);
                        Note("BLOCKTEST host set speed Fast, timeScale now " +
                             Time.timeScale.ToString("0.##"));
                    }
                }
                return;
            }

            if (_selfTestStage == 2)
            {
                _selfTestStage = 3;

                // Long enough that EVERY peer has logged its fingerprint first. The two
                // sides do not reach this stage together — a client arms its self-test
                // only once the world has been transferred and applied, several seconds
                // after the host — so a gap sized for the host alone lets the earthquake
                // land between the two VERIFY lines and fake a desync.
                _selfTestAt = Time.time + 12f;

                LogWorldFingerprint();
                return;
            }

            // Deliberately after the fingerprint. An earthquake tears out blocks around
            // each player, so running it earlier would move the very numbers the
            // comparison depends on and look like a desync.
            _selfTestStage = 4;
            if (IsHost && WorldGeneration.world != null)
            {
                WorldGeneration.world.earthquakeTime = 4f;
                Note("BLOCKTEST host started a 4s earthquake");
            }
        }

        /// <summary>
        /// Kills one networked crate, to exercise destruction and loot replication the
        /// same way the block write exercises terrain.
        ///
        /// Damages it the way the game does — by zeroing <c>health</c> — rather than
        /// destroying the object, so the real <c>BuildingEntity.Update</c> path runs and
        /// the test proves the hook the game actually uses.
        /// </summary>
        private uint DestroyOneNetworkedBuilding()
        {
            foreach (var id in _worldSync.Entities.Ids())
            {
                if (!_worldSync.Entities.TryGet(id, out var go)) continue;

                var entity = go.GetComponent<BuildingEntity>();
                if (entity == null || entity.health < 0.5f) continue;

                // One that actually drops something, or the loot half of the test proves
                // nothing.
                bool drops = (entity.itemsDropOnDestroy != null && entity.itemsDropOnDestroy.Length > 0)
                          || (entity.alwaysDrop != null && entity.alwaysDrop.Length > 0)
                          || entity.guaranteedDropAmount > 0;
                if (!drops) continue;

                Note("BLOCKTEST destroying entity " + id + " (" +
                     (_worldSync.Entities.KeyOf(id) ?? "?") + ")");
                entity.health = 0f;
                return id;
            }

            Note("BLOCKTEST found no networked building entity with drops");
            return uint.MaxValue;
        }

        /// <summary>
        /// Wounds one crate without felling it, to exercise damage replication.
        ///
        /// Kept well clear of the death threshold on purpose: the killing blow is
        /// deliberately never sent — the despawn carries it — so a test that destroyed
        /// the entity would prove nothing about damage.
        /// </summary>
        private void DamageOneNetworkedBuilding(uint skip)
        {
            const float amount = 25f;

            foreach (var id in _worldSync.Entities.Ids())
            {
                if (id == skip) continue;
                if (!_worldSync.Entities.TryGet(id, out var go)) continue;

                var entity = go.GetComponent<BuildingEntity>();
                if (entity == null || entity.health < amount * 2f) continue;

                entity.health -= amount;
                Note("BLOCKTEST damaged entity " + id + " (" +
                     (_worldSync.Entities.KeyOf(id) ?? "?") + ") by " + amount +
                     ", now " + entity.health.ToString("F0"));
                return;
            }

            Note("BLOCKTEST found no networked building entity healthy enough to wound");
        }

        /// <summary>
        /// Reads a bound shortcut, deliberately NOT through
        /// <c>KeyboardShortcut.IsDown()</c>.
        ///
        /// BepInEx's version additionally requires that <em>no other key at all</em> is
        /// held: it tests every supported key code except the mouse buttons and fails if
        /// any of them is down. In a config menu that is sensible; in a game where the
        /// player is holding a movement key, or crouch, or the throw key, it means the
        /// hotkey silently does nothing most of the time it is pressed. That is exactly
        /// how F9 came to look broken — it was never reached, and it prints only to the
        /// log, so there was nothing on screen to contradict it.
        ///
        /// So: main key down, configured modifiers held, nothing else considered.
        /// </summary>
        private static bool Pressed(ConfigEntry<KeyboardShortcut> binding)
        {
            if (binding == null) return false;

            var shortcut = binding.Value;
            if (shortcut.MainKey == KeyCode.None) return false;
            if (!Input.GetKeyDown(shortcut.MainKey)) return false;

            foreach (var modifier in shortcut.Modifiers)
                if (!Input.GetKey(modifier)) return false;

            return true;
        }

        private float _nextTriggerPoll;
        private DateTime _lastTriggerStamp = DateTime.MinValue;

        /// <summary>
        /// Fires a VERIFY on every running instance when a sentinel file is touched.
        ///
        /// This exists because F9 could not be relied on — see <see cref="Pressed"/> — but
        /// it turns out to be the better instrument regardless. Both processes share
        /// <c>persistentDataPath</c> (Unity derives it from company and product, not
        /// install path), so ONE touch samples BOTH peers within a second of each other.
        /// A human pressing a key in two windows cannot do that, and simultaneity is the
        /// whole point: the two worlds are being compared at a moment in time, and
        /// anything the players do between the two samples is indistinguishable from
        /// divergence.
        ///
        /// The file is never deleted. Each process remembers the timestamp it last acted
        /// on, so neither can consume the trigger before the other has seen it.
        ///
        ///   (from any shell)  echo. > "%USERPROFILE%\AppData\LocalLow\Orsoniks\CasualtiesUnknown\CCoop\verify.trigger"
        /// </summary>
        private void HandleVerifyTrigger()
        {
            if (Time.unscaledTime < _nextTriggerPoll) return;
            _nextTriggerPoll = Time.unscaledTime + 0.5f;

            try
            {
                var path = Path.Combine(Path.Combine(Application.persistentDataPath, "CCoop"),
                                        "verify.trigger");
                if (!File.Exists(path)) return;

                var stamp = File.GetLastWriteTimeUtc(path);
                if (stamp <= _lastTriggerStamp) return;

                // First sighting of a file left over from an earlier run must not fire:
                // it would produce a VERIFY on start-up that nobody asked for and that
                // the other peer, started at a different moment, may not match.
                bool first = _lastTriggerStamp == DateTime.MinValue;
                _lastTriggerStamp = stamp;
                if (first) return;

                LogWorldFingerprint();
            }
            catch (Exception e)
            {
                Note("verify trigger unreadable: " + e.Message);
            }
        }

        /// <summary>
        /// CCOOP_KEYPROBE=1 — logs every key this process sees go down.
        ///
        /// F9 produced no VERIFY line even after the binding was read directly, which
        /// leaves two very different explanations: either the plugin sees no keyboard
        /// input at all, or it sees keys but something upstream is eating F9 in
        /// particular. This separates them, and nothing else will.
        /// </summary>
        private void HandleKeyProbe()
        {
            if (!EnvFlag("CCOOP_KEYPROBE") || !Input.anyKeyDown) return;

            foreach (KeyCode code in Enum.GetValues(typeof(KeyCode)))
            {
                if (code == KeyCode.None) continue;
                try { if (Input.GetKeyDown(code)) Note("[keyprobe] " + code); }
                catch { return; }   // legacy input unavailable; one report is enough
            }
        }

        private void HandleInput()
        {
            if (P == null) return;

            if (Pressed(P.HostKey) && _session == null) StartHosting();
            else if (Pressed(P.JoinKey) && _session == null) StartJoining();
            else if (Pressed(P.LeaveKey) && _session != null) Leave();
            else if (Pressed(P.VerifyKey)) LogWorldFingerprint();
        }

        /// <summary>
        /// Prints a hash of the live world.
        ///
        /// The one check that actually answers "do these two players still share a
        /// world?", and it has to be taken from the live grid rather than from the
        /// payload that was transferred — the whole point is to catch edits that landed
        /// on one peer and not the other, long after the transfer succeeded.
        /// </summary>
        /// <summary>
        /// Every <c>Item</c> alive in this process.
        ///
        /// Counts unregistered items too, which is the point: the registry can only see
        /// what we gave an id, so a peer that wrongly rolled its own copy of somebody
        /// else's loot would look identical there and show up only here.
        /// </summary>
        private static int WorldItemCount()
        {
            try { return Item.allItems != null ? Item.allItems.Count : -1; }
            catch { return -1; }
        }

        /// <summary>
        /// Total health across every networked world object, as "count:sum".
        ///
        /// The cheapest way to answer "have both peers taken the same damage?". Absolute
        /// values differ — the client is short one entity that never spawns — so compare
        /// how the sum *moves*: replicated damage moves it by the same amount on both
        /// sides, and damage that stayed local moves only one.
        /// </summary>
        private string EntityHealthSum()
        {
            double sum = 0;
            int n = 0;
            foreach (var id in _worldSync.Entities.Ids())
            {
                GameObject go;
                if (!_worldSync.Entities.TryGet(id, out go)) continue;
                var entity = go.GetComponent<BuildingEntity>();
                if (entity == null) continue;
                sum += entity.health;
                n++;
            }
            return n + ":" + sum.ToString("F0");
        }

        private void LogWorldFingerprint()
        {
            if (_worldSync == null || !WorldSync.WorldReady)
            {
                Note("VERIFY no world to hash");
                WindowTitle.SetNote("VERIFY: no world");
                return;
            }

            ushort a, b, c;
            var live = _worldSync.CaptureTerrain(out a, out b, out c);
            string role = _session == null ? "offline" : (IsHost ? "host" : "client");
            string terrain = live != null ? WorldSnapshotCodec.Sha256Hex(live) : "<unreadable>";

            var fluid = _worldSync.CaptureFluid();

            Note("VERIFY " + role + " terrain " + terrain +
                 " fluid " + (fluid != null
                     ? WorldSnapshotCodec.Sha256Hex(fluid).Substring(0, 16) : "<unreadable>") +
                 " entities " + _worldSync.Entities.Fingerprint() +
                 " runtime " + _worldSync.Entities.RuntimeCount +
                 " health " + EntityHealthSum() +
                 " timescale " + Time.timeScale.ToString("0.##") +
                 " modifier " + LayerModifierSync.Describe(LayerModifierSync.Current) +
                 " temp " + (WorldGeneration.world != null
                     ? WorldGeneration.world.temperatureOffset.ToString("F0") : "?") +
                 " items " + WorldItemCount() +
                 " blocks sent/applied " + (_blockSync != null ? _blockSync.TotalSent : 0) +
                 "/" + (_blockSync != null ? _blockSync.TotalApplied : 0) +
                 " damage sent/applied " +
                 (_damageSync != null ? _damageSync.TotalSent.ToString("F0") : "0") + "/" +
                 (_damageSync != null ? _damageSync.TotalApplied.ToString("F0") : "0"));

            // The point of F9 is to compare two peers, and its output lands in two
            // different files. Putting the deciding fields in the window title moves the
            // comparison on screen, with both windows visible at once — and gives the
            // keypress the visible feedback it never had, which is why a dead binding
            // went unnoticed. All four must match; terrain is the definitive one.
            WindowTitle.SetNote("VERIFY " + terrain.Substring(0, Math.Min(8, terrain.Length)) +
                                " rt" + _worldSync.Entities.RuntimeCount +
                                " hp" + EntityHealthSum() +
                                " x" + Time.timeScale.ToString("0.##"));
        }

        private void StartHosting()
        {
            try
            {
                _transport = new LiteNetTransport();
                _transport.StartHost((ushort)P.Port.Value, ProtocolConstants.MaxPeers);

                var host = new HostSession(
                    _transport,
                    CoopPlugin.BuildRequirements(4, P.RequireIdenticalPlugins.Value),
                    P.PlayerName.Value);

                Attach(host);
                _statusLine = "hosting on port " + P.Port.Value;
                Note(_statusLine);
            }
            catch (Exception e)
            {
                CoopPlugin.Warn("could not host: " + e.Message);
                Note("host failed: " + e.Message);
                Cleanup();
            }
        }

        private void StartJoining()
        {
            try
            {
                _transport = new LiteNetTransport();
                var client = new ClientSession(_transport, CoopPlugin.BuildHello(P.PlayerName.Value));
                Attach(client);

                _transport.StartClient(P.JoinAddress.Value, (ushort)P.Port.Value);
                _statusLine = "connecting to " + P.JoinAddress.Value + ":" + P.Port.Value;
                Note(_statusLine);
            }
            catch (Exception e)
            {
                CoopPlugin.Warn("could not join: " + e.Message);
                Note("join failed: " + e.Message);
                Cleanup();
            }
        }

        private void Attach(CoopSession session)
        {
            _session = session;
            _worldSync = new WorldSync(s => { Note(s); return null; });
            _blockSync = new BlockSync(Note);
            _damageSync = new EntityDamageSync(Note);
            _itemMotion = new ItemMotionSync(s => { Note(s); return null; });

            // Arm the guard as soon as a session exists, not once it is established: a
            // save during the handshake would hit the same shared slot.
            SaveGuard.SessionActive = true;

            // Only a client suppresses generation; the host must generate the world it
            // is going to send.
            InstantiateWorldPatch.SuppressGeneration = session is ClientSession;

            session.WorldSnapshotReady += OnWorldSnapshotReady;
            session.InteractionRequested += OnInteractionRequested;
            session.InteractionResolved += OnInteractionResolved;

            // Buffered rather than applied here: the event fires inside Poll, which can
            // land in the middle of a world transfer, and there may be no grid to write
            // into yet.
            session.BlockDeltasReceived += (peer, m) => _blockSync?.Enqueue(m);
            session.BlockHitsReceived += (peer, m) => _blockSync?.Enqueue(m);
            session.EntitySpawned += (peer, m) => _blockSync?.Enqueue(m);
            session.EntityDespawned += (peer, m) => _blockSync?.Enqueue(m);
            session.EntityDamaged += (peer, m) => _damageSync?.Enqueue(m);
            session.EntityMoved += (peer, m) => _itemMotion?.Enqueue(m);
            session.PlayerAttacked += OnPlayerAttacked;
            session.PlayerAppearanceChanged += OnPlayerAppearance;
            session.PlayerEquipmentChanged += OnPlayerEquipment;
            session.WorldEventReceived += OnWorldEvent;
            session.TimeScaleRequested += OnTimeScaleRequested;
            session.TimeScaleChanged += m => TimeScaleSync.Apply(m.Speed, Note);
            session.WorldSnapshotProgress += p => _worldProgress = p;
            session.PeerLayerReady += (peer, ok, detail) =>
                Note("peer " + peer + " world " + (ok ? "applied" : "FAILED: " + detail));
            session.Diagnostic += Note;
            session.RemotePlayerJoined += OnPlayerJoined;
            session.RemotePlayerLeft += OnPlayerLeft;
            session.RemotePlayerMoved += OnPlayerMoved;
            session.SessionEstablished += () =>
            {
                _statusLine = "connected as peer " + session.LocalPeerId;
                Note(_statusLine);
            };
            session.SessionRejected += r =>
            {
                _statusLine = "rejected: " + r.Describe();
                Note(_statusLine);
            };
        }

        private void Leave()
        {
            Note("leaving session");
            Cleanup();
            _statusLine = "offline";
        }

        private void Cleanup()
        {
            SaveGuard.SessionActive = false;
            InstantiateWorldPatch.SuppressGeneration = false;
            _worldSync = null;
            _blockSync = null;
            _damageSync = null;
            _itemMotion = null;
            MutationsLiveThisFrame = false;
            _pending = null;
            _applied = null;
            _appliedInto = null;
            _worldSent = false;
            _worldBroadcastDone = false;
            _worldApplied = false;
            _worldProgress = 0f;
            _lastQuakeTime = 0f;
            _pendingDrops.Clear();

            foreach (var p in _proxies.Values)
                if (p != null) Destroy(p.gameObject);
            _proxies.Clear();

            try { _session?.Dispose(); } catch { /* teardown */ }
            _session = null;
            _transport = null;
        }

        private void OnDestroy() => Cleanup();

        // ---------------------------------------------------------------- players

        // ---------------------------------------------------------------- world sync

        private WorldSync _worldSync;
        /// <summary>Received and verified, waiting for a world to write it into.</summary>
        private LayerSnapshot _pending;

        // Last successfully applied payload, kept so it can be re-applied if the game
        // swaps in a new world array (layer transitions).
        /// <summary>The last snapshot applied, kept so it can be re-applied if the game
        /// swaps in a fresh world array on a layer transition.</summary>
        private LayerSnapshot _applied;
        private object _appliedInto;
        private bool _bodyFrozen;
        private bool _applyingWorld;
        private bool _worldSent;
        private float _worldProgress;
        private bool _worldWasGenerating;

        /// <summary>
        /// True once this layer's world has been broadcast at least once.
        ///
        /// Distinct from <see cref="_worldSent"/>, which a newly joining player clears to
        /// force a re-send: terrain edits must keep flowing to the clients that already
        /// have the world while that re-send is arranged.
        /// </summary>
        private bool _worldBroadcastDone;

        /// <summary>Client-side counterpart: the host's world is in place and settled.</summary>
        private bool _worldApplied;

        /// <summary>
        /// Host-side: send the world once generation finishes, and again whenever a new
        /// player joins a session whose world already exists.
        /// </summary>
        private void HandleHostWorldBroadcast()
        {
            var host = _session as HostSession;
            if (host == null || _worldSync == null) return;

            var world = WorldGeneration.world;
            bool generating = world != null && world.generatingWorld;

            // Generation finished: whatever we had is stale, and so is any terrain edit
            // still queued against the world that has just been thrown away.
            if (_worldWasGenerating && !generating)
            {
                _worldSent = false;
                _worldBroadcastDone = false;
                _blockSync?.DropOutbound();
                _blockSync?.DropInbound();
                _damageSync?.Clear();
                _itemMotion?.Clear();
            }
            _worldWasGenerating = generating;

            if (_worldSent || host.RemotePlayerCount == 0) return;
            if (!WorldSync.WorldReady) return;

            ushort cw, ch, cs;
            var raw = _worldSync.CaptureTerrain(out cw, out ch, out cs);
            if (raw == null) return;

            var body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
            float sx = body != null ? body.transform.position.x : 0f;
            float sy = body != null ? body.transform.position.y : 0f;

            var entities = _worldSync.CaptureEntities();

            if (EnvFlag("CCOOP_BLOCKTEST")) LayerModifierSync.ForceForTest(Note);

            // Read from the live modifier set rather than remembered: the host rolls it
            // deep inside GenerateWorld and there is no callback to hook.
            byte modifier = LayerModifierSync.Current;

            var fluid = _worldSync.CaptureFluid();

            uint id = host.BroadcastWorldSnapshot(raw, cw, ch, cs, world.biomeDepth, sx, sy,
                                                  entities, modifier, fluid);
            if (id != 0)
            {
                _worldSent = true;
                _worldBroadcastDone = true;
                Note("[layer] modifier " + LayerModifierSync.Describe(modifier) + " sent with the world");
                // Logged so a client's post-apply hash can be diffed against it: equal
                // hashes are proof the two peers hold identical terrain, which is the
                // whole point of the transfer.
                Note("TERRAINHASH host " + WorldSnapshotCodec.Sha256Hex(raw) +
                     " (" + raw.Length + " B, layer " + world.biomeDepth + ")");

                if (fluid != null)
                    Note("[world] FLUIDHASH host " + WorldSnapshotCodec.Sha256Hex(fluid) +
                         " (" + fluid.Length + " B)");
            }
        }

        private void OnWorldSnapshotReady(LayerSnapshot snapshot)
        {
            // The world may not exist yet (client still at the menu), so hold the
            // payload until there is something to write it into.
            _pending = snapshot;
            Note("world received (terrain " + snapshot.Terrain.Length + " B, fluid " +
                 snapshot.Fluid.Length + " B, " + (snapshot.Entities?.Entities.Count ?? 0) +
                 " entities); waiting for a world to apply into");
        }

        private void HandleClientWorldApply()
        {
            if (_applyingWorld || _worldSync == null) return;

            var blocks = WorldSync.CurrentBlocks;
            if (blocks == null) return;

            // Hold the body still from the moment a client has a world but no terrain in
            // it yet: otherwise the character falls through the empty world and takes
            // fall damage before the host's terrain arrives.
            if (!IsHost && !_bodyFrozen && _applied == null)
            {
                _worldSync.FreezeLocalBody(true);
                _bodyFrozen = true;
            }

            // If the world array was swapped out from under us — InstantiateWorld
            // allocates a fresh zeroed grid on every layer transition — the terrain we
            // applied is gone. Re-apply the cached payload rather than leaving the
            // client in an empty world with no way to recover short of a new transfer.
            if (_pending == null && _applied != null && !ReferenceEquals(blocks, _appliedInto))
            {
                Note("world array was replaced; re-applying cached terrain");
                _pending = _applied;
            }

            if (_pending == null) return;

            var snapshot = _pending;
            _pending = null;
            _applyingWorld = true;

            // The incoming world supersedes everything we knew about the old one. Edits
            // queued against it name cells in a grid that is about to be overwritten.
            _worldApplied = false;
            _blockSync?.DropOutbound();
            _blockSync?.DropInbound();
            _damageSync?.Clear();
            _itemMotion?.Clear();

            // Cache so the payload survives a later world rebuild.
            _applied = snapshot;
            _appliedInto = blocks;

            StartCoroutine(_worldSync.ApplyTerrain(snapshot, (ok, detail) =>
            {
                _applyingWorld = false;
                _worldApplied = ok;
                Note("world apply " + (ok ? "succeeded" : "FAILED: " + detail));

                // After the terrain and entities, because Flooded pours water into the
                // world and Dry empties it — both meaningless against a world that is
                // not there yet.
                if (ok) LayerModifierSync.Apply(snapshot.Prepare.LayerModifier, Note);

                // Release only after PlaceLocalBody has put the body where the host says,
                // so it settles onto real ground instead of falling from the old spot.
                if (_bodyFrozen)
                {
                    _worldSync.FreezeLocalBody(false);
                    _bodyFrozen = false;
                }

                if (ok && _worldSync != null)
                {
                    // Re-read the LIVE world rather than re-hashing the payload: that
                    // proves the bytes actually landed in worldBlocks, not merely that
                    // they arrived intact.
                    ushort a, b, c;
                    var applied = _worldSync.CaptureTerrain(out a, out b, out c);
                    if (applied != null)
                        Note("TERRAINHASH client " + WorldSnapshotCodec.Sha256Hex(applied) +
                             " (" + applied.Length + " B, layer " + snapshot.Prepare.BiomeDepth + ")");
                }

                var client = _session as ClientSession;
                client?.SendLayerReady(snapshot.Prepare.TransferId, ok, detail);
            }));
        }

        // ---------------------------------------------------------------- world mutation

        private BlockSync _blockSync;
        private EntityDamageSync _damageSync;
        private ItemMotionSync _itemMotion;

        /// <summary>
        /// <see cref="WorldMutationsLive"/> as of this frame, for Harmony patches that run
        /// far too often to re-evaluate it.
        ///
        /// Static because <c>BuildingEntity.Update</c> is patched: that is ~1,900 calls a
        /// frame, and outside a session every one of them must cost a single field read.
        /// One frame of staleness is harmless — the gate only opens and closes around
        /// world transfers.
        /// </summary>
        internal static bool MutationsLiveThisFrame { get; private set; }

        /// <summary>
        /// Whether our own world changes should be published, and remote ones applied.
        ///
        /// Closed during generation, during a world transfer, and before this peer has a
        /// world the others would recognise. Every one of those windows produces terrain
        /// writes by the thousand — the generator's, ours as we apply the host's grid,
        /// and the ones spawned entities make to the blocks they sit on — none of which
        /// describe a change any peer needs to hear about.
        /// </summary>
        internal bool WorldMutationsLive
        {
            get
            {
                if (_session == null || _session.Status != SessionStatus.Established) return false;
                if (_blockSync == null || _applyingWorld) return false;

                var world = WorldGeneration.world;
                if (world == null || world.generatingWorld) return false;

                return IsHost ? _worldBroadcastDone : _worldApplied;
            }
        }

        /// <summary>Harmony entry point: the local game just wrote a block.</summary>
        internal void RecordLocalBlockEdit(WorldGeneration world, Vector2Int pos, ushort block)
        {
            if (!WorldMutationsLive) return;
            _blockSync.RecordLocalEdit(world, pos, block);
        }

        /// <summary>Harmony entry point: a block on this peer got more cracked.</summary>
        internal void RecordBlockHit(Vector2Int pos, float damage)
        {
            if (!WorldMutationsLive) return;
            _blockSync.RecordLocalHit(pos, damage);
        }

        /// <summary>
        /// Harmony entry point: look at one world object's health.
        ///
        /// Called for every building entity every frame, so the gate is checked by the
        /// caller against <see cref="MutationsLiveThisFrame"/> rather than here.
        /// </summary>
        internal void TrackEntityDamage(BuildingEntity entity)
        {
            if (_damageSync == null || _worldSync == null || entity == null) return;
            _damageSync.Track(entity, _worldSync.Entities);
        }

        /// <summary>
        /// Harmony entry point: publish the items a game method just produced.
        ///
        /// Registered locally under an id from our own reserved range as well as sent, so
        /// that a later pickup of one of these items refers to the same entity on every
        /// peer instead of falling back to local-only handling.
        /// </summary>
        internal void PublishCapturedSpawns()
        {
            var captured = SpawnCapture.Captured;
            if (captured.Count == 0) return;
            if (!WorldMutationsLive || _worldSync == null) return;

            foreach (var spawn in captured)
            {
                if (spawn.Go == null) continue;

                uint id = _worldSync.Entities.AllocateRuntimeId(_session.LocalPeerId);
                if (id == 0) continue;   // range exhausted; already reported

                // Marked as ours so a later world capture skips it: it already has a
                // runtime id, and letting the manifest give it a second one would leave
                // one object answering to two.
                spawn.Go.name = WorldSync.SpawnedNamePrefix + spawn.Id;
                _worldSync.Entities.Register(id, spawn.Go, spawn.Id ?? string.Empty);

                var item = spawn.Go.GetComponent<Item>();
                var rb = spawn.Go.GetComponent<Rigidbody2D>();
                var pos = spawn.Go.transform.position;

                _session.SendEntitySpawn(new EntitySpawnMessage
                {
                    EntityId = id,
                    ItemId = spawn.Id ?? string.Empty,
                    PositionX = pos.x,
                    PositionY = pos.y,
                    Rotation = spawn.Go.transform.eulerAngles.z,
                    Condition = item != null ? item.condition : 1f,
                    // Read off the object rather than recomputed: the peer that rolled
                    // this drop already applied the game's own proximity rule, and that
                    // decision has to be obeyed rather than re-taken (it sets gravity).
                    Floats = spawn.Go.GetComponent<FreshItemDrop>() != null,
                    VelocityX = rb != null ? rb.velocity.x : 0f,
                    VelocityY = rb != null ? rb.velocity.y : 0f,
                });
            }

            Note("published " + captured.Count + " spawned item" +
                 (captured.Count == 1 ? "" : "s"));
        }

        /// <summary>
        /// A world object is one frame from breaking apart. Decide whose it is.
        ///
        /// Entity health is not replicated — each peer only damages its own copy — so the
        /// peer whose copy dies first is the one that actually destroyed it, and owns its
        /// loot. It rolls the drops here (through <c>Utils.Create</c>, so they are
        /// captured), stops the game rolling them again, and tells everyone. Every other
        /// peer kills its copy on the despawn without rolling anything.
        ///
        /// Host arbitration would be wrong here: a client chopping a crate would get
        /// nothing until the host's untouched copy happened to die, which it never would.
        /// </summary>
        internal void OnBuildingEntityDying(BuildingEntity entity)
        {
            if (!WorldMutationsLive || _worldSync == null || entity == null) return;

            // Not a networked object — spawned at runtime, or never in the manifest.
            // Nothing to say about it, and nobody to say it to.
            if (!_worldSync.Entities.TryGetId(entity.gameObject, out var id)) return;

            if (!_blockSync.ClaimDestruction(id))
            {
                // Someone else got there first. Their drops are already on the way.
                BuildingDrops.Blank(entity);
                return;
            }

            SpawnCapture.Begin();
            try
            {
                BuildingDrops.Roll(entity);
                BuildingDrops.Blank(entity);

                // Destruction first, then its loot: peers should lose the crate before
                // items appear where it stood.
                _session.SendEntityDespawn(new EntityDespawnMessage { EntityId = id });
                PublishCapturedSpawns();

                Note("destroyed entity " + id + " (" + (_worldSync.Entities.KeyOf(id) ?? "?") +
                     "), " + SpawnCapture.Captured.Count + " drops");
            }
            finally
            {
                SpawnCapture.End();
            }

            _worldSync.Entities.Remove(id);
        }

        /// <summary>
        /// Gives up a destruction that another peer caused, before we can claim it.
        ///
        /// Used when applying a peer's terrain edit fells a prop on this side too: the
        /// prop is genuinely destroyed here, but the player who dug owns what falls out
        /// of it. Claiming the id as well as blanking the drops means our own
        /// <see cref="OnBuildingEntityDying"/> will not announce it a moment later.
        /// </summary>
        internal void DisownDestruction(BuildingEntity entity)
        {
            if (_worldSync == null || _blockSync == null || entity == null) return;

            BuildingDrops.Blank(entity);

            if (_worldSync.Entities.TryGetId(entity.gameObject, out var id))
                _blockSync.ClaimDestruction(id);
        }

        // ---------------------------------------------------------------- attacks

        /// <summary>Harmony entry point: the local player swung at something.</summary>
        internal void AnnounceAttack(Body body, AttackInfo atk)
        {
            if (_session == null || _session.Status != SessionStatus.Established) return;

            _session.SendPlayerAttack(new PlayerAttackMessage
            {
                Anim = atk.attackAnim.name,
                Rotation = AttackSync.ArcRotation(body),
                FacingRight = body.isRight,
                Sound = AttackSync.SwingSound(atk),
            });
        }

        /// <summary>Draws somebody else's swing on their proxy.</summary>
        private void OnPlayerAttacked(byte peerId, PlayerAttackMessage attack)
        {
            if (!_proxies.TryGetValue(peerId, out var proxy) || proxy == null) return;
            proxy.PlayAttack(attack.Anim, attack.Rotation, attack.FacingRight, attack.Sound);
        }

        // ---------------------------------------------------------------- world events

        /// <summary>
        /// Held high on a client so its own earthquake timer never fires. The game rolls
        /// 600–1750 s between quakes, so this is an ordinary value rather than a sentinel:
        /// if the session ends, the timer simply carries on from here.
        /// </summary>
        private const float ClientQuakeDelayPin = 900f;

        private float _lastQuakeTime;

        /// <summary>
        /// Keeps both players in the same earthquake.
        ///
        /// The game rolls the interval and the duration per peer from the unseeded global
        /// RNG, inside <c>WorldGeneration.Update</c> with no method to hook — so each
        /// player got their own quakes at their own times. Terrain sync made that worse
        /// rather than better: the blocks a quake tears out now cross the wire, and it
        /// tears them out around *the shaking player*, so the other one watched holes open
        /// around themselves during a quake they were not in.
        ///
        /// Only the schedule needs to travel. Shake, stumbling and block damage are all
        /// produced correctly by each peer for its own player once they agree on *when*.
        /// </summary>
        private void HandleWorldEvents()
        {
            var world = WorldGeneration.world;
            if (world == null || !WorldMutationsLive) return;

            if (!IsHost)
            {
                // Never roll our own; the host says when the ground shakes.
                world.earthquakeDelay = ClientQuakeDelayPin;
                return;
            }

            float remaining = world.earthquakeTime;

            // The timer only ever counts down, so a rise means one was just rolled.
            if (remaining > _lastQuakeTime + 0.01f && remaining > 0.5f)
            {
                (_session as HostSession)?.SendWorldEvent(new WorldEventMessage
                {
                    Kind = WorldEventKind.Earthquake,
                    Duration = remaining,
                });
                Note("[event] earthquake for " + remaining.ToString("F0") + "s; announced");

                // The game drops Time.timeScale to 1 here by assigning the field directly,
                // going around SetTimeScale and so around the shared clock. Push it back
                // through the proper path or the peers disagree about the speed from now on.
                if (PlayerCamera.main != null)
                    PlayerCamera.main.SetTimeScale(PlayerCamera.SpeedType.Normal);
            }

            _lastQuakeTime = remaining;
        }

        /// <summary>Client: the host scheduled something for the whole layer.</summary>
        private void OnWorldEvent(WorldEventMessage worldEvent)
        {
            var world = WorldGeneration.world;
            if (world == null || worldEvent.Kind != WorldEventKind.Earthquake) return;

            world.earthquakeTime = worldEvent.Duration;
            Note("[event] earthquake from the host, " + worldEvent.Duration.ToString("F0") + "s");
        }

        // ---------------------------------------------------------------- time scale

        /// <summary>
        /// The local game wants to change speed. Returns true to let it, false to cancel.
        ///
        /// Three cases, and the interesting part is that most changes are *not* the
        /// player asking for a fast-forward:
        ///
        /// - <paramref name="force"/> marks the pause handler, the death screen and
        ///   start-up. Those stay local — one player opening a menu must not freeze
        ///   everybody else's game.
        /// - <c>UnconsciousFast</c>, <c>DyingFast</c> and <c>Slowmo</c> come from one
        ///   body or one player's screen. Refused outright in a session: running the
        ///   whole world at 25x because a single player passed out is worse than making
        ///   them wait, and letting only that peer do it is worse still.
        /// - The three deliberate speeds are the session's, and the host owns them.
        /// </summary>
        internal bool OnLocalTimeScale(PlayerCamera.SpeedType speed, bool force)
        {
            if (force) return true;

            var wanted = (SessionSpeed)(byte)speed;
            if (!TimeScaleMessage.IsShareable(wanted))
            {
                // Deny rather than substitute Normal: the current session speed is
                // already correct, and overriding it here would fight the host.
                return false;
            }

            // The game re-asserts the speed it already has from several places — the
            // movement reset, UI sequences, the unconsciousness check — and in single
            // player each is a harmless no-op. Here every one would be a packet and a
            // round trip, so a request for the speed we are already running at is
            // allowed through locally and never sent.
            var camera = PlayerCamera.main;
            if (camera != null && camera.curTimeScale == speed)
            {
                if (EnvFlag("CCOOP_BLOCKTEST")) LogTimeScaleCaller(speed, "redundant");
                return true;
            }

            if (EnvFlag("CCOOP_BLOCKTEST")) LogTimeScaleCaller(speed, "change");

            var message = new TimeScaleMessage { Speed = wanted };

            if (IsHost)
            {
                // Apply locally as the game intended, then tell everyone. Broadcasting
                // also raises the event here, which is harmless — applying the speed we
                // already hold is a no-op.
                _session.SendTimeScale(message);
                return true;
            }

            // Client: ask, do not act. The host's answer comes back as a broadcast.
            _session.SendTimeScale(message);
            return false;
        }

        /// <summary>
        /// Names the game code that asked for a speed change.
        ///
        /// Test-flag only — building a stack trace is far too expensive for a path the
        /// game touches several times a second. Worth having because "who keeps setting
        /// it back to Normal?" is otherwise unanswerable from the outside.
        /// </summary>
        private void LogTimeScaleCaller(PlayerCamera.SpeedType speed, string kind)
        {
            try
            {
                var trace = new System.Diagnostics.StackTrace(2, false);
                var sb = new StringBuilder();
                for (int i = 0; i < trace.FrameCount && i < 5; i++)
                {
                    var m = trace.GetFrame(i).GetMethod();
                    if (m == null) continue;
                    sb.Append(m.DeclaringType != null ? m.DeclaringType.Name : "?")
                      .Append('.').Append(m.Name).Append(" <- ");
                }
                Note("TIMESCALE " + kind + " " + speed + " from " + sb);
            }
            catch { /* diagnostics must never throw */ }
        }

        /// <summary>Host: a client asked for a speed. The host's own config decides.</summary>
        private void OnTimeScaleRequested(byte peerId, TimeScaleMessage request)
        {
            var host = _session as HostSession;
            if (host == null) return;

            if (!P.AllowSpeedUp.Value && request.Speed != SessionSpeed.Normal)
            {
                // Answer anyway rather than ignore: the client cancelled its own change
                // and would otherwise be left wondering why nothing happened.
                Note("refused speed " + request.Speed + " from peer " + peerId +
                     ": speed-up is disabled for this session");
                host.SendTimeScale(new TimeScaleMessage { Speed = SessionSpeed.Normal });
                return;
            }

            host.SendTimeScale(request);
        }

        /// <summary>Sends what we changed this frame and applies what others changed.</summary>
        private void HandleWorldMutations()
        {
            if (_blockSync == null || _worldSync == null || _damageSync == null) return;

            // Published once a frame for the patches that cannot afford to ask.
            MutationsLiveThisFrame = WorldMutationsLive;

            if (!WorldMutationsLive)
            {
                // Anything captured while the gate was shut describes a world the other
                // peers do not have; sending it later would corrupt theirs.
                _blockSync.DropOutbound();
                return;
            }

            _blockSync.Flush(_session.SendBlockDeltas);
            _blockSync.FlushHits(_session.SendBlockHits);
            _damageSync.Flush(_session.SendEntityDamage);

            // Damage before destruction: a peer's killing blow and the despawn that
            // follows it travel on the same ordered channel, and applying them in the
            // other order would let our copy die from the damage a moment before we are
            // told who killed it — and claim the kill.
            _damageSync.ApplyInbound(_worldSync.Entities);
            _blockSync.ApplyInbound(_worldSync.Entities);

            // After the spawns above, so an item created this frame is already registered
            // and its first motion update has something to move.
            _itemMotion?.Flush(_worldSync.Entities, _session.LocalPeerId, _session.SendEntityMotion);
            _itemMotion?.ApplyInbound(_worldSync.Entities);
        }

        // ---------------------------------------------------------------- transactions

        private uint _nextRequestId = 1;

        /// <summary>
        /// Client: cancel the local pickup and ask the host instead.
        /// Returns false if no request could be made, in which case the caller should
        /// allow the local pickup — being unable to pick anything up is far worse for the
        /// player than an unsynced item, and an unregistered item is already local-only.
        /// </summary>
        internal bool RequestPickup(Item item, int slot)
        {
            var client = _session as ClientSession;
            if (client == null || _worldSync == null) return false;

            if (!_worldSync.Entities.TryGetId(item.gameObject, out var entityId))
            {
                Note("'" + item.id + "' is not a networked entity; picking up locally only");
                return false;
            }

            client.SendInteractionRequest(new InteractionRequestMessage
            {
                RequestId = _nextRequestId++,
                Kind = InteractionKind.PickUpItem,
                EntityId = entityId,
                Slot = (byte)Mathf.Clamp(slot, 0, 255),
                ExpectedRevision = _worldSync.Entities.RevisionOf(entityId),
            });
            return true;
        }

        /// <summary>Host: its own pickup already happened; tell everyone.</summary>
        internal void AnnounceHostPickup(Item item, int slot)
        {
            var host = _session as HostSession;
            if (host == null || _worldSync == null) return;
            if (!_worldSync.Entities.TryGetId(item.gameObject, out var entityId)) return;

            var revision = _worldSync.Entities.Bump(entityId);
            _worldSync.Entities.Remove(entityId);

            host.BroadcastInteractionResult(new InteractionResultMessage
            {
                RequestId = 0,                       // unsolicited: the host acted itself
                ActorPeerId = ProtocolConstants.HostPeerId,
                Kind = InteractionKind.PickUpItem,
                EntityId = entityId,
                Slot = (byte)Mathf.Clamp(slot, 0, 255),
                Granted = true,
                Failure = InteractionFailure.None,
                NewRevision = revision,
                ItemId = item.id ?? string.Empty,
            });
        }

        /// <summary>
        /// Client: cancel the local drop and ask the host instead. Returns false if no
        /// request could be sent, in which case the caller allows the local drop.
        /// </summary>
        internal bool RequestDrop(Item item, int slot, Vector2 launch)
        {
            var client = _session as ClientSession;
            if (client == null) return false;

            uint requestId = _nextRequestId++;

            // Remembered by request id rather than trusted to still be in this slot when
            // the answer arrives: the player keeps playing during the round trip and can
            // move or swap what they are holding.
            _pendingDrops[requestId] = item;

            client.SendInteractionRequest(new InteractionRequestMessage
            {
                RequestId = requestId,
                Kind = InteractionKind.DropItem,
                EntityId = 0,                  // the item is not in the world yet
                Slot = (byte)Mathf.Clamp(slot, 0, 255),
                ExpectedRevision = 0,
                ItemId = item.id ?? string.Empty,
                VelocityX = launch.x,
                VelocityY = launch.y,
            });
            return true;
        }

        /// <summary>
        /// Drops this peer asked for and has not heard back about, by request id.
        /// Cleared with the session; a stale entry is harmless because the item is
        /// resolved through Unity's destroyed-object check before use.
        /// </summary>
        private readonly Dictionary<uint, Item> _pendingDrops = new Dictionary<uint, Item>();

        /// <summary>
        /// Publishes the state a prop was just left in.
        ///
        /// The host broadcasts and lets its own raise apply it; a client asks and the
        /// host's answer applies it everywhere. Either way the state is applied in exactly
        /// one place, which matters because the host receives its own broadcasts — see
        /// the duplicate-drop note in STATUS for what happens when it does not.
        /// </summary>
        internal void AnnouncePropUse(LifepodButton button)
        {
            if (_worldSync == null || button == null) return;

            byte state = PropSync.StateOf(button);
            var pos = (Vector2)button.transform.position;

            // Logged once per press: it names the hierarchy a prop actually lives in,
            // which is how the id-based first attempt was found to be addressing objects
            // that neither peer had ever registered.
            Note("[prop] pressed '" + button.gameObject.name + "' under '" +
                 ParentChain(button.transform) + "' at " + pos.x.ToString("F2") + "," +
                 pos.y.ToString("F2") + " -> state " + state);

            var host = _session as HostSession;
            if (host != null)
            {
                host.BroadcastInteractionResult(new InteractionResultMessage
                {
                    RequestId = 0,
                    ActorPeerId = ProtocolConstants.HostPeerId,
                    Kind = InteractionKind.UseProp,
                    EntityId = 0,
                    Slot = state,
                    Granted = true,
                    Failure = InteractionFailure.None,
                    PositionX = pos.x,
                    PositionY = pos.y,
                });
                return;
            }

            var client = _session as ClientSession;
            if (client == null) return;

            client.SendInteractionRequest(new InteractionRequestMessage
            {
                RequestId = _nextRequestId++,
                Kind = InteractionKind.UseProp,
                EntityId = 0,
                Slot = state,
                ExpectedRevision = 0,
                ItemId = string.Empty,
                PositionX = pos.x,
                PositionY = pos.y,
            });
        }

        /// <summary>Names a transform's ancestors, outermost first, for diagnostics.</summary>
        private static string ParentChain(Transform t)
        {
            var names = new List<string>();
            for (var p = t != null ? t.parent : null; p != null; p = p.parent) names.Insert(0, p.name);
            return names.Count == 0 ? "<scene root>" : string.Join("/", names.ToArray());
        }

        /// <summary>Host: its own drop already happened; register and announce it.</summary>
        internal void AnnounceHostDrop(Item item, int slot, Vector2 launch)
        {
            var host = _session as HostSession;
            if (host == null || _worldSync == null) return;

            var id = _worldSync.Entities.AllocateRuntimeId(ProtocolConstants.HostPeerId);
            _worldSync.Entities.Register(id, item.gameObject, item.id ?? string.Empty);

            var pos = item.transform.position;
            host.BroadcastInteractionResult(new InteractionResultMessage
            {
                RequestId = 0,
                ActorPeerId = ProtocolConstants.HostPeerId,
                Kind = InteractionKind.DropItem,
                EntityId = id,
                Slot = (byte)Mathf.Clamp(slot, 0, 255),
                Granted = true,
                Failure = InteractionFailure.None,
                NewRevision = 0,
                ItemId = item.id ?? string.Empty,
                PositionX = pos.x,
                PositionY = pos.y,
                // Announced before ThrowItem gets to assign it, so peers start the item
                // moving on the frame it appears instead of a motion update later.
                VelocityX = launch.x,
                VelocityY = launch.y,
            });
        }

        /// <summary>Host: arbitrate a client's request.</summary>
        private void OnInteractionRequested(byte peerId, InteractionRequestMessage request)
        {
            var host = _session as HostSession;
            if (host == null || _worldSync == null) return;

            // Range is checked against the REQUESTER's position, not ours. The host's own
            // body is irrelevant to whether that player could reach the item.
            var actorPos = PositionOfPeer(peerId);

            if (request.Kind == InteractionKind.DropItem)
            {
                HandleDropRequest(host, peerId, request, actorPos);
                return;
            }

            if (request.Kind == InteractionKind.UseProp)
            {
                // Not run through Validate: that path is written for items and refuses
                // anything without an Item component, which a wall switch does not have.
                // Nothing is created or consumed here, so the worst a bad id can do is
                // address a prop that is not there — which Apply reports and ignores.
                host.BroadcastInteractionResult(new InteractionResultMessage
                {
                    RequestId = request.RequestId,
                    ActorPeerId = peerId,
                    Kind = InteractionKind.UseProp,
                    EntityId = 0,
                    Slot = request.Slot,
                    Granted = true,
                    Failure = InteractionFailure.None,
                    PositionX = request.PositionX,
                    PositionY = request.PositionY,
                });
                return;
            }

            var failure = ItemTransactions.Validate(
                _worldSync.Entities, request, actorPos, Body.interactionRange * 2f);

            var result = new InteractionResultMessage
            {
                RequestId = request.RequestId,
                ActorPeerId = peerId,
                Kind = request.Kind,
                EntityId = request.EntityId,
                Slot = request.Slot,
                Granted = failure == InteractionFailure.None,
                Failure = failure,
            };

            if (result.Granted)
            {
                result.NewRevision = _worldSync.Entities.Bump(request.EntityId);
                if (_worldSync.Entities.TryGet(request.EntityId, out var go))
                {
                    var item = go.GetComponent<Item>();
                    result.ItemId = item != null ? (item.id ?? string.Empty) : string.Empty;

                    // Remove it here too: the host's own world must not keep an item that
                    // now belongs to a remote player's inventory.
                    _worldSync.Entities.Remove(request.EntityId);
                    Destroy(go);
                }
            }
            else
            {
                Note("refused pickup from peer " + peerId + ": " + failure);
            }

            host.BroadcastInteractionResult(result);
        }

        /// <summary>
        /// Host: a client dropped something. The host spawns the world item so that it
        /// owns the authoritative copy, then tells every peer — including the dropper,
        /// whose own copy was never created because the drop was cancelled locally.
        /// </summary>
        private void HandleDropRequest(HostSession host, byte peerId,
                                       InteractionRequestMessage request, Vector2 actorPos)
        {
            // Refuse a position we do not actually know; dropping at the origin would
            // teleport the item across the map.
            if (actorPos.x > 1e6f)
            {
                host.BroadcastInteractionResult(new InteractionResultMessage
                {
                    RequestId = request.RequestId,
                    ActorPeerId = peerId,
                    Kind = InteractionKind.DropItem,
                    EntityId = 0,
                    Granted = false,
                    Failure = InteractionFailure.NotPermitted,
                });
                Note("refused drop from peer " + peerId + ": position unknown");
                return;
            }

            GameObject created = null;
            try { created = Utils.Create(request.ItemId, actorPos, 0f); }
            catch (Exception e) { Note("host could not create '" + request.ItemId + "': " + e.Message); }

            if (created == null)
            {
                host.BroadcastInteractionResult(new InteractionResultMessage
                {
                    RequestId = request.RequestId,
                    ActorPeerId = peerId,
                    Kind = InteractionKind.DropItem,
                    EntityId = 0,
                    Granted = false,
                    Failure = InteractionFailure.UnknownEntity,
                    ItemId = request.ItemId,
                });
                return;
            }

            // Allocated from the host's range even though a client asked for it: the host
            // is the one creating the object, so it must not spend the client's ids.
            var id = _worldSync.Entities.AllocateRuntimeId(ProtocolConstants.HostPeerId);
            created.name = WorldSync.SpawnedNamePrefix + request.ItemId;
            _worldSync.Entities.Register(id, created, request.ItemId);

            // A throw, not a drop. This matters more than it looks: the id above is from
            // the host's range, so the host OWNS this item for motion sync. Leaving its
            // copy at rest would stream that rest back over the thrower's own copy and
            // cancel the throw a frame after it started.
            var launch = new Vector2(request.VelocityX, request.VelocityY);
            if (launch.sqrMagnitude > 0f)
            {
                var createdRb = created.GetComponent<Rigidbody2D>();
                if (createdRb != null)
                {
                    createdRb.velocity = launch;
                    createdRb.angularVelocity = UnityEngine.Random.Range(-800f, 800f);
                    createdRb.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
                }
            }

            host.BroadcastInteractionResult(new InteractionResultMessage
            {
                RequestId = request.RequestId,
                ActorPeerId = peerId,
                Kind = InteractionKind.DropItem,
                EntityId = id,
                Slot = request.Slot,
                Granted = true,
                Failure = InteractionFailure.None,
                NewRevision = 0,
                ItemId = request.ItemId,
                PositionX = actorPos.x,
                PositionY = actorPos.y,
                VelocityX = launch.x,
                VelocityY = launch.y,
            });
        }

        /// <summary>All peers: apply the host's authoritative outcome.</summary>
        private void OnInteractionResolved(InteractionResultMessage result)
        {
            if (_worldSync == null) return;

            if (!result.Granted)
            {
                // Only the requester needs telling; others never attempted it.
                if (result.ActorPeerId == _session.LocalPeerId)
                {
                    _pendingDrops.Remove(result.RequestId);
                    Note(result.Kind + " refused: " + result.Describe());
                }
                return;
            }

            bool actorIsLocal = result.ActorPeerId == _session.LocalPeerId;

            if (result.Kind == InteractionKind.UseProp)
            {
                // The presser's own copy is already in this state — the game did it. Doing
                // it again would be harmless for the heat toggle, which converges, but
                // would run the shower a second time.
                if (actorIsLocal) return;

                PropSync.Apply(new Vector2(result.PositionX, result.PositionY), result.Slot, Note);
                return;
            }

            // The host already applied its own action when it announced it.
            if (actorIsLocal && IsHost) return;

            if (result.Kind == InteractionKind.DropItem)
            {
                if (actorIsLocal) CompleteLocalDrop(result);
                else ItemTransactions.ApplyGrantedDrop(_worldSync.Entities, result, false, Note);
                return;
            }

            ItemTransactions.ApplyGrantedPickup(_worldSync.Entities, result, actorIsLocal, Note);
        }

        /// <summary>
        /// Finishes a drop the client had cancelled while waiting for permission.
        ///
        /// The item is still in the inventory, so the real drop runs now (with the
        /// re-entrancy guard set), and the resulting world item is registered under the
        /// host's id and snapped to the host's position so every peer agrees on both
        /// identity and location.
        /// </summary>
        private void CompleteLocalDrop(InteractionResultMessage result)
        {
            var body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
            if (body == null) return;

            // The item we actually asked about. Falling back to the slot covers a result
            // for a request this peer no longer remembers, e.g. after a reconnect.
            Item item;
            if (_pendingDrops.TryGetValue(result.RequestId, out item))
                _pendingDrops.Remove(result.RequestId);
            if (item == null) item = body.GetItem(result.Slot);

            if (item == null)
            {
                // The host spawned its own copy regardless, so the world is consistent;
                // this peer simply has nothing left to drop.
                Note("drop " + result.EntityId + " granted but the item is gone locally");
                return;
            }

            PickUpItemPatch.ApplyingAuthoritative = true;
            try
            {
                body.DropItem(item);

                item.transform.position = new Vector3(
                    result.PositionX, result.PositionY, item.transform.position.z);
                item.name = WorldSync.SpawnedNamePrefix + result.ItemId;
                _worldSync.Entities.Register(result.EntityId, item.gameObject, result.ItemId);

                // A throw, not a drop: the item only becomes a world object here, so this
                // is the first moment its launch velocity can mean anything. Taken from
                // the host's answer rather than remembered locally, so every peer starts
                // it identically — including the host, which owns it from here on.
                var launch = new Vector2(result.VelocityX, result.VelocityY);
                if (launch.sqrMagnitude > 0f)
                {
                    var rb = item.GetComponent<Rigidbody2D>();
                    if (rb != null)
                    {
                        rb.velocity = launch;
                        rb.angularVelocity = UnityEngine.Random.Range(-800f, 800f);
                        rb.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
                    }
                    Note("thrown '" + result.ItemId + "' as entity " + result.EntityId);
                }
                else Note("dropped '" + result.ItemId + "' as entity " + result.EntityId);
            }
            catch (Exception e)
            {
                Note("could not complete local drop: " + e.Message);
            }
            finally
            {
                PickUpItemPatch.ApplyingAuthoritative = false;
            }
        }

        private Vector2 PositionOfPeer(byte peerId)
        {
            foreach (var p in _session.RemotePlayers)
            {
                if (p.PeerId != peerId || p.LatestTransform == null) continue;
                return new Vector2(p.LatestTransform.PositionX, p.LatestTransform.PositionY);
            }

            // No movement snapshot yet: refuse by distance rather than guess, by
            // reporting a position nothing can be near.
            return new Vector2(float.MaxValue * 0.5f, float.MaxValue * 0.5f);
        }

        private void OnPlayerJoined(RemotePlayer p)
        {
            Note("joined: " + p.Name + " (peer " + p.PeerId + ")");
            // A newcomer needs the world even if we generated it long ago.
            _worldSent = false;
        }

        private void OnPlayerLeft(RemotePlayer p)
        {
            Note("left: " + p.Name);
            if (_proxies.TryGetValue(p.PeerId, out var proxy))
            {
                if (proxy != null) Destroy(proxy.gameObject);
                _proxies.Remove(p.PeerId);
            }
        }

        private void OnPlayerMoved(RemotePlayer p)
        {
            if (_proxies.TryGetValue(p.PeerId, out var proxy) && proxy != null)
                proxy.Receive(p.LatestTransform);
        }

        /// <summary>
        /// Creates proxies lazily. A player may be announced while we are still in the
        /// menu, where there is no body to clone from, so creation is retried each frame
        /// until a template exists.
        /// </summary>
        private void ReconcileProxies()
        {
            var body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
            if (body == null) return;

            foreach (var p in _session.RemotePlayers)
            {
                if (_proxies.ContainsKey(p.PeerId)) continue;

                var proxy = ProxyFactory.Create(body, p.PeerId, p.Name);
                if (proxy == null) continue;

                _proxies[p.PeerId] = proxy;
                if (p.HasTransform) proxy.Receive(p.LatestTransform);
                Note("proxy created for peer " + p.PeerId);
            }
        }

        // ---------------------------------------------------------------- sending

        private float _nextAppearanceSend;

        /// <summary>
        /// Publishes what this player's body looks like, a couple of times a second.
        ///
        /// Far below the movement rate deliberately: wounds and dirt change on the
        /// timescale of getting hurt or wading through a puddle, not of walking, and this
        /// is ~70 bytes rather than a pose. Sent unconditionally rather than on change —
        /// comparing thirteen limbs' worth of shader values every frame to save a packet
        /// every half second is the wrong trade, and an unconditional stream also repairs
        /// a proxy created part-way through a session.
        /// </summary>
        private void SendLocalAppearance()
        {
            if (_session.Status != SessionStatus.Established) return;
            if (Time.time < _nextAppearanceSend) return;
            _nextAppearanceSend = Time.time + 0.5f;

            var body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
            if (body == null) return;

            var look = RemoteProxy.Describe(body);
            if (look != null) _session.SendPlayerAppearance(look);
        }

        private float _nextEquipmentSend;

        /// <summary>
        /// Publishes what this player is holding and wearing.
        ///
        /// Repeated rather than sent on change, for the same reason appearance is: it is a
        /// handful of bytes, and a proxy created part-way through a session then fills its
        /// hands within half a second without anyone having to notice it needs telling.
        /// </summary>
        private void SendLocalEquipment()
        {
            if (_session.Status != SessionStatus.Established) return;
            if (Time.time < _nextEquipmentSend) return;
            _nextEquipmentSend = Time.time + 0.5f;

            var body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
            if (body == null || body.slots == null) return;

            var kit = new PlayerEquipmentMessage();
            for (int i = 0; i < body.slots.Length && i < PlayerEquipmentMessage.MaxItems; i++)
            {
                Item item;
                try { item = body.GetItem(i); }
                catch { continue; }

                if (item == null || string.IsNullOrEmpty(item.id)) continue;
                kit.Items.Add(new EquippedItem { Slot = (byte)i, ItemId = item.id });
            }

            _session.SendPlayerEquipment(kit);
        }

        private void OnPlayerEquipment(byte peerId, PlayerEquipmentMessage kit)
        {
            RemoteProxy proxy;
            if (_proxies.TryGetValue(peerId, out proxy) && proxy != null)
                proxy.ApplyEquipment(kit);
        }

        private void OnPlayerAppearance(byte peerId, PlayerAppearanceMessage look)
        {
            RemoteProxy proxy;
            if (_proxies.TryGetValue(peerId, out proxy) && proxy != null)
                proxy.ApplyAppearance(look);
        }

        private void SendLocalTransform()
        {
            if (_session.Status != SessionStatus.Established) return;

            float interval = 1f / Mathf.Max(1, P.SendRateHz.Value);
            if (Time.time < _nextSendTime) return;
            _nextSendTime = Time.time + interval;

            var body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
            if (body == null) return;

            var msg = new PlayerTransformMessage
            {
                PositionX = body.transform.position.x,
                PositionY = body.transform.position.y,
                // Facing is Body.isRight, a bool the game flips on turn — NOT
                // transform.localScale, which stays +1 and made every remote player
                // appear permanently right-facing.
                FacingRight = body.isRight,
            };

            var rb = body.GetComponent<Rigidbody2D>();
            if (rb != null)
            {
                msg.VelocityX = rb.velocity.x;
                msg.VelocityY = rb.velocity.y;
            }

            if (body.limbs != null)
            {
                // Local, not world: limbs are flat siblings under the body root joined
                // by HingeJoint2D, so position is an independent degree of freedom and
                // must be sent alongside rotation. Local space also means the root's
                // facing flip mirrors the pose without extra work on the receiver.
                var limbs = new LimbPose[Mathf.Min(body.limbs.Length,
                                                   PlayerTransformMessage.MaxLimbCount)];
                for (int i = 0; i < limbs.Length; i++)
                {
                    var limb = body.limbs[i];
                    if (limb == null) continue;

                    var lp = limb.transform.localPosition;
                    limbs[i].LocalX = lp.x;
                    limbs[i].LocalY = lp.y;
                    limbs[i].Rotation = limb.transform.localEulerAngles.z;
                }
                msg.Limbs = limbs;
            }

            _session.SendLocalTransform(msg);
        }

        // ---------------------------------------------------------------- overlay

        private void Note(string s)
        {
            CoopPlugin.Say("[coop] " + s);
            _recentLog.Add(s);
            if (_recentLog.Count > 8) _recentLog.RemoveAt(0);
        }

        private void OnGUI()
        {
            if (P == null || !P.ShowOverlay.Value) return;

            const int w = 340;
            var rect = new Rect(10, 10, w, 186 + _recentLog.Count * 16);

            // Role in the box title: with two identical windows side by side there is
            // otherwise no way to tell which one is the host.
            string role = _session == null ? "OFFLINE" : (IsHost ? "HOST" : "CLIENT");
            GUI.Box(rect, "CasualtiesCoop " + CoopPlugin.Version + "  —  " + role);

            int y = 30;

            // fontSize only — FontStyle lives in UnityEngine.TextRenderingModule, and the
            // role is already unmissable at this size.
            var big = new GUIStyle(GUI.skin.label) { fontSize = 20 };
            GUI.Label(new Rect(20, y, w - 20, 28), role, big); y += 30;

            GUI.Label(new Rect(20, y, w - 20, 20), "Status: " + _statusLine); y += 18;

            if (_session != null)
            {
                GUI.Label(new Rect(20, y, w - 20, 20),
                    "Peers: " + _session.RemotePlayerCount + "   Proxies: " + _proxies.Count); y += 18;
                GUI.Label(new Rect(20, y, w - 20, 20),
                    "Session: " + _session.SessionId + "   Tick: " + _session.Tick); y += 18;

                if (_blockSync != null)
                {
                    // Terrain sync is invisible unless you happen to be looking at the
                    // cell someone else dug, so show that it is happening at all.
                    GUI.Label(new Rect(20, y, w - 20, 20),
                        "Blocks sent " + _blockSync.TotalSent +
                        "   applied " + _blockSync.TotalApplied +
                        (WorldMutationsLive ? "" : "   (paused)")); y += 18;
                }

                if (_applyingWorld)
                {
                    GUI.Label(new Rect(20, y, w - 20, 20), "Applying world..."); y += 18;
                }
                else if (_worldProgress > 0f && _worldProgress < 1f)
                {
                    GUI.Label(new Rect(20, y, w - 20, 20),
                        "Receiving world: " + _worldProgress.ToString("P0")); y += 18;
                }

                if (_transport != null && _session.RemotePlayerCount > 0)
                {
                    var stats = _transport.GetStats(new CCoop.Transport.PeerId(
                        _session.RemotePlayers.First().PeerId));
                    GUI.Label(new Rect(20, y, w - 20, 20),
                        "RTT: " + stats.RoundTripMs + " ms   loss: " +
                        stats.PacketLossPercent.ToString("F1") + "%"); y += 18;
                }
            }
            else
            {
                GUI.Label(new Rect(20, y, w - 20, 20),
                    P.HostKey.Value + " host   " + P.JoinKey.Value + " join"); y += 18;
            }

            y += 6;
            foreach (var line in _recentLog)
            {
                GUI.Label(new Rect(20, y, w - 20, 20), line);
                y += 16;
            }
        }
    }
}
