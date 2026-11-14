using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using Steamworks;
using UnityEngine;

namespace CCoop.Plugin.Runtime
{
    /// <summary>Facts about the running game and the local player.</summary>
    internal static class GameInfo
    {
        public const uint FullGameAppId = 4576490;
        public const uint DemoAppId = 4576510;

        public static readonly string[] CharacterNames = { "Experiment", "Milky", "Dune" };

        private static string _build;

        /// <summary>
        /// Short hash of Assembly-CSharp. Clients take the host's world wholesale, so a
        /// different game build cannot be "mostly compatible" and is refused at the door.
        /// </summary>
        public static string GameBuild
        {
            get
            {
                if (_build != null) return _build;
                try
                {
                    var path = Path.Combine(Path.Combine(Application.dataPath, "Managed"), "Assembly-CSharp.dll");
                    using (var sha = SHA256.Create())
                    using (var fs = File.OpenRead(path))
                        _build = BitConverter.ToString(sha.ComputeHash(fs), 0, 8).Replace("-", "").ToLowerInvariant();
                }
                catch (Exception e)
                {
                    Log.Warn("could not hash the game assembly: " + e.Message);
                    _build = "unknown";
                }
                return _build;
            }
        }

        private static FieldInfo _steamInitialized;

        /// <summary>
        /// True once the game's own SteamManager has initialised Steamworks. We never call
        /// SteamAPI.Init ourselves: the game owns the lifetime and pumps the callbacks.
        /// </summary>
        public static bool SteamReady
        {
            get
            {
                try
                {
                    var mgr = SteamManager.instance;
                    if (mgr == null) return false;
                    if (_steamInitialized == null)
                        _steamInitialized = typeof(SteamManager).GetField("initialized", BindingFlags.Instance | BindingFlags.NonPublic);
                    return _steamInitialized != null && (bool)_steamInitialized.GetValue(mgr);
                }
                catch
                {
                    return false;
                }
            }
        }

        public static string SteamName
        {
            get
            {
                if (!SteamReady) return null;
                try { return SteamFriends.GetPersonaName(); }
                catch { return null; }
            }
        }

        public static ulong SteamId
        {
            get
            {
                if (!SteamReady) return 0;
                try { return SteamUser.GetSteamID().m_SteamID; }
                catch { return 0; }
            }
        }

        public static string PlayerName
        {
            get
            {
                var configured = CoopPlugin.Instance != null ? CoopPlugin.Instance.PlayerName.Value : null;
                if (!string.IsNullOrEmpty(configured)) return configured;
                return SteamName ?? Environment.UserName;
            }
        }

        public static string CharacterName(int index) =>
            index >= 0 && index < CharacterNames.Length ? CharacterNames[index] : CharacterNames[CharacterNames.Length - 1];

        /// <summary>The local player's body, or null outside a run.</summary>
        public static Body LocalBody => PlayerCamera.main != null ? PlayerCamera.main.body : null;

        public static bool InRunScene => WorldGeneration.world != null && PlayerCamera.main != null;

        public static bool OnMainMenu => PreRunScript.instance != null && WorldGeneration.world == null;
    }
}
