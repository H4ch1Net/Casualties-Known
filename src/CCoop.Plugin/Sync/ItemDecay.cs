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
    /// Loose items rot, rust and run down only where a player is: the game decays an item
    /// only while its chunk is drawn. Left to each machine, food on the floor would be
    /// fresh on one screen and gone on another. In co-op the host decides for every loose
    /// networked item: it decays the ones near it (the game does that) and the ones near
    /// a teammate (here), crumbles them at zero for everyone, and sends the conditions
    /// that changed. Clients don't decay loose networked items themselves. Items in
    /// hands, bags and containers still decay on their holder's machine, as before.
    /// </summary>
    internal sealed class ItemDecay : CoopModule
    {
        private const float PassEvery = 1f;
        private const float SendEvery = 3f;
        private const float Step = 0.01f;     // condition change worth sending
        private const float NearPlayer = 40f;  // about what a player's screen keeps drawn

        private readonly Dictionary<uint, float> _sent = new Dictionary<uint, float>();
        private readonly List<KeyValuePair<uint, Item>> _loose = new List<KeyValuePair<uint, Item>>();
        private float _gameClock, _passClock, _nextPass, _nextSend;

        internal static int Crumbled, DecayedForOthers, ConditionsApplied, CrumblesSeen;

        internal override void Attach(NetSession s) => WorldNet.Register<ItemConditions>(s, OnConditions);

        internal override void Detach() => Clear();
        internal override void OnWorldLost() => Clear();

        private void Clear()
        {
            _sent.Clear();
            _loose.Clear();
            _gameClock = _passClock = 0f;
        }

        // ------------------------------------------------------------------ host

        internal override void Tick()
        {
            if (!Coop.WorldLive || !Coop.IsHost || !WorldAccess.Settled) return;
            _gameClock += Time.deltaTime;  // game time: sleeping speeds rot up, as in single player
            float now = Time.unscaledTime;
            if (now < _nextPass) return;
            _nextPass = now + PassEvery;
            float elapsed = _gameClock - _passClock;
            _passClock = _gameClock;
            bool send = now >= _nextSend;
            if (send) _nextSend = now + SendEvery;

            _loose.Clear();
            foreach (var kv in EntityRegistry.All)
            {
                var go = kv.Value;
                if (go == null || go.transform.parent != null) continue;
                if (go.TryGetComponent<Item>(out var it)) _loose.Add(new KeyValuePair<uint, Item>(kv.Key, it));
            }

            ItemConditions batch = null;
            foreach (var kv in _loose)
            {
                var it = kv.Value;
                if (it == null) continue;
                Vector2 at = it.transform.position;
                if (!DrawnHere(at) && !Drains(it))
                {
                    // Nobody here sees it, so the game won't touch it; a teammate might.
                    if (elapsed > 0f && NearTeammate(at))
                    {
                        DecayBy(it, elapsed);
                        DecayedForOthers++;
                    }
                    if (it.condition <= 0f && it.Stats != null && it.Stats.destroyAtZeroCondition)
                    {
                        Crumble(kv.Key, it);
                        continue;
                    }
                }
                if (!send) continue;
                float c = it.condition;
                bool known = _sent.TryGetValue(kv.Key, out var last);
                if (known ? Mathf.Abs(c - last) < Step : c >= 0.999f)
                {
                    if (!known) _sent[kv.Key] = c;
                    continue;
                }
                _sent[kv.Key] = c;
                if (batch == null) batch = new ItemConditions();
                batch.Items.Add(new ConditionEntry { Id = kv.Key, Condition = c });
                if (batch.Items.Count == ItemConditions.Max)
                {
                    WorldNet.Publish(batch);
                    batch = null;
                }
            }
            if (batch != null) WorldNet.Publish(batch);
            _loose.Clear();
            if (send && _sent.Count > 4096) _sent.Clear();
        }

        /// <summary>Whether this machine draws the chunk at <paramref name="at"/> (the game's own decay test).</summary>
        internal static bool DrawnHere(Vector2 at)
        {
            var w = WorldGeneration.world;
            if (w == null || !w.worldExists) return false;
            try
            {
                var r = w.GetClosestChunkRenderer(w.WorldToBlockPos(at));
                return r != null && r.enabled;
            }
            catch { return false; }
        }

        private static bool NearTeammate(Vector2 at)
        {
            foreach (var rec in Coop.Players.Values)
                if (rec.Id != Coop.LocalId && Coop.InLayer(rec) && rec.Alive && Vector2.Distance(at, rec.Position) < NearPlayer)
                    return true;
            return false;
        }

        /// <summary>
        /// The game's own decay step over <paramref name="seconds"/>: it uses this frame's
        /// time, and its rate scales with the item's decay multiplier, so the multiplier
        /// is stretched for the one call.
        /// </summary>
        private static void DecayBy(Item it, float seconds)
        {
            float dt = Time.deltaTime;
            if (dt <= 0f || seconds <= 0f) return;
            float saved = it.decayMultiplier;
            it.decayMultiplier = saved * (seconds / dt);
            try { it.HandleDecay(); }
            catch (Exception e) { Log.Debug("[decay] " + it.id + ": " + e.Message); }
            finally { it.decayMultiplier = saved; }
        }

        /// <summary>Host: an item the game isn't drawing here reached zero; it crumbles for everyone.</summary>
        private static void Crumble(uint id, Item it)
        {
            EntityRegistry.Remove(id);
            WorldNet.Publish(new EntityDespawn { Id = id, Broken = true });
            Crumbled++;
            if (it.TryGetComponent<Container>(out var c))
            {
                c.ContainerBroke();  // its contents spill, and the host publishes them
                UnityEngine.Object.Destroy(it.gameObject, 0.05f);
            }
            else UnityEngine.Object.Destroy(it.gameObject);
        }

        /// <summary>Host, from the game's own Update: an item it draws is about to crumble; tell everyone.</summary>
        internal static void BeforeGameCrumble(Item it)
        {
            if (!Coop.WorldLive || !Coop.IsHost || it.transform.parent != null) return;
            if (!EntityRegistry.TryGetId(it.gameObject, out var id) || !DrawnHere(it.transform.position)) return;
            EntityRegistry.Remove(id);
            WorldNet.Publish(new EntityDespawn { Id = id, Broken = true });
            Crumbled++;
        }

        // ------------------------------------------------------------------ clients

        /// <summary>Clients leave loose networked items' decay to the host.</summary>
        internal static bool HostDecides(Item it) =>
            Coop.IsClient && Coop.WorldLive && it.transform.parent == null && !Drains(it) && EntityRegistry.TryGetId(it.gameObject, out _);

        /// <summary>Battery items run down instead of rotting; their charge isn't sent, so each machine keeps running theirs.</summary>
        private static bool Drains(Item it) => it.Stats != null && (it.Stats.decayInfo & 0x10) != 0;

        private static void OnConditions(byte origin, ItemConditions m)
        {
            if (Coop.IsHost) return;
            foreach (var e in m.Items)
            {
                if (!EntityRegistry.TryGet(e.Id, out var go) || go.transform.parent != null) continue;
                var it = go.GetComponent<Item>();
                if (it == null) continue;
                // Zero is the host's to act on: it sends the crumble.
                it.condition = it.Stats != null && it.Stats.destroyAtZeroCondition ? Mathf.Max(e.Condition, 0.001f) : e.Condition;
                ConditionsApplied++;
            }
        }

        /// <summary>The game's crumble effect, for an item that crumbled on the host.</summary>
        internal static void CrumbleEffect(GameObject go)
        {
            if (go == null) return;
            CrumblesSeen++;
            if (!DrawnHere(go.transform.position)) return;
            try
            {
                var sr = go.GetComponent<SpriteRenderer>();
                if (sr != null && sr.sprite != null && UnityEngine.Object.Instantiate(Resources.Load("ItemBreakParticle"), go.transform.position, go.transform.rotation) is GameObject fx)
                {
                    var ps = fx.GetComponent<ParticleSystem>();
                    var shape = ps.shape;
                    shape.texture = sr.sprite.texture;
                    shape.sprite = sr.sprite;
                    ps.Play();
                }
                UnityEngine.Object.Instantiate(Resources.Load<GameObject>("DustMini"), go.transform.position, Quaternion.identity);
            }
            catch (Exception e) { Log.Debug("[decay] crumble effect: " + e.Message); }
        }
    }

    [HarmonyPatch(typeof(Item), nameof(Item.HandleDecay))]
    internal static class ItemDecayPatch
    {
        private static bool Prefix(Item __instance) => !ItemDecay.HostDecides(__instance);
    }

    [HarmonyPatch(typeof(Item), "Update")]
    internal static class ItemCrumblePatch
    {
        private static void Prefix(Item __instance)
        {
            if (__instance.condition > 0f || !Coop.IsHost) return;
            var st = __instance.Stats;
            if (st != null && st.destroyAtZeroCondition) ItemDecay.BeforeGameCrumble(__instance);
        }
    }
}
