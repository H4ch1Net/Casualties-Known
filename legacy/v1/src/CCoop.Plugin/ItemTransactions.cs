using System;
using CCoop.Protocol.Messages;
using HarmonyLib;
using UnityEngine;

namespace CCoop.Plugin
{
    /// <summary>
    /// Routes item pickups through the host.
    ///
    /// Patches <c>Body.PickUpItem</c> rather than <c>PlayerCamera.TryPickupFromWorld</c>.
    /// The latter is where the player's *intent* forms, but intercepting it would mean
    /// re-implementing its raycast (mouse → <c>ScreenToWorldPoint</c> →
    /// <c>Physics2D.OverlapPoint</c> → range check) just to learn which item was meant —
    /// fragile, and duplicated logic that would drift from the game's. By the time
    /// <c>PickUpItem</c> runs the game has already resolved the item and the slot, so the
    /// patch is small and hard to get subtly wrong.
    ///
    /// Client behaviour is the important part: the pickup is **cancelled locally** and
    /// sent to the host as a request. Letting it proceed optimistically would mean two
    /// players can take the same item and both keep it — the classic co-op duplication
    /// bug, which in a game this item-tight is a ruined run.
    /// </summary>
    [HarmonyPatch(typeof(Body), nameof(Body.PickUpItem))]
    internal static class PickUpItemPatch
    {
        /// <summary>
        /// Set while an authoritative result is being applied, so the patch does not
        /// re-enter and bounce the same pickup back to the host forever.
        /// </summary>
        internal static bool ApplyingAuthoritative;

        private static bool Prefix(Body __instance, Item item, int slot)
        {
            try
            {
                if (ApplyingAuthoritative) return true;

                var runner = CoopRunner.Active;
                if (runner == null || !runner.InSession) return true;   // single player
                if (item == null) return true;

                // Only the local player's pickups are ours to arbitrate. The host also
                // drives remote bodies through this same method when applying results.
                var localBody = PlayerCamera.main != null ? PlayerCamera.main.body : null;
                if (__instance != localBody) return true;

                if (runner.IsHost)
                {
                    // The host is authoritative: let it happen, then tell everyone.
                    runner.AnnounceHostPickup(item, slot);
                    return true;
                }

                // Client: ask, do not act. The host's answer re-drives this method with
                // ApplyingAuthoritative set. If no request could be sent (the item is not
                // a networked entity) allow the local pickup rather than leaving the
                // player unable to act.
                return !runner.RequestPickup(item, slot);
            }
            catch (Exception e)
            {
                // Never let a networking fault break the game's own item handling.
                CoopPlugin.Warn("pickup patch error (allowing local pickup): " + e.Message);
                return true;
            }
        }
    }

    /// <summary>
    /// Routes item drops through the host, completing the pickup/drop pair.
    ///
    /// Without this, a client that picks an item up (removing it from the host's world)
    /// and then drops it leaves an item existing ONLY on that client — the host, which is
    /// supposed to be authoritative over every world item, has lost track of it entirely.
    /// That is a live desync, not merely a missing feature.
    ///
    /// Patches the <c>Item</c> overload, not the <c>int</c> one. <c>DropItem(int)</c> only
    /// resolves the slot and calls <c>DropItem(Item)</c>, and most of the game calls the
    /// <c>Item</c> overload directly — dragging an item out of the inventory UI, swapping
    /// a worn item, death. Hooking the slot overload caught a minority of real drops and
    /// made drop sync look intermittent for reasons unrelated to the network code.
    /// </summary>
    /// <summary>
    /// Makes a throw's launch velocity available to the drop it is about to perform.
    ///
    /// <c>ThrowItem</c> calls <c>DropItem</c> first and assigns <c>rb.velocity</c> several
    /// lines later, so anything hooked on the drop sees a stationary item. On the host
    /// that was merely late — the announce went out saying zero and the other player
    /// watched thrown items drop straight down. On a client it was fatal twice over: the
    /// drop is cancelled and asked of the host, so the velocity lands on an item still in
    /// the player's hand where it does nothing, AND the host's copy — which owns the item
    /// for motion sync, because the host allocates its id — is created at rest and then
    /// streams that rest back over the thrower's own copy. The throw was not just
    /// invisible to others; it was cancelled a frame after it began.
    ///
    /// So the vector is computed here, in a prefix, before the drop goes out.
    /// <c>ThrowVelocity</c> is pure — aim, jump speed, strength, weight and the body's own
    /// velocity, no randomness — so computing it early gives the same answer the game is
    /// about to compute. The one input that does move is the item's position, and only on
    /// the host, where <c>DropItem</c> unparents it first; that shifts the aim vector by a
    /// fraction of a unit on a throw that is about to travel many.
    ///
    /// The angular spin beside it is <c>Random.Range(-800, 800)</c> and is deliberately
    /// NOT reproduced this way — each peer rolls its own, because a tumbling item's spin
    /// is not worth a wire field.
    /// </summary>
    [HarmonyPatch(typeof(Body), nameof(Body.ThrowItem))]
    internal static class ThrowItemPatch
    {
        /// <summary>
        /// The launch velocity of the throw currently running, or zero when the drop in
        /// progress is an ordinary one. Read by <see cref="DropItemPatch"/> during the
        /// call and cleared immediately after.
        /// </summary>
        internal static Vector2 PendingLaunch;

        private static void Prefix(Body __instance, float force)
        {
            PendingLaunch = Vector2.zero;
            try
            {
                var runner = CoopRunner.Active;
                if (runner == null || !runner.InSession) return;

                var localBody = PlayerCamera.main != null ? PlayerCamera.main.body : null;
                if (__instance != localBody) return;

                // Nothing in hand: the game will do nothing either.
                var item = __instance.GetItem(__instance.handSlot);
                if (item == null) return;

                PendingLaunch = __instance.ThrowVelocity(item, Mathf.Clamp01(force));
            }
            catch (Exception e)
            {
                PendingLaunch = Vector2.zero;
                CoopPlugin.Warn("throw prefix error: " + e.Message);
            }
        }

        /// <summary>
        /// Cleared unconditionally, so an ordinary drop later in the frame cannot be
        /// mistaken for a throw.
        /// </summary>
        private static void Postfix() => PendingLaunch = Vector2.zero;
    }

    [HarmonyPatch(typeof(Body), nameof(Body.DropItem), new[] { typeof(Item) })]
    internal static class DropItemPatch
    {
        private static bool Prefix(Body __instance, Item item)
        {
            try
            {
                if (PickUpItemPatch.ApplyingAuthoritative) return true;

                var runner = CoopRunner.Active;
                if (runner == null || !runner.InSession) return true;

                var localBody = PlayerCamera.main != null ? PlayerCamera.main.body : null;
                if (__instance != localBody) return true;
                if (item == null) return true;

                // The original does nothing unless the item is actually in a slot — worn
                // items are not, so several call sites are no-ops. Announcing a drop that
                // is not going to happen would create a world item out of nothing.
                if (!__instance.HoldingItem(item)) return true;

                int slot = __instance.SlotOf(item);

                // Zero for an ordinary drop; set by ThrowItemPatch for a throw. It has to
                // be computed before this call rather than read back afterwards, because
                // ThrowItem assigns the velocity only *after* DropItem has returned — by
                // which time the announce below has already gone out saying the item is
                // stationary.
                var launch = ThrowItemPatch.PendingLaunch;

                if (runner.IsHost)
                {
                    // The host's drop is authoritative; let it happen and announce the
                    // resulting world item so clients can spawn their own copy.
                    runner.AnnounceHostDrop(item, slot, launch);
                    return true;
                }

                return !runner.RequestDrop(item, slot, launch);
            }
            catch (Exception e)
            {
                CoopPlugin.Warn("drop patch error (allowing local drop): " + e.Message);
                return true;
            }
        }
    }

    /// <summary>Applies authoritative pickup and drop results locally.</summary>
    internal static class ItemTransactions
    {
        /// <summary>
        /// Applies a granted pickup.
        ///
        /// If the actor is the local player the item goes into their inventory; if it is
        /// a remote player the item is simply removed from this peer's world, because
        /// remote inventories are not simulated here — only the host holds those.
        /// </summary>
        internal static void ApplyGrantedPickup(NetEntityRegistry registry,
                                                InteractionResultMessage result,
                                                bool actorIsLocal,
                                                Action<string> log)
        {
            if (!registry.TryGet(result.EntityId, out var go))
            {
                // Normal when a client never spawned the entity, or already removed it.
                log?.Invoke("pickup " + result.EntityId + " granted but entity is not present locally");
                return;
            }

            var item = go.GetComponent<Item>();

            if (!actorIsLocal)
            {
                registry.Remove(result.EntityId);
                UnityEngine.Object.Destroy(go);
                log?.Invoke("peer " + result.ActorPeerId + " took entity " + result.EntityId +
                            "; removed from our world");
                return;
            }

            if (item == null)
            {
                log?.Invoke("entity " + result.EntityId + " has no Item component; cannot pick up");
                return;
            }

            var body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
            if (body == null) return;

            PickUpItemPatch.ApplyingAuthoritative = true;
            try
            {
                body.PickUpItem(item, result.Slot, force: true);
                registry.Remove(result.EntityId);
                log?.Invoke("picked up entity " + result.EntityId + " into slot " + result.Slot);
            }
            catch (Exception e)
            {
                log?.Invoke("failed to apply pickup " + result.EntityId + ": " + e.Message);
            }
            finally
            {
                PickUpItemPatch.ApplyingAuthoritative = false;
            }
        }

        /// <summary>
        /// Applies a granted drop: spawns the item into the world at the host's chosen
        /// position and registers it under the host's id, so every peer thereafter refers
        /// to the same object.
        ///
        /// The local actor is skipped: their own drop already produced an item, and
        /// spawning a second one would duplicate it.
        /// </summary>
        internal static void ApplyGrantedDrop(NetEntityRegistry registry,
                                              InteractionResultMessage result,
                                              bool actorIsLocal,
                                              Action<string> log)
        {
            if (actorIsLocal) return;

            // Already ours: do not make a second one.
            //
            // The host reaches here for a client's drop, having ALREADY created the world
            // item while arbitrating the request — its own broadcast comes back to it, and
            // the host is not the actor, so without this it built a duplicate and
            // re-registered the id over the first copy. The orphan stayed in the world,
            // unaddressable, and only became obvious once throwing gave the two copies
            // different velocities: one flew and one sat there. Worse, the registered copy
            // was the stationary one, so motion sync then streamed *that* over the
            // thrower's own item and cancelled every subsequent throw.
            //
            // Written as an id check rather than "am I the host?" because it also covers
            // the other way to get here twice — a duplicate delivery of the same result.
            if (registry.TryGet(result.EntityId, out _))
            {
                log?.Invoke("drop " + result.EntityId + " is already registered here; not spawning again");
                return;
            }

            if (string.IsNullOrEmpty(result.ItemId))
            {
                log?.Invoke("drop result carried no item id; nothing to spawn");
                return;
            }

            try
            {
                var pos = new Vector2(result.PositionX, result.PositionY);
                var created = Utils.Create(result.ItemId, pos, 0f);
                if (created == null)
                {
                    log?.Invoke("could not create dropped item '" + result.ItemId + "'");
                    return;
                }

                created.name = WorldSync.SpawnedNamePrefix + result.ItemId;
                registry.Register(result.EntityId, created, result.ItemId);
                log?.Invoke("peer " + result.ActorPeerId + " dropped '" + result.ItemId +
                            "' as entity " + result.EntityId);
            }
            catch (Exception e)
            {
                log?.Invoke("failed to apply drop '" + result.ItemId + "': " + e.Message);
            }
        }

        /// <summary>
        /// Host-side validation. Deliberately conservative: it is far better to refuse a
        /// legitimate pickup (the player simply clicks again) than to grant a duplicate.
        /// </summary>
        internal static InteractionFailure Validate(NetEntityRegistry registry,
                                                    InteractionRequestMessage request,
                                                    Vector2 actorPosition,
                                                    float maxRange)
        {
            if (!registry.TryGet(request.EntityId, out var go))
                return InteractionFailure.AlreadyTaken;

            if (go.GetComponent<Item>() == null)
                return InteractionFailure.NotPermitted;

            // Revision guard: the client acted on a view of this entity that has since
            // changed, so its decision may no longer make sense.
            if (request.ExpectedRevision != registry.RevisionOf(request.EntityId))
                return InteractionFailure.RevisionMismatch;

            float distance = Vector2.Distance(actorPosition, go.transform.position);
            if (distance > maxRange)
                return InteractionFailure.OutOfRange;

            return InteractionFailure.None;
        }
    }
}
