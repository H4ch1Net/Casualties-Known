using System.Collections.Generic;
using CCoop.Net.Session;
using CCoop.Plugin.Protocol;
using CCoop.Plugin.Runtime;
using UnityEngine;

namespace CCoop.Plugin.Sync
{
    /// <summary>
    /// "Spawn apart" (Hard and Brutal): from layer 2 on, each player arrives at their own
    /// spot across the layer — same depth, spread sideways, never deeper — and the team has
    /// to find each other. The host picks open ground with headroom near the layer's own
    /// arrival height and sends each client theirs as it finishes loading.
    /// </summary>
    internal sealed class SpawnSpread : CoopModule
    {
        private const float Spacing = 30f;

        internal override void Attach(NetSession s) => s.On<SpawnPoint>(OnSpawn);

        private static bool Applies(WorldGeneration w) =>
            HostRules.Current.SpawnApart && w != null && w.biomeDepth >= 1 && w.biomeDepth <= 8;

        /// <summary>Host: a client finished loading; send it its own spot.</summary>
        internal void HostPeerEnteredLayer(byte id)
        {
            var w = WorldGeneration.world;
            var host = GameInfo.LocalBody;
            if (!Coop.IsHost || !Applies(w) || host == null) return;

            // Slot by join order, alternating right and left of the arrival point.
            var ids = new List<byte>(Coop.Players.Keys);
            ids.Sort();
            int slot = Mathf.Max(1, ids.IndexOf(id));
            float side = slot % 2 == 1 ? 1f : -1f;
            float offset = side * Spacing * ((slot + 1) / 2);
            Vector2 arrival = RunArrival ?? (Vector2)host.transform.position;
            var spot = FindOpen(w, arrival, offset);
            if (!spot.HasValue) return;
            Session.SendTo(id, new SpawnPoint { X = spot.Value.x, Y = spot.Value.y });
            Log.Info("[spawn] " + Coop.Player(id).Name + " starts at " + spot.Value.ToString("F0") + " (" + offset.ToString("+0;-0") + " m from the host)");
        }

        /// <summary>Host: where the layer put us, captured when it went live (before we walked off).</summary>
        private static Vector2? RunArrival;

        internal override void OnWorldLive()
        {
            var host = GameInfo.LocalBody;
            RunArrival = host != null ? (Vector2)host.transform.position : (Vector2?)null;
        }

        /// <summary>Open ground near <paramref name="from"/> + offset sideways: air with headroom over a solid block.</summary>
        internal static Vector2? FindOpen(WorldGeneration w, Vector2 from, float offset)
        {
            float halfWidth = w.width * 0.5f - 12f;
            for (float shrink = 1f; shrink > 0.2f; shrink -= 0.25f)
            {
                float x = Mathf.Clamp(from.x + offset * shrink, -halfWidth, halfWidth);
                // Search downward from a little above the arrival height, then upward.
                for (int dy = 0; dy < 70; dy++)
                {
                    foreach (int sign in new[] { -1, 1 })
                    {
                        if (sign == 1 && dy == 0) continue;
                        var p = new Vector2(x, from.y + 6f + sign * dy);
                        if (Open(w, p)) return p + Vector2.up * 1.2f;
                    }
                }
            }
            return null;
        }

        private static bool Open(WorldGeneration w, Vector2 p)
        {
            try
            {
                return w.GetBlock(p) == 0 && w.GetBlock(p + Vector2.up) == 0 && w.GetBlock(p + Vector2.up * 2f) == 0 &&
                       w.GetBlock(p + Vector2.down) != 0;
            }
            catch { return false; }
        }

        private void OnSpawn(byte origin, SpawnPoint m)
        {
            if (Coop.IsHost || GameInfo.LocalBody == null) return;
            BodyFreeze.PlaceAt(new Vector2(m.X, m.Y));
            CoopRuntime.Notice("You've come down apart from the others. Find them!");
            Log.Info("[spawn] starting apart at " + new Vector2(m.X, m.Y).ToString("F0"));
        }
    }
}
