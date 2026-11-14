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
    /// Traders. What a trader has for sale is one shelf for the whole team: the host's
    /// stock goes out with the layer, and every purchase is cleared with the host first,
    /// so two players can't buy the same last bandage. How a trader feels about each
    /// player (reputation, haggling, the credit from items they handed over) stays with
    /// that player, the way the game measures it from their own body. Anger is shared:
    /// a trader that turns hostile on one player turns on the group.
    /// </summary>
    internal sealed class TraderSync : CoopModule
    {
        /// <summary>Raised while running a purchase the host has granted.</summary>
        internal static bool Granting;

        private readonly HashSet<string> _pending = new HashSet<string>();

        internal override void Attach(NetSession s) => WorldNet.Register<TraderTrade>(s, OnTrade, relay: false);
        internal override void Detach() => _pending.Clear();
        internal override void OnWorldLost() => _pending.Clear();

        private static TraderItem Find(TraderScript t, string id, byte pref, int value)
        {
            if (t == null || t.items == null) return null;
            TraderItem loose = null;
            foreach (var i in t.items)
            {
                if (i == null || i.id != id) continue;
                if ((byte)i.preference == pref && i.value == value) return i;
                if (loose == null) loose = i;
            }
            return loose;
        }

        private static TraderTrade Describe(uint trader, TraderOp op, TraderItem item) => new TraderTrade
        {
            Trader = trader, Op = op, Item = item?.id ?? "", Pref = item != null ? (byte)item.preference : (byte)0, Value = item?.value ?? 0,
        };

        private static void Refresh(TraderScript t)
        {
            var cam = PlayerCamera.main;
            if (cam == null || cam.currentTrader != t) return;
            try
            {
                cam.RefreshTraderInventories();
                cam.UpdateTradeTexts();
            }
            catch (Exception e) { Log.Debug("trader refresh: " + e.Message); }
        }

        // ------------------------------------------------------------------ purchases

        /// <summary>Client, before a purchase: ask the host. Returns whether the game may go ahead now.</summary>
        internal bool BeforePurchase(TraderScript t, TraderItem item)
        {
            if (!Coop.IsClient || !Coop.WorldLive || Granting || t == null || item == null) return true;
            var b = t.GetComponent<BuildingEntity>();
            if (b != null && b.health < 200f) return true;            // the game refuses on its own
            if (t.valueGiven < t.ItemPrice(item)) return true;        // can't afford it: the game says so
            if (!EntityRegistry.TryGetId(t.gameObject, out var id)) return true;
            string key = id + ":" + item.id;
            if (!_pending.Add(key)) return false;                      // already asked
            WorldNet.Publish(Describe(id, TraderOp.BuyRequest, item));
            return false;
        }

        /// <summary>Host, after its own purchase: the item is off everyone's shelf.</summary>
        internal void AfterHostPurchase(TraderScript t, TraderItem item, bool removed)
        {
            if (!Coop.IsHost || !Coop.WorldLive || !removed || Coop.Applying) return;
            if (!EntityRegistry.TryGetId(t.gameObject, out var id)) return;
            WorldNet.Publish(Describe(id, TraderOp.Removed, item));
            Log.Info("[trade] bought " + item.id + " from " + t.name);
        }

        private void OnTrade(byte origin, TraderTrade m)
        {
            if (!EntityRegistry.TryGet(m.Trader, out var go)) return;
            var t = go.GetComponent<TraderScript>();
            if (t == null) return;
            var item = Find(t, m.Item, m.Pref, m.Value);
            switch (m.Op)
            {
                case TraderOp.BuyRequest:
                    if (!Coop.IsHost) return;
                    if (item == null)
                    {
                        WorldNet.SendToWorldPeer(origin, new TraderTrade { Trader = m.Trader, Op = TraderOp.BuyDenied, Item = m.Item, Pref = m.Pref, Value = m.Value });
                        return;
                    }
                    t.items.Remove(item);
                    Refresh(t);
                    WorldNet.SendToWorldPeer(origin, new TraderTrade { Trader = m.Trader, Op = TraderOp.BuyGranted, Item = m.Item, Pref = m.Pref, Value = m.Value });
                    ForwardExcept(origin, new TraderTrade { Trader = m.Trader, Op = TraderOp.Removed, Item = m.Item, Pref = m.Pref, Value = m.Value });
                    Log.Info("[trade] " + Coop.Player(origin).Name + " bought " + m.Item);
                    break;

                case TraderOp.BuyGranted:
                    _pending.Remove(m.Trader + ":" + m.Item);
                    if (item == null)
                    {
                        // Our shelf had lost track of it; the host's word stands.
                        item = new TraderItem { id = m.Item, preference = (TraderScript.TraderItemPreference)m.Pref, value = m.Value };
                        t.items.Add(item);
                    }
                    Granting = true;
                    int saved = Coop.ApplyingRemote;
                    Coop.ApplyingRemote = 0;  // the bought item must be published like any new item
                    try { t.TryPurchase(item); }
                    catch (Exception e) { Log.Warn("[trade] purchase failed: " + e.Message); }
                    finally { Granting = false; Coop.ApplyingRemote = saved; }
                    break;

                case TraderOp.BuyDenied:
                    _pending.Remove(m.Trader + ":" + m.Item);
                    if (item != null) t.items.Remove(item);
                    Refresh(t);
                    CoopRuntime.Notice("A teammate just bought that");
                    break;

                case TraderOp.Removed:
                    if (item != null) t.items.Remove(item);
                    Refresh(t);
                    break;

                case TraderOp.Added:
                    if (item == null)
                    {
                        t.items.Add(new TraderItem { id = m.Item, preference = (TraderScript.TraderItemPreference)m.Pref, value = m.Value });
                        Refresh(t);
                    }
                    if (Coop.IsHost) ForwardExcept(origin, m);
                    break;

                case TraderOp.Hostile:
                    if (t.hostility < 100f) t.hostility = 100f;
                    if (Coop.IsHost) ForwardExcept(origin, m);
                    break;
            }
        }

        private static void ForwardExcept(byte origin, TraderTrade m)
        {
            foreach (var rec in Coop.Players.Values)
                if (rec.Id != origin && rec.Id != NetSession.HostId) WorldNet.SendToWorldPeer(rec.Id, m);
        }

        /// <summary>Something about a trader changed here that everyone should see.</summary>
        internal static void Announce(TraderScript t, TraderOp op, TraderItem item = null)
        {
            if (!Coop.WorldLive || Coop.Applying || t == null) return;
            if (!EntityRegistry.TryGetId(t.gameObject, out var id)) return;
            WorldNet.Publish(Describe(id, op, item));
            if (op == TraderOp.Hostile) Log.Info("[trade] " + t.name + " turned hostile");
        }

        // ------------------------------------------------------------------ the layer snapshot

        /// <summary>A copy made from the snapshot: the host's stock replaces the one Start rolls.</summary>
        private static readonly Dictionary<int, byte[]> Stashed = new Dictionary<int, byte[]>();

        internal static void Write(CCoop.Net.Wire.PacketWriter w, TraderScript t)
        {
            w.WriteSingle(t.MoveRange.min);
            w.WriteSingle(t.MoveRange.max);
            w.WriteBool(t.farEnoughToMove);
            w.WriteSingle(t.hostility);
            var items = t.items ?? new List<TraderItem>();
            w.WriteUInt16((ushort)Mathf.Min(items.Count, 200));
            for (int i = 0; i < items.Count && i < 200; i++)
            {
                w.WriteString(items[i].id ?? "");
                w.WriteByte((byte)items[i].preference);
                w.WriteInt32(items[i].value);
                w.WriteBool(items[i].bought);
            }
        }

        internal static void Stash(GameObject go, byte[] section) => Stashed[go.GetInstanceID()] = section;

        /// <summary>After the trader's own Start: put the host's stock and range back.</summary>
        internal static void AfterStart(TraderScript t)
        {
            if (!Stashed.TryGetValue(t.gameObject.GetInstanceID(), out var data)) return;
            Stashed.Remove(t.gameObject.GetInstanceID());
            var r = new CCoop.Net.Wire.PacketReader(data, 0, data.Length);
            float min = r.ReadSingle(), max = r.ReadSingle();
            bool far = r.ReadBool();
            float hostility = r.ReadSingle();
            int n = r.ReadCount(200);
            var items = new List<TraderItem>();
            for (int i = 0; i < n && !r.Failed; i++)
                items.Add(new TraderItem
                {
                    id = r.ReadString(),
                    preference = (TraderScript.TraderItemPreference)r.ReadByte(),
                    value = r.ReadInt32(),
                    bought = r.ReadBool(),
                });
            if (r.Failed) { Log.Warn("[trade] bad trader state in the snapshot"); return; }
            t.MoveRange = new RangeF(min, max);
            t.farEnoughToMove = far;
            t.hostility = hostility;
            t.items = items;
        }
    }

    // ====================================================================== hooks

    [HarmonyPatch(typeof(TraderScript), nameof(TraderScript.TryPurchase))]
    internal static class TraderPurchasePatch
    {
        private static bool Prefix(TraderScript __instance, TraderItem item, out bool __state)
        {
            __state = __instance != null && __instance.items != null && __instance.items.Contains(item);
            var sync = Coop.Get<TraderSync>();
            return sync == null || sync.BeforePurchase(__instance, item);
        }

        private static void Postfix(TraderScript __instance, TraderItem item, bool __state)
        {
            bool removed = __state && __instance.items != null && !__instance.items.Contains(item);
            Coop.Get<TraderSync>()?.AfterHostPurchase(__instance, item, removed);
        }
    }

    [HarmonyPatch(typeof(TraderScript), nameof(TraderScript.MeetPlayer))]
    internal static class TraderMeetPatch
    {
        private static void Prefix(TraderScript __instance, out int __state) => __state = __instance.items?.Count ?? 0;

        private static void Postfix(TraderScript __instance, int __state)
        {
            // A bleeding player is offered a free bandage; it goes on the shared shelf.
            if (__instance.items == null || __instance.items.Count <= __state) return;
            foreach (var i in __instance.items)
                if (i.id == "bandage") { TraderSync.Announce(__instance, TraderOp.Added, i); break; }
        }
    }

    [HarmonyPatch(typeof(TraderScript), "OnWillRenderObject")]
    internal static class TraderHostilityPatch
    {
        private static void Prefix(TraderScript __instance, out bool __state) => __state = __instance.hostility >= 100f;

        private static void Postfix(TraderScript __instance, bool __state)
        {
            if (!__state && __instance.hostility >= 100f) TraderSync.Announce(__instance, TraderOp.Hostile);
        }
    }

    [HarmonyPatch(typeof(TraderScript), "Start")]
    internal static class TraderStartPatch
    {
        private static void Postfix(TraderScript __instance)
        {
            try { TraderSync.AfterStart(__instance); }
            catch (Exception e) { Log.Warn("[trade] could not apply the host's stock: " + e.Message); }
        }
    }
}
