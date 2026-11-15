using System;
using CCoop.Plugin.Protocol;
using CCoop.Plugin.Runtime;
using CCoop.Plugin.World;
using HarmonyLib;
using UnityEngine;
using CCoop.Plugin.UI;

namespace CCoop.Plugin.Sync
{
    // Fixes from the game audit (docs/game-systems.md §4). Each is small and tied to one
    // game method, so they live together rather than scattered through the modules.

    /// <summary>
    /// A client's scaffold has no terrain, so PlaceBody finds no ground — and on the End
    /// lab layer it throws looking for ENDLABSPAWN, aborting generation. The snapshot
    /// places the body anyway, so a scaffold skips it.
    /// </summary>
    [HarmonyPatch(typeof(Body), nameof(Body.PlaceBody))]
    internal static class ScaffoldPlaceBodyPatch
    {
        private static bool Prefix() => !Skip.Content;
    }

    /// <summary>
    /// The bottom-of-layer panel's "Save &amp; exit" would save (blocked in a session) and
    /// then drop this player out of the team's run. In a session it does nothing but
    /// explain; "Continue" still asks the host to descend.
    /// </summary>
    [HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.SaveAndExit))]
    internal static class SaveAndExitPatch
    {
        private static bool Prefix()
        {
            if (!Coop.InSession) return true;
            CoopRuntime.Notice(Lang.T("Saving is off in co-op. Press Continue to descend together, or leave from the co-op menu (F1)."));
            return false;
        }
    }

    // ------------------------------------------------------------------ death loot

    /// <summary>A guard's death drops (pistol, magazine, a medical item) come from the host only.</summary>
    [HarmonyPatch(typeof(GuardScript), nameof(GuardScript.Die))]
    internal static class GuardDiePatch
    {
        private static void Prefix() { if (Coop.IsClient && Coop.WorldLive) EntitySync.SuppressLoot++; }
        private static void Finalizer() { if (EntitySync.SuppressLoot > 0) EntitySync.SuppressLoot--; }
    }

    /// <summary>
    /// A trader dropping its stock runs on whichever machines render it. Clients drop
    /// nothing and ask the host, whose stock list empties after one drop — so the host
    /// can't be asked twice.
    /// </summary>
    [HarmonyPatch(typeof(TraderScript), nameof(TraderScript.DropInventory))]
    internal static class TraderDropPatch
    {
        private static void Prefix(TraderScript __instance)
        {
            if (!Coop.IsClient || !Coop.WorldLive) return;
            EntitySync.SuppressLoot++;
            if (EntityRegistry.TryGetId(__instance.gameObject, out var id))
                WorldNet.Publish(new EntityAction { Id = id, Kind = EntityActionKind.TraderDropInventory });
        }

        private static void Finalizer()
        {
            if (EntitySync.SuppressLoot > 0) EntitySync.SuppressLoot--;
        }
    }

    /// <summary>A fan blade snapping drops scrap; only the host's scrap is real.</summary>
    [HarmonyPatch(typeof(FanTrap), nameof(FanTrap.BreakFan))]
    internal static class FanBreakPatch
    {
        private static void Prefix(FanTrap __instance, int index)
        {
            if (!Coop.IsClient || !Coop.WorldLive) return;
            EntitySync.SuppressLoot++;
            if (EntityRegistry.TryGetId(__instance.gameObject, out var id))
                WorldNet.Publish(new EntityAction { Id = id, Kind = EntityActionKind.FanBreak, Arg = index });
        }

        private static void Finalizer()
        {
            if (EntitySync.SuppressLoot > 0) EntitySync.SuppressLoot--;
        }
    }

    // ------------------------------------------------------------------ laser drone

    /// <summary>
    /// Opening halves a drone's health and closing doubles it. That is not damage, but
    /// the health hook would report it as such and every other machine would subtract it.
    /// </summary>
    [HarmonyPatch(typeof(LaserDroneScript), nameof(LaserDroneScript.Open))]
    internal static class DroneOpenPatch
    {
        private static void Prefix() => EntitySync.SuppressDamage++;
        private static void Finalizer() { if (EntitySync.SuppressDamage > 0) EntitySync.SuppressDamage--; }
    }

    [HarmonyPatch(typeof(LaserDroneScript), nameof(LaserDroneScript.Close))]
    internal static class DroneClosePatch
    {
        private static void Prefix() => EntitySync.SuppressDamage++;
        private static void Finalizer() { if (EntitySync.SuppressDamage > 0) EntitySync.SuppressDamage--; }
    }

    // ------------------------------------------------------------------ creature puppets

    /// <summary>
    /// These creatures move themselves by writing transforms, velocities or rotations,
    /// which also moves a kinematic body. On a machine that does not own the creature that
    /// fights the streamed position, so their AI does not run there. A guard still dies
    /// there (its corpse animation), because death is decided by synced health.
    /// </summary>
    [HarmonyPatch(typeof(GuardScript), "Update")]
    internal static class GuardPuppetPatch
    {
        private static readonly AccessTools.FieldRef<GuardScript, BuildingEntity> Bld = AccessTools.FieldRefAccess<GuardScript, BuildingEntity>("bld");
        private static readonly AccessTools.FieldRef<GuardScript, bool> DidDie = AccessTools.FieldRefAccess<GuardScript, bool>("didDie");

        private static bool Prefix(GuardScript __instance)
        {
            if (!CreatureSync.IsPuppetObject(__instance.gameObject)) return true;
            try
            {
                var bld = Bld(__instance);
                if (bld != null && bld.health <= 0f && !DidDie(__instance)) __instance.Die();
            }
            catch (Exception e) { Log.Debug("guard puppet: " + e.Message); }
            return false;
        }
    }

    [HarmonyPatch(typeof(CrystalEnemy), "Update")]
    internal static class CrystalEnemyPuppetPatch
    {
        private static bool Prefix(CrystalEnemy __instance) => !CreatureSync.IsPuppetObject(__instance.gameObject);
    }

    [HarmonyPatch(typeof(CrystalEnemy), "FixedUpdate")]
    internal static class CrystalEnemyPuppetFixedPatch
    {
        private static bool Prefix(CrystalEnemy __instance) => !CreatureSync.IsPuppetObject(__instance.gameObject);
    }

    [HarmonyPatch(typeof(LaserDroneScript), "Update")]
    internal static class DronePuppetPatch
    {
        private static bool Prefix(LaserDroneScript __instance) => !CreatureSync.IsPuppetObject(__instance.gameObject);
    }

    [HarmonyPatch(typeof(LaserDroneScript), "FixedUpdate")]
    internal static class DronePuppetFixedPatch
    {
        private static bool Prefix(LaserDroneScript __instance) => !CreatureSync.IsPuppetObject(__instance.gameObject);
    }

    [HarmonyPatch(typeof(TechballScript), "Update")]
    internal static class TechballPuppetPatch
    {
        private static bool Prefix(TechballScript __instance) => !CreatureSync.IsPuppetObject(__instance.gameObject);
    }
}
