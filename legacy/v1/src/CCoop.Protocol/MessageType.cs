namespace CCoop.Protocol
{
    /// <summary>
    /// Every message on the wire. Values are explicit and never reused: an id that
    /// once meant something must not be recycled for a different meaning, or peers on
    /// mismatched builds will silently misinterpret each other rather than failing.
    ///
    /// <see cref="MessageTypes.IsDefined"/> is the authority on validity — a decoder
    /// must reject anything it does not know rather than pass it along.
    /// </summary>
    public enum MessageType : ushort
    {
        // ---- session lifecycle (DeliveryClass.Control) ----
        /// <summary>Client -> host. Compatibility fingerprint and requested name.</summary>
        Hello = 1,
        /// <summary>Host -> client. Accepted; assigns a peer id and session id.</summary>
        Welcome = 2,
        /// <summary>Host -> client. Refused, with a machine and human readable reason.</summary>
        Reject = 3,
        /// <summary>Host -> all. Another peer joined.</summary>
        PeerJoined = 4,
        /// <summary>Host -> all. A peer left.</summary>
        PeerLeft = 5,
        /// <summary>Either direction. Orderly shutdown.</summary>
        Disconnect = 6,
        Ping = 7,
        Pong = 8,

        // ---- layer / world (Control + Bulk) ----
        /// <summary>Host -> all. Run configuration for the layer about to load.</summary>
        LayerPrepare = 20,
        /// <summary>Client -> host. Scene loaded and snapshot applied.</summary>
        LayerReady = 21,
        /// <summary>Host -> all. Begin play.</summary>
        LayerCommit = 22,
        /// <summary>
        /// Host -> client, fragmented. The authoritative world. Required because the
        /// game has no world seed, so clients cannot regenerate the host's layer.
        /// </summary>
        WorldSnapshot = 23,
        /// <summary>Client -> host. Request a fresh world snapshot after desync.</summary>
        WorldResyncRequest = 24,
        /// <summary>
        /// Any peer -> all. A batch of terrain edits. The world is transferred once and
        /// would otherwise never change again on the peers who did not make the edit, so
        /// two players mining the same wall diverge permanently.
        /// </summary>
        BlockDelta = 25,
        /// <summary>
        /// Client -> host as a request, host -> all as the answer. The speed everyone
        /// runs at. <c>Time.timeScale</c> is global to a process and drives physics,
        /// animation and AI, so one peer speeding up desynchronises every system at once.
        /// </summary>
        TimeScale = 26,
        /// <summary>
        /// Any peer -> all. How damaged a block looks. Purely cosmetic: the crack overlay
        /// and hit sound, which otherwise exist only for the player swinging, so everyone
        /// else sees walls vanish with no warning and no feedback.
        /// </summary>
        BlockHit = 27,
        /// <summary>
        /// Host -> all. A layer-wide event such as an earthquake. Only the schedule
        /// travels; each peer already produces the consequences for its own player.
        /// </summary>
        WorldEvent = 28,

        // ---- players ----
        PlayerSpawn = 40,
        PlayerDespawn = 41,
        /// <summary>Owner -> host -> others. Position, velocity, facing, pose.</summary>
        PlayerTransform = 42,
        /// <summary>Host -> owner. Movement rejected; snap back.</summary>
        PlayerCorrection = 43,
        /// <summary>
        /// Owner -> host -> others. A swing, for its animation and sound only. The damage
        /// it does already travels as terrain edits and entity damage.
        /// </summary>
        PlayerAttack = 44,
        /// <summary>
        /// Owner -> host -> others. Wounds, dirt and blood on a body. Drawn by a shader
        /// from values only the owning peer's game computes, so without this a remote
        /// player looks exactly as they did when their proxy was cloned.
        /// </summary>
        PlayerAppearance = 45,
        /// <summary>
        /// Owner -> host -> others. What a player is holding and wearing. Proxies destroy
        /// the items they inherit from the clone — a live one could be taken out of a
        /// remote player's hands — so without this everyone else looks empty-handed.
        /// </summary>
        PlayerEquipment = 46,

        // ---- generic entities ----
        EntitySpawn = 60,
        EntityDespawn = 61,
        /// <summary>
        /// Any peer -> all. Damage dealt to world objects. Sent as an amount rather than
        /// a resulting health so that two players hitting the same crate accumulate:
        /// absolute values would overwrite each other and only the larger blow would
        /// count.
        /// </summary>
        EntityStateDelta = 62,
        /// <summary>
        /// Owner -> all. Where a loose item is and how it is moving. A spawn describes
        /// only the moment an item appeared; without this each peer then simulates its
        /// physics alone and they settle in different places.
        /// </summary>
        EntityMotion = 63,

        // ---- transactions ----
        /// <summary>Client -> host. Intent, carrying the entity revision it observed.</summary>
        InteractionRequest = 80,
        /// <summary>Host -> all. Committed, or refused with a reason.</summary>
        InteractionResult = 81,
        InventoryRequest = 82,
        InventoryDelta = 83,
        InventorySnapshot = 84,

        // ---- body / medical ----
        BodyActionRequest = 100,
        BodyStateDelta = 101,
        BodyStateSnapshot = 102,
        DamageEvent = 103,
        DeathEvent = 104,
        /// <summary>Client -> host. Minigame outcome, for host validation.</summary>
        MinigameResult = 105,

        // ---- diagnostics ----
        StateHash = 120,
        DesyncReport = 121,
    }

    /// <summary>Validation helpers for <see cref="MessageType"/>.</summary>
    public static class MessageTypes
    {
        /// <summary>
        /// True only for values this build defines. Deliberately a switch rather than
        /// <c>Enum.IsDefined</c>: no boxing, no reflection, and no allocation on a path
        /// that runs for every inbound packet including hostile ones.
        /// </summary>
        public static bool IsDefined(ushort value)
        {
            switch ((MessageType)value)
            {
                case MessageType.Hello:
                case MessageType.Welcome:
                case MessageType.Reject:
                case MessageType.PeerJoined:
                case MessageType.PeerLeft:
                case MessageType.Disconnect:
                case MessageType.Ping:
                case MessageType.Pong:
                case MessageType.LayerPrepare:
                case MessageType.LayerReady:
                case MessageType.LayerCommit:
                case MessageType.WorldSnapshot:
                case MessageType.WorldResyncRequest:
                case MessageType.BlockDelta:
                case MessageType.TimeScale:
                case MessageType.BlockHit:
                case MessageType.WorldEvent:
                case MessageType.PlayerSpawn:
                case MessageType.PlayerDespawn:
                case MessageType.PlayerTransform:
                case MessageType.PlayerCorrection:
                case MessageType.PlayerAttack:
                case MessageType.PlayerAppearance:
                case MessageType.PlayerEquipment:
                case MessageType.EntitySpawn:
                case MessageType.EntityDespawn:
                case MessageType.EntityStateDelta:
                case MessageType.EntityMotion:
                case MessageType.InteractionRequest:
                case MessageType.InteractionResult:
                case MessageType.InventoryRequest:
                case MessageType.InventoryDelta:
                case MessageType.InventorySnapshot:
                case MessageType.BodyActionRequest:
                case MessageType.BodyStateDelta:
                case MessageType.BodyStateSnapshot:
                case MessageType.DamageEvent:
                case MessageType.DeathEvent:
                case MessageType.MinigameResult:
                case MessageType.StateHash:
                case MessageType.DesyncReport:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>The delivery guarantee each message requires.</summary>
        public static DeliveryClass DeliveryFor(MessageType type)
        {
            switch (type)
            {
                case MessageType.PlayerTransform:
                // Appearance is a stream too, and a slow one: a lost update is corrected
                // by the next within half a second.
                case MessageType.PlayerAppearance:
                case MessageType.PlayerEquipment:
                // An item in flight is a stream like a player is: only the newest
                // position is worth having. Note the sender deliberately deviates for
                // the one message that says an item has come to REST, which goes
                // reliably — losing that leaves two peers disagreeing about where the
                // item finished, and nothing later would correct it.
                case MessageType.EntityMotion:
                    return DeliveryClass.Snapshots;

                case MessageType.WorldSnapshot:
                case MessageType.InventorySnapshot:
                case MessageType.BodyStateSnapshot:
                    return DeliveryClass.Bulk;

                // Damage rides with spawn and despawn rather than on the snapshot
                // channel. It has to be reliable — a dropped blow is damage that never
                // happened on one peer, and nothing later would correct it — and it has
                // to stay ordered against the despawn that follows the killing hit, or
                // the other peer sees the death before the damage that caused it and
                // claims the destruction as its own.
                case MessageType.EntityStateDelta:
                case MessageType.EntitySpawn:
                case MessageType.EntityDespawn:
                case MessageType.PlayerSpawn:
                case MessageType.PlayerDespawn:
                    return DeliveryClass.EntityLifecycle;

                // Terrain edits are reliable-ordered for the same reason interactions
                // are: one dropped edit is a hole (or a wall) that exists on only one
                // peer for the rest of the layer, and nothing later would correct it.
                case MessageType.BlockDelta:
                // Cosmetic, but ordered with the edits it precedes: a crack arriving
                // after the block it belongs to was cleared would be drawn on empty air.
                case MessageType.BlockHit:
                case MessageType.PlayerAttack:
                case MessageType.InteractionRequest:
                case MessageType.InteractionResult:
                case MessageType.InventoryRequest:
                case MessageType.InventoryDelta:
                case MessageType.BodyActionRequest:
                case MessageType.BodyStateDelta:
                case MessageType.DamageEvent:
                case MessageType.DeathEvent:
                case MessageType.MinigameResult:
                    return DeliveryClass.Transactions;

                case MessageType.StateHash:
                case MessageType.DesyncReport:
                    return DeliveryClass.Diagnostics;

                default:
                    return DeliveryClass.Control;
            }
        }
    }
}
