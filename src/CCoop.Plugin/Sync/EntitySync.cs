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
    /// World objects: damage, destruction and loot, and objects that appear at runtime.
    ///
    /// Damage travels as amounts, so two players chopping one crate add up. Destruction
    /// is the host's alone: a client whose copy dies holds it at death's door and asks;
    /// the host breaks its own copy, letting the game roll the loot once, and tells
    /// everyone what broke and what fell out. One authority means no race in which two
    /// players both claim a kill and both get the loot.
    /// </summary>
    internal sealed class EntitySync : CoopModule
    {
        private readonly Dictionary<uint, float> _damage = new Dictionary<uint, float>();
        private readonly HashSet<uint> _killRequested = new HashSet<uint>();
        private readonly List<GameObject> _spawnQueue = new List<GameObject>();
        internal static bool RemoteBreak;

        /// <summary>Client: items the game creates while this is raised are local duplicates of host loot.</summary>
        internal static int SuppressLoot;

        /// <summary>Health changes made while this is raised are not damage (a drone opening).</summary>
        internal static int SuppressDamage;

        private float _nextVanishScan;
        private readonly List<uint> _dead = new List<uint>();
        internal int Spawned, Destroyed, Vanished;

        internal override void Attach(NetSession s)
        {
            WorldNet.Register<EntityDamage>(s, OnDamage);
            WorldNet.Register<EntityKill>(s, OnKill, relay: false);
            WorldNet.Register<EntityDespawn>(s, OnDespawn);
            WorldNet.Register<EntitySpawn>(s, OnSpawn);
            WorldNet.Register<EntityAction>(s, OnAction, relay: false);
            WorldNet.Register<EntityGone>(s, OnGone, relay: false);
        }

        internal override void Detach() => Clear();
        internal override void OnWorldLost() => Clear();

        private void Clear()
        {
            _damage.Clear();
            _killRequested.Clear();
            _spawnQueue.Clear();
            Spawned = Destroyed = Vanished = 0;
            SuppressLoot = 0;
            SuppressDamage = 0;
        }

        // ------------------------------------------------------------------ damage

        internal void RecordDamage(BuildingEntity e, float amount)
        {
            if (!EntityRegistry.TryGetId(e.gameObject, out var id)) return;
            _damage.TryGetValue(id, out var pending);
            _damage[id] = pending + amount;
        }

        private static void OnDamage(byte origin, EntityDamage m)
        {
            foreach (var h in m.Hits)
            {
                if (!EntityRegistry.TryGet(h.Id, out var go)) continue;
                var b = go.GetComponent<BuildingEntity>();
                if (b == null || b.health < 0.5f) continue;
                b.health -= h.Amount;
                ShowHit(b, h.Amount);
            }
        }

        private static void ShowHit(BuildingEntity b, float amount)
        {
            try
            {
                var w = WorldGeneration.world;
                if (w == null || b == null) return;
                Vector2 at = b.transform.position;
                var sr = b.GetComponent<SpriteRenderer>();
                if (sr != null && sr.sprite != null) w.CreateHitFlash(sr.sprite, at, b.transform.rotation, Color.red, b.transform);
                WorldGeneration.CreateDamageNumber(at, Mathf.RoundToInt(amount));
                if (b.hitSound != null) Sound.Play(b.hitSound, at);
            }
            catch (Exception e) { Log.Debug("hit feedback: " + e.Message); }
        }

        // ------------------------------------------------------------------ destruction

        /// <summary>
        /// Called from CheckDamage when an object is about to break. Returns whether the
        /// game may go ahead.
        /// </summary>
        internal bool OnDying(BuildingEntity e)
        {
            if (RemoteBreak) return true;
            if (!Coop.WorldLive || !EntityRegistry.TryGetId(e.gameObject, out var id)) return true;
            if (Coop.IsHost) return true;  // the host breaks; HostBroke publishes the result

            if (_killRequested.Add(id)) WorldNet.Publish(new EntityKill { Id = id });
            return false;
        }

        /// <summary>Host: an object broke here; tell everyone, with the loot it dropped.</summary>
        internal void HostBroke(uint id, Vector2 at, List<GameObject> created)
        {
            EntityRegistry.Remove(id);
            WorldNet.Publish(new EntityDespawn { Id = id, Broken = true });
            Destroyed++;

            // The game floats fresh loot only near the *local* player; in co-op the
            // player who broke it may be someone else, so any nearby player counts.
            bool near = false;
            foreach (var rec in Coop.Players.Values)
                if (rec.Id != Coop.LocalId && rec.HasPosition && Vector2.Distance(rec.Position, at) < 8f) near = true;

            foreach (var go in created)
            {
                if (go == null || go.transform.parent != null) continue;
                if (go.GetComponent<Item>() == null && go.GetComponent<BuildingEntity>() == null) continue;
                if (near && go.GetComponent<Item>() != null && go.GetComponent<FreshItemDrop>() == null) go.AddComponent<FreshItemDrop>();
                PublishSpawn(go);
            }
        }

        private void OnKill(byte origin, EntityKill m)
        {
            if (!Coop.IsHost) return;
            if (!EntityRegistry.TryGet(m.Id, out var go))
            {
                // Already gone here: make sure the asker drops it too.
                WorldNet.SendToWorldPeer(origin, new EntityDespawn { Id = m.Id, Broken = true });
                return;
            }
            var b = go.GetComponent<BuildingEntity>();
            if (b == null) return;
            // Raised by the remote guard, but the host's break must still publish.
            int saved = Coop.ApplyingRemote;
            Coop.ApplyingRemote = 0;
            try { b.health = 0f; }
            finally { Coop.ApplyingRemote = saved; }
        }

        private void OnDespawn(byte origin, EntityDespawn m)
        {
            _killRequested.Remove(m.Id);
            if (!EntityRegistry.TryGet(m.Id, out var go)) return;
            EntityRegistry.Remove(m.Id);
            var b = go.GetComponent<BuildingEntity>();
            if (m.Broken && b != null)
            {
                Blank(b);
                RemoteBreak = true;
                bool wasClient = Coop.IsClient;
                if (wasClient) SuppressLoot++;
                try
                {
                    // Zero, not just below the threshold: guards and similar decide their
                    // own death on "health <= 0" in Update, and must reach it here too.
                    // Indestructible objects (guards) keep their corpse.
                    b.health = 0f;
                    if (!b.indestructible) b.CheckDamage();
                }
                finally
                {
                    RemoteBreak = false;
                    if (wasClient && SuppressLoot > 0) SuppressLoot--;
                }
            }
            else UnityEngine.Object.Destroy(go);
            Destroyed++;
        }

        // ------------------------------------------------------------------ vanished objects

        /// <summary>
        /// Networked objects destroyed here by the game itself (eaten, used up by crafting,
        /// exploded, decayed, burrowed through) are gone for everyone. Clients report them;
        /// the host removes its copy and tells the rest.
        /// </summary>
        private void ScanForVanished()
        {
            if (Time.unscaledTime < _nextVanishScan) return;
            _nextVanishScan = Time.unscaledTime + 1f;
            if (!WorldAccess.Settled || WorldAccess.Regenerating) return;
            _dead.Clear();
            foreach (var kv in EntityRegistry.All)
                if (kv.Value == null) _dead.Add(kv.Key);
            foreach (var id in _dead)
            {
                EntityRegistry.Remove(id);
                Vanished++;
                if (Coop.IsHost) WorldNet.Publish(new EntityDespawn { Id = id, Broken = false });
                else WorldNet.Publish(new EntityGone { Id = id });
            }
        }

        private static void OnGone(byte origin, EntityGone m)
        {
            if (!Coop.IsHost) return;
            if (EntityRegistry.TryGet(m.Id, out var go)) UnityEngine.Object.Destroy(go);
            EntityRegistry.Remove(m.Id);
            // The asker already lost it; everyone else hears about it here.
            foreach (var rec in Coop.Players.Values)
                if (rec.Id != origin && rec.Id != NetSession.HostId)
                    WorldNet.SendToWorldPeer(rec.Id, new EntityDespawn { Id = m.Id, Broken = false });
        }

        private static readonly AccessTools.FieldRef<FanTrap, SpriteRenderer> Fan1 = AccessTools.FieldRefAccess<FanTrap, SpriteRenderer>("fan1");
        private static readonly AccessTools.FieldRef<FanTrap, SpriteRenderer> Fan2 = AccessTools.FieldRefAccess<FanTrap, SpriteRenderer>("fan2");

        /// <summary>Host: a client asked for a one-off action whose loot only the host may make.</summary>
        private static void OnAction(byte origin, EntityAction m)
        {
            if (!Coop.IsHost || !EntityRegistry.TryGet(m.Id, out var go)) return;
            int saved = Coop.ApplyingRemote;
            Coop.ApplyingRemote = 0;  // the loot this makes must be published
            try
            {
                switch (m.Kind)
                {
                    case EntityActionKind.TraderDropInventory:
                        var trader = go.GetComponent<TraderScript>();
                        if (trader != null && trader.items != null && trader.items.Count > 0) trader.DropInventory();
                        break;
                    case EntityActionKind.FanBreak:
                        var fan = go.GetComponent<FanTrap>();
                        if (fan == null || (m.Arg != 0 && m.Arg != 1)) break;
                        var blade = m.Arg == 1 ? Fan2(fan) : Fan1(fan);
                        if (blade != null && blade.gameObject.activeSelf) fan.BreakFan(m.Arg);
                        break;
                }
            }
            catch (Exception e) { Log.Warn("[entity] action " + m.Kind + " failed: " + e.Message); }
            finally { Coop.ApplyingRemote = saved; }
        }

        private static readonly ItemDrop[] NoDrops = new ItemDrop[0];

        internal static void Blank(BuildingEntity b)
        {
            b.itemsDropOnDestroy = NoDrops;
            b.alwaysDrop = NoDrops;
            b.guaranteedDropAmount = 0;
        }

        // ------------------------------------------------------------------ spawns

        /// <summary>Something appeared through the game's item factory; decide at end of frame.</summary>
        internal void QueueSpawn(GameObject go)
        {
            if (go != null && _spawnQueue.Count < 4096) _spawnQueue.Add(go);
        }

        internal override void LateTick()
        {
            if (!Coop.WorldLive)
            {
                _spawnQueue.Clear();
                _damage.Clear();
                return;
            }

            ScanForVanished();

            if (_damage.Count > 0)
            {
                var batch = new EntityDamage();
                foreach (var kv in _damage)
                {
                    batch.Hits.Add(new DamageEntry { Id = kv.Key, Amount = kv.Value });
                    if (batch.Hits.Count == EntityDamage.Max) { WorldNet.Publish(batch); batch = new EntityDamage(); }
                }
                if (batch.Hits.Count > 0) WorldNet.Publish(batch);
                _damage.Clear();
            }

            if (_spawnQueue.Count == 0) return;
            foreach (var go in _spawnQueue)
            {
                if (go == null || go.transform.parent != null) continue;  // picked up, or in a container
                if (EntityRegistry.TryGetId(go, out _)) continue;
                bool item = go.GetComponent<Item>() != null;
                bool building = go.GetComponent<BuildingEntity>() != null;
                if (!item && !building) continue;
                // Creatures and props appearing on their own come from spawners that
                // run on every peer; only the host's count.
                if (building && !item && !Coop.IsHost) continue;
                PublishSpawn(go);
            }
            _spawnQueue.Clear();
        }

        internal uint PublishSpawn(GameObject go)
        {
            uint id = EntityRegistry.AllocateRuntime(Coop.LocalId);
            string key = WorldAccess.KeyFor(go);
            EntityRegistry.Register(id, go, key);
            var item = go.GetComponent<Item>();
            var rb = go.GetComponent<Rigidbody2D>();
            var t = go.transform;
            WorldNet.Publish(new EntitySpawn
            {
                Id = id,
                Key = key,
                IsItem = item != null,
                X = t.position.x,
                Y = t.position.y,
                Rotation = t.eulerAngles.z,
                VX = rb != null ? rb.velocity.x : 0f,
                VY = rb != null ? rb.velocity.y : 0f,
                Spin = rb != null ? rb.angularVelocity : 0f,
                Fresh = go.GetComponent<FreshItemDrop>() != null,
                State = item != null ? ItemCodec.Encode(item) : new byte[0],
            });
            Spawned++;
            return id;
        }

        private void OnSpawn(byte origin, EntitySpawn m)
        {
            if (EntityRegistry.TryGet(m.Id, out var existing))
            {
                // A known object re-announced: its state changed (a container's contents).
                var it = existing.GetComponent<Item>();
                if (it != null && m.State.Length > 0) ItemCodec.Apply(it, ItemCodec.Decode(m.State));
                return;
            }
            GameObject go = null;
            var pos = new Vector2(m.X, m.Y);
            if (m.IsItem) go = Utils.Create(m.Key, pos, m.Rotation);
            else
            {
                var prefab = WorldAccess.ResolvePrefab(m.Key);
                if (prefab != null) go = UnityEngine.Object.Instantiate(prefab, pos, Quaternion.Euler(0f, 0f, m.Rotation));
            }
            if (go == null)
            {
                Log.Warn("[entity] could not create '" + m.Key + "' from a peer");
                return;
            }
            var rb = go.GetComponent<Rigidbody2D>();
            if (rb != null)
            {
                rb.velocity = new Vector2(m.VX, m.VY);
                rb.angularVelocity = m.Spin;
            }
            var item = go.GetComponent<Item>();
            if (item != null && m.State.Length > 0) ItemCodec.Apply(item, ItemCodec.Decode(m.State));
            if (m.Fresh && go.GetComponent<FreshItemDrop>() == null) go.AddComponent<FreshItemDrop>();
            EntityRegistry.Register(m.Id, go, m.Key);
            Spawned++;
        }
    }

    // ====================================================================== patches

    /// <summary>Damage dealt here, from any of the dozen places that write health.</summary>
    [HarmonyPatch(typeof(BuildingEntity), "set_health")]
    internal static class HealthSetterPatch
    {
        private static readonly AccessTools.FieldRef<BuildingEntity, float> Raw = AccessTools.FieldRefAccess<BuildingEntity, float>("_health");

        private static void Prefix(BuildingEntity __instance, out float __state)
        {
            __state = Raw(__instance);
        }

        private static void Postfix(BuildingEntity __instance, float __state)
        {
            if (!Coop.WorldLive || Coop.Applying || EntitySync.SuppressDamage > 0) return;
            float now = Raw(__instance);
            float dealt = __state - now;
            if (dealt <= 0.001f || now < 0.5f || __state < 0.5f) return;
            Coop.Get<EntitySync>()?.RecordDamage(__instance, dealt);
        }
    }

    /// <summary>The moment an object breaks. Host: capture the loot. Client: ask the host.</summary>
    [HarmonyPatch(typeof(BuildingEntity), nameof(BuildingEntity.CheckDamage))]
    internal static class CheckDamagePatch
    {
        private static readonly AccessTools.FieldRef<BuildingEntity, float> Raw = AccessTools.FieldRefAccess<BuildingEntity, float>("_health");
        private static readonly AccessTools.FieldRef<BuildingEntity, bool> Done = AccessTools.FieldRefAccess<BuildingEntity, bool>("didDestroyRoutine");

        private sealed class State
        {
            public uint Id;
            public HashSet<int> Before;
        }

        private static bool Prefix(BuildingEntity __instance, out State __state)
        {
            __state = null;
            if (!(Raw(__instance) < 0.5f) || Done(__instance)) return true;
            var sync = Coop.Get<EntitySync>();
            if (sync == null || !Coop.WorldLive) return true;
            if (!sync.OnDying(__instance)) return false;
            if (Coop.IsHost && !EntitySync.RemoteBreak && EntityRegistry.TryGetId(__instance.gameObject, out var id))
            {
                var before = new HashSet<int>();
                foreach (var go in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
                    before.Add(go.GetInstanceID());
                __state = new State { Id = id, Before = before };
            }
            return true;
        }

        private static void Postfix(BuildingEntity __instance, State __state)
        {
            if (__state == null) return;
            try
            {
                var created = new List<GameObject>();
                foreach (var go in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
                    if (!__state.Before.Contains(go.GetInstanceID())) created.Add(go);
                Vector2 at = __instance != null ? (Vector2)__instance.transform.position : Vector2.zero;
                Coop.Get<EntitySync>()?.HostBroke(__state.Id, at, created);
            }
            catch (Exception e) { Log.Warn("[entity] break capture: " + e.Message); }
        }
    }

    /// <summary>Items made by the game at runtime (block loot, crafting leftovers, drops).</summary>
    [HarmonyPatch(typeof(Utils), nameof(Utils.Create), new[] { typeof(string), typeof(Vector2), typeof(float) })]
    internal static class UtilsCreatePatch
    {
        private static void Postfix(GameObject __result)
        {
            if (__result == null || !Coop.WorldLive) return;
            if (EntitySync.SuppressLoot > 0 && Coop.IsClient)
            {
                // A local duplicate of loot the host is making. Destroyed at end of frame,
                // so the caller can still finish configuring it.
                UnityEngine.Object.Destroy(__result);
                return;
            }
            if (Coop.Applying) return;
            Coop.Get<EntitySync>()?.QueueSpawn(__result);
        }
    }
}
