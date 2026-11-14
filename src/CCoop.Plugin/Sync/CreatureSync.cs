using System;
using System.Collections;
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
    /// Creatures, by proximity ownership.
    ///
    /// The game only simulates what is near its own camera, and creature AI hunts by
    /// physics overlap and bites by collision with real limbs. So the right machine to
    /// run a creature is the one whose player is closest to it: there it sees a real
    /// target and bites a real body, with every wound the medical system models. The
    /// host assigns each creature to its nearest player (with hysteresis so ownership
    /// does not flicker), the owner simulates it and streams where it is, and everyone
    /// else shows it as a kinematic puppet that still collides — so it can still bite
    /// whoever it bumps into.
    /// </summary>
    internal sealed class CreatureSync : CoopModule
    {
        private const float AssignInterval = 0.5f;
        private const float StateInterval = 0.1f;
        private const float SwitchMargin = 10f;
        private const float StreamRange = 120f;

        private readonly Dictionary<uint, byte> _owner = new Dictionary<uint, byte>();
        private readonly Dictionary<uint, BodyMotion> _target = new Dictionary<uint, BodyMotion>();
        private readonly HashSet<uint> _puppets = new HashSet<uint>();
        private readonly List<uint> _creatures = new List<uint>();
        private float _nextAssign, _nextState, _nextFull, _nextScan;

        private static CreatureSync _self;
        private static readonly HashSet<int> PuppetInstances = new HashSet<int>();

        internal override void Attach(NetSession s)
        {
            _self = this;
            WorldNet.Register<CreatureOwners>(s, (o, m) => { if (!Coop.IsHost) foreach (var kv in m.Owners) SetOwner(kv.Key, kv.Value); });
            WorldNet.Register<CreatureStates>(s, (o, m) => { foreach (var b in m.Bodies) _target[b.Id] = b; }, queueWhilePending: false);
        }

        internal override void Detach() { Clear(); _self = null; }
        internal override void OnWorldLost() => Clear();

        private void Clear()
        {
            foreach (var id in new List<uint>(_puppets)) Release(id);
            _owner.Clear();
            _target.Clear();
            _puppets.Clear();
            _creatures.Clear();
            PuppetInstances.Clear();
        }

        internal static bool IsPuppet(BuildingEntity b) => b != null && PuppetInstances.Contains(b.gameObject.GetInstanceID());

        internal static bool IsPuppetObject(GameObject go) => go != null && PuppetInstances.Count > 0 && PuppetInstances.Contains(go.GetInstanceID());

        internal string Describe()
        {
            int mine = 0;
            foreach (var id in _creatures) if (OwnerOf(id) == Coop.LocalId) mine++;
            return _creatures.Count + " tracked, " + mine + " simulated here, " + _puppets.Count + " puppets, " + _target.Count + " with remote state";
        }

        private static bool IsCreature(GameObject go)
        {
            var b = go.GetComponent<BuildingEntity>();
            if (b == null || go.GetComponent<Rigidbody2D>() == null) return false;
            if (b.animal) return true;
            return go.GetComponent<GuardScript>() != null || go.GetComponent<LaserDroneScript>() != null ||
                   go.GetComponent<SpiderHandler>() != null || go.GetComponent<CrystalEnemy>() != null ||
                   go.GetComponent<TechballScript>() != null;
        }

        private byte OwnerOf(uint id) => _owner.TryGetValue(id, out var o) ? o : NetSession.HostId;

        // ------------------------------------------------------------------ loop

        internal override void Tick()
        {
            if (!Coop.WorldLive) return;
            float now = Time.unscaledTime;

            if (now >= _nextScan)
            {
                _nextScan = now + 1f;
                _creatures.Clear();
                foreach (var kv in EntityRegistry.All)
                    if (kv.Value != null && IsCreature(kv.Value)) _creatures.Add(kv.Key);
            }

            if (Coop.IsHost && now >= _nextAssign)
            {
                _nextAssign = now + AssignInterval;
                Assign(now >= _nextFull);
                if (now >= _nextFull) _nextFull = now + 5f;
            }

            if (now >= _nextState)
            {
                _nextState = now + StateInterval;
                Stream();
            }
        }

        private void Assign(bool full)
        {
            var changes = new CreatureOwners();
            var players = new List<KeyValuePair<byte, Vector2>>();
            var body = GameInfo.LocalBody;
            // Creatures are run by a living player's machine, where they can hunt a real
            // body; the dead only spectate. With nobody alive the host keeps them.
            if (body != null && body.alive) players.Add(new KeyValuePair<byte, Vector2>(NetSession.HostId, body.transform.position));
            foreach (var rec in Coop.Players.Values)
                if (rec.Id != NetSession.HostId && rec.InWorld && rec.HasPosition && rec.Alive)
                    players.Add(new KeyValuePair<byte, Vector2>(rec.Id, rec.Position));
            if (players.Count == 0 && body != null) players.Add(new KeyValuePair<byte, Vector2>(NetSession.HostId, body.transform.position));
            if (players.Count == 0) return;

            foreach (var id in _creatures)
            {
                if (!EntityRegistry.TryGet(id, out var go)) continue;
                Vector2 p = go.transform.position;
                byte current = OwnerOf(id);
                float currentDist = float.MaxValue;
                byte best = current;
                float bestDist = float.MaxValue;
                foreach (var kv in players)
                {
                    float d = Vector2.Distance(p, kv.Value);
                    if (kv.Key == current) currentDist = d;
                    if (d < bestDist) { bestDist = d; best = kv.Key; }
                }
                byte chosen = current;
                if (currentDist == float.MaxValue || bestDist + SwitchMargin < currentDist) chosen = best;
                if (chosen != current || !_owner.ContainsKey(id))
                {
                    SetOwner(id, chosen);
                    changes.Owners.Add(new KeyValuePair<uint, byte>(id, chosen));
                }
                else if (full) changes.Owners.Add(new KeyValuePair<uint, byte>(id, chosen));
                if (changes.Owners.Count >= CreatureOwners.Max) { WorldNet.Publish(changes); changes = new CreatureOwners(); }
            }
            if (changes.Owners.Count > 0) WorldNet.Publish(changes);
        }

        private void SetOwner(uint id, byte owner)
        {
            _owner[id] = owner;
            if (owner == Coop.LocalId) Release(id);
            else MakePuppet(id);
        }

        private void MakePuppet(uint id)
        {
            if (_puppets.Contains(id) || !EntityRegistry.TryGet(id, out var go)) return;
            var rb = go.GetComponent<Rigidbody2D>();
            if (rb == null) return;
            _puppets.Add(id);
            PuppetInstances.Add(go.GetInstanceID());
            rb.bodyType = RigidbodyType2D.Kinematic;
            rb.velocity = Vector2.zero;
            rb.angularVelocity = 0f;
        }

        private void Release(uint id)
        {
            if (!_puppets.Remove(id)) return;
            if (!EntityRegistry.TryGet(id, out var go)) return;
            PuppetInstances.Remove(go.GetInstanceID());
            var rb = go.GetComponent<Rigidbody2D>();
            if (rb == null) return;
            rb.bodyType = RigidbodyType2D.Dynamic;
            if (_target.TryGetValue(id, out var t))
            {
                rb.velocity = new Vector2(t.VX, t.VY);
                rb.angularVelocity = t.Spin;
            }
        }

        private void Stream()
        {
            var others = new List<Vector2>();
            foreach (var rec in Coop.Players.Values)
                if (rec.Id != Coop.LocalId && rec.HasPosition) others.Add(rec.Position);
            if (others.Count == 0) return;

            var msg = new CreatureStates();
            foreach (var id in _creatures)
            {
                if (OwnerOf(id) != Coop.LocalId) continue;
                if (!EntityRegistry.TryGet(id, out var go)) continue;
                Vector2 p = go.transform.position;
                bool wanted = false;
                foreach (var o in others) if (Vector2.Distance(p, o) < StreamRange) { wanted = true; break; }
                if (!wanted) continue;
                var rb = go.GetComponent<Rigidbody2D>();
                msg.Bodies.Add(new BodyMotion
                {
                    Id = id, X = p.x, Y = p.y, Rotation = go.transform.eulerAngles.z,
                    VX = rb != null ? rb.velocity.x : 0f, VY = rb != null ? rb.velocity.y : 0f,
                    Spin = rb != null ? rb.angularVelocity : 0f,
                });
                if (msg.Bodies.Count == CreatureStates.Max) { WorldNet.Publish(msg); msg = new CreatureStates(); }
            }
            if (msg.Bodies.Count > 0) WorldNet.Publish(msg);
        }

        internal override void LateTick()
        {
            if (!Coop.WorldLive || _puppets.Count == 0) return;
            float k = 1f - Mathf.Exp(-Time.unscaledDeltaTime * 15f);
            foreach (var id in _puppets)
            {
                if (!EntityRegistry.TryGet(id, out var go)) continue;
                var rb = go.GetComponent<Rigidbody2D>();
                if (rb == null) continue;
                // Several scripts switch bodies back to Dynamic (a drone opening, for one);
                // a puppet must stay kinematic or local physics fights the stream.
                if (rb.bodyType != RigidbodyType2D.Kinematic) rb.bodyType = RigidbodyType2D.Kinematic;
                if (!_target.TryGetValue(id, out var t)) continue;
                var target = new Vector2(t.X, t.Y);
                var pos = Vector2.Lerp(rb.position, target, k);
                if ((pos - target).sqrMagnitude > 25f) pos = target;
                rb.position = pos;
                go.transform.position = new Vector3(pos.x, pos.y, go.transform.position.z);
                float rot = Mathf.LerpAngle(go.transform.eulerAngles.z, t.Rotation, k);
                rb.rotation = rot;
                go.transform.rotation = Quaternion.Euler(0f, 0f, rot);
                rb.velocity = Vector2.zero;
            }
        }

        // ------------------------------------------------------------------ cave ticks

        internal static IEnumerator SpawnTicks(CaveTickSpawner spawner)
        {
            var prefab = Resources.Load("cavetick");
            for (int i = 0; i < 16; i++)
            {
                if (spawner == null) yield break;
                var go = UnityEngine.Object.Instantiate(prefab,
                    spawner.transform.position + new Vector3(UnityEngine.Random.Range(-1f, 1f), UnityEngine.Random.Range(-1f, 1f)),
                    Quaternion.Euler(0f, 0f, UnityEngine.Random.value * 360f)) as GameObject;
                if (go != null && Coop.WorldLive) Coop.Get<EntitySync>()?.PublishSpawn(go);
                yield return new WaitForSeconds(0.1f);
            }
            if (spawner != null) UnityEngine.Object.Destroy(spawner);
        }
    }

    // ====================================================================== patches

    /// <summary>Puppets are kinematic; the game would flip them back to dynamic every frame.</summary>
    [HarmonyPatch(typeof(BuildingEntity), nameof(BuildingEntity.Update))]
    internal static class BuildingEntityUpdatePatch
    {
        private static bool Prefix(BuildingEntity __instance) => !CreatureSync.IsPuppet(__instance);
    }

    /// <summary>Cave ticks hatch where a player walks in; publish them so all peers see the same swarm.</summary>
    [HarmonyPatch(typeof(CaveTickSpawner), "SpawnSpiders")]
    internal static class CaveTickPatch
    {
        private static bool Prefix(CaveTickSpawner __instance, ref IEnumerator __result)
        {
            if (!Coop.WorldLive) return true;
            __result = CreatureSync.SpawnTicks(__instance);
            return false;
        }
    }

    /// <summary>Guard spawners run on every peer; only the host's spawn guards.</summary>
    [HarmonyPatch(typeof(GuardSpawner), nameof(GuardSpawner.CreateGuard))]
    internal static class GuardSpawnerPatch
    {
        private static bool Prefix() => !(Coop.IsClient && Coop.WorldLive);
    }
}
