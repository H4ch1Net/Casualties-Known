using System;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace CCoop.Plugin.Runtime
{
    [BepInPlugin(Guid, Name, Version)]
    public sealed class CoopPlugin : BaseUnityPlugin
    {
        public const string Guid = "ccoop.casualtiescoop";
        public const string Name = "CasualtiesCoop";
        public const string Version = "2.0.0";

        internal static CoopPlugin Instance { get; private set; }

        /// <summary>
        /// The plugin has loaded and its settings can be read. Test this, never
        /// <c>Instance != null</c>: the game destroys plugin components on scene load, and
        /// Unity's == then calls the destroyed plugin null although its settings (plain C#
        /// objects) are still there. That silently switched off every hotkey, name tags,
        /// voice and the host's respawn rule after the first scene change.
        /// </summary>
        internal static bool Ready => !ReferenceEquals(Instance, null);

        internal ConfigEntry<string> PlayerName;
        internal ConfigEntry<string> Language;
        internal ConfigEntry<int> MaxPlayers;
        internal ConfigEntry<int> Port;
        internal ConfigEntry<string> LastAddress;
        internal ConfigEntry<string> LobbyType;
        internal ConfigEntry<string> HostPassword;

        /// <summary>
        /// The password a hosted session asks for. Dev instances started by the launcher
        /// (CCOOP_AUTOSTART) join each other without one, whatever the player's config says.
        /// </summary>
        internal string SessionPassword => string.IsNullOrEmpty(Dev.DevHarness.AutostartRole) ? (HostPassword.Value ?? "") : "";
        internal ConfigEntry<string> Respawn;
        internal ConfigEntry<int> ExitShare;
        internal ConfigEntry<bool> HostArrows;
        internal ConfigEntry<bool> HostNameTags;
        internal ConfigEntry<bool> SpawnApart;
        internal ConfigEntry<bool> FriendlyFire;
        internal ConfigEntry<int> LootScale;
        internal ConfigEntry<int> DangerScale;
        internal ConfigEntry<int> VoiceRangePercent;
        internal ConfigEntry<bool> ShowArrows;
        internal ConfigEntry<bool> ReduceFlashes;
        internal ConfigEntry<bool> ColorBlindColors;
        internal ConfigEntry<float> UiScale;
        internal ConfigEntry<bool> SteamTimeline;
        internal ConfigEntry<bool> AllowSpeedUp;
        internal ConfigEntry<KeyCode> MenuKey;
        internal ConfigEntry<KeyCode> ChatKey;
        internal ConfigEntry<KeyCode> AltChatKey;
        internal ConfigEntry<KeyCode> GestureKey;
        internal ConfigEntry<KeyCode> CatchKey;
        internal ConfigEntry<bool> ShowNames;
        internal ConfigEntry<string> VoiceMode;
        internal ConfigEntry<KeyCode> VoiceKey;
        internal ConfigEntry<float> VoiceVolume;
        internal ConfigEntry<float> VoiceRange;
        internal ConfigEntry<float> VoiceGate;
        internal ConfigEntry<bool> VerboseLog;
        internal ConfigEntry<bool> UpdateCheckEnabled;
        internal ConfigEntry<string> UpdateRepository;

        /// <summary>Patch classes that failed to install, for the status panel.</summary>
        internal static string[] FailedPatches = new string[0];

        private void Awake()
        {
            Instance = this;
            Log.Init(Logger);

            PlayerName = Config.Bind("General", "PlayerName", "",
                "Name shown to other players. Empty uses your Steam name.");
            MaxPlayers = Config.Bind("Host", "MaxPlayers", 4,
                new ConfigDescription("Players allowed in a session you host, including you.", new AcceptableValueRange<int>(2, 8)));
            LobbyType = Config.Bind("Host", "LobbyType", "FriendsOnly",
                new ConfigDescription("Steam lobby visibility when hosting.", new AcceptableValueList<string>("FriendsOnly", "Public", "InviteOnly")));
            HostPassword = Config.Bind("Host", "Password", "",
                "Password players must enter to join sessions you host. Empty means no password.");
            Respawn = Config.Bind("Host", "Respawn", "NextLayer",
                new ConfigDescription("What happens to players who die while others live on: spectate and come back on the next layer, " +
                                      "stay out for the rest of the run, or come back beside a teammate after 15 seconds.",
                                      new AcceptableValueList<string>("NextLayer", "Permadeath", "Quick")));
            ExitShare = Config.Bind("Host", "ExitShare", 50,
                new ConfigDescription("Percent of living players who must be at an exit (or in the drill pod) before the team descends. " +
                                      "0 lets the first player to arrive take everyone; 100 waits for all.",
                                      new AcceptableValueRange<int>(0, 100)));
            HostArrows = Config.Bind("Host", "TeammateArrows", true, "Allow arrows at the screen edge pointing to teammates (each player can still turn theirs off).");
            HostNameTags = Config.Bind("Host", "NameTags", true, "Show names above teammates.");
            SpawnApart = Config.Bind("Host", "SpawnApart", false,
                "From layer 2 on, players arrive spread out across the layer (same depth, sideways) and have to find each other.");
            FriendlyFire = Config.Bind("Host", "FriendlyFire", false,
                "Players' guns hurt teammates. In the game itself a player's shot never hits a body, so this is a co-op rule.");
            LootScale = Config.Bind("Host", "LootScale", 0,
                new ConfigDescription("0: one set of loot for the team, as in single-player. 1: layers carry more loot with more players (+35% per extra player).",
                                      new AcceptableValueRange<int>(0, 1)));
            DangerScale = Config.Bind("Host", "DangerScale", 0,
                new ConfigDescription("0: enemies as the run settings say. 1: +10% enemy strength per extra player. 2: +25% per extra player.",
                                      new AcceptableValueRange<int>(0, 2)));
            VoiceRangePercent = Config.Bind("Host", "VoiceRangePercent", 100,
                new ConfigDescription("How far voices carry, as a percent of each player's own voice range.", new AcceptableValueRange<int>(25, 100)));
            Port = Config.Bind("Host", "Port", 7777, "UDP port for LAN/IP hosting.");
            AllowSpeedUp = Config.Bind("Host", "AllowSpeedUp", true,
                "Whether players may fast-forward time. Time is shared, so this is decided by the host.");
            LastAddress = Config.Bind("Join", "LastAddress", "127.0.0.1", "Last address joined over LAN/IP.");
            MenuKey = Config.Bind("Controls", "MenuKey", KeyCode.F1, "Opens the co-op menu.");
            ChatKey = Config.Bind("Controls", "ChatKey", KeyCode.Return, "Opens chat while in a session.");
            CatchKey = Config.Bind("Controls", "CatchKey", KeyCode.J, "Hold to catch a teammate falling onto you (softens their landing; a long fall hurts your arms).");
            GestureKey = Config.Bind("Controls", "GestureKey", KeyCode.H, "Hold for the gesture wheel (wave, point, help, hug...); release on a gesture.");
            AltChatKey = Config.Bind("Controls", "AltChatKey", KeyCode.T,
                "Also opens chat, but only while none of the game's own controls is bound to it (by default the game throws with T).");
            ShowNames = Config.Bind("UI", "ShowNames", true, "Show names above other players.");
            ShowArrows = Config.Bind("UI", "TeammateArrows", true, "Arrows at the screen edge pointing to teammates who are off screen (when the host allows them).");
            ReduceFlashes = Config.Bind("UI", "ReduceFlashes", false,
                "Softer co-op screen effects: no screen flashes or shakes from the mod (a teammate dying nearby, a revival), no pulsing in the spectator view, and still ping markers.");
            Language = Config.Bind("UI", "Language", "auto",
                "The mod's language: auto follows the game's language; or a code such as EN or zh-CN. Translations are lang/<code>.txt beside the mod.");
            ColorBlindColors = Config.Bind("UI", "ColorBlindColors", false,
                "Player colours (pings, arrows, gestures) from a palette that stays distinct with common colour blindness.");
            VoiceMode = Config.Bind("Voice", "Mode", "PushToTalk",
                new ConfigDescription("Proximity voice over Steam: hold a key to talk, an always-open mic (Steam filters silence), or off (you also hear nobody).",
                                      new AcceptableValueList<string>("PushToTalk", "Open", "Off")));
            VoiceKey = Config.Bind("Voice", "PushToTalkKey", KeyCode.V, "Hold to talk when Mode is PushToTalk.");
            VoiceVolume = Config.Bind("Voice", "Volume", 1f,
                new ConfigDescription("Volume of other players' voices.", new AcceptableValueRange<float>(0f, 1f)));
            VoiceRange = Config.Bind("Voice", "Range", 35f,
                new ConfigDescription("Distance (world units) at which a voice fades out completely. Full volume within 5.",
                                      new AcceptableValueRange<float>(10f, 200f)));
            VoiceGate = Config.Bind("Voice", "OpenMicThreshold", 0f,
                new ConfigDescription("Open mic only sends sound louder than this (0 = send everything Steam passes on). Set it from the co-op window, which shows your mic level.",
                                      new AcceptableValueRange<float>(0f, 0.3f)));
            UiScale = Config.Bind("UI", "UiScale", 0f,
                new ConfigDescription("Size of the co-op window. 0 picks one from the screen (larger on Steam Deck).", new AcceptableValueRange<float>(0f, 3f)));
            SteamTimeline = Config.Bind("UI", "SteamTimeline", true,
                "Mark co-op moments (layers, joins, deaths) on Steam's game-recording timeline.");
            VerboseLog = Config.Bind("Debug", "VerboseLog", false, "Write detailed sync logs.");
            UpdateCheckEnabled = Config.Bind("Update", "CheckForUpdates", true,
                "Once per launch, ask GitHub whether a newer release exists (one anonymous request; nothing is downloaded).");
            UpdateRepository = Config.Bind("Update", "Repository", "",
                "GitHub repository to check, as owner/name. Empty: no check.");
            Log.Verbose = VerboseLog.Value;

            InstallPatches();
            GameCheck.Run();
            Sync.CoopSaves.RecoverFromCrash();

            // Our own persistent object: plugin components are destroyed on scene load
            // in this game, and the session must survive menu <-> run transitions.
            var go = new GameObject("CasualtiesCoop") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(go);
            go.AddComponent<CoopRuntime>();

            Log.Info(Name + " " + Version + " loaded. Press " + MenuKey.Value + " for the co-op menu. Log: " + Log.FilePath);
            if (GameInfo.UnderWine) Log.Info("[env] running under Wine/Proton (Linux or Steam Deck); Linux home " + (GameInfo.LinuxHome() ?? "not reachable"));
        }

        /// <summary>
        /// Installs each patch class on its own, so one that no longer binds after a game
        /// update is reported by name instead of silently taking every later patch with it.
        /// </summary>
        private void InstallPatches()
        {
            var harmony = new Harmony(Guid);
            var failed = new System.Collections.Generic.List<string>();
            int ok = 0;
            foreach (var type in Assembly.GetExecutingAssembly().GetTypes()
                         .Where(t => t.GetCustomAttributes(typeof(HarmonyPatch), false).Length > 0)
                         .OrderBy(t => t.FullName))
            {
                try
                {
                    harmony.CreateClassProcessor(type).Patch();
                    ok++;
                }
                catch (Exception e)
                {
                    failed.Add(type.Name);
                    Log.Error("patch " + type.Name + " failed: " + (e.InnerException ?? e).Message);
                }
            }
            FailedPatches = failed.ToArray();
            if (failed.Count == 0) Log.Info("installed " + ok + " patches");
            else Log.Warn("installed " + ok + " patches; " + failed.Count + " FAILED: " + string.Join(", ", failed));
        }
    }
}
