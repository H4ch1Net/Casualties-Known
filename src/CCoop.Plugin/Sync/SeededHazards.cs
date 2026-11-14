using CCoop.Plugin.Runtime;
using HarmonyLib;
using UnityEngine;

namespace CCoop.Plugin.Sync
{
    /// <summary>
    /// Hazards that roll their own look and behaviour when they start: a fan's spin speed,
    /// a geyser's liquid, a grabber plant's reach. In co-op their dice are seeded from
    /// where they stand and the layer, the same on every machine, so each one is the same
    /// hazard for everyone. The game's own Start does the rolling; only the seed changes,
    /// and Unity's random state is put back afterwards.
    /// </summary>
    internal static class SeededHazards
    {
        internal static int Seeded;

        internal static int Seed(Vector2 at, int salt)
        {
            unchecked
            {
                uint h = 2166136261u;
                h = (h ^ (uint)Mathf.RoundToInt(at.x * 10f)) * 16777619u;
                h = (h ^ (uint)Mathf.RoundToInt(at.y * 10f)) * 16777619u;
                h = (h ^ (uint)salt) * 16777619u;
                h = (h ^ Coop.Epoch) * 16777619u;
                return (int)h;
            }
        }

        internal static bool Begin(Vector2 at, int salt, out Random.State saved)
        {
            saved = default(Random.State);
            if (!Coop.InSession) return false;
            saved = Random.state;
            Random.InitState(Seed(at, salt));
            Seeded++;
            return true;
        }

        internal static void End(bool began, Random.State saved)
        {
            if (began) Random.state = saved;
        }
    }

    [HarmonyPatch(typeof(FanTrap), "Start")]
    internal static class FanSeedPatch
    {
        private static readonly AccessTools.FieldRef<FanTrap, float> Spin = AccessTools.FieldRefAccess<FanTrap, float>("spinSpeed");
        private static readonly AccessTools.FieldRef<FanTrap, float> Progress = AccessTools.FieldRefAccess<FanTrap, float>("fanProgress");

        private static void Prefix(FanTrap __instance, out (bool, Random.State) __state)
        {
            bool began = SeededHazards.Begin(__instance.transform.position, 11, out var saved);
            __state = (began, saved);
        }

        private static void Postfix(FanTrap __instance, (bool, Random.State) __state)
        {
            if (!__state.Item1) return;
            // The blades' angle from the layer's shared clock, so they turn in step too.
            var w = WorldGeneration.world;
            if (w != null)
            {
                float offset = (SeededHazards.Seed(__instance.transform.position, 12) & 0xFFFF) / 65536f;
                Progress(__instance) = Mathf.Repeat(w.layerTimeSpent * Spin(__instance) + offset, 1f);
            }
        }

        private static void Finalizer((bool, Random.State) __state) => SeededHazards.End(__state.Item1, __state.Item2);
    }

    [HarmonyPatch(typeof(GeyserScript), "Start")]
    internal static class GeyserSeedPatch
    {
        private static void Prefix(GeyserScript __instance, out (bool, Random.State) __state)
        {
            var at = __instance.transform.parent != null ? __instance.transform.parent.position : __instance.transform.position;
            bool began = SeededHazards.Begin(at, 13, out var saved);
            __state = (began, saved);
        }

        private static void Finalizer((bool, Random.State) __state) => SeededHazards.End(__state.Item1, __state.Item2);
    }

    [HarmonyPatch(typeof(GrabberPlant), "Start")]
    internal static class GrabberSeedPatch
    {
        private static void Prefix(GrabberPlant __instance, out (bool, Random.State) __state)
        {
            bool began = SeededHazards.Begin(__instance.transform.position, 14, out var saved);
            __state = (began, saved);
        }

        private static void Finalizer((bool, Random.State) __state) => SeededHazards.End(__state.Item1, __state.Item2);
    }

    /// <summary>A geyser that starts to rumble here rumbles (and erupts) on every machine.</summary>
    [HarmonyPatch(typeof(GeyserScript), nameof(GeyserScript.TryRumble))]
    internal static class GeyserRumblePatch
    {
        private static readonly AccessTools.FieldRef<GeyserScript, float> RumbleTime = AccessTools.FieldRefAccess<GeyserScript, float>("rumbleTime");

        private static void Prefix(GeyserScript __instance, out float __state) => __state = RumbleTime(__instance);

        private static void Postfix(GeyserScript __instance, float __state)
        {
            if (__state <= 0f && RumbleTime(__instance) > 0f) WorldEvents.Fire(Protocol.WorldEventKind.GeyserRumble, __instance);
        }
    }
}
