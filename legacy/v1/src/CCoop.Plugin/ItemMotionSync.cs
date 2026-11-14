using System;
using System.Collections.Generic;
using CCoop.Protocol.Messages;
using UnityEngine;

namespace CCoop.Plugin
{
    /// <summary>
    /// Keeps loose items in the same place on every screen.
    ///
    /// An <c>EntitySpawn</c> describes the instant an item appeared — where, and with what
    /// impulse — and until now nothing said anything about it ever again. Two rigid bodies
    /// given identical starting conditions do not stay together: they meet the ground at
    /// slightly different moments, collide with different neighbours in a different order,
    /// and come to rest somewhere else. So the worlds drifted apart item by item, and it
    /// showed up in play three separate ways before it was recognised as one fault:
    ///
    ///   - a thrown item that merely dropped straight down on the other screen
    ///     (<c>ThrowItem</c> assigns the velocity *after* <c>DropItem</c> announces, so
    ///     the spawn carried a velocity of zero),
    ///   - loot from a broken crate scattering differently for each player,
    ///   - a dropped item rolling downhill for one player while the other still saw it
    ///     sitting at their feet.
    ///
    /// **Ownership costs nothing.** Runtime ids are handed out from a per-peer block, so
    /// the id of an item already says which peer is entitled to speak for it. No election,
    /// no handover, and two peers can never both claim the same item.
    ///
    /// **What is sent, and how.** While an item is awake its position is a stream: only
    /// the newest matters and a dropped packet costs nothing, so it goes unreliably at a
    /// modest rate. When it falls asleep exactly one more message goes out, reliably,
    /// carrying the final position. That last message is the one that actually matters —
    /// it is what makes both worlds agree where the item *ended up* — and it is also why
    /// this is cheap: an item at rest is silent, and almost every item is at rest.
    /// </summary>
    internal sealed class ItemMotionSync
    {
        /// <summary>
        /// Updates per second for an item in flight. Below the player rate (20 Hz)
        /// deliberately: a tumbling crowbar needs less fidelity than a player body, and
        /// several items can be in the air at once.
        /// </summary>
        private const float SendRateHz = 12f;

        /// <summary>
        /// Position error below which a receiver leaves the item alone.
        ///
        /// Physics answers differ in the last decimal every frame, and correcting that
        /// would fight the local simulation forever and make resting items shiver.
        /// </summary>
        private const float SnapThreshold = 0.05f;

        private readonly Func<string, object> _log;

        /// <summary>Ids we were sending for, and whether they were awake last look.</summary>
        private readonly Dictionary<uint, bool> _awake = new Dictionary<uint, bool>();

        private readonly List<EntityMotionMessage> _inbound = new List<EntityMotionMessage>();

        private float _nextSend;

        internal ItemMotionSync(Func<string, object> log) { _log = log; }

        private void Note(string message) { _log?.Invoke(message); }

        internal int TotalSent { get; private set; }
        internal int TotalApplied { get; private set; }

        /// <summary>Forgets everything. Ids and objects are both reissued with a layer.</summary>
        internal void Clear()
        {
            _awake.Clear();
            _inbound.Clear();
        }

        // ---------------------------------------------------------------- outbound

        /// <summary>
        /// Publishes the items this peer owns and that are moving, plus a final position
        /// for any that have just stopped.
        /// </summary>
        internal void Flush(NetEntityRegistry entities, byte localPeerId,
                            Action<EntityMotionMessage> send)
        {
            if (entities == null || send == null) return;
            if (Time.unscaledTime < _nextSend) return;
            _nextSend = Time.unscaledTime + 1f / SendRateHz;

            var moving = new EntityMotionMessage { AtRest = false };
            var stopped = new EntityMotionMessage { AtRest = true };

            foreach (var id in entities.Ids())
            {
                if (!OwnedBy(id, localPeerId)) continue;

                GameObject go;
                if (!entities.TryGet(id, out go)) { _awake.Remove(id); continue; }

                var rb = go.GetComponent<Rigidbody2D>();
                if (rb == null) continue;

                // A held item is parented to the body and simulated by nobody; publishing
                // it would drag the other peer's copy along by the hand.
                if (!rb.simulated || go.transform.parent != null) { _awake.Remove(id); continue; }

                bool awakeNow = rb.IsAwake();
                bool wasAwake;
                bool known = _awake.TryGetValue(id, out wasAwake);

                if (awakeNow)
                {
                    _awake[id] = true;
                    if (moving.Motions.Count < EntityMotionMessage.MaxMotionsPerMessage)
                        moving.Motions.Add(Describe(id, go, rb));
                }
                else if (known && wasAwake)
                {
                    // Just came to rest: one reliable message, then silence.
                    _awake[id] = false;
                    if (stopped.Motions.Count < EntityMotionMessage.MaxMotionsPerMessage)
                        stopped.Motions.Add(Describe(id, go, rb));
                }
                else if (!known)
                {
                    // First sight of an item that is already asleep — nothing to say.
                    _awake[id] = false;
                }
            }

            if (moving.Motions.Count > 0)
            {
                send(moving);
                TotalSent += moving.Motions.Count;
            }
            if (stopped.Motions.Count > 0)
            {
                send(stopped);
                TotalSent += stopped.Motions.Count;
                Note("[motion] " + stopped.Motions.Count + " item" +
                     (stopped.Motions.Count == 1 ? "" : "s") + " came to rest");
            }
        }

        private static EntityMotion Describe(uint id, GameObject go, Rigidbody2D rb)
        {
            var p = go.transform.position;
            return new EntityMotion
            {
                EntityId = id,
                X = p.x,
                Y = p.y,
                Rotation = go.transform.eulerAngles.z,
                VelocityX = rb.velocity.x,
                VelocityY = rb.velocity.y,
            };
        }

        /// <summary>
        /// Whether a runtime id was allocated by this peer.
        ///
        /// Manifest ids — everything below <see cref="NetEntityRegistry.RuntimeIdBase"/> —
        /// are world generation's, identical on every peer and not anybody's to move.
        /// </summary>
        private static bool OwnedBy(uint id, byte peerId)
        {
            if (id < NetEntityRegistry.RuntimeIdBase) return false;
            uint owner = (id - NetEntityRegistry.RuntimeIdBase) / NetEntityRegistry.PerPeerStride;
            return owner == peerId;
        }

        // ---------------------------------------------------------------- inbound

        internal void Enqueue(EntityMotionMessage msg)
        {
            if (msg != null) _inbound.Add(msg);
        }

        /// <summary>Moves our copies to where their owner says they are.</summary>
        internal void ApplyInbound(NetEntityRegistry entities)
        {
            if (_inbound.Count == 0) return;

            int applied = 0;
            foreach (var msg in _inbound)
            {
                foreach (var m in msg.Motions)
                {
                    GameObject go;
                    if (!entities.TryGet(m.EntityId, out go)) continue;

                    // Ours to display, never to correct: if a player has picked it up, the
                    // owner has not noticed yet and moving it would tear it out of a hand.
                    if (go.transform.parent != null) continue;

                    var rb = go.GetComponent<Rigidbody2D>();
                    var target = new Vector3(m.X, m.Y, go.transform.position.z);

                    if (msg.AtRest)
                    {
                        // Final word: put it exactly there and stop it dead, so both
                        // worlds agree about where this item finished.
                        go.transform.position = target;
                        go.transform.rotation = Quaternion.Euler(0f, 0f, m.Rotation);
                        if (rb != null)
                        {
                            rb.velocity = Vector2.zero;
                            rb.angularVelocity = 0f;
                            rb.Sleep();
                        }
                    }
                    else
                    {
                        // Velocity first: it lets the local physics carry the item
                        // plausibly between updates rather than teleporting 12 times a
                        // second, and the position only corrects the drift that leaves.
                        if (rb != null) rb.velocity = new Vector2(m.VelocityX, m.VelocityY);

                        if ((go.transform.position - target).sqrMagnitude >
                            SnapThreshold * SnapThreshold)
                        {
                            go.transform.position = target;
                            go.transform.rotation = Quaternion.Euler(0f, 0f, m.Rotation);
                        }
                    }
                    applied++;
                }
            }
            _inbound.Clear();
            TotalApplied += applied;
        }
    }
}
