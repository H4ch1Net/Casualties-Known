using System;
using CCoop.Protocol;
using CCoop.Protocol.Messages;
using CCoop.Transport;

namespace CCoop.Core
{
    /// <summary>
    /// Joining side of a co-op session.
    ///
    /// Sends the compatibility fingerprint on connect and then follows the host's lead:
    /// the host owns world state, so a client that disagrees about the build is refused
    /// rather than allowed to desync quietly.
    /// </summary>
    public sealed class ClientSession : CoopSession
    {
        private readonly HelloMessage _hello;

        /// <summary>Set when the host refuses us, for the UI to show.</summary>
        public RejectMessage Rejection { get; private set; }

        public ClientSession(INetworkTransport transport, HelloMessage hello)
            : base(transport)
        {
            _hello = hello ?? throw new ArgumentNullException(nameof(hello));
            Status = SessionStatus.Connecting;
        }

        protected override void OnPeerConnected(PeerId peer)
        {
            Status = SessionStatus.Handshaking;
            var bytes = _hello.Encode(NextSequence());
            Transport.Send(PeerId.Host, bytes, 0, bytes.Length, DeliveryClass.Control);
            Log("connected; handshake sent");
        }

        protected override void OnPeerDisconnected(PeerId peer, DisconnectReason reason)
        {
            // A rejection already explains itself; do not overwrite it with the
            // disconnect that necessarily follows.
            if (Status != SessionStatus.Rejected)
                Status = reason == DisconnectReason.LocalRequest
                    ? SessionStatus.Disconnected
                    : SessionStatus.Failed;

            foreach (var id in new System.Collections.Generic.List<byte>(PlayerIds()))
                RemovePlayer(id);

            Log("disconnected from host: " + reason);
        }

        private System.Collections.Generic.IEnumerable<byte> PlayerIds()
        {
            foreach (var p in Players) yield return p.Key;
        }

        protected override void HandleMessage(PeerId from, PacketHeader header, ref PacketReader reader)
        {
            switch (header.Type)
            {
                case MessageType.Welcome:
                    if (WelcomeMessage.TryDecode(ref reader, header, out var welcome))
                    {
                        SessionId = welcome.SessionId;
                        LocalPeerId = welcome.AssignedPeerId;
                        Status = SessionStatus.Established;
                        Log("welcomed as peer " + LocalPeerId + " into session " + SessionId);
                        RaiseEstablished();
                    }
                    break;

                case MessageType.Reject:
                    if (RejectMessage.TryDecode(ref reader, out var reject))
                    {
                        Rejection = reject;
                        Status = SessionStatus.Rejected;
                        Log("rejected: " + reject.Describe());
                        RaiseRejected(reject);
                    }
                    break;

                case MessageType.PlayerSpawn:
                    if (PlayerSpawnMessage.TryDecode(ref reader, out var spawn))
                    {
                        // Never create a proxy for ourselves; we already have a body.
                        if (spawn.PeerId != LocalPeerId)
                            AddPlayer(spawn.PeerId, spawn.PlayerName);
                    }
                    break;

                case MessageType.PlayerDespawn:
                    if (PlayerDespawnMessage.TryDecode(ref reader, out var despawn))
                        RemovePlayer(despawn.PeerId);
                    break;

                case MessageType.PlayerTransform:
                    if (header.SenderPeerId == LocalPeerId) return; // our own, relayed back
                    if (PlayerTransformMessage.TryDecode(ref reader, out var transform))
                    {
                        // Movement can outrun the spawn message on a lossy link, so
                        // materialise the player rather than dropping the update.
                        if (!Players.ContainsKey(header.SenderPeerId))
                            AddPlayer(header.SenderPeerId, "player" + header.SenderPeerId);

                        ApplyTransform(header.SenderPeerId, header.Sequence, transform);
                    }
                    break;

                case MessageType.LayerPrepare:
                    if (LayerPrepareMessage.TryDecode(ref reader, out var prepare))
                    {
                        // A new transfer supersedes any in flight: the host has moved on
                        // to another layer and the old partial data is worthless.
                        _assembler = new WorldSnapshotAssembler(prepare);
                        Log("world transfer " + prepare.TransferId + " incoming: " +
                            prepare.ChunkCount + " chunks, " + prepare.CompressedLength + " B");
                        RaiseWorldSnapshotProgress(0f);
                    }
                    break;

                case MessageType.WorldSnapshot:
                    HandleWorldChunk(ref reader);
                    break;

                case MessageType.BlockDelta:
                    // Our own edits come back through the host's relay only to other
                    // clients, but guard anyway: re-applying them is harmless, yet
                    // re-broadcasting them would be an endless loop.
                    if (header.SenderPeerId == LocalPeerId) return;
                    if (BlockDeltaMessage.TryDecode(ref reader, out var deltas))
                        RaiseBlockDeltas(header.SenderPeerId, deltas);
                    break;

                case MessageType.WorldEvent:
                    if (WorldEventMessage.TryDecode(ref reader, out var worldEvent))
                        RaiseWorldEvent(worldEvent);
                    break;

                case MessageType.PlayerAttack:
                    if (header.SenderPeerId == LocalPeerId) return;
                    if (PlayerAttackMessage.TryDecode(ref reader, out var attack))
                        RaisePlayerAttacked(header.SenderPeerId, attack);
                    break;

                case MessageType.BlockHit:
                    if (header.SenderPeerId == LocalPeerId) return;
                    if (BlockHitMessage.TryDecode(ref reader, out var hits))
                        RaiseBlockHits(header.SenderPeerId, hits);
                    break;

                case MessageType.EntitySpawn:
                    if (header.SenderPeerId == LocalPeerId) return;
                    if (EntitySpawnMessage.TryDecode(ref reader, out var spawned))
                        RaiseEntitySpawned(header.SenderPeerId, spawned);
                    break;

                case MessageType.EntityDespawn:
                    if (header.SenderPeerId == LocalPeerId) return;
                    if (EntityDespawnMessage.TryDecode(ref reader, out var despawned))
                        RaiseEntityDespawned(header.SenderPeerId, despawned);
                    break;

                case MessageType.EntityStateDelta:
                    if (header.SenderPeerId == LocalPeerId) return;
                    if (EntityDamageMessage.TryDecode(ref reader, out var damage))
                        RaiseEntityDamaged(header.SenderPeerId, damage);
                    break;

                case MessageType.PlayerAppearance:
                    if (header.SenderPeerId == LocalPeerId) return;
                    if (PlayerAppearanceMessage.TryDecode(ref reader, out var look))
                        RaisePlayerAppearance(header.SenderPeerId, look);
                    break;

                case MessageType.PlayerEquipment:
                    if (header.SenderPeerId == LocalPeerId) return;
                    if (PlayerEquipmentMessage.TryDecode(ref reader, out var kit))
                        RaisePlayerEquipment(header.SenderPeerId, kit);
                    break;

                case MessageType.EntityMotion:
                    if (header.SenderPeerId == LocalPeerId) return;
                    if (EntityMotionMessage.TryDecode(ref reader, out var motion))
                        RaiseEntityMoved(header.SenderPeerId, motion);
                    break;

                case MessageType.TimeScale:
                    // Applied even when it is the answer to our own request: the host
                    // may have decided differently, and its word is the session's.
                    if (TimeScaleMessage.TryDecode(ref reader, out var speed))
                        RaiseTimeScaleChanged(speed);
                    break;

                case MessageType.InteractionResult:
                    if (InteractionResultMessage.TryDecode(ref reader, out var result))
                        RaiseInteractionResolved(result);
                    break;

                case MessageType.Pong:
                    break;

                default:
                    Log("client ignoring " + header.Type);
                    break;
            }
        }

        private WorldSnapshotAssembler _assembler;

        private void HandleWorldChunk(ref PacketReader reader)
        {
            if (!WorldSnapshotChunkMessage.TryDecode(ref reader, out var chunk)) return;

            if (_assembler == null)
            {
                // Chunks without a preceding LayerPrepare cannot be sized or verified.
                Log("dropping world chunk " + chunk.ChunkIndex + ": no transfer announced");
                return;
            }

            if (!_assembler.Accept(chunk, out var acceptError))
            {
                Log("rejected world chunk: " + acceptError);
                return;
            }

            RaiseWorldSnapshotProgress(_assembler.Progress);
            if (!_assembler.IsComplete) return;

            var prepare = _assembler.Prepare;
            if (_assembler.TryFinish(out var payload, out var error))
            {
                _assembler = null;

                if (!WorldPayload.TrySplit(payload, prepare.TerrainLength, prepare.FluidLength,
                                           out var terrain, out var fluid, out var entityBytes,
                                           out var splitError))
                {
                    Log("world transfer " + prepare.TransferId + " unusable: " + splitError);
                    SendLayerReady(prepare.TransferId, false, splitError);
                    return;
                }

                EntityManifest manifest = null;
                if (entityBytes.Length > 0 &&
                    !EntityManifest.TryDeserialize(entityBytes, out manifest, out var manifestError))
                {
                    // Terrain without contents is worse than a clean failure: the player
                    // would stand in an empty but convincing world.
                    Log("world transfer " + prepare.TransferId + " entity manifest invalid: " + manifestError);
                    SendLayerReady(prepare.TransferId, false, manifestError);
                    return;
                }

                Log("world transfer " + prepare.TransferId + " verified (terrain " + terrain.Length +
                    " B, fluid " + fluid.Length + " B, " +
                    (manifest?.Entities.Count ?? 0) + " entities)");

                RaiseWorldSnapshotReady(new LayerSnapshot
                {
                    Prepare = prepare,
                    Terrain = terrain,
                    Fluid = fluid,
                    Entities = manifest,
                });
            }
            else
            {
                // Do not apply a partially-verified world; a corrupt one would surface
                // much later as inexplicable terrain bugs.
                Log("world transfer " + prepare.TransferId + " FAILED: " + error);
                _assembler = null;
                SendLayerReady(prepare.TransferId, false, error);
            }
        }

        /// <summary>Asks the host to perform an interaction on our behalf.</summary>
        public void SendInteractionRequest(InteractionRequestMessage request)
        {
            if (request == null || Status != SessionStatus.Established) return;
            var bytes = request.Encode(LocalPeerId, SessionId, NextSequence(), Tick);
            Transport.Send(PeerId.Host, bytes, 0, bytes.Length, DeliveryClass.Transactions);
        }

        /// <summary>Tells the host whether the layer was applied successfully.</summary>
        public void SendLayerReady(uint transferId, bool success, string detail = "")
        {
            var bytes = new LayerReadyMessage
            {
                TransferId = transferId,
                Success = success,
                Detail = detail ?? string.Empty,
            }.Encode(LocalPeerId, SessionId, NextSequence(), Tick);

            Transport.Send(PeerId.Host, bytes, 0, bytes.Length, DeliveryClass.Control);
        }

        public override void SendLocalTransform(PlayerTransformMessage transform)
        {
            if (transform == null) return;
            if (Status != SessionStatus.Established) return;

            var bytes = transform.Encode(LocalPeerId, SessionId, NextSequence(), Tick);
            Transport.Send(PeerId.Host, bytes, 0, bytes.Length, DeliveryClass.Snapshots);
        }

        public override void SendBlockDeltas(BlockDeltaMessage deltas)
        {
            if (deltas == null || deltas.Edits.Count == 0) return;
            if (Status != SessionStatus.Established) return;

            var bytes = deltas.Encode(LocalPeerId, SessionId, NextSequence(), Tick);
            Transport.Send(PeerId.Host, bytes, 0, bytes.Length, DeliveryClass.Transactions);
        }

        public override void SendEntitySpawn(EntitySpawnMessage spawn)
        {
            if (spawn == null) return;
            if (Status != SessionStatus.Established) return;

            var bytes = spawn.Encode(LocalPeerId, SessionId, NextSequence(), Tick);
            Transport.Send(PeerId.Host, bytes, 0, bytes.Length, DeliveryClass.EntityLifecycle);
        }

        public override void SendTimeScale(TimeScaleMessage speed)
        {
            if (speed == null || Status != SessionStatus.Established) return;
            if (!TimeScaleMessage.IsShareable(speed.Speed)) return;

            var bytes = speed.Encode(LocalPeerId, SessionId, NextSequence(), Tick);
            Transport.Send(PeerId.Host, bytes, 0, bytes.Length, DeliveryClass.Control);
        }

        public override void SendPlayerAttack(PlayerAttackMessage attack)
        {
            if (attack == null || Status != SessionStatus.Established) return;
            var bytes = attack.Encode(LocalPeerId, SessionId, NextSequence(), Tick);
            Transport.Send(PeerId.Host, bytes, 0, bytes.Length, DeliveryClass.Transactions);
        }

        public override void SendBlockHits(BlockHitMessage hits)
        {
            if (hits == null || hits.Cracks.Count == 0) return;
            if (Status != SessionStatus.Established) return;

            var bytes = hits.Encode(LocalPeerId, SessionId, NextSequence(), Tick);
            Transport.Send(PeerId.Host, bytes, 0, bytes.Length, DeliveryClass.Transactions);
        }

        public override void SendEntityDamage(EntityDamageMessage damage)
        {
            if (damage == null || damage.Damages.Count == 0) return;
            if (Status != SessionStatus.Established) return;

            var bytes = damage.Encode(LocalPeerId, SessionId, NextSequence(), Tick);
            Transport.Send(PeerId.Host, bytes, 0, bytes.Length, DeliveryClass.EntityLifecycle);
        }

        public override void SendPlayerAppearance(PlayerAppearanceMessage appearance)
        {
            if (appearance == null) return;
            if (Status != SessionStatus.Established) return;

            var bytes = appearance.Encode(LocalPeerId, SessionId, NextSequence(), Tick);
            Transport.Send(PeerId.Host, bytes, 0, bytes.Length, DeliveryClass.Snapshots);
        }

        public override void SendPlayerEquipment(PlayerEquipmentMessage equipment)
        {
            if (equipment == null) return;
            if (Status != SessionStatus.Established) return;

            var bytes = equipment.Encode(LocalPeerId, SessionId, NextSequence(), Tick);
            Transport.Send(PeerId.Host, bytes, 0, bytes.Length, DeliveryClass.Snapshots);
        }

        public override void SendEntityMotion(EntityMotionMessage motion)
        {
            if (motion == null || motion.Motions.Count == 0) return;
            if (Status != SessionStatus.Established) return;

            var bytes = motion.Encode(LocalPeerId, SessionId, NextSequence(), Tick);
            Transport.Send(PeerId.Host, bytes, 0, bytes.Length,
                           motion.AtRest ? DeliveryClass.EntityLifecycle
                                         : DeliveryClass.Snapshots);
        }

        public override void SendEntityDespawn(EntityDespawnMessage despawn)
        {
            if (despawn == null) return;
            if (Status != SessionStatus.Established) return;

            var bytes = despawn.Encode(LocalPeerId, SessionId, NextSequence(), Tick);
            Transport.Send(PeerId.Host, bytes, 0, bytes.Length, DeliveryClass.EntityLifecycle);
        }
    }
}
