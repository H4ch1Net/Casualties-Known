using System;
using System.Collections.Generic;
using CCoop.Protocol;
using CCoop.Protocol.Messages;
using CCoop.Transport;

namespace CCoop.Core
{
    /// <summary>
    /// Authoritative side of a co-op session.
    ///
    /// Admits clients, assigns identity, and relays movement. Everything a client
    /// asserts about the world will eventually be validated here; for now the host
    /// owns admission and player presence.
    /// </summary>
    public sealed class HostSession : CoopSession
    {
        private readonly HostRequirements _requirements;
        private readonly string _hostName;
        private readonly Dictionary<byte, bool> _handshaked = new Dictionary<byte, bool>();

        public HostSession(INetworkTransport transport, HostRequirements requirements, string hostName)
            : base(transport)
        {
            _requirements = requirements ?? throw new ArgumentNullException(nameof(requirements));
            _hostName = hostName ?? "host";

            SessionId = NewSessionId();
            LocalPeerId = ProtocolConstants.HostPeerId;
            Status = SessionStatus.Established;
        }

        /// <summary>
        /// Session ids exist to stop a stale packet from a previous session being
        /// accepted after a reconnect, so they only need to differ between runs.
        /// </summary>
        private static uint NewSessionId()
        {
            var t = (uint)DateTime.UtcNow.Ticks;
            return t == 0 ? 1u : t;
        }

        protected override void OnPeerConnected(PeerId peer)
        {
            // Connected at the transport level, but not yet a player: nothing is
            // announced until the Hello passes validation.
            _handshaked[peer.Value] = false;
            Log("peer " + peer.Value + " connected, awaiting handshake");
        }

        protected override void OnPeerDisconnected(PeerId peer, DisconnectReason reason)
        {
            _handshaked.Remove(peer.Value);

            if (Players.ContainsKey(peer.Value))
            {
                RemovePlayer(peer.Value);

                var despawn = new PlayerDespawnMessage { PeerId = peer.Value }
                    .Encode(SessionId, NextSequence(), Tick);
                Transport.Broadcast(despawn, 0, despawn.Length, DeliveryClass.EntityLifecycle);
            }
            Log("peer " + peer.Value + " disconnected: " + reason);
        }

        protected override void HandleMessage(PeerId from, PacketHeader header, ref PacketReader reader)
        {
            switch (header.Type)
            {
                case MessageType.Hello:
                    HandleHello(from, ref reader);
                    break;

                case MessageType.PlayerTransform:
                    HandleTransform(from, header, ref reader);
                    break;

                case MessageType.Ping:
                    // Cheap liveness probe; echo straight back.
                    var pong = BuildPong();
                    Transport.Send(from, pong, 0, pong.Length, DeliveryClass.Control);
                    break;

                case MessageType.InteractionRequest:
                    if (!Handshaked(from)) break;
                    if (InteractionRequestMessage.TryDecode(ref reader, out var request))
                        RaiseInteractionRequested(from.Value, request);
                    break;

                case MessageType.BlockDelta:
                    if (!Handshaked(from)) break;
                    if (BlockDeltaMessage.TryDecode(ref reader, out var deltas))
                    {
                        RaiseBlockDeltas(from.Value, deltas);
                        RelayToOthers(from, deltas.Encode(from.Value, SessionId, header.Sequence, Tick),
                                      DeliveryClass.Transactions);
                    }
                    break;

                case MessageType.PlayerAttack:
                    if (!Handshaked(from)) break;
                    if (PlayerAttackMessage.TryDecode(ref reader, out var attack))
                    {
                        RaisePlayerAttacked(from.Value, attack);
                        RelayToOthers(from, attack.Encode(from.Value, SessionId, header.Sequence, Tick),
                                      DeliveryClass.Transactions);
                    }
                    break;

                case MessageType.BlockHit:
                    if (!Handshaked(from)) break;
                    if (BlockHitMessage.TryDecode(ref reader, out var hits))
                    {
                        RaiseBlockHits(from.Value, hits);
                        RelayToOthers(from, hits.Encode(from.Value, SessionId, header.Sequence, Tick),
                                      DeliveryClass.Transactions);
                    }
                    break;

                case MessageType.EntitySpawn:
                    if (!Handshaked(from)) break;
                    if (EntitySpawnMessage.TryDecode(ref reader, out var spawned))
                    {
                        RaiseEntitySpawned(from.Value, spawned);
                        RelayToOthers(from, spawned.Encode(from.Value, SessionId, header.Sequence, Tick),
                                      DeliveryClass.EntityLifecycle);
                    }
                    break;

                case MessageType.EntityDespawn:
                    if (!Handshaked(from)) break;
                    if (EntityDespawnMessage.TryDecode(ref reader, out var despawned))
                    {
                        RaiseEntityDespawned(from.Value, despawned);
                        RelayToOthers(from, despawned.Encode(from.Value, SessionId, header.Sequence, Tick),
                                      DeliveryClass.EntityLifecycle);
                    }
                    break;

                case MessageType.EntityStateDelta:
                    if (!Handshaked(from)) break;
                    if (EntityDamageMessage.TryDecode(ref reader, out var damage))
                    {
                        RaiseEntityDamaged(from.Value, damage);
                        RelayToOthers(from, damage.Encode(from.Value, SessionId, header.Sequence, Tick),
                                      DeliveryClass.EntityLifecycle);
                    }
                    break;

                case MessageType.PlayerAppearance:
                    if (!Handshaked(from)) break;
                    if (PlayerAppearanceMessage.TryDecode(ref reader, out var look))
                    {
                        RaisePlayerAppearance(from.Value, look);
                        RelayToOthers(from, look.Encode(from.Value, SessionId, header.Sequence, Tick),
                                      DeliveryClass.Snapshots);
                    }
                    break;

                case MessageType.PlayerEquipment:
                    if (!Handshaked(from)) break;
                    if (PlayerEquipmentMessage.TryDecode(ref reader, out var kit))
                    {
                        RaisePlayerEquipment(from.Value, kit);
                        RelayToOthers(from, kit.Encode(from.Value, SessionId, header.Sequence, Tick),
                                      DeliveryClass.Snapshots);
                    }
                    break;

                case MessageType.EntityMotion:
                    if (!Handshaked(from)) break;
                    if (EntityMotionMessage.TryDecode(ref reader, out var motion))
                    {
                        RaiseEntityMoved(from.Value, motion);
                        // Resting positions must arrive; a lost one leaves two peers
                        // disagreeing about where an item finished for good.
                        RelayToOthers(from, motion.Encode(from.Value, SessionId, header.Sequence, Tick),
                                      motion.AtRest ? DeliveryClass.EntityLifecycle
                                                    : DeliveryClass.Snapshots);
                    }
                    break;

                case MessageType.TimeScale:
                    // A request, not an assertion: the host decides and answers with a
                    // broadcast. Relaying it directly would let one client set the speed
                    // for a session whose host had disallowed it.
                    if (!Handshaked(from)) break;
                    if (TimeScaleMessage.TryDecode(ref reader, out var speed))
                        RaiseTimeScaleRequested(from.Value, speed);
                    break;

                case MessageType.LayerReady:
                    if (LayerReadyMessage.TryDecode(ref reader, out var ready))
                    {
                        Log("peer " + from.Value + " layer " +
                            (ready.Success ? "applied" : "FAILED: " + ready.Detail));
                        RaisePeerLayerReady(from.Value, ready.Success, ready.Detail);
                    }
                    break;

                default:
                    // Unhandled-but-valid messages are ignored rather than fatal, so a
                    // client running a newer feature set degrades instead of crashing us.
                    Log("host ignoring " + header.Type + " from " + from.Value);
                    break;
            }
        }

        /// <summary>A peer that has not completed the handshake may not change the world.</summary>
        private bool Handshaked(PeerId from) =>
            _handshaked.TryGetValue(from.Value, out bool done) && done;

        /// <summary>
        /// Forwards a client's message to the other clients, preserving the original
        /// sender id so receivers can tell whose action it was — and skipping the sender,
        /// who has already applied it locally.
        /// </summary>
        private void RelayToOthers(PeerId from, byte[] bytes, DeliveryClass delivery)
        {
            foreach (var kv in Players)
            {
                if (kv.Key == from.Value) continue;
                Transport.Send(new PeerId(kv.Key), bytes, 0, bytes.Length, delivery);
            }
        }

        private void HandleHello(PeerId from, ref PacketReader reader)
        {
            if (_handshaked.TryGetValue(from.Value, out bool done) && done)
            {
                Deny(from, RejectReason.AlreadyInSession, "");
                return;
            }

            if (!HelloMessage.TryDecode(ref reader, out var hello))
            {
                Deny(from, RejectReason.MalformedHandshake, "");
                return;
            }

            // Count the host itself towards capacity.
            int current = Players.Count + 1;
            var verdict = HandshakeValidator.Validate(hello, _requirements, current);
            if (!verdict.Accepted)
            {
                Deny(from, verdict.Reason, verdict.Detail);
                return;
            }

            _handshaked[from.Value] = true;

            var welcome = new WelcomeMessage
            {
                SessionId = SessionId,
                AssignedPeerId = from.Value,
                HostPeerCount = (byte)current,
                ServerTick = Tick,
                HostPlayerName = _hostName,
            }.Encode(NextSequence());
            Transport.Send(from, welcome, 0, welcome.Length, DeliveryClass.Control);

            var joined = AddPlayer(from.Value, hello.PlayerName);

            // Tell everyone about the newcomer...
            var spawn = new PlayerSpawnMessage { PeerId = joined.PeerId, PlayerName = joined.Name }
                .Encode(SessionId, NextSequence(), Tick);
            Transport.Broadcast(spawn, 0, spawn.Length, DeliveryClass.EntityLifecycle);

            // ...and tell the newcomer about everyone already here, including the host.
            // Without this a late joiner sees an empty world until each existing player
            // happens to move.
            var hostSpawn = new PlayerSpawnMessage
            {
                PeerId = ProtocolConstants.HostPeerId,
                PlayerName = _hostName,
            }.Encode(SessionId, NextSequence(), Tick);
            Transport.Send(from, hostSpawn, 0, hostSpawn.Length, DeliveryClass.EntityLifecycle);

            foreach (var existing in Players.Values)
            {
                if (existing.PeerId == joined.PeerId) continue;
                var s = new PlayerSpawnMessage { PeerId = existing.PeerId, PlayerName = existing.Name }
                    .Encode(SessionId, NextSequence(), Tick);
                Transport.Send(from, s, 0, s.Length, DeliveryClass.EntityLifecycle);
            }
        }

        private void Deny(PeerId to, RejectReason reason, string detail)
        {
            var bytes = new RejectMessage { Reason = reason, Detail = detail ?? string.Empty }
                .Encode(NextSequence());
            Transport.Send(to, bytes, 0, bytes.Length, DeliveryClass.Control);
            Log("rejected peer " + to.Value + ": " + reason);
        }

        private void HandleTransform(PeerId from, PacketHeader header, ref PacketReader reader)
        {
            // A peer that has not completed the handshake has no business moving.
            if (!_handshaked.TryGetValue(from.Value, out bool done) || !done) return;

            if (!PlayerTransformMessage.TryDecode(ref reader, out var transform)) return;

            ApplyTransform(from.Value, header.Sequence, transform);

            // Relay to the other clients. The sender's own peer id is preserved so
            // receivers know whose proxy to move; echoing it back to the sender would
            // fight their local prediction.
            var relay = transform.Encode(from.Value, SessionId, header.Sequence, Tick);
            foreach (var kv in Players)
            {
                if (kv.Key == from.Value) continue;
                Transport.Send(new PeerId(kv.Key), relay, 0, relay.Length, DeliveryClass.Snapshots);
            }
        }

        public override void SendLocalTransform(PlayerTransformMessage transform)
        {
            if (transform == null) return;
            var bytes = transform.Encode(ProtocolConstants.HostPeerId, SessionId, NextSequence(), Tick);
            Transport.Broadcast(bytes, 0, bytes.Length, DeliveryClass.Snapshots);
        }

        public override void SendBlockDeltas(BlockDeltaMessage deltas)
        {
            if (deltas == null || deltas.Edits.Count == 0) return;
            var bytes = deltas.Encode(ProtocolConstants.HostPeerId, SessionId, NextSequence(), Tick);
            Transport.Broadcast(bytes, 0, bytes.Length, DeliveryClass.Transactions);
        }

        public override void SendEntitySpawn(EntitySpawnMessage spawn)
        {
            if (spawn == null) return;
            var bytes = spawn.Encode(ProtocolConstants.HostPeerId, SessionId, NextSequence(), Tick);
            Transport.Broadcast(bytes, 0, bytes.Length, DeliveryClass.EntityLifecycle);
        }

        public override void SendEntityDespawn(EntityDespawnMessage despawn)
        {
            if (despawn == null) return;
            var bytes = despawn.Encode(ProtocolConstants.HostPeerId, SessionId, NextSequence(), Tick);
            Transport.Broadcast(bytes, 0, bytes.Length, DeliveryClass.EntityLifecycle);
        }

        public override void SendEntityDamage(EntityDamageMessage damage)
        {
            if (damage == null || damage.Damages.Count == 0) return;
            var bytes = damage.Encode(ProtocolConstants.HostPeerId, SessionId, NextSequence(), Tick);
            Transport.Broadcast(bytes, 0, bytes.Length, DeliveryClass.EntityLifecycle);
        }

        public override void SendPlayerAppearance(PlayerAppearanceMessage appearance)
        {
            if (appearance == null) return;
            var bytes = appearance.Encode(ProtocolConstants.HostPeerId, SessionId, NextSequence(), Tick);
            Transport.Broadcast(bytes, 0, bytes.Length, DeliveryClass.Snapshots);
        }

        public override void SendPlayerEquipment(PlayerEquipmentMessage equipment)
        {
            if (equipment == null) return;
            var bytes = equipment.Encode(ProtocolConstants.HostPeerId, SessionId, NextSequence(), Tick);
            Transport.Broadcast(bytes, 0, bytes.Length, DeliveryClass.Snapshots);
        }

        public override void SendEntityMotion(EntityMotionMessage motion)
        {
            if (motion == null || motion.Motions.Count == 0) return;
            var bytes = motion.Encode(ProtocolConstants.HostPeerId, SessionId, NextSequence(), Tick);
            Transport.Broadcast(bytes, 0, bytes.Length,
                                motion.AtRest ? DeliveryClass.EntityLifecycle
                                              : DeliveryClass.Snapshots);
        }

        /// <summary>
        /// Announces the session speed to everyone, including back to whoever asked.
        /// Raised locally too, so the host applies exactly what it published.
        /// </summary>
        public override void SendTimeScale(TimeScaleMessage speed)
        {
            if (speed == null || !TimeScaleMessage.IsShareable(speed.Speed)) return;

            var bytes = speed.Encode(ProtocolConstants.HostPeerId, SessionId, NextSequence(), Tick);
            Transport.Broadcast(bytes, 0, bytes.Length, DeliveryClass.Control);
            RaiseTimeScaleChanged(speed);
        }

        public override void SendBlockHits(BlockHitMessage hits)
        {
            if (hits == null || hits.Cracks.Count == 0) return;
            var bytes = hits.Encode(ProtocolConstants.HostPeerId, SessionId, NextSequence(), Tick);
            Transport.Broadcast(bytes, 0, bytes.Length, DeliveryClass.Transactions);
        }

        /// <summary>
        /// Schedules a layer-wide event for everyone, this peer included, so the host
        /// runs exactly what it published.
        /// </summary>
        public void SendWorldEvent(WorldEventMessage worldEvent)
        {
            if (worldEvent == null) return;
            var bytes = worldEvent.Encode(ProtocolConstants.HostPeerId, SessionId, NextSequence(), Tick);
            Transport.Broadcast(bytes, 0, bytes.Length, DeliveryClass.Control);
        }

        public override void SendPlayerAttack(PlayerAttackMessage attack)
        {
            if (attack == null) return;
            var bytes = attack.Encode(ProtocolConstants.HostPeerId, SessionId, NextSequence(), Tick);
            Transport.Broadcast(bytes, 0, bytes.Length, DeliveryClass.Transactions);
        }

        private byte[] BuildPong()
        {
            var w = new PacketWriter(32);
            int lp = PacketHeader.Write(w, MessageType.Pong, ProtocolConstants.HostPeerId,
                                        SessionId, NextSequence(), Tick);
            PacketHeader.PatchPayloadLength(w, lp);
            return w.ToArray();
        }

        /// <summary>Stops admitting new players, e.g. once a run is under way.</summary>
        public void CloseToNewPlayers() => _requirements.AcceptingNewPlayers = false;

        /// <summary>
        /// Broadcasts an authoritative interaction outcome to every peer, including the
        /// requester. All peers need it, not just the actor: everyone must remove a taken
        /// item from their own world or it lingers as a ghost the others can still see.
        /// </summary>
        public void BroadcastInteractionResult(InteractionResultMessage result)
        {
            if (result == null) return;
            var bytes = result.Encode(SessionId, NextSequence(), Tick);
            Transport.Broadcast(bytes, 0, bytes.Length, DeliveryClass.Transactions);
            RaiseInteractionResolved(result);
        }

        private uint _nextTransferId = 1;

        /// <summary>
        /// Sends the current layer's terrain to every connected client.
        ///
        /// Necessary because the game has no world seed: a client cannot regenerate the
        /// host's layer, so it has to receive it. Sent reliable-ordered on the Bulk
        /// channel, which is a separate transport channel precisely so a ~145 KiB
        /// transfer cannot head-of-line block movement or control traffic.
        /// </summary>
        /// <param name="rawTerrain">Block grid, 2 bytes per block, row-major.</param>
        /// <returns>The transfer id, or 0 if there was nothing to send.</returns>
        public uint BroadcastWorldSnapshot(byte[] rawTerrain, ushort chunkWidth, ushort chunkHeight,
                                           ushort chunkSize, int biomeDepth, float spawnX, float spawnY,
                                           EntityManifest entities = null,
                                           byte layerModifier = LayerPrepareMessage.NoLayerModifier,
                                           byte[] rawFluid = null)
        {
            if (rawTerrain == null || rawTerrain.Length == 0) return 0;
            if (Players.Count == 0) return 0;

            // Terrain and entities travel as one verified transfer: a client holding a
            // layer's terrain but not its contents would look correct and be unplayable.
            var entityBytes = entities != null ? entities.Serialize() : new byte[0];
            if (rawFluid == null) rawFluid = new byte[0];
            var payload = WorldPayload.Combine(rawTerrain, rawFluid, entityBytes);
            var compressed = WorldSnapshotCodec.Compress(payload);
            var slices = WorldSnapshotCodec.Split(compressed);

            if (slices.Count > ushort.MaxValue)
            {
                Log("world too large to transfer: " + slices.Count + " chunks");
                return 0;
            }

            uint transferId = _nextTransferId++;

            var prepare = new LayerPrepareMessage
            {
                TransferId = transferId,
                ChunkWidth = chunkWidth,
                ChunkHeight = chunkHeight,
                ChunkSize = chunkSize,
                BiomeDepth = biomeDepth,
                SpawnX = spawnX,
                SpawnY = spawnY,
                CompressedLength = compressed.Length,
                ChunkCount = (ushort)slices.Count,
                CompressedHash = WorldSnapshotCodec.Sha256Hex(compressed),
                UncompressedLength = payload.Length,
                TerrainLength = rawTerrain.Length,
                FluidLength = rawFluid.Length,
                LayerModifier = layerModifier,
            };

            var prepareBytes = prepare.Encode(SessionId, NextSequence(), Tick);
            Transport.Broadcast(prepareBytes, 0, prepareBytes.Length, DeliveryClass.Control);

            for (int i = 0; i < slices.Count; i++)
            {
                var bytes = new WorldSnapshotChunkMessage
                {
                    TransferId = transferId,
                    ChunkIndex = (ushort)i,
                    Data = slices[i],
                }.Encode(SessionId, NextSequence(), Tick);

                Transport.Broadcast(bytes, 0, bytes.Length, DeliveryClass.Bulk);
            }

            Log("world snapshot " + transferId + " sent: terrain " + rawTerrain.Length +
                " B + fluid " + rawFluid.Length + " B + entities " + entityBytes.Length +
                " B (" + (entities?.Entities.Count ?? 0) + " objects) -> " +
                compressed.Length + " B in " + slices.Count + " chunks");
            return transferId;
        }
    }
}
