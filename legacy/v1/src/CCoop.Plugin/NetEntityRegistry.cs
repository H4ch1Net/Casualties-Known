using System;
using System.Collections.Generic;
using UnityEngine;

namespace CCoop.Plugin
{
    /// <summary>
    /// Maps network entity ids to the local GameObjects that represent them.
    ///
    /// Every peer needs to agree on which object is which, or "player A picked up item X"
    /// cannot even be expressed — host and client hold separate GameObjects with no
    /// correspondence.
    ///
    /// The id is simply the entity's index in the world manifest. Both sides derive it
    /// from the same ordered list, so identity comes for free: no id negotiation, no
    /// allocation protocol, and no chance of the two sides disagreeing as long as the
    /// manifest matches. Unity instance ids could not be used — they are process-local.
    ///
    /// Ids are stable for the lifetime of a layer and are reissued on the next transfer,
    /// which is also when the world itself is replaced.
    /// </summary>
    internal sealed class NetEntityRegistry
    {
        private readonly Dictionary<uint, GameObject> _byId = new Dictionary<uint, GameObject>();
        private readonly Dictionary<int, uint> _idByInstance = new Dictionary<int, uint>();
        private readonly Dictionary<uint, uint> _revisions = new Dictionary<uint, uint>();

        /// <summary>
        /// Prefab key per id. Stored separately from the GameObject because the two peers
        /// name their objects differently — the host holds "glowplant(Clone)" while the
        /// client holds "CCoopSpawn_glowplant" — so hashing names would report a mismatch
        /// on two registries that actually agree.
        /// </summary>
        private readonly Dictionary<uint, string> _keys = new Dictionary<uint, string>();

        /// <summary>Entities registered for the current layer.</summary>
        internal int Count => _byId.Count;

        internal void Clear()
        {
            _byId.Clear();
            _idByInstance.Clear();
            _revisions.Clear();
            _keys.Clear();
            _nextRuntimeId = 0;
        }

        internal void Register(uint id, GameObject go, string key)
        {
            if (go == null) return;

            // Re-registering a live id silently orphans the object that held it: it stays
            // in the world, visible and pickable, but nothing can address it any more.
            // That is exactly how a client's thrown item came to duplicate — the host
            // created one copy while arbitrating the drop and a second when it applied its
            // own broadcast — and it went unnoticed for days because both copies sat in
            // the same place. Loud, because a silent overwrite is invisible until the two
            // copies happen to move differently.
            GameObject existing;
            if (_byId.TryGetValue(id, out existing) && existing != null &&
                !ReferenceEquals(existing, go))
            {
                CoopPlugin.Warn("[registry] id " + id + " re-registered over a live object ('" +
                                KeyOf(id) + "' -> '" + (key ?? "") + "'); the old one is orphaned");
            }

            _byId[id] = go;
            _idByInstance[go.GetInstanceID()] = id;
            _keys[id] = key ?? string.Empty;
            if (!_revisions.ContainsKey(id)) _revisions[id] = 0;
        }

        internal string KeyOf(uint id) => _keys.TryGetValue(id, out var k) ? k : null;

        /// <summary>
        /// Every live id, as a snapshot. A copy rather than the live keys because
        /// <see cref="TryGet"/> prunes destroyed entries as it finds them, which would
        /// invalidate an enumerator mid-loop.
        /// </summary>
        internal List<uint> Ids() => new List<uint>(_byId.Keys);

        /// <summary>
        /// Where peer-owned runtime ids begin. Manifest entities are numbered by their
        /// index and a layer holds a couple of thousand, so this sits far above any of
        /// them with room to spare.
        /// </summary>
        internal const uint RuntimeIdBase = 0x0100_0000;

        /// <summary>Ids reserved to each peer. One peer cannot exhaust another's.</summary>
        internal const uint PerPeerStride = 0x0010_0000;

        private uint _nextRuntimeId;

        /// <summary>
        /// Next id for an entity created after the world transfer — a dropped item, or
        /// loot from a broken block.
        ///
        /// Scoped to the allocating peer rather than handed out by the host. The game
        /// rolls drops with <c>UnityEngine.Random</c> at the moment of destruction, so
        /// the peer that broke something is the only one that knows what fell out; making
        /// it ask the host for an id first would put a round trip in front of every
        /// pickaxe swing. Disjoint ranges mean two peers spawning in the same frame
        /// cannot collide, with no negotiation at all.
        ///
        /// Ids only ever increase within a layer: reusing a freed one would let a late
        /// message about the old entity land on the new one.
        /// </summary>
        internal uint AllocateRuntimeId(byte peerId)
        {
            uint first = RuntimeIdBase + peerId * PerPeerStride;
            if (_nextRuntimeId < first) _nextRuntimeId = first;

            // Running off the end of our block would start issuing the next peer's ids,
            // silently aliasing two different objects. A layer would need a million
            // spawns to get here, so report it rather than trying to recover.
            if (_nextRuntimeId >= first + PerPeerStride)
            {
                CoopPlugin.Warn("[world] peer " + peerId + " exhausted its runtime id range");
                return 0;
            }

            return _nextRuntimeId++;
        }

        /// <summary>
        /// Resolves an id to a live object. Returns false for entities that have since
        /// been destroyed, which is the normal way a client learns an item was taken.
        /// </summary>
        internal bool TryGet(uint id, out GameObject go)
        {
            if (_byId.TryGetValue(id, out go) && go != null) return true;

            // Unity's null is a destroyed-object sentinel, not a real null; drop the
            // entry so the id is reported as unknown from now on.
            if (go == null) _byId.Remove(id);
            go = null;
            return false;
        }

        /// <summary>Finds the id for a GameObject, walking up to a registered parent.</summary>
        internal bool TryGetId(GameObject go, out uint id)
        {
            id = 0;
            if (go == null) return false;

            for (var t = go.transform; t != null; t = t.parent)
            {
                if (_idByInstance.TryGetValue(t.gameObject.GetInstanceID(), out id)) return true;
            }
            return false;
        }

        /// <summary>
        /// How many live entities were created after the world transfer — drops, block
        /// loot, dropped items.
        ///
        /// Worth a counter of its own because it is the one entity population that both
        /// peers must agree on exactly. Raw <c>Item.allItems</c> cannot answer that: the
        /// two worlds already hold different numbers of items, since prefabs like
        /// <c>CorpseScript</c> spawn random loot in <c>Start</c> and so produce different
        /// results on each peer. Runtime ids are only ever created by replication, so
        /// counting them is noise-free.
        /// </summary>
        internal int RuntimeCount
        {
            get
            {
                int n = 0;
                foreach (var kv in _byId)
                    if (kv.Key >= RuntimeIdBase && kv.Value != null) n++;
                return n;
            }
        }

        internal uint RevisionOf(uint id) => _revisions.TryGetValue(id, out var r) ? r : 0u;

        /// <summary>
        /// Bumps an entity's revision. Clients quote the revision they saw, so a request
        /// based on a stale view is rejected instead of silently applied.
        /// </summary>
        internal uint Bump(uint id)
        {
            var next = RevisionOf(id) + 1;
            _revisions[id] = next;
            return next;
        }

        internal void Remove(uint id)
        {
            if (_byId.TryGetValue(id, out var go) && go != null)
                _idByInstance.Remove(go.GetInstanceID());
            _byId.Remove(id);
        }

        /// <summary>
        /// A cheap fingerprint of the registry, so both peers can be compared.
        ///
        /// Combines id, prefab key and rounded position. Position is quantised to whole
        /// units: entities settle under physics after spawning, so exact coordinates
        /// legitimately drift a little between peers and would produce false mismatches.
        /// </summary>
        internal string Fingerprint()
        {
            ulong hash = 1469598103934665603UL; // FNV-1a offset basis
            var ids = new List<uint>(_byId.Keys);
            ids.Sort();

            foreach (var id in ids)
            {
                if (!_byId.TryGetValue(id, out var go) || go == null) continue;

                Mix(ref hash, id);
                foreach (char c in KeyOf(id) ?? string.Empty) Mix(ref hash, c);
                Mix(ref hash, unchecked((uint)Mathf.RoundToInt(go.transform.position.x)));
                Mix(ref hash, unchecked((uint)Mathf.RoundToInt(go.transform.position.y)));
            }

            return _byId.Count + ":" + hash.ToString("x16");
        }

        private static void Mix(ref ulong hash, uint value)
        {
            for (int i = 0; i < 4; i++)
            {
                hash ^= (byte)(value >> (i * 8));
                hash *= 1099511628211UL; // FNV prime
            }
        }
    }
}
