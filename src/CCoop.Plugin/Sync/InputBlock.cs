using System;
using CCoop.Plugin.Runtime;
using HarmonyLib;
using UnityEngine;

namespace CCoop.Plugin.Sync
{
    /// <summary>
    /// Holds game input back while chat or one of the co-op window's text fields has the
    /// keyboard, and lets the local character speak a chat line with the game's own
    /// speech distortion.
    ///
    /// Every rebindable game control is read through <c>KeyBinds.GetBind(name)</c>, so
    /// answering <c>KeyCode.None</c> there silences movement, attacks, the console key,
    /// pause and the rest in one place.
    /// </summary>
    internal static class InputBlock
    {
        internal static bool Blocking => Chat.Typing || UI.CoopWindow.TypingInField;

        /// <summary>The game's controls are held (typing, or a gesture wheel waiting for a click).</summary>
        internal static bool HoldGame => Blocking || GestureSync.HoldingInput || (UI.CoopWindow.IsOpen && GameInfo.InRunScene);

        internal static bool ConsoleOpen
        {
            get
            {
                try { return ConsoleScript.instance != null && ConsoleScript.instance.active; }
                catch { return false; }
            }
        }

        /// <summary>
        /// Has the local character say <paramref name="text"/> through its Talker (which
        /// slurs it for brain damage, a full mouth or being underwater) and returns what
        /// came out, or null if the character couldn't speak.
        /// </summary>
        internal static string Speak(Body body, string text)
        {
            var talker = body != null ? body.talker : null;
            if (talker == null) return null;
            try
            {
                string before = TalkPatch.Current(talker);
                talker.Talk(text, null, true, true);
                string after = TalkPatch.Current(talker);
                return !string.IsNullOrEmpty(after) && !ReferenceEquals(after, before) ? after : null;
            }
            catch (Exception e)
            {
                Log.Debug("speak: " + e.Message);
                return null;
            }
        }
    }

    [HarmonyPatch(typeof(KeyBinds), nameof(KeyBinds.GetBind))]
    internal static class KeyBindsBlockPatch
    {
        private static bool _raw;

        /// <summary>The key bound to an action, even while the mod holds the game's keys off.</summary>
        internal static KeyCode Raw(string action)
        {
            _raw = true;
            try { return KeyBinds.GetBind(action); }
            finally { _raw = false; }
        }

        private static bool Prefix(string action, ref KeyCode __result)
        {
            if (_raw) return true;
            bool click = action == "attack" || action == "iteminteract";
            if (InputBlock.HoldGame || (click && (UI.UiMouse.Over || UI.TeammatePanel.MenuUp)) ||
                (action == "iteminteract" && UI.TeammatePanel.ClaimsPress) || (action == "pause" && UI.TeammatePanel.Captures))
            {
                __result = KeyCode.None;
                return false;
            }
            return true;
        }
    }
}
