using CCoop.Plugin.Runtime;
using HarmonyLib;

namespace CCoop.Plugin.Sync
{
    /// <summary>
    /// The host's loot and danger rules, applied where the game reads its own run
    /// settings, so the run settings themselves (and every save) stay the game's.
    ///
    /// Loot density is read only while the host builds a layer (clients receive the
    /// result), so more loot simply means the host generates more. Enemy strength is read
    /// as each creature starts, on every machine: everyone uses the same player count.
    /// </summary>
    [HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.GetRunSettingFloat))]
    internal static class TeamScalingPatch
    {
        private static int _loggedDepth = -1;

        private static void Postfix(string name, ref float __result)
        {
            if (!Coop.InSession || name == null) return;
            var rules = HostRules.Current;
            if (rules.LootScale == 0 && rules.DangerScale == 0) return;
            int players = Coop.Players.Count;
            if (players <= 1) return;
            if (name == "baselootdensity")
            {
                float f = rules.LootFactor(players);
                __result *= f;
                var w = WorldGeneration.world;
                if (f != 1f && w != null && w.biomeDepth != _loggedDepth)
                {
                    _loggedDepth = w.biomeDepth;
                    Log.Info("[rules] layer " + (w.biomeDepth + 1) + " loot x" + f.ToString("F2") + " for " + players + " players");
                }
            }
            else if (name == "enemystrength") __result *= rules.DangerFactor(players);
        }
    }
}
