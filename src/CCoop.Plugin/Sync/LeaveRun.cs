using System;
using CCoop.Plugin.Runtime;
using CCoop.Plugin.UI;
using HarmonyLib;
using UnityEngine;

namespace CCoop.Plugin.Sync
{
    /// <summary>
    /// Leaving a co-op run for the main menu. The pause menu's middle button is the game's
    /// "Give up" (four presses and the character dies), which in co-op only ends in
    /// spectating and a respawn: there was no way out. In a session it becomes the game's
    /// own Exit button, as in the tutorial: press once to arm it, again to leave.
    ///
    ///  * A client leaves the session and goes to the menu. Its character is saved, and its
    ///    puppet goes from the others' screens; joining again brings it back where it was.
    ///  * The host takes everyone back to the menu, still in the session; the run is saved
    ///    and can be continued from the lobby.
    /// </summary>
    internal static class LeaveRun
    {
        private const float ArmedFor = 8f;

        private static float _armedAt = -99f;
        private static Sprite _giveUp;
        private static bool _shownExit;

        private static bool Applies =>
            Coop.InSession && WorldGeneration.world != null && WorldGeneration.world.biomeOverride != WorldGeneration.OverrideSceneType.Tutorial;

        private static bool Armed => Time.unscaledTime - _armedAt < ArmedFor;

        /// <summary>Leaves now (the pause button's second press, or the lobby's Leave in a run).</summary>
        internal static void Leave()
        {
            _armedAt = -99f;
            var s = Coop.Session;
            bool host = s != null && s.IsHost;
            Log.Info("[run] " + (host ? "host takes everyone back to the menu" : "leaving the run for the menu"));
            if (!host && Coop.Runtime != null) Coop.Runtime.Leave();
            RunFlow.ReturnToMenu();
        }

        [HarmonyPatch(typeof(PauseHandler), nameof(PauseHandler.Update))]
        internal static class ExitButtonPatch
        {
            private static void Postfix(PauseHandler __instance)
            {
                try
                {
                    var button = __instance.quitButton;
                    if (button == null) return;
                    var tip = button.GetComponent<UITooltip>();
                    if (!Applies)
                    {
                        if (_shownExit)
                        {
                            // Out of the session: the game's own Give up again.
                            _shownExit = false;
                            if (_giveUp != null) button.sprite = _giveUp;
                            if (tip != null) tip.skipLocale = false;
                            __instance.ResetQuitPressed();
                        }
                        return;
                    }
                    if (!_shownExit)
                    {
                        _shownExit = true;
                        if (_giveUp == null) _giveUp = button.sprite;
                        if (__instance.exitSprite != null) button.sprite = __instance.exitSprite;
                        __instance.timesQuitPressed = 0;
                    }
                    if (tip == null) return;
                    tip.skipLocale = true;
                    bool host = Coop.Session != null && Coop.Session.IsHost;
                    if (Armed)
                    {
                        button.color = new Color(1f, 0.75f, 0.6f);
                        tip.tipName = Lang.T("Press again to leave");
                        tip.tipDesc = host ? Lang.T("Everyone goes back to the menu. The run is saved.") : Lang.T("Your character is saved for when you join again.");
                    }
                    else
                    {
                        button.color = Color.white;
                        tip.tipName = host ? Lang.T("Back to the menu") : Lang.T("Leave to the menu");
                        tip.tipDesc = "";
                    }
                }
                catch (Exception e) { Log.Debug("exit button: " + e.Message); }
            }
        }

        [HarmonyPatch(typeof(PauseHandler), nameof(PauseHandler.TryQuit))]
        internal static class ExitPressPatch
        {
            private static bool Prefix()
            {
                if (!Applies) return true;
                if (!Armed)
                {
                    _armedAt = Time.unscaledTime;
                    try { PlayerCamera.PlayUISound("click"); } catch { /* no sound */ }
                    return false;
                }
                Leave();
                return false;
            }
        }
    }
}
