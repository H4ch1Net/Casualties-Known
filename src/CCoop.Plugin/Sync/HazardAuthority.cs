using CCoop.Plugin.Runtime;
using HarmonyLib;
using UnityEngine;

namespace CCoop.Plugin.Sync
{
    /// <summary>
    /// Hazards that run on every machine that renders them and hit whatever is in reach.
    /// Their hits on the local player are right as they are, but their damage to objects,
    /// items and blocks would count once per machine (damage travels as amounts). Only
    /// the machine of the player nearest the hazard applies it; the others run it
    /// body-only, like a replayed explosion.
    /// </summary>
    internal static class HazardAuthority
    {
        /// <summary>True if no other living player in this layer is closer to <paramref name="at"/>.</summary>
        internal static bool LocalIsNearest(Vector2 at)
        {
            var body = GameInfo.LocalBody;
            float mine = body != null && body.alive ? Vector2.Distance(at, body.transform.position) : float.MaxValue;
            foreach (var rec in Coop.Players.Values)
            {
                if (rec.Id == Coop.LocalId || !rec.InWorld || !rec.HasPosition || !rec.Alive) continue;
                float d = Vector2.Distance(at, rec.Position);
                if (d < mine || (d == mine && rec.Id < Coop.LocalId)) return false;
            }
            return true;
        }

        /// <summary>Starts a body-only pass if this machine isn't the hazard's authority. Returns whether it did.</summary>
        internal static bool Begin(Component c)
        {
            if (!Coop.WorldLive || c == null || ExplosionSync.BodyOnly) return false;
            if (LocalIsNearest(c.transform.position)) return false;
            ExplosionSync.BodyOnly = true;
            return true;
        }

        internal static void End(bool began)
        {
            if (began) ExplosionSync.BodyOnly = false;
        }
    }

    [HarmonyPatch(typeof(FanTrap), "OnWillRenderObject")]
    internal static class FanAuthorityPatch
    {
        private static void Prefix(FanTrap __instance, out bool __state) => __state = HazardAuthority.Begin(__instance);
        private static void Finalizer(bool __state) => HazardAuthority.End(__state);
    }
}
