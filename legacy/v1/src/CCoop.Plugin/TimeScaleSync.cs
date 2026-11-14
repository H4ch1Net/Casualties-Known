using System;
using CCoop.Protocol.Messages;
using HarmonyLib;
using UnityEngine;

namespace CCoop.Plugin
{
    /// <summary>
    /// Keeps every peer running at the same speed.
    ///
    /// <c>Time.timeScale</c> is global to a process and drives physics, animation, AI and
    /// fluid alike, so a peer that speeds up does not merely see things sooner — its
    /// entire simulation runs away from everyone else's at once. Of everything left
    /// unsynchronised this is the cheapest to break and the widest in effect: one press
    /// of the speed key puts the two worlds 20x out of step.
    ///
    /// <c>SetTimeScale</c> is the single funnel — the speed keys, the movement reset, the
    /// pause handler, unconsciousness and the reading effect all end here — so one patch
    /// sees every change and can decide what to do with each.
    /// </summary>
    [HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.SetTimeScale))]
    internal static class SetTimeScalePatch
    {
        /// <summary>Set while applying the host's answer, so it is not sent back.</summary>
        internal static bool ApplyingAuthoritative;

        private static bool Prefix(PlayerCamera.SpeedType speed, bool force)
        {
            try
            {
                if (ApplyingAuthoritative) return true;

                var runner = CoopRunner.Active;
                if (runner == null || !runner.InSession) return true;   // single player

                return runner.OnLocalTimeScale(speed, force);
            }
            catch (Exception e)
            {
                // Never leave the player stuck at 20x because of a networking fault.
                CoopPlugin.Warn("time scale patch error (allowing local change): " + e.Message);
                return true;
            }
        }
    }

    /// <summary>Applies a session speed locally, without echoing it back to the host.</summary>
    internal static class TimeScaleSync
    {
        internal static void Apply(SessionSpeed speed, Action<string> log)
        {
            var camera = PlayerCamera.main;
            if (camera == null) return;

            SetTimeScalePatch.ApplyingAuthoritative = true;
            try
            {
                // switchSound stays on: the click is the only feedback a player gets that
                // the speed changed, and it should sound the same whoever asked for it.
                camera.SetTimeScale((PlayerCamera.SpeedType)(byte)speed);
                log?.Invoke("[time] session speed is now " + speed +
                            " (" + Time.timeScale.ToString("0.##") + "x)");
            }
            catch (Exception e)
            {
                log?.Invoke("[time] could not apply " + speed + ": " + e.Message);
            }
            finally
            {
                SetTimeScalePatch.ApplyingAuthoritative = false;
            }
        }
    }
}
