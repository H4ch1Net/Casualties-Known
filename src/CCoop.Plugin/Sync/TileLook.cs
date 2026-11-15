using System;
using System.Reflection;
using CCoop.Plugin.Runtime;
using CCoop.Plugin.World;
using HarmonyLib;
using UnityEngine;

namespace CCoop.Plugin.Sync
{
    /// <summary>
    /// The look of the rock: when a chunk is first drawn, the game turns and flips its
    /// tiles from one random number (ChunkScript.RandomizeTiles), so the same wall looked
    /// different on every machine. In a session that number comes from the layer and the
    /// chunk's place instead, the same everywhere. A client redraws its chunks once the
    /// host's layer is in, so the pattern is worked out on the same terrain.
    /// </summary>
    [HarmonyPatch(typeof(ChunkScript), "OnWillRenderObject")]
    internal static class TileLook
    {
        private static readonly FieldInfo HasRandomized = AccessTools.Field(typeof(ChunkScript), "hasRandomized");

        private static void Prefix(ChunkScript __instance, out UnityEngine.Random.State? __state)
        {
            __state = null;
            if (!Coop.InSession || HasRandomized == null) return;
            var w = WorldGeneration.world;
            if (w == null || w.generatingWorld || (bool)HasRandomized.GetValue(__instance)) return;
            __state = UnityEngine.Random.state;
            UnityEngine.Random.InitState(Seed(__instance.pos, w.biomeDepth));
        }

        private static void Postfix(UnityEngine.Random.State? __state)
        {
            if (__state.HasValue) UnityEngine.Random.state = __state.Value;
        }

        private static int Seed(Vector2Int pos, int depth) =>
            unchecked(Coop.Epoch * 73856093 ^ depth * 19349663 ^ pos.x * 83492791 ^ pos.y * 486187739);

        /// <summary>Client, the host's layer applied: every chunk works its pattern out again (on the host's terrain).</summary>
        internal static void Redraw()
        {
            if (HasRandomized == null) return;
            try
            {
                var chunks = WorldAccess.Chunks;
                if (chunks == null) return;
                foreach (var tm in chunks)
                {
                    var cs = tm != null ? tm.GetComponent<ChunkScript>() : null;
                    if (cs != null) HasRandomized.SetValue(cs, false);
                }
            }
            catch (Exception e) { Log.Debug("[layer] tile look: " + e.Message); }
        }
    }
}
