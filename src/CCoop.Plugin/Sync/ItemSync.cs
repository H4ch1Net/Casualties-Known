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
    /// Picking up, dropping and throwing, plus loose items' physics.
    ///
    /// Pickup is arbitrated by the host so two players grabbing the same item cannot
    /// both get it: a client asks and picks up when granted (one round trip). Drops need
    /// no arbitration — the dropper creates the object, so the dropper names it, from
    /// its own id range — and the dropper then owns the item's physics until it rests.
    /// </summary>
    internal sealed class ItemSync : CoopModule
    {
        private const float MotionInterval = 1f / 12f;
        private const float Snap = 0.08f;

        internal static bool Granting;
        private readonly Dictionary<uint, byte> _pendingTake = new Dictionary<uint, byte>();
        private readonly Dictionary<uint, bool> _awake = new Dictionary<uint, bool>();
        private readonly HashSet<uint> _dirtyContainers = new HashSet<uint>();
        private float _nextMotion;

        internal override void Attach(NetSession s)
        {
            WorldNet.Register<ItemTake>(s, OnTake, relay: false);
            WorldNet.Register<ItemTaken>(s, OnTaken);
            WorldNet.Register<EntityMotion>(s, (o, m) => ApplyMotion(m.Bodies, false), queueWhilePending: false);
            WorldNet.Register<EntityRest>(s, (o, m) => ApplyMotion(m.Bodies, true));
            // A client's denial is addressed only to it and bypasses the world relay.
            s.On<ItemTaken>((o, m) => { if (!m.Granted && !Coop.IsHost) _pendingTake.Remove(m.Id); });
        }

        internal override void Detach() => Clear();
        internal override void OnWorldLost() => Clear();

        private void Clear()
        {
            _pendingTake.Clear();
            _awake.Clear();
            _dirtyContainers.Clear();
        }

        // ------------------------------------------------------------------ pickup

        /// <summary>Returns whether the local pickup may go ahead now.</summary>
        internal bool OnLocalPickup(Item item, int slot)
        {
            if (!EntityRegistry.TryGetId(item.gameObject, out var id)) return true;
            if (Coop.IsHost) return true;  // the postfix announces it
            if (_pendingTake.ContainsKey(id)) return false;
            _pendingTake[id] = (byte)Mathf.Clamp(slot, 0, 255);
            WorldNet.Publish(new ItemTake { Id = id, Slot = (byte)Mathf.Clamp(slot, 0, 255) });
            return false;
        }

        internal void HostPickedUp(uint id, int slot)
        {
            EntityRegistry.Remove(id);
            _awake.Remove(id);
            WorldNet.Publish(new ItemTaken { Id = id, Taker = NetSession.HostId, Slot = (byte)Mathf.Clamp(slot, 0, 255), Granted = true });
        }

        private void OnTake(byte origin, ItemTake m)
        {
            if (!Coop.IsHost) return;
            if (!EntityRegistry.TryGet(m.Id, out var go) || go.transform.parent != null)
            {
                Session.SendTo(origin, new ItemTaken { Id = m.Id, Taker = origin, Slot = m.Slot, Granted = false });
                return;
            }
            EntityRegistry.Remove(m.Id);
            _awake.Remove(m.Id);
            UnityEngine.Object.Destroy(go);
            var granted = new ItemTaken { Id = m.Id, Taker = origin, Slot = m.Slot, Granted = true };
            WorldNet.SendToWorldPeer(origin, granted);
            // Everyone else learns it is gone through the normal world relay, which skips
            // the origin; WorldNet.Register already relays this message to them.
            foreach (var rec in Coop.Players.Values)
                if (rec.Id != NetSession.HostId && rec.Id != origin) WorldNet.SendToWorldPeer(rec.Id, granted);
        }

        private void OnTaken(byte origin, ItemTaken m)
        {
            if (Coop.IsHost || !m.Granted) return;
            _pendingTake.Remove(m.Id);
            if (!EntityRegistry.TryGet(m.Id, out var go))
            {
                EntityRegistry.Remove(m.Id);
                return;
            }
            EntityRegistry.Remove(m.Id);
            _awake.Remove(m.Id);
            if (m.Taker != Coop.LocalId)
            {
                UnityEngine.Object.Destroy(go);
                return;
            }
            var body = GameInfo.LocalBody;
            var item = go.GetComponent<Item>();
            if (body == null || item == null) { UnityEngine.Object.Destroy(go); return; }
            Granting = true;
            try { body.PickUpItem(item, m.Slot, force: true); }
            catch (Exception e) { Log.Warn("[item] could not complete a pickup: " + e.Message); }
            finally { Granting = false; }
        }

        // ------------------------------------------------------------------ drop / containers

        internal void OnLocalDrop(Item item)
        {
            // Velocity is assigned by ThrowItem after DropItem returns, so the spawn is
            // published at end of frame when the launch is known.
            if (item != null) Coop.Get<EntitySync>()?.QueueSpawn(item.gameObject);
        }

        internal void MarkContainer(Container c)
        {
            if (c == null) return;
            var root = c.transform;
            while (root.parent != null && root.parent.GetComponent<Container>() != null) root = root.parent;
            if (root.parent == null && EntityRegistry.TryGetId(root.gameObject, out var id)) _dirtyContainers.Add(id);
        }

        /// <summary>A loose networked item's state changed here (worn, drained): send it again.</summary>
        internal void MarkChanged(GameObject go)
        {
            if (go != null && go.transform.parent == null && EntityRegistry.TryGetId(go, out var id)) _dirtyContainers.Add(id);
        }

        // ------------------------------------------------------------------ motion

        internal override void LateTick()
        {
            if (!Coop.WorldLive) return;

            if (_dirtyContainers.Count > 0)
            {
                foreach (var id in _dirtyContainers)
                {
                    if (!EntityRegistry.TryGet(id, out var go)) continue;
                    var item = go.GetComponent<Item>();
                    if (item == null) continue;
                    WorldNet.Publish(new EntitySpawn { Id = id, Key = EntityRegistry.KeyOf(id) ?? item.id, IsItem = true, X = go.transform.position.x, Y = go.transform.position.y, State = ItemCodec.Encode(item) });
                }
                _dirtyContainers.Clear();
            }

            if (Time.unscaledTime < _nextMotion) return;
            _nextMotion = Time.unscaledTime + MotionInterval;

            var moving = new EntityMotion();
            var rested = new EntityRest();
            foreach (var kv in EntityRegistry.All)
            {
                uint id = kv.Key;
                if (!Owns(id)) continue;
                var go = kv.Value;
                if (go == null || go.transform.parent != null) continue;
                var rb = go.GetComponent<Rigidbody2D>();
                if (rb == null || !rb.simulated || go.GetComponent<Item>() == null) continue;
                bool awake = rb.IsAwake() && rb.bodyType == RigidbodyType2D.Dynamic && rb.velocity.sqrMagnitude > 0.0004f;
                _awake.TryGetValue(id, out var was);
                if (awake)
                {
                    _awake[id] = true;
                    if (moving.Bodies.Count < EntityMotion.Max) moving.Bodies.Add(Describe(id, go, rb));
                }
                else if (was)
                {
                    _awake[id] = false;
                    if (rested.Bodies.Count < EntityRest.Max) rested.Bodies.Add(Describe(id, go, rb));
                }
            }
            if (moving.Bodies.Count > 0) WorldNet.Publish(moving);
            if (rested.Bodies.Count > 0) WorldNet.Publish(rested);
        }

        /// <summary>Runtime items belong to their creator; generated ones to the host.</summary>
        internal static bool Owns(uint id)
        {
            if (id >= EntityRegistry.StructureBase && id < EntityRegistry.RuntimeBase) return false;
            return EntityRegistry.CreatorOf(id) == Coop.LocalId;
        }

        private static BodyMotion Describe(uint id, GameObject go, Rigidbody2D rb) => new BodyMotion
        {
            Id = id, X = go.transform.position.x, Y = go.transform.position.y, Rotation = go.transform.eulerAngles.z,
            VX = rb.velocity.x, VY = rb.velocity.y, Spin = rb.angularVelocity,
        };

        private static void ApplyMotion(List<BodyMotion> bodies, bool atRest)
        {
            foreach (var m in bodies)
            {
                if (!EntityRegistry.TryGet(m.Id, out var go) || go.transform.parent != null) continue;
                var rb = go.GetComponent<Rigidbody2D>();
                var target = new Vector3(m.X, m.Y, go.transform.position.z);
                if (atRest)
                {
                    go.transform.position = target;
                    go.transform.rotation = Quaternion.Euler(0f, 0f, m.Rotation);
                    if (rb != null)
                    {
                        rb.velocity = Vector2.zero;
                        rb.angularVelocity = 0f;
                        rb.Sleep();
                    }
                    continue;
                }
                if (rb != null)
                {
                    rb.velocity = new Vector2(m.VX, m.VY);
                    rb.angularVelocity = m.Spin;
                }
                if ((go.transform.position - target).sqrMagnitude > Snap * Snap)
                {
                    go.transform.position = target;
                    go.transform.rotation = Quaternion.Euler(0f, 0f, m.Rotation);
                }
            }
        }
    }

    // ====================================================================== patches

    [HarmonyPatch(typeof(Body), nameof(Body.PickUpItem))]
    internal static class PickUpItemPatch
    {
        private static bool Prefix(Body __instance, Item item, int slot, out uint __state)
        {
            __state = 0;
            if (ItemSync.Granting || !Coop.WorldLive || Coop.Applying || item == null) return true;
            if (!Coop.IsLocalBody(__instance)) return true;
            if (!EntityRegistry.TryGetId(item.gameObject, out var id)) return true;
            var sync = Coop.Get<ItemSync>();
            if (sync == null) return true;
            if (!sync.OnLocalPickup(item, slot)) return false;
            __state = id;
            return true;
        }

        private static void Postfix(Item item, int slot, uint __state)
        {
            if (__state == 0 || item == null) return;
            if (item.transform.parent == null) return;  // the pickup did not happen
            Coop.Get<ItemSync>()?.HostPickedUp(__state, slot);
        }
    }

    [HarmonyPatch(typeof(Body), nameof(Body.DropItem), new[] { typeof(Item) })]
    internal static class DropItemPatch
    {
        private static void Postfix(Body __instance, Item item)
        {
            if (!Coop.WorldLive || Coop.Applying || item == null || !Coop.IsLocalBody(__instance)) return;
            if (item.transform.parent != null) return;
            Coop.Get<ItemSync>()?.OnLocalDrop(item);
        }
    }

    [HarmonyPatch(typeof(Container), nameof(Container.LoadItem))]
    internal static class ContainerLoadPatch
    {
        private static void Postfix(Container __instance)
        {
            if (!Coop.WorldLive || Coop.Applying) return;
            Coop.Get<ItemSync>()?.MarkContainer(__instance);
        }
    }

    [HarmonyPatch(typeof(Container), nameof(Container.UnloadItem))]
    internal static class ContainerUnloadPatch
    {
        private static void Postfix(Container __instance)
        {
            if (!Coop.WorldLive || Coop.Applying) return;
            Coop.Get<ItemSync>()?.MarkContainer(__instance);
        }
    }
}
