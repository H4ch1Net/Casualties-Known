using System;
using CCoop.Net.Session;
using CCoop.Plugin.Protocol;
using CCoop.Plugin.Runtime;
using HarmonyLib;
using UnityEngine;
using CCoop.Plugin.UI;

namespace CCoop.Plugin.Sync
{
    /// <summary>
    /// Lifepod heater and shower. Only the resulting state travels, so applying it twice
    /// is harmless. Buttons are addressed by position: structure replay is exact, so
    /// every peer has the same button in the same place.
    /// </summary>
    internal sealed class PropSync : CoopModule
    {
        private const byte Shower = 255;

        internal override void Attach(NetSession s) => WorldNet.Register<PropUse>(s, OnUse);

        internal override void OnWorldLost() => ShowerPerPlayerPatch.UsedHere.Clear();

        internal static void Announce(LifepodButton b)
        {
            if (!Coop.WorldLive || Coop.Applying || b == null) return;
            if (b.type != 0 && !ShowerPerPlayerPatch.Fired) return;  // already used: nothing ran
            byte state = b.type != 0 ? Shower : (byte)Mathf.Clamp(b.controller != null ? b.controller.heatState : 0, 0, 2);
            WorldNet.Publish(new PropUse { X = b.transform.position.x, Y = b.transform.position.y, State = state });
        }

        private static void OnUse(byte origin, PropUse m)
        {
            LifepodButton best = null;
            float bestDist = 0.4f;
            var at = new Vector2(m.X, m.Y);
            foreach (var b in UnityEngine.Object.FindObjectsOfType<LifepodButton>())
            {
                float d = Vector2.Distance(at, b.transform.position);
                if (d < bestDist) { bestDist = d; best = b; }
            }
            if (best == null || best.controller == null) return;
            if (m.State == Shower)
            {
                best.controller.ActivateShower();
                // A teammate's shower isn't ours: the button stays lit until we use it.
                var c = best.controller;
                if (c.shower != null && !ShowerPerPlayerPatch.UsedHere.Contains(c.shower.GetInstanceID()) && c.disinfectSprite != null)
                    c.disinfectSprite.color = c.onColor;
                return;
            }
            for (int guard = 0; best.controller.heatState != m.State && guard < 6; guard++)
                best.controller.ToggleHeatState();
        }
    }

    /// <summary>
    /// In the game the lifepod shower works once. In co-op it works once for each
    /// player: a teammate's shower (replayed here) runs and washes whoever stands in it,
    /// but doesn't use up ours.
    /// </summary>
    [HarmonyPatch(typeof(LifepodShower), nameof(LifepodShower.Activate))]
    internal static class ShowerPerPlayerPatch
    {
        /// <summary>Showers the local player has used (instance ids; cleared each layer).</summary>
        internal static readonly System.Collections.Generic.HashSet<int> UsedHere = new System.Collections.Generic.HashSet<int>();

        /// <summary>The last local Activate actually ran the shower.</summary>
        internal static bool Fired;

        private static void Prefix(LifepodShower __instance)
        {
            Fired = false;
            if (!Coop.InSession) { Fired = !__instance.activated; return; }
            int id = __instance.GetInstanceID();
            if (Coop.Applying) { __instance.activated = false; return; }
            if (UsedHere.Add(id))
            {
                __instance.activated = false;
                Fired = true;
                Log.Info("[prop] shower used");
            }
        }

        private static void Postfix(LifepodShower __instance)
        {
            if (Coop.InSession && Coop.Applying) __instance.activated = UsedHere.Contains(__instance.GetInstanceID());
        }
    }

    [HarmonyPatch(typeof(LifepodButton), nameof(LifepodButton.OnUse))]
    internal static class LifepodButtonPatch
    {
        private static void Postfix(LifepodButton __instance)
        {
            try { PropSync.Announce(__instance); }
            catch (Exception e) { Log.Debug("prop hook: " + e.Message); }
        }
    }

    /// <summary>
    /// The game has one save slot and the host's world is not saveable at all (saves
    /// hold the player only, and regenerate the layer on load). Saving mid-session would
    /// overwrite the single-player save with a co-op character; block it.
    /// </summary>
    [HarmonyPatch(typeof(SaveSystem), nameof(SaveSystem.SaveGame))]
    internal static class SaveGuardPatch
    {
        private static bool Prefix()
        {
            if (!Coop.InSession || SaveGate.Allowed) return true;
            Log.Info("[save] blocked during a co-op session");
            CoopRuntime.Notice(Lang.T("Saving is disabled during co-op"));
            return false;
        }
    }
}
