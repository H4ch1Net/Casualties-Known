using System;
using System.Collections.Generic;
using CCoop.Protocol.Messages;
using HarmonyLib;
using UnityEngine;

namespace CCoop.Plugin
{
    /// <summary>
    /// Records every terrain edit this peer makes.
    ///
    /// <c>SetBlock</c> is the single funnel for terrain state: mining goes through
    /// <c>DamageBlock</c> which ends in <c>SetBlock(pos, 0)</c>, explosions and
    /// earthquakes go through <c>GenerateBlockCircle</c> which calls it per cell, and
    /// placing a block calls it directly. Hooking it therefore captures every change
    /// without having to understand — or reimplement — any of those systems.
    ///
    /// A postfix rather than a prefix: only edits that actually happened are worth
    /// sending, and by then the method has clamped the coordinates itself.
    /// </summary>
    [HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.SetBlock))]
    internal static class SetBlockPatch
    {
        private static void Postfix(WorldGeneration __instance, Vector2Int pos, ushort block)
        {
            try
            {
                CoopRunner.Active?.RecordLocalBlockEdit(__instance, pos, block);
            }
            catch (Exception e)
            {
                // Terrain editing must keep working even if the session does not.
                CoopPlugin.Warn("SetBlock patch error: " + e.Message);
            }
        }
    }

    /// <summary>
    /// Captures the items a breaking block drops.
    ///
    /// <c>DamageBlock</c> rolls its loot with <c>Random.value</c> — 40% for glass shards,
    /// 75% for scrap from metal, one of three woods picked at random — and the condition
    /// of what it makes is random too. None of that is reproducible from the same inputs,
    /// so the other peers have to be told what fell out rather than working it out.
    ///
    /// Only the peer whose player swung runs this method at all; the others just receive
    /// the resulting terrain edit. So the peer that captures the drops is unambiguously
    /// their owner, and there is nothing to arbitrate.
    /// </summary>
    [HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.DamageBlock),
                  new[] { typeof(Vector2Int), typeof(float), typeof(bool), typeof(bool), typeof(bool) })]
    internal static class DamageBlockPatch
    {
        private static void Prefix() => SpawnCapture.Begin();

        /// <summary>
        /// Records how damaged the cell now looks, so other peers can draw the same
        /// crack. Read *after* the hit rather than computed from it: the game scales
        /// damage by block type and metal bonus, and reading the accumulated total avoids
        /// reproducing any of that.
        ///
        /// A broken block leaves no entry, which is exactly right — the terrain delta
        /// already tells the others it is gone.
        /// </summary>
        private static void Postfix(WorldGeneration __instance, Vector2Int pos)
        {
            try
            {
                var damage = __instance.GetBlockDamage(pos);
                if (damage != null) CoopRunner.Active?.RecordBlockHit(pos, damage.damage);
            }
            catch (Exception e)
            {
                CoopPlugin.Warn("block hit patch error: " + e.Message);
            }
        }

        /// <summary>
        /// A finalizer rather than a postfix: it also runs when the original throws, so
        /// a fault inside the game's own code cannot leave capture switched on and turn
        /// every later item creation into a spurious broadcast.
        /// </summary>
        private static Exception Finalizer(Exception __exception)
        {
            try
            {
                // Only the outermost scope publishes. A nested one would send the outer
                // scope's items too, and then the outer scope would send them again.
                if (SpawnCapture.Depth == 1) CoopRunner.Active?.PublishCapturedSpawns();
            }
            catch (Exception e) { CoopPlugin.Warn("block loot publish error: " + e.Message); }
            finally { SpawnCapture.End(); }

            return __exception;   // never swallow the game's own exception
        }
    }

    /// <summary>
    /// Notes items created while a capture scope is open.
    ///
    /// <c>Utils.Create</c> is the game's item factory, so watching it is how we learn
    /// what a game method produced without having to duplicate the method's logic. The
    /// scope keeps that narrow: outside an explicit capture window this records nothing,
    /// which matters because <c>Utils.Create</c> also runs for crafting, console commands
    /// and our own replication of somebody else's spawn.
    /// </summary>
    [HarmonyPatch(typeof(Utils), nameof(Utils.Create),
                  new[] { typeof(string), typeof(Vector2), typeof(float) })]
    internal static class UtilsCreatePatch
    {
        private static void Postfix(string id, GameObject __result)
        {
            if (__result != null) SpawnCapture.Record(id, __result);
        }
    }

    /// <summary>
    /// Stops a prop dropping loot twice when terrain sync fells it on both peers.
    ///
    /// <c>UpdateChunk</c> fires the chunk's <c>ChunkUpdated</c> event, and props that
    /// need ground register <c>CheckSeating</c> with it. So the moment a peer's terrain
    /// edit is applied here, every prop that was standing on the removed block sets its
    /// own health to zero and is destroyed on the next frame — which is correct, and is
    /// how entity destruction propagates for free.
    ///
    /// The loot is not free: both peers would reach <c>BuildingEntity.Update</c> at
    /// roughly the same moment and each roll its own set. The digging peer owns the
    /// destruction and is already sending its drops, so this side stands down before it
    /// can claim them — without waiting for the despawn message, which travels on a
    /// different channel and may arrive after.
    /// </summary>
    [HarmonyPatch(typeof(BuildingEntity), nameof(BuildingEntity.CheckSeating))]
    internal static class CheckSeatingPatch
    {
        private static void Postfix(BuildingEntity __instance)
        {
            // Only inside the window where we are writing someone else's terrain. The
            // same method runs constantly from OnBecameVisible, where the local player
            // is the cause and the drops are rightfully theirs.
            if (!BlockSync.ApplyingRemoteTerrain) return;
            if (__instance == null || __instance.health >= 0.5f) return;

            CoopRunner.Active?.DisownDestruction(__instance);
        }
    }

    /// <summary>One object created inside a capture scope.</summary>
    internal struct CapturedSpawn
    {
        public string Id;
        public GameObject Go;
    }

    /// <summary>Collects <c>Utils.Create</c> results for the duration of a game call.</summary>
    internal static class SpawnCapture
    {
        private static int _depth;
        private static readonly List<CapturedSpawn> Items = new List<CapturedSpawn>();

        internal static bool Active => _depth > 0;

        internal static int Depth => _depth;

        internal static void Begin()
        {
            // Nested only if the game ever damages a block from inside a damage handler;
            // counting rather than flagging means the inner scope cannot close the outer.
            if (_depth == 0) Items.Clear();
            _depth++;
        }

        internal static void End()
        {
            if (_depth > 0) _depth--;
            if (_depth == 0) Items.Clear();
        }

        internal static void Record(string id, GameObject go)
        {
            if (_depth <= 0) return;
            Items.Add(new CapturedSpawn { Id = id, Go = go });
        }

        /// <summary>What has been captured so far in the innermost scope.</summary>
        internal static List<CapturedSpawn> Captured => Items;
    }

    /// <summary>
    /// Keeps terrain and world items the same on every peer after the initial transfer.
    ///
    /// The world is streamed once and would otherwise never change again for anyone but
    /// the player who changed it — two players mining the same wall end up in different
    /// worlds permanently, and neither can see what the other dug.
    ///
    /// Terrain travels peer-to-peer rather than through host arbitration. An edit names
    /// the block a cell now holds instead of asking for a change, so it is idempotent and
    /// two peers editing the same cell converge on the same answer. There is nothing to
    /// duplicate and therefore nothing to arbitrate — and mining stays instant instead of
    /// waiting a round trip per swing.
    /// </summary>
    internal sealed class BlockSync
    {
        /// <summary>
        /// Ceiling on edits held between flushes. Sized well above the biggest legitimate
        /// burst (a large explosion carves a few thousand cells) so hitting it means
        /// something is wrong, and is worth saying out loud rather than absorbing.
        /// </summary>
        internal const int MaxPendingEdits = 16384;

        /// <summary>
        /// Set while another peer's terrain is being written in. Read by
        /// <see cref="CheckSeatingPatch"/>, which needs to tell "this prop fell because
        /// somebody else dug" from "this prop fell because I did".
        /// </summary>
        internal static bool ApplyingRemoteTerrain { get; private set; }

        private readonly Action<string> _log;

        /// <summary>
        /// Outbound edits, keyed by cell. Last write wins, which is exactly what a
        /// receiver applying them in order would end up with, and it collapses the
        /// repeated writes the game makes to the same cell within a frame.
        /// </summary>
        private readonly Dictionary<int, ushort> _outbound = new Dictionary<int, ushort>();

        /// <summary>
        /// Crack depth per cell, waiting to be sent. Keyed by cell so a flurry of hits on
        /// one block collapses to the latest total — which is all a receiver needs, since
        /// the value is absolute.
        /// </summary>
        private readonly Dictionary<int, float> _outboundHits = new Dictionary<int, float>();

        private readonly List<BlockHitMessage> _inboundHits = new List<BlockHitMessage>();

        private readonly List<BlockDeltaMessage> _inbound = new List<BlockDeltaMessage>();
        private readonly List<EntitySpawnMessage> _inboundSpawns = new List<EntitySpawnMessage>();
        private readonly List<EntityDespawnMessage> _inboundDespawns = new List<EntityDespawnMessage>();

        /// <summary>
        /// Entities whose destruction is already accounted for — either a peer announced
        /// it, or we did. Guards the one thing that must happen exactly once: rolling and
        /// publishing an entity's drops.
        /// </summary>
        private readonly HashSet<uint> _destroyed = new HashSet<uint>();

        private bool _reportedOverflow;

        internal BlockSync(Action<string> log) { _log = log; }

        private void Note(string s) => _log?.Invoke(s);

        internal int PendingOutbound => _outbound.Count;
        internal int PendingInbound => _inbound.Count;

        /// <summary>Cells published and cells applied this layer, for the overlay.</summary>
        internal int TotalSent { get; private set; }
        internal int TotalApplied { get; private set; }

        // ---------------------------------------------------------------- capture

        /// <summary>
        /// Queues one of our own edits. Coordinates are clamped the same way
        /// <c>SetBlock</c> clamps them, so we never publish a cell the game did not
        /// actually write.
        /// </summary>
        internal void RecordLocalEdit(WorldGeneration world, Vector2Int pos, ushort block)
        {
            if (world == null) return;

            int x = Mathf.Clamp(pos.x, 0, (int)world.width - 1);
            int y = Mathf.Clamp(pos.y, 0, (int)world.height - 1);

            if (_outbound.Count >= MaxPendingEdits && !_outbound.ContainsKey(Key(x, y)))
            {
                if (!_reportedOverflow)
                {
                    _reportedOverflow = true;
                    Note("[block] more than " + MaxPendingEdits + " edits in one frame; " +
                         "dropping the rest — terrain may now differ between peers");
                }
                return;
            }

            _outbound[Key(x, y)] = block;
        }

        private static int Key(int x, int y) => ((x & 0xFFFF) << 16) | (y & 0xFFFF);

        /// <summary>Queues how damaged one of our cells now looks.</summary>
        internal void RecordLocalHit(Vector2Int pos, float damage)
        {
            if (_outboundHits.Count >= MaxPendingEdits) return;
            _outboundHits[Key(pos.x, pos.y)] = damage;
        }

        /// <summary>Sends this frame's crack depths.</summary>
        internal void FlushHits(Action<BlockHitMessage> send)
        {
            if (_outboundHits.Count == 0) return;

            var batch = new BlockHitMessage();
            foreach (var kv in _outboundHits)
            {
                batch.Cracks.Add(new BlockCrack((ushort)(kv.Key >> 16),
                                                (ushort)(kv.Key & 0xFFFF),
                                                kv.Value));

                if (batch.Cracks.Count == BlockHitMessage.MaxCracksPerMessage)
                {
                    send(batch);
                    batch = new BlockHitMessage();
                }
            }
            if (batch.Cracks.Count > 0) send(batch);

            _outboundHits.Clear();
        }

        internal void Enqueue(BlockHitMessage msg)
        {
            if (msg == null) return;
            if (_inboundHits.Count >= MaxBufferedMessages) return;
            _inboundHits.Add(msg);
        }

        /// <summary>
        /// Draws another peer's cracks and plays the hit.
        ///
        /// Never routed through <c>DamageBlock</c>: that would break the block a second
        /// time on this peer and roll a second set of loot. Only the overlay and the
        /// sound, with the authoritative removal still arriving as a terrain edit.
        /// </summary>
        private void ApplyHits()
        {
            var world = WorldGeneration.world;
            if (world == null || world.blockDamages == null) return;

            foreach (var msg in _inboundHits)
            {
                foreach (var crack in msg.Cracks)
                {
                    var pos = new Vector2Int(crack.X, crack.Y);
                    try { ApplyHit(world, pos, crack.Damage); }
                    catch (Exception e)
                    {
                        Note("[block] crack at " + crack.X + "," + crack.Y + " failed: " + e.Message);
                    }
                }
            }
            _inboundHits.Clear();
        }

        private static void ApplyHit(WorldGeneration world, Vector2Int pos, float damage)
        {
            var existing = world.GetBlockDamage(pos);

            // Cracks carry the cell's TOTAL accumulated damage, so the blow that was just
            // landed is the difference from what we already had. Reported in play as
            // "can't see the damage numbers when hitting a block" — the crack deepened and
            // the sound played, but the number the attacker saw stayed on their screen.
            //
            // Falling out of a duplicate delivery for free is the point of sending totals:
            // a repeat gives a difference of zero and so no second number.
            float previous = existing != null ? existing.damage : 0f;

            if (existing == null)
            {
                // The game caps its own list at 128 and evicts the oldest; match that, or
                // a long mining session leaves hundreds of sprites behind.
                while (world.blockDamages.Count >= 128)
                {
                    world.blockDamages[0].DestroySprite();
                    world.blockDamages.RemoveAt(0);
                }

                existing = new BlockDamage { pos = pos, damage = damage };
                world.blockDamages.Add(existing);
            }
            else
            {
                existing.damage = damage;
            }

            existing.UpdateSprite();

            var at = world.BlockToWorldPos(pos);

            int dealt = Mathf.RoundToInt(damage - previous);
            if (dealt > 0) WorldGeneration.CreateDamageNumber(at, dealt);

            var info = world.GetBlockInfo(world.GetBlock(pos));
            if (info != null && !string.IsNullOrEmpty(info.hitsound))
                Sound.Play(info.hitsound, at);
        }

        // ---------------------------------------------------------------- outbound

        /// <summary>
        /// Sends everything queued this frame, split into batches the wire format allows.
        /// </summary>
        internal void Flush(Action<BlockDeltaMessage> send)
        {
            if (_outbound.Count == 0) return;

            var batch = new BlockDeltaMessage();
            int sent = 0;

            foreach (var kv in _outbound)
            {
                batch.Edits.Add(new BlockEdit((ushort)(kv.Key >> 16),
                                              (ushort)(kv.Key & 0xFFFF),
                                              kv.Value));

                if (batch.Edits.Count == BlockDeltaMessage.MaxEditsPerMessage)
                {
                    send(batch);
                    sent += batch.Edits.Count;
                    batch = new BlockDeltaMessage();
                }
            }

            if (batch.Edits.Count > 0)
            {
                send(batch);
                sent += batch.Edits.Count;
            }

            TotalSent += sent;
            Note("[block] sent " + sent + " terrain edit" + (sent == 1 ? "" : "s") +
                 " (" + TotalSent + " this layer)");
            _outbound.Clear();
            _reportedOverflow = false;
        }

        /// <summary>
        /// Discards queued edits without sending them — used when the world they refer to
        /// no longer exists, where publishing them would corrupt the next layer.
        /// </summary>
        internal void DropOutbound()
        {
            _outbound.Clear();
            _outboundHits.Clear();
        }

        // ---------------------------------------------------------------- inbound

        /// <summary>
        /// How many messages may wait for a world to arrive. Bounded because the peer
        /// sending them has no idea we are stalled: a client that never finishes applying
        /// a world would otherwise accumulate the host's entire session in memory.
        /// </summary>
        private const int MaxBufferedMessages = 4096;

        /// <summary>
        /// Holds a peer's edits until there is a settled world to write them into.
        ///
        /// A client can receive edits while it is still applying the world snapshot —
        /// the transfer takes several seconds of coroutine work and the host keeps
        /// playing. Applying them then would either write into a grid that is about to be
        /// overwritten wholesale, or into no grid at all.
        /// </summary>
        internal void Enqueue(BlockDeltaMessage msg)
        {
            if (msg == null) return;
            if (_inbound.Count >= MaxBufferedMessages)
            {
                Note("[block] buffered edits full; discarding — terrain will need a resync");
                return;
            }
            _inbound.Add(msg);
        }

        internal void Enqueue(EntitySpawnMessage msg)
        {
            if (msg == null) return;
            if (_inboundSpawns.Count >= MaxBufferedMessages)
            {
                Note("[block] buffered spawns full; discarding");
                return;
            }
            _inboundSpawns.Add(msg);
        }

        internal void Enqueue(EntityDespawnMessage msg)
        {
            if (msg == null) return;

            // Recorded the instant it arrives, not when it is applied: our own
            // BuildingEntity.Update may reach the same entity first, and it must not
            // claim a destruction somebody else has already announced.
            _destroyed.Add(msg.EntityId);

            if (_inboundDespawns.Count >= MaxBufferedMessages) return;
            _inboundDespawns.Add(msg);
        }

        /// <summary>
        /// True if this entity's destruction already belongs to someone — so it must be
        /// destroyed quietly, without rolling or publishing any loot.
        /// </summary>
        internal bool IsDestructionClaimed(uint id) => _destroyed.Contains(id);

        /// <summary>Takes ownership of an entity's destruction. False if already claimed.</summary>
        internal bool ClaimDestruction(uint id) => _destroyed.Add(id);

        /// <summary>Forgets buffered remote changes, e.g. when the layer is replaced.</summary>
        internal void DropInbound()
        {
            _inbound.Clear();
            _inboundHits.Clear();
            _inboundSpawns.Clear();
            _inboundDespawns.Clear();

            // Ids are reissued with the next world, so a claim against the old layer
            // would silence a legitimate destruction in the new one.
            _destroyed.Clear();
        }

        /// <summary>
        /// Writes buffered remote edits into the live world and rebuilds the affected
        /// chunks. Safe to call every frame; does nothing when there is nothing to do.
        /// </summary>
        internal void ApplyInbound(NetEntityRegistry entities)
        {
            // Cracks before edits: a hit that lands after its block was cleared would be
            // drawn on empty air.
            if (_inboundHits.Count > 0) ApplyHits();
            if (_inbound.Count > 0) ApplyTerrain();

            // Despawns before spawns: a crate's own loot arrives right behind it, and
            // destroying afterwards risks catching an item that landed inside it.
            if (_inboundDespawns.Count > 0) ApplyDespawns(entities);
            if (_inboundSpawns.Count > 0) ApplySpawns(entities);
        }

        private void ApplyDespawns(NetEntityRegistry entities)
        {
            foreach (var despawn in _inboundDespawns)
            {
                try { ApplyDespawn(entities, despawn.EntityId); }
                catch (Exception e)
                {
                    Note("[block] could not despawn " + despawn.EntityId + ": " + e.Message);
                }
            }
            _inboundDespawns.Clear();
        }

        private void ApplyDespawn(NetEntityRegistry entities, uint id)
        {
            if (!entities.TryGet(id, out var go))
            {
                // Never spawned here, or already gone. Normal, not an error.
                return;
            }

            var building = go.GetComponent<BuildingEntity>();
            if (building != null)
            {
                // Zeroing health rather than destroying outright: the game's own Update
                // then breaks it apart with the right particles, dust and sound, and
                // `AnimalDeath` still fires for creatures. Blanked first so that same
                // pass does not also roll a second set of drops — the peer that owned
                // this destruction has already sent us theirs.
                BuildingDrops.Blank(building);
                building.health = 0f;
            }
            else
            {
                UnityEngine.Object.Destroy(go);
            }

            entities.Remove(id);
            Note("[block] entity " + id + " destroyed by a peer");
        }

        private void ApplyTerrain()
        {
            var world = WorldGeneration.world;
            var grid = WorldSync.CurrentGrid;
            if (world == null || grid == null) return;   // keep them buffered

            int w = grid.GetLength(0), h = grid.GetLength(1);
            int chunkSize = WorldGeneration.CHUNKSIZE;
            int applied = 0;

            // One rebuild per chunk however many cells changed in it: UpdateChunk redraws
            // all 4096 tiles, so doing it per cell would be thousands of times the work.
            var dirtyChunks = new HashSet<int>();

            foreach (var msg in _inbound)
            {
                foreach (var e in msg.Edits)
                {
                    if (e.X >= w || e.Y >= h) continue;   // a differently-sized world

                    grid[e.X, e.Y] = e.Block;
                    applied++;

                    // A cell someone else cleared may still carry our own accumulated
                    // crack damage. Left behind it would show a damage overlay on empty
                    // air and count towards breaking whatever is put there next.
                    if (e.Block == 0) ClearBlockDamage(world, new Vector2Int(e.X, e.Y));

                    dirtyChunks.Add((e.X / chunkSize) * (int)world.chunkHeight + e.Y / chunkSize);
                }
            }
            _inbound.Clear();

            ApplyingRemoteTerrain = true;
            try
            {
                foreach (int key in dirtyChunks)
                {
                    int cx = key / (int)world.chunkHeight;
                    int cy = key % (int)world.chunkHeight;
                    try { world.UpdateChunk(new Vector2Int(cx, cy)); }
                    catch (Exception e) { Note("[block] chunk " + cx + "," + cy + " failed: " + e.Message); }
                }
            }
            finally
            {
                // Leaving this set would silence the local player's own drops for the
                // rest of the run.
                ApplyingRemoteTerrain = false;
            }

            TotalApplied += applied;
            Note("[block] applied " + applied + " remote edit" + (applied == 1 ? "" : "s") +
                 " across " + dirtyChunks.Count + " chunk" + (dirtyChunks.Count == 1 ? "" : "s") +
                 " (" + TotalApplied + " this layer)");
        }

        private static void ClearBlockDamage(WorldGeneration world, Vector2Int pos)
        {
            var damages = world.blockDamages;
            if (damages == null) return;

            for (int i = damages.Count - 1; i >= 0; i--)
            {
                if (damages[i] == null || damages[i].pos != pos) continue;
                damages[i].DestroySprite();
                damages.RemoveAt(i);
            }
        }

        private void ApplySpawns(NetEntityRegistry entities)
        {
            // Items need a world to fall into; without one they would spawn at the right
            // coordinates in an empty layer and drift away before it arrives.
            if (!WorldSync.WorldReady) return;

            int applied = 0;
            foreach (var spawn in _inboundSpawns)
            {
                try
                {
                    ApplySpawn(entities, spawn);
                    applied++;
                }
                catch (Exception e)
                {
                    Note("[block] could not spawn '" + spawn.ItemId + "': " + e.Message);
                }
            }
            _inboundSpawns.Clear();

            if (applied > 0)
                Note("[block] spawned " + applied + " item" + (applied == 1 ? "" : "s") +
                     " from peers");
        }

        private void ApplySpawn(NetEntityRegistry entities, EntitySpawnMessage spawn)
        {
            // Already present: a duplicate delivery, or an id we somehow reused. Either
            // way creating a second object would be a visible item dupe.
            if (entities.TryGet(spawn.EntityId, out _))
            {
                Note("[block] spawn " + spawn.EntityId + " already exists; ignoring");
                return;
            }

            var go = Utils.Create(spawn.ItemId, new Vector2(spawn.PositionX, spawn.PositionY),
                                  spawn.Rotation);
            if (go == null)
            {
                Note("[block] unknown item '" + spawn.ItemId + "' in a spawn from a peer");
                return;
            }

            go.name = WorldSync.SpawnedNamePrefix + spawn.ItemId;

            var item = go.GetComponent<Item>();
            // Assigned directly rather than through SetCondition: that method drains a
            // water container in proportion to the change, which is meaningless for an
            // item being created and would empty flasks that spawn part-full.
            if (item != null) item.condition = spawn.Condition;

            var rb = go.GetComponent<Rigidbody2D>();
            if (rb != null) rb.velocity = new Vector2(spawn.VelocityX, spawn.VelocityY);

            // Loot that bursts out near a player floats for a few seconds, outlined and
            // giving off particles, so it can be grabbed before it settles.
            //
            // Obeyed here, NOT re-decided. The first attempt re-applied the game's own
            // proximity rule locally, reasoning that a float is presentation like an
            // earthquake's shake and so belongs to each screen. That was wrong:
            // FreshItemDrop.Awake sets gravityScale to 0, which is simulation state, so a
            // peer that declines the float runs different physics from the peer that took
            // it. Reported in play exactly as that predicts — the far player's copy was
            // held up by motion sync while its own gravity pulled it down, so it jittered
            // in mid-air, with no outline and no particles.
            //
            // FreshItemDrop deletes itself in Awake when the game's item-floating setting
            // is off, so the player's own preference still applies. Two peers configured
            // differently would disagree about gravity again; both share settings.json
            // here, and a real split install would need the flag to win instead.
            if (spawn.Floats) go.AddComponent<FreshItemDrop>();

            entities.Register(spawn.EntityId, go, spawn.ItemId);
        }
    }
}
