using System;
using System.Collections.Generic;
using CCoop.Net.Session;
using CCoop.Plugin.Protocol;
using CCoop.Plugin.Runtime;
using CCoop.Plugin.World;
using HarmonyLib;
using UnityEngine;

namespace CCoop.Plugin.Sync
{
    /// <summary>
    /// The game crafts with anything within 10 m, items on the ground included. Two
    /// players crafting from the same ground item at once would both use it up. A client
    /// asks the host for the ground items a recipe would use and crafts when granted;
    /// the host keeps them for that client for a few seconds, and its own crafting skips
    /// items another player is about to use. Items used up vanish for everyone as before;
    /// those worn down (a tool) or drained (a bottle) are re-sent with their new state.
    /// </summary>
    internal sealed class CraftSync : CoopModule
    {
        private const float Hold = 3f;

        internal static bool Granting;

        private readonly Dictionary<uint, KeyValuePair<byte, float>> _held = new Dictionary<uint, KeyValuePair<byte, float>>();
        private Recipe _pending;
        private byte _request;
        private float _pendingUntil;

        internal static int Granted, Denied;

        internal override void Attach(NetSession s) => WorldNet.Register<CraftClaim>(s, OnClaim, relay: false);
        internal override void Detach() => Clear();
        internal override void OnWorldLost() => Clear();

        private void Clear()
        {
            _held.Clear();
            _pending = null;
        }

        internal override void Tick()
        {
            if (_pending != null && Time.unscaledTime > _pendingUntil)
            {
                _pending = null;
                CoopRuntime.Notice("The host didn't answer; try crafting again");
            }
        }

        /// <summary>The ground items (networked, loose) a recipe would use right now.</summary>
        private static List<uint> GroundItems(Recipe r)
        {
            var used = r.GetItemsForRecipe();
            if (used == null) return null;
            var ids = new List<uint>();
            foreach (var it in used)
                if (it != null && it.transform.parent == null && EntityRegistry.TryGetId(it.gameObject, out var id) && !ids.Contains(id))
                    ids.Add(id);
            return ids;
        }

        /// <summary>Before the game crafts. Returns whether it may go ahead now.</summary>
        internal bool BeforeMake(Recipe r)
        {
            if (Granting || !Coop.WorldLive || r == null) return true;
            List<uint> ids;
            try { ids = GroundItems(r); }
            catch (Exception e)
            {
                Log.Debug("[craft] " + e.Message);
                return true;
            }
            if (ids == null || ids.Count == 0) return true;  // nothing on the ground, or the game refuses on its own

            if (Coop.IsHost)
            {
                foreach (var id in ids)
                    if (HeldByOther(id, NetSession.HostId))
                    {
                        CoopRuntime.Notice("A teammate is crafting with that");
                        PlayerCamera.main?.PlayUISound(PlayerCamera.UISoundType.Deny);
                        return false;
                    }
                return true;
            }

            if (_pending != null) return false;  // already asked
            if (ids.Count > CraftClaim.Max) return true;
            _pending = r;
            _request++;
            _pendingUntil = Time.unscaledTime + Hold;
            var ask = new CraftClaim { Op = CraftOp.Ask, Request = _request };
            ask.Ids.AddRange(ids);
            WorldNet.Publish(ask);
            return false;
        }

        /// <summary>Self-test: ask for an item the host doesn't have; it must refuse.</summary>
        internal void DevAskFor(uint id)
        {
            if (!Coop.IsClient || _pending != null || Recipes.recipes == null || Recipes.recipes.Count == 0) return;
            _pending = Recipes.recipes[0];
            _request++;
            _pendingUntil = Time.unscaledTime + Hold;
            var ask = new CraftClaim { Op = CraftOp.Ask, Request = _request };
            ask.Ids.Add(id);
            WorldNet.Publish(ask);
        }

        private bool HeldByOther(uint id, byte me) =>
            _held.TryGetValue(id, out var h) && h.Key != me && Time.unscaledTime < h.Value;

        private void OnClaim(byte origin, CraftClaim m)
        {
            switch (m.Op)
            {
                case CraftOp.Ask:
                    if (!Coop.IsHost) return;
                    bool ok = true;
                    foreach (var id in m.Ids)
                        if (!EntityRegistry.TryGet(id, out var go) || go.transform.parent != null || HeldByOther(id, origin)) { ok = false; break; }
                    if (ok)
                        foreach (var id in m.Ids) _held[id] = new KeyValuePair<byte, float>(origin, Time.unscaledTime + Hold);
                    WorldNet.SendToWorldPeer(origin, new CraftClaim { Op = ok ? CraftOp.Granted : CraftOp.Denied, Request = m.Request });
                    if (ok) Granted++;
                    else Denied++;
                    break;

                case CraftOp.Granted:
                case CraftOp.Denied:
                    if (Coop.IsHost || m.Request != _request || _pending == null) return;
                    var r = _pending;
                    _pending = null;
                    if (m.Op == CraftOp.Denied)
                    {
                        Denied++;
                        CoopRuntime.Notice("A teammate got to it first");
                        PlayerCamera.main?.PlayUISound(PlayerCamera.UISoundType.Deny);
                        return;
                    }
                    Granted++;
                    Granting = true;
                    try { r.TryMake(); }
                    catch (Exception e) { Log.Warn("[craft] could not finish crafting: " + e.Message); }
                    finally { Granting = false; }
                    break;
            }
        }
    }

    [HarmonyPatch(typeof(Recipe), nameof(Recipe.TryMake))]
    internal static class CraftMakePatch
    {
        private static bool Prefix(Recipe __instance) => Coop.Get<CraftSync>()?.BeforeMake(__instance) ?? true;
    }

    /// <summary>A ground item worn down or drained by crafting: everyone gets its new state.</summary>
    [HarmonyPatch(typeof(RecipeItem), nameof(RecipeItem.UseItem))]
    internal static class CraftWearPatch
    {
        private static void Postfix(Item item)
        {
            if (!Coop.WorldLive || item == null || item.transform.parent != null) return;
            Coop.Get<ItemSync>()?.MarkChanged(item.gameObject);
        }
    }
}
