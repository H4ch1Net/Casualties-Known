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
                if (rec.Id == Coop.LocalId || !Coop.InLayer(rec) || !rec.Alive) continue;
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

    /// <summary>
    /// Pistons each roll their own rhythm and keep their own time, so a teammate sees a
    /// piston up while it crushes you. In co-op a piston's rhythm comes from its position
    /// (the same on every machine) and its phase from the layer's shared clock, so every
    /// machine's piston rises and falls together. The game still does the moving, the
    /// sound and the crush test against the local player.
    /// </summary>
    [HarmonyPatch(typeof(PistonScript), "Update")]
    internal static class PistonPhasePatch
    {
        private static readonly AccessTools.FieldRef<PistonScript, float> Delay = AccessTools.FieldRefAccess<PistonScript, float>("delay");
        private static readonly AccessTools.FieldRef<PistonScript, float> DesiredDelay = AccessTools.FieldRefAccess<PistonScript, float>("desiredDelay");
        private static readonly AccessTools.FieldRef<PistonScript, bool> Up = AccessTools.FieldRefAccess<PistonScript, bool>("up");

        private static float Hash01(Vector2 p, int salt)
        {
            unchecked
            {
                int h = Mathf.RoundToInt(p.x * 4f) * 73856093 ^ Mathf.RoundToInt(p.y * 4f) * 19349663 ^ salt * 83492791;
                h ^= h >> 13;
                h *= 0x5bd1e995;
                h ^= h >> 15;
                return (h & 0xFFFFFF) / (float)0x1000000;
            }
        }

        private static void Prefix(PistonScript __instance)
        {
            var w = WorldGeneration.world;
            if (!Coop.WorldLive || w == null) return;
            Vector2 at = __instance.transform.position;
            float period = 1f + 1.5f * Hash01(at, 1);  // the game's own range, 1 to 2.5 s
            DesiredDelay(__instance) = period;
            double phase = (w.layerTimeSpent + Hash01(at, 2) * period * 2f) / period;
            bool up = ((long)System.Math.Floor(phase) & 1) == 1;
            if (Up(__instance) != up) Delay(__instance) = 0f;  // the game toggles it this frame
            else Delay(__instance) = (float)((1.0 - (phase - System.Math.Floor(phase))) * period) + 0.001f;
        }
    }
}
