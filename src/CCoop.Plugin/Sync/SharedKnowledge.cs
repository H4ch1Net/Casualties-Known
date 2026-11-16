using System;
using CCoop.Plugin.Runtime;
using HarmonyLib;

namespace CCoop.Plugin.Sync
{
    /// <summary>
    /// What an item is (its name, how full or worn it is, what's in it) shows only to a
    /// character smart enough to recognize it (Recognition: min against the local body's
    /// intelligence); one character has none at all. In co-op a teammate who knows the
    /// item tells you: it counts as recognized when any living teammate in the layer would
    /// recognize it.
    /// </summary>
    [HarmonyPatch(typeof(Recognition), nameof(Recognition.recognizable), MethodType.Getter)]
    internal static class SharedKnowledge
    {
        private static void Postfix(Recognition __instance, ref bool __result)
        {
            if (__result || __instance == null || !Coop.InSession || !Coop.WorldLive) return;
            try
            {
                foreach (var rec in Coop.Players.Values)
                {
                    if (rec.Id == Coop.LocalId || !rec.Alive || !Coop.InLayer(rec) || rec.Intellect < 0) continue;
                    if (rec.Intellect >= __instance.min) { __result = true; return; }
                }
            }
            catch (Exception e) { Log.Debug("shared knowledge: " + e.Message); }
        }
    }
}
