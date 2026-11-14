using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace CCoop.Plugin
{
    /// <summary>
    /// Carries the decorative tile layers that belong to generated structures.
    ///
    /// The third class of generated object the entity manifest cannot see, and the last
    /// one reported missing in play: the glass panes, vents and panelling inside
    /// lifepods, medical buildings and corridors. Roots travel because the capture sweeps
    /// scene roots; chunk decoration travels because the capture was extended to sweep
    /// chunk children. These travel by neither, for a reason visible in one line of
    /// <c>GenerateEntityAtPos</c>:
    ///
    /// <code>
    ///   foreach (Transform item in basObj.transform)
    ///       if (item.GetComponent&lt;Item&gt;() || item.GetComponent&lt;BuildingEntity&gt;() ||
    ///           item.gameObject.name == "DOSPAWN")
    ///       {
    ///           var go = Instantiate(item.gameObject, pos + item.localPosition, item.localRotation);
    ///           if (go.GetComponent&lt;Tilemap&gt;()) go.transform.SetParent(worldGrid.transform);
    ///       }
    /// </code>
    ///
    /// A child carrying a <c>Tilemap</c> is re-parented to <c>worldGrid</c> — neither a
    /// scene root nor a chunk child — so nothing ever recorded it.
    ///
    /// **Only the Tilemap children are replayed.** That is what makes this cheap: the
    /// earlier design sketches both worried about double-spawning, because replaying a
    /// whole structure would recreate the <c>Item</c> and <c>BuildingEntity</c> children
    /// the manifest already carries. But those are exactly the children that stay at the
    /// scene root, and the Tilemap ones are exactly the children that do not. The two
    /// sets are disjoint by construction, so replaying only the second set needs no
    /// suppression window and no compound keys — it cannot duplicate anything.
    ///
    /// Nothing about the method is random: the children are picked by a fixed predicate
    /// and placed at prefab-relative offsets. So a client replaying it lands the same
    /// objects in the same places, and only the placement — prefab name and position —
    /// has to travel.
    /// </summary>
    internal static class StructureSync
    {
        /// <summary>
        /// Marks a manifest entry as a structure placement rather than a prefab to
        /// instantiate. Reserved rather than added as a new payload section: the manifest
        /// record is already exactly (key, x, y, rotation), which is a placement, so this
        /// rides the transfer that is already proven to carry 2,300 objects.
        /// </summary>
        internal const string KeyPrefix = "@struct:";

        internal struct Placement
        {
            public string Prefab;
            public float X;
            public float Y;
        }

        /// <summary>
        /// Placements seen during the host's current generation pass, in order.
        /// Host-side only; a client never runs <c>GenerateEntityAtPos</c>.
        /// </summary>
        private static readonly List<Placement> _placements = new List<Placement>();

        internal static int Count => _placements.Count;

        internal static IEnumerable<Placement> Placements => _placements;

        /// <summary>
        /// Forgets the previous layer's placements.
        ///
        /// Called when generation starts rather than after a capture: the capture can run
        /// more than once for one layer (a second player joining re-captures), and
        /// clearing there would send the second client an empty set.
        /// </summary>
        internal static void Clear() => _placements.Clear();

        internal static void Record(string prefab, Vector2 pos)
        {
            if (string.IsNullOrEmpty(prefab)) return;
            _placements.Add(new Placement { Prefab = prefab, X = pos.x, Y = pos.y });
        }

        /// <summary>
        /// Recreates one structure's tile decoration, reproducing
        /// <c>GenerateEntityAtPos</c> exactly for the children it skips.
        ///
        /// The half-unit shift is the game's own first line and has to be repeated here:
        /// the recorded position is the one the caller passed, not the shifted local the
        /// method works with, so that what travels is what the world generator decided
        /// rather than an intermediate value.
        /// </summary>
        /// <returns>How many objects were created.</returns>
        internal static int Replay(GameObject prefab, float x, float y, string namePrefix)
        {
            var world = WorldGeneration.world;
            if (prefab == null || world == null || world.worldGrid == null) return 0;

            Vector2 pos = new Vector2(x, y) - Vector2.one * 0.5f;
            int created = 0;

            foreach (Transform child in prefab.transform)
            {
                if (child == null) continue;

                // The game's own predicate for "this child gets instantiated".
                bool spawns = child.GetComponent<Item>() != null ||
                              child.GetComponent<BuildingEntity>() != null ||
                              child.gameObject.name == "DOSPAWN";
                if (!spawns) continue;

                // ...and the branch that decides it goes to worldGrid, which is precisely
                // the set the manifest never saw. Everything else is already there.
                if (child.GetComponent<Tilemap>() == null) continue;

                var go = UnityEngine.Object.Instantiate(
                    child.gameObject, pos + (Vector2)child.localPosition, child.localRotation);
                if (go == null) continue;

                // Prefixed so a re-apply removes exactly what we made. These do not sit at
                // the scene root, so the root sweep alone would never find them and every
                // re-apply would leave another copy behind.
                go.name = namePrefix + go.name;
                go.transform.SetParent(world.worldGrid.transform);
                created++;
            }

            return created;
        }
    }

    /// <summary>
    /// Records where world generation placed each structure.
    ///
    /// A prefix, not a postfix: <c>GenerateEntityAtPos</c> shifts its <c>pos</c> parameter
    /// by half a unit on its first line, and a postfix would read that shifted local. The
    /// value worth transmitting is the one the generator chose.
    ///
    /// Recorded unconditionally rather than only while hosting. Generation finishes before
    /// a peer necessarily knows its role, and a client suppresses the whole coroutine
    /// anyway, so this only ever fires on a peer generating its own world — where it costs
    /// one small struct per structure.
    /// </summary>
    [HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.GenerateEntityAtPos))]
    internal static class GenerateEntityAtPosPatch
    {
        private static void Prefix(Vector2 pos, GameObject basObj)
        {
            try
            {
                if (basObj == null) return;
                StructureSync.Record(basObj.name, pos);
            }
            catch (Exception e)
            {
                CoopPlugin.Warn("structure record failed: " + e.Message);
            }
        }
    }
}
