using System;
using CCoop.Plugin.Runtime;
using HarmonyLib;

namespace CCoop.Plugin.Sync
{
    /// <summary>
    /// The host's Sleep rule turned off: nobody gets tired (the game drains Body.energy in
    /// HandleBody; it's kept full) and nobody can nap (Body.canTakeNap). Sleeping pills
    /// still put you to sleep, as the game does.
    /// </summary>
    internal static class NoSleep
    {
        internal static bool On => Coop.InSession && !HostRules.Current.Sleep;

        [HarmonyPatch(typeof(Body), "HandleBody")]
        internal static class EnergyPatch
        {
            private static void Postfix(Body __instance)
            {
                try
                {
                    if (On && !__instance.sleeping && ReferenceEquals(__instance, GameInfo.LocalBody)) __instance.energy = 100f;
                }
                catch (Exception e) { Log.Debug("no sleep: " + e.Message); }
            }
        }

        [HarmonyPatch(typeof(Body), nameof(Body.canTakeNap), MethodType.Getter)]
        internal static class NapPatch
        {
            private static bool Prefix(Body __instance, ref bool __result)
            {
                if (!On || __instance.GetComponent<SleepingPills>() != null) return true;
                __result = false;
                return false;
            }
        }
    }
}
