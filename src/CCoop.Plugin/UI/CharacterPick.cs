using System;
using CCoop.Plugin.Runtime;
using HarmonyLib;
using UnityEngine;

namespace CCoop.Plugin.UI
{
    /// <summary>
    /// Each player's character for co-op runs, picked in the lobby (or on the game's own
    /// character screen, which stays in step). Kept in the mod's config because the game
    /// forgets its pick every time the menu loads. Unlocks are each player's own, by the
    /// game's rule: a character opens once you've won a run with the one before it.
    /// The pick reaches the others through the player profile the mod already sends.
    /// </summary>
    internal static class CharacterPick
    {
        internal const int Count = 3;
        private static int _appliedScene = -1;
        private static object _appliedSession;

        internal static bool Unlocked(int c)
        {
            if (c <= 0) return true;
            if (c >= Count) return false;
            try { return PlayerPrefs.GetInt("wonchar" + (c - 1)) > 0; }
            catch { return false; }
        }

        /// <summary>The character this player goes into co-op runs as.</summary>
        internal static int Current
        {
            get
            {
                int c = CoopPlugin.Ready ? Mathf.Clamp(CoopPlugin.Instance.Character.Value, 0, Count - 1) : 0;
                return Unlocked(c) ? c : 0;
            }
        }

        /// <summary>Picks a character (if unlocked), here and on the game's screen.</summary>
        internal static void Pick(int c)
        {
            if (!Unlocked(c)) return;
            if (CoopPlugin.Ready) CoopPlugin.Instance.Character.Value = c;
            PreRunScript.characterSelected = c;
            Refresh();
            try { PlayerCamera.PlayUISound("click"); } catch { /* no sound */ }
        }

        /// <summary>The next unlocked character in <paramref name="dir"/> (+1 or -1).</summary>
        internal static void Step(int dir)
        {
            int c = Current;
            for (int i = 1; i <= Count; i++)
            {
                int n = ((c + dir * i) % Count + Count) % Count;
                if (Unlocked(n)) { if (n != c) Pick(n); return; }
            }
        }

        /// <summary>
        /// On the menu in a session: the game's pick follows the lobby's (once per menu load,
        /// so a saved character set up for a join is left alone).
        /// </summary>
        internal static void Ensure()
        {
            if (!GameInfo.OnMainMenu || !Coop.InSession || PreRunScript.instance == null) return;
            int scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
            if (scene == _appliedScene && ReferenceEquals(_appliedSession, Coop.Session)) return;
            _appliedScene = scene;
            _appliedSession = Coop.Session;
            PreRunScript.characterSelected = Current;
            Refresh();
        }

        /// <summary>The game's character screen texts, without its click sound.</summary>
        private static void Refresh()
        {
            try { if (PreRunScript.instance != null) Traverse.Create(PreRunScript.instance).Method("UpdateCharacterText").GetValue(); }
            catch { /* the screen isn't built */ }
        }

        internal static string Name(int c)
        {
            try
            {
                string s = Locale.GetOther("char" + c + "name");
                if (!string.IsNullOrEmpty(s) && s != "char" + c + "name") return s;
            }
            catch { /* no language loaded */ }
            return GameInfo.CharacterName(c);
        }

        internal static string Text(int c, string part)
        {
            try
            {
                string key = "char" + c + part;
                string s = Locale.GetOther(key);
                return s == key ? "" : s;
            }
            catch { return ""; }
        }

        internal static int[] Stats(int c)
        {
            try { return Skills.BaseSkills(c); }
            catch { return null; }
        }
    }

    /// <summary>A pick on the game's own character screen becomes the co-op pick too.</summary>
    [HarmonyPatch(typeof(PreRunScript), nameof(PreRunScript.SelectCharacter))]
    internal static class SelectCharacterPatch
    {
        private static void Postfix(int chr)
        {
            if (CoopPlugin.Ready && CharacterPick.Unlocked(chr)) CoopPlugin.Instance.Character.Value = chr;
        }
    }

    /// <summary>The host's run starts with the lobby's pick.</summary>
    [HarmonyPatch(typeof(PreRunScript), nameof(PreRunScript.StartRun))]
    internal static class StartRunCharacterPatch
    {
        private static void Prefix()
        {
            try { if (Coop.InSession) PreRunScript.characterSelected = CharacterPick.Current; }
            catch (Exception e) { Log.Debug("start run character: " + e.Message); }
        }
    }
}
