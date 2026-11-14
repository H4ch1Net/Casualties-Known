using System;
using System.Collections.Generic;
using CCoop.Protocol.Messages;
using HarmonyLib;
using UnityEngine;

namespace CCoop.Plugin
{
    /// <summary>
    /// Intercepts a world object the moment it is about to break apart.
    ///
    /// <c>BuildingEntity</c> has no destroy method: <c>Update</c> tests
    /// <c>health &lt; 0.5f</c> every frame and, when it passes, plays the effects, rolls
    /// its drops and calls <c>Destroy</c>. There is no earlier or narrower hook — damage
    /// arrives as a bare <c>health -=</c> from a dozen call sites — so this runs on every
    /// building entity every frame and has to stay cheap. The health test is deliberately
    /// the first thing it does; on a real layer that is ~1,900 float comparisons a frame
    /// and nothing else.
    /// </summary>
    [HarmonyPatch(typeof(BuildingEntity), nameof(BuildingEntity.Update))]
    internal static class BuildingEntityUpdatePatch
    {
        private static void Prefix(BuildingEntity __instance)
        {
            // A single static read for every entity on every frame outside a session,
            // which is the case that has to cost nothing.
            if (!CoopRunner.MutationsLiveThisFrame) return;

            try
            {
                var runner = CoopRunner.Active;
                if (runner == null) return;

                runner.TrackEntityDamage(__instance);
                if (__instance.health < 0.5f) runner.OnBuildingEntityDying(__instance);
            }
            catch (Exception e)
            {
                // A networking fault must not stop the world object being destroyed, or
                // the player is left hitting an indestructible crate.
                CoopPlugin.Warn("building destroy patch error: " + e.Message);
            }
        }
    }

    /// <summary>
    /// Keeps world objects at the same health on every peer.
    ///
    /// Damage arrives as a bare <c>health -=</c> from a dozen unrelated call sites —
    /// melee, bullets, explosions, a cactus, a creature burrowing — with no method to
    /// hook and no event. So health is *observed* rather than intercepted: the
    /// <see cref="BuildingEntityUpdatePatch"/> already visits every entity every frame,
    /// and a drop since the last visit is damage somebody just dealt here.
    ///
    /// Sending the amount rather than the resulting health is what makes two players
    /// chopping the same crate work. Without it each peer only ever applies its own
    /// player's blows, so a crate needs felling twice — and if the two split the work it
    /// may never fall at all.
    /// </summary>
    internal sealed class EntityDamageSync
    {
        /// <summary>Below this the entity is dead and the despawn takes over.</summary>
        private const float DeathThreshold = 0.5f;

        /// <summary>
        /// Ignore changes smaller than this. Health is a float that other systems nudge;
        /// a packet per rounding error would be pure noise.
        /// </summary>
        private const float MinimumReportable = 0.01f;

        private readonly Action<string> _log;

        /// <summary>
        /// Health as of our last look, per entity. Keyed by Unity instance id rather than
        /// network id because it is read for every entity every frame and must be one
        /// cheap lookup; the network id is only needed once something actually changed.
        /// </summary>
        private readonly Dictionary<int, float> _lastHealth = new Dictionary<int, float>();

        private readonly Dictionary<uint, float> _outbound = new Dictionary<uint, float>();
        private readonly List<EntityDamageMessage> _inbound = new List<EntityDamageMessage>();

        internal EntityDamageSync(Action<string> log) { _log = log; }

        internal int PendingOutbound => _outbound.Count;

        /// <summary>Damage published and applied this layer, for the overlay.</summary>
        internal float TotalSent { get; private set; }
        internal float TotalApplied { get; private set; }

        /// <summary>
        /// Notes an entity's health, and queues any drop since last time.
        /// </summary>
        internal void Track(BuildingEntity entity, NetEntityRegistry entities)
        {
            int key = entity.GetInstanceID();
            float now = entity.health;

            float last;
            if (!_lastHealth.TryGetValue(key, out last))
            {
                // First sighting: nothing to compare against, so adopt it as the baseline.
                _lastHealth[key] = now;
                return;
            }

            if (now >= last)
            {
                if (now > last) _lastHealth[key] = now;   // repaired or reset
                return;
            }

            _lastHealth[key] = now;

            // The killing blow is deliberately not reported. The despawn that follows
            // carries strictly more information — the other peer destroys its copy
            // outright — and reporting it as well would make an entity that is already
            // gone take damage.
            if (now < DeathThreshold) return;

            float damage = last - now;
            if (damage < MinimumReportable) return;

            uint id;
            if (!entities.TryGetId(entity.gameObject, out id)) return;   // not networked

            float pending;
            _outbound.TryGetValue(id, out pending);
            _outbound[id] = pending + damage;
        }

        /// <summary>Sends everything dealt this frame, split into wire-sized batches.</summary>
        internal void Flush(Action<EntityDamageMessage> send)
        {
            if (_outbound.Count == 0) return;

            var batch = new EntityDamageMessage();
            foreach (var kv in _outbound)
            {
                batch.Damages.Add(new EntityDamage(kv.Key, kv.Value));
                TotalSent += kv.Value;

                if (batch.Damages.Count == EntityDamageMessage.MaxDamagesPerMessage)
                {
                    send(batch);
                    batch = new EntityDamageMessage();
                }
            }
            if (batch.Damages.Count > 0) send(batch);

            Note("[damage] sent " + _outbound.Count + " damaged object" +
                 (_outbound.Count == 1 ? "" : "s") +
                 " (" + TotalSent.ToString("F0") + " total this layer)");
            _outbound.Clear();
        }

        internal void Enqueue(EntityDamageMessage msg)
        {
            if (msg != null) _inbound.Add(msg);
        }

        /// <summary>Applies a peer's blows on top of our own.</summary>
        internal void ApplyInbound(NetEntityRegistry entities)
        {
            if (_inbound.Count == 0) return;

            int hit = 0;
            foreach (var msg in _inbound)
            {
                foreach (var d in msg.Damages)
                {
                    GameObject go;
                    if (!entities.TryGet(d.EntityId, out go)) continue;

                    var entity = go.GetComponent<BuildingEntity>();
                    if (entity == null) continue;

                    entity.health -= d.Amount;
                    TotalApplied += d.Amount;
                    hit++;

                    ShowHit(entity, d.Amount);

                    // Move the baseline with it. Otherwise our next look would read this
                    // as damage dealt here and send it straight back, and the two peers
                    // would keep hitting the crate with each other's axe forever.
                    _lastHealth[entity.GetInstanceID()] = entity.health;
                }
            }
            _inbound.Clear();

            if (hit > 0)
                Note("[damage] applied to " + hit + " object" + (hit == 1 ? "" : "s") +
                     " (" + TotalApplied.ToString("F0") + " total this layer)");
        }

        /// <summary>
        /// Reproduces the feedback the game gives whoever landed the blow.
        ///
        /// <c>Body.Attack</c> plays three things on a hit — a red flash of the object's
        /// own sprite, a floating damage number, and the entity's <c>hitSound</c> — and
        /// all three are local to the attacker. Replicating only <c>health</c> therefore
        /// left the other player watching a crate lose condition in total silence and
        /// then burst, which is what was reported in play: "the crate didn't flash red".
        ///
        /// Driven from the amount rather than re-derived: this is the damage a peer
        /// actually dealt, so the number shown matches the number they saw.
        /// </summary>
        private static void ShowHit(BuildingEntity entity, float amount)
        {
            try
            {
                var world = WorldGeneration.world;
                if (world == null) return;

                var at = (Vector2)entity.transform.position;

                var sprite = entity.GetComponent<SpriteRenderer>();
                if (sprite != null && sprite.sprite != null)
                    world.CreateHitFlash(sprite.sprite, at, entity.transform.rotation,
                                         Color.red, entity.transform);

                WorldGeneration.CreateDamageNumber(at, Mathf.RoundToInt(amount));

                if (entity.hitSound != null) Sound.Play(entity.hitSound, at);
            }
            catch (Exception e)
            {
                // Presentation only: never let it cost the damage that was already applied.
                CoopPlugin.Warn("hit feedback failed: " + e.Message);
            }
        }

        /// <summary>Drops everything. Ids and objects are both reissued with a new layer.</summary>
        internal void Clear()
        {
            _lastHealth.Clear();
            _outbound.Clear();
            _inbound.Clear();
        }

        private void Note(string s) => _log?.Invoke(s);
    }

    /// <summary>
    /// Reproduces <c>BuildingEntity</c>'s own drop roll so the results can be seen.
    ///
    /// The game creates its drops with <c>Object.Instantiate(Resources.Load(id), ...)</c>
    /// rather than <c>Utils.Create</c>, which leaves nothing to hook: the objects simply
    /// appear. Rolling the same distribution here — through <c>Utils.Create</c>, which is
    /// character-for-character the same call — puts them where <see cref="SpawnCapture"/>
    /// can see them, so they can be given ids and sent.
    ///
    /// This duplicates game logic, which is normally the wrong move. It is acceptable
    /// here because the roll is driven entirely by public data on the component
    /// (<c>itemsDropOnDestroy</c>, <c>alwaysDrop</c>, <c>guaranteedDropAmount</c>,
    /// <c>itemCategoriesToAdd</c>, <c>dropChanceMultiplier</c>) rather than by the
    /// hardcoded behaviour delegates in <c>SetupItems</c>, which must never be re-run
    /// remotely. **If the game's drop fields ever change shape, this must change with
    /// them** — the assembly hash in the handshake is what catches that.
    /// </summary>
    internal static class BuildingDrops
    {
        internal static readonly ItemDrop[] None = new ItemDrop[0];

        /// <summary>Stops the game rolling drops the caller has already taken over.</summary>
        internal static void Blank(BuildingEntity entity)
        {
            if (entity == null) return;
            entity.itemsDropOnDestroy = None;
            entity.alwaysDrop = None;
            entity.guaranteedDropAmount = 0;
        }

        /// <summary>
        /// Rolls and creates everything this entity drops. Call inside a
        /// <see cref="SpawnCapture"/> scope, then <see cref="Blank"/> the entity.
        /// </summary>
        internal static void Roll(BuildingEntity entity)
        {
            if (entity == null) return;

            Vector2 pos = entity.transform.position;

            // The game adds a highlight outline to drops near the local player. Purely
            // cosmetic and rightly per-peer, so it is decided locally on each side rather
            // than travelling with the spawn.
            var body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
            bool fresh = body != null && Vector2.Distance(pos, body.transform.position) < 8f;

            if (entity.itemsDropOnDestroy != null)
            {
                foreach (var drop in entity.itemsDropOnDestroy)
                {
                    if (drop == null) continue;
                    if (UnityEngine.Random.Range(0f, 1f) < drop.chance * entity.dropChanceMultiplier)
                        Create(drop.id, pos, drop.conditionMin, drop.conditionMax, fresh);
                }
            }

            if (entity.guaranteedDropAmount > 0 &&
                entity.itemCategoriesToAdd != null && entity.itemCategoriesToAdd.Length > 0)
            {
                for (int i = 0; i < entity.guaranteedDropAmount; i++)
                {
                    var category = entity.itemCategoriesToAdd[
                        UnityEngine.Random.Range(0, entity.itemCategoriesToAdd.Length)];

                    List<string> pool;
                    if (ItemLootPool.pool == null ||
                        !ItemLootPool.pool.TryGetValue(category, out pool) ||
                        pool == null || pool.Count == 0)
                    {
                        // The game would throw here; a missing category is not worth
                        // taking the player's world down for.
                        CoopPlugin.Warn("loot category '" + category + "' is empty or unknown");
                        continue;
                    }

                    Create(pool[UnityEngine.Random.Range(0, pool.Count)], pos, 1f, 1f, fresh);
                }
            }

            if (entity.alwaysDrop != null)
            {
                foreach (var drop in entity.alwaysDrop)
                {
                    if (drop == null) continue;
                    Create(drop.id, pos, drop.conditionMin, drop.conditionMax, fresh);
                }
            }
        }

        private static void Create(string id, Vector2 pos, float conditionMin, float conditionMax,
                                   bool fresh)
        {
            if (string.IsNullOrEmpty(id)) return;

            var go = Utils.Create(id, pos, UnityEngine.Random.Range(0f, 360f));
            if (go == null)
            {
                CoopPlugin.Warn("drop '" + id + "' could not be created");
                return;
            }

            var rb = go.GetComponent<Rigidbody2D>();
            if (rb != null)
                rb.velocity = new Vector2(UnityEngine.Random.Range(-7f, 7f),
                                          UnityEngine.Random.Range(-7f, 7f));

            var item = go.GetComponent<Item>();
            if (item != null) item.SetCondition(UnityEngine.Random.Range(conditionMin, conditionMax));

            if (fresh) go.AddComponent<FreshItemDrop>();
        }
    }
}
