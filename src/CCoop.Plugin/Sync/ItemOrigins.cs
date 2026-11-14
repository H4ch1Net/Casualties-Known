using System.Collections.Generic;
using CCoop.Plugin.Runtime;
using CCoop.Plugin.World;
using HarmonyLib;
using UnityEngine;

namespace CCoop.Plugin.Sync
{
    /// <summary>
    /// Items the game makes without its item factory (<c>Utils.Create</c>): rounds and
    /// magazines unloaded from guns, casings thrown out when racking, batteries taken out,
    /// splints and tourniquets taken off, steak from a cooker, food that turns into
    /// another item, anything an inventory had no room for. Every item runs Start once,
    /// after its maker has finished with it, so that is where a new loose item is noticed.
    ///
    /// Two makers run on every machine and must not: corpse loot (the host's is already in
    /// the world) and the cooker (only the meat's owner cooks it).
    /// </summary>
    [HarmonyPatch(typeof(Item), "Start")]
    internal static class ItemStartPatch
    {
        private static void Postfix(Item __instance)
        {
            if (__instance == null || !Coop.WorldLive || Coop.Applying) return;
            if (!WorldAccess.Settled || WorldAccess.Regenerating) return;
            var go = __instance.gameObject;
            if (go.transform.parent != null || EntityRegistry.TryGetId(go, out _)) return;
            if (go.name.StartsWith("CCoop", System.StringComparison.Ordinal)) return;  // our own previews
            Coop.Get<EntitySync>()?.QueueSpawn(go);
        }
    }

    /// <summary>One item taken out of a container (by hand, from the inventory screen): now loose.</summary>
    [HarmonyPatch(typeof(Container), nameof(Container.UnloadItem))]
    internal static class ContainerUnloadItemSpawnPatch
    {
        private static void Postfix(Item item)
        {
            if (item == null || !Coop.WorldLive || Coop.Applying) return;
            if (item.transform.parent == null && !EntityRegistry.TryGetId(item.gameObject, out _))
                Coop.Get<EntitySync>()?.QueueSpawn(item.gameObject);
        }
    }

    /// <summary>
    /// A container spilling everything (it broke, or crafting used it up). A loose world
    /// container can break on every machine at once; only its owner's spill counts, and the
    /// others' copies of the contents are dropped (the owner's arrive as spawns).
    /// </summary>
    [HarmonyPatch(typeof(Container), nameof(Container.UnloadAllItems))]
    internal static class ContainerSpillPatch
    {
        private static void Prefix(Container __instance, out List<Item> __state)
        {
            __state = null;
            if (!Coop.WorldLive || Coop.Applying) return;
            __state = new List<Item>();
            foreach (Transform t in __instance.transform)
                if (t.TryGetComponent<Item>(out var it)) __state.Add(it);
        }

        private static void Postfix(Container __instance, List<Item> __state)
        {
            if (__state == null || __state.Count == 0) return;
            bool mine = true;
            var root = __instance.transform;
            while (root.parent != null) root = root.parent;
            if (!WorldAccess.IsPlayerObject(root.gameObject) && EntityRegistry.TryGetId(root.gameObject, out var id))
                mine = ItemSync.Owns(id);
            var sync = Coop.Get<EntitySync>();
            foreach (var it in __state)
            {
                if (it == null || it.transform.parent != null || EntityRegistry.TryGetId(it.gameObject, out _)) continue;
                if (mine) sync?.QueueSpawn(it.gameObject);
                else Object.Destroy(it.gameObject);
            }
        }
    }

    /// <summary>Clients: a corpse's random loot was rolled by the host when the layer was made.</summary>
    [HarmonyPatch(typeof(CorpseScript), "Start")]
    internal static class CorpseLootPatch
    {
        private static bool Prefix(CorpseScript __instance)
        {
            if (!Coop.IsClient) return true;
            // The one other thing Start does: one character can break human corpses.
            var body = GameInfo.LocalBody;
            if (!__instance.animalCorpse && body != null && body.charType == 2)
            {
                var b = __instance.GetComponent<BuildingEntity>();
                if (b != null) b.cantHit = false;
            }
            return false;
        }
    }

    /// <summary>A cooker turns meat into steak wherever the meat touches it; only the meat's owner may.</summary>
    [HarmonyPatch(typeof(Heater), "OnCollisionEnter2D")]
    internal static class CookerOwnerPatch
    {
        private static bool Prefix(Heater __instance, Collision2D collision)
        {
            if (!Coop.WorldLive || !__instance.cooker || collision == null) return true;
            var go = collision.gameObject;
            if (go == null || go.GetComponent<Item>() == null) return true;
            return !EntityRegistry.TryGetId(go, out var id) || ItemSync.Owns(id);
        }
    }
}
