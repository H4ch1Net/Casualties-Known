using System;
using System.Collections.Generic;
using CCoop.Protocol;
using CCoop.Protocol.Messages;
using CCoop.Transport;

namespace CCoop.Core
{
    /// <summary>A remote participant as tracked by the local session.</summary>
    public sealed class RemotePlayer
    {
        public byte PeerId;
        public string Name = string.Empty;

        /// <summary>Most recent accepted transform, or null before the first one.</summary>
        public PlayerTransformMessage LatestTransform;

        /// <summary>
        /// Sequence of the newest accepted transform. Movement is unreliable-sequenced,
        /// so late arrivals must be dropped rather than applied — otherwise a player
        /// visibly jerks backwards whenever the network reorders two frames.
        /// </summary>
        public uint LastTransformSequence;

        public bool HasTransform => LatestTransform != null;
    }

    public enum SessionStatus : byte
    {
        Idle = 0,
        Connecting,
        Handshaking,
        Established,
        Rejected,
        Disconnected,
        Failed,
    }

    /// <summary>
    /// Shared session behaviour: owns the transport, decodes inbound packets, and
    /// raises game-facing events.
    ///
    /// Deliberately free of any Unity reference so the whole session lifecycle can be
    /// exercised headlessly over loopback UDP. The Unity layer is thin glue that calls
    /// <see cref="Poll"/> once per frame and reacts to events.
    ///
    /// Threading: every event is raised inside <see cref="Poll"/>, on the caller's
    /// thread, so handlers may safely touch Unity objects.
    /// </summary>
    public abstract class CoopSession : IDisposable
    {
        protected readonly INetworkTransport Transport;
        protected readonly Dictionary<byte, RemotePlayer> Players = new Dictionary<byte, RemotePlayer>();

        private uint _sequence;

        protected CoopSession(INetworkTransport transport)
        {
            Transport = transport ?? throw new ArgumentNullException(nameof(transport));
            Transport.PacketReceived += OnPacket;
            Transport.PeerConnected += OnPeerConnectedInternal;
            Transport.PeerDisconnected += OnPeerDisconnectedInternal;
            Transport.TransportError += e => Log("transport error: " + e);
        }

        public SessionStatus Status { get; protected set; } = SessionStatus.Idle;
        public uint SessionId { get; protected set; }
        public byte LocalPeerId { get; protected set; }
        public uint Tick { get; private set; }

        public IEnumerable<RemotePlayer> RemotePlayers => Players.Values;
        public int RemotePlayerCount => Players.Count;

        // ---- game-facing events (all raised on the polling thread) ----
        public event Action<RemotePlayer> RemotePlayerJoined;
        public event Action<RemotePlayer> RemotePlayerLeft;
        public event Action<RemotePlayer> RemotePlayerMoved;
        public event Action SessionEstablished;
        public event Action<RejectMessage> SessionRejected;
        public event Action<string> Diagnostic;

        /// <summary>
        /// Client-side: the host's world arrived and passed verification.
        /// </summary>
        public event Action<LayerSnapshot> WorldSnapshotReady;

        /// <summary>Client-side: transfer progress, 0..1, for a loading indicator.</summary>
        public event Action<float> WorldSnapshotProgress;

        /// <summary>Host-side: a client finished applying (or failed to apply) the world.</summary>
        public event Action<byte, bool, string> PeerLayerReady;

        protected void RaiseWorldSnapshotReady(LayerSnapshot snapshot) =>
            WorldSnapshotReady?.Invoke(snapshot);
        protected void RaiseWorldSnapshotProgress(float p) => WorldSnapshotProgress?.Invoke(p);
        protected void RaisePeerLayerReady(byte peer, bool ok, string detail) =>
            PeerLayerReady?.Invoke(peer, ok, detail);

        /// <summary>Host-side: a client is asking to interact with a world entity.</summary>
        public event Action<byte, InteractionRequestMessage> InteractionRequested;

        /// <summary>All peers: the host's authoritative outcome for an interaction.</summary>
        public event Action<InteractionResultMessage> InteractionResolved;

        protected void RaiseInteractionRequested(byte peer, InteractionRequestMessage m) =>
            InteractionRequested?.Invoke(peer, m);

        protected void RaiseInteractionResolved(InteractionResultMessage m) =>
            InteractionResolved?.Invoke(m);

        /// <summary>
        /// Terrain edits made by another peer, with the peer that made them.
        ///
        /// Peer-to-peer rather than host-arbitrated, unlike interactions. A terrain edit
        /// names the resulting block rather than requesting a change, so two peers
        /// editing the same cell converge instead of racing — there is nothing to
        /// duplicate and therefore nothing to arbitrate. Making mining wait for a round
        /// trip would cost far more than it bought.
        /// </summary>
        public event Action<byte, BlockDeltaMessage> BlockDeltasReceived;

        /// <summary>
        /// A layer-wide event the host scheduled. No client counterpart: only the host
        /// authors these, so there is no request form and nothing to arbitrate.
        /// </summary>
        public event Action<WorldEventMessage> WorldEventReceived;

        protected void RaiseWorldEvent(WorldEventMessage m) => WorldEventReceived?.Invoke(m);

        /// <summary>Another peer swung; show it on their proxy.</summary>
        public event Action<byte, PlayerAttackMessage> PlayerAttacked;

        /// <summary>Raised when a peer reports how battered its body looks.</summary>
        public event Action<byte, PlayerAppearanceMessage> PlayerAppearanceChanged;

        /// <summary>Raised when a peer reports what it is holding and wearing.</summary>
        public event Action<byte, PlayerEquipmentMessage> PlayerEquipmentChanged;

        protected void RaisePlayerAttacked(byte peer, PlayerAttackMessage m) =>
            PlayerAttacked?.Invoke(peer, m);

        protected void RaisePlayerAppearance(byte peer, PlayerAppearanceMessage m) =>
            PlayerAppearanceChanged?.Invoke(peer, m);

        protected void RaisePlayerEquipment(byte peer, PlayerEquipmentMessage m) =>
            PlayerEquipmentChanged?.Invoke(peer, m);

        /// <summary>Publishes our own swing, for the others to draw.</summary>
        public abstract void SendPlayerAttack(PlayerAttackMessage attack);

        /// <summary>Publishes how this player's body looks.</summary>
        public abstract void SendPlayerAppearance(PlayerAppearanceMessage appearance);

        /// <summary>Publishes what this player is holding and wearing.</summary>
        public abstract void SendPlayerEquipment(PlayerEquipmentMessage equipment);

        /// <summary>Cosmetic block damage from another peer — cracks and hit sounds.</summary>
        public event Action<byte, BlockHitMessage> BlockHitsReceived;

        protected void RaiseBlockHits(byte peer, BlockHitMessage m) =>
            BlockHitsReceived?.Invoke(peer, m);

        /// <summary>Publishes how damaged our blocks look.</summary>
        public abstract void SendBlockHits(BlockHitMessage hits);

        /// <summary>A world item another peer created and we must create too.</summary>
        public event Action<byte, EntitySpawnMessage> EntitySpawned;

        /// <summary>A world object another peer destroyed and we must destroy too.</summary>
        public event Action<byte, EntityDespawnMessage> EntityDespawned;

        /// <summary>Damage another peer dealt to world objects, to apply on top of ours.</summary>
        public event Action<byte, EntityDamageMessage> EntityDamaged;

        /// <summary>Raised when a peer reports where its loose items are.</summary>
        public event Action<byte, EntityMotionMessage> EntityMoved;

        /// <summary>Host-side: a client is asking to change the session's speed.</summary>
        public event Action<byte, TimeScaleMessage> TimeScaleRequested;

        /// <summary>All peers: the speed the session now runs at.</summary>
        public event Action<TimeScaleMessage> TimeScaleChanged;

        protected void RaiseBlockDeltas(byte peer, BlockDeltaMessage m) =>
            BlockDeltasReceived?.Invoke(peer, m);

        protected void RaiseEntitySpawned(byte peer, EntitySpawnMessage m) =>
            EntitySpawned?.Invoke(peer, m);

        protected void RaiseEntityDespawned(byte peer, EntityDespawnMessage m) =>
            EntityDespawned?.Invoke(peer, m);

        protected void RaiseEntityMoved(byte peer, EntityMotionMessage m) =>
            EntityMoved?.Invoke(peer, m);

        protected void RaiseEntityDamaged(byte peer, EntityDamageMessage m) =>
            EntityDamaged?.Invoke(peer, m);

        /// <summary>Publishes our own terrain edits. Silent when not established.</summary>
        public abstract void SendBlockDeltas(BlockDeltaMessage deltas);

        /// <summary>Publishes a world item we just created.</summary>
        public abstract void SendEntitySpawn(EntitySpawnMessage spawn);

        /// <summary>Publishes a world object we just destroyed.</summary>
        public abstract void SendEntityDespawn(EntityDespawnMessage despawn);

        /// <summary>Publishes damage we dealt to world objects.</summary>
        public abstract void SendEntityDamage(EntityDamageMessage damage);

        /// <summary>Publishes where this peer's loose items are.</summary>
        public abstract void SendEntityMotion(EntityMotionMessage motion);

        /// <summary>
        /// Host: sets the session speed. Client: asks the host to.
        /// </summary>
        public abstract void SendTimeScale(TimeScaleMessage speed);

        protected void RaiseTimeScaleRequested(byte peer, TimeScaleMessage m) =>
            TimeScaleRequested?.Invoke(peer, m);

        protected void RaiseTimeScaleChanged(TimeScaleMessage m) =>
            TimeScaleChanged?.Invoke(m);

        protected void Log(string s) => Diagnostic?.Invoke(s);

        protected uint NextSequence() => ++_sequence;

        /// <summary>Pumps the transport and advances the simulation tick.</summary>
        public virtual void Poll()
        {
            Transport.Poll();
            Tick++;
        }

        /// <summary>Sends the local player's transform. Called by the Unity layer.</summary>
        public abstract void SendLocalTransform(PlayerTransformMessage transform);

        // ---------------------------------------------------------------- dispatch

        private void OnPacket(PeerId from, byte[] buffer, int offset, int count)
        {
            var result = PacketHeader.TryRead(buffer, offset, count, out var header);
            if (result != DecodeResult.Ok)
            {
                // Malformed traffic is expected on a public socket; drop it quietly
                // rather than tearing down an otherwise healthy session.
                Log("dropped packet from " + from + ": " + result);
                return;
            }

            var reader = PacketHeader.PayloadReader(buffer, offset, count);
            HandleMessage(from, header, ref reader);
        }

        protected abstract void HandleMessage(PeerId from, PacketHeader header, ref PacketReader reader);

        private void OnPeerConnectedInternal(PeerId peer) => OnPeerConnected(peer);
        private void OnPeerDisconnectedInternal(PeerId peer, DisconnectReason reason) =>
            OnPeerDisconnected(peer, reason);

        protected abstract void OnPeerConnected(PeerId peer);
        protected abstract void OnPeerDisconnected(PeerId peer, DisconnectReason reason);

        // ---------------------------------------------------------------- helpers

        protected RemotePlayer AddPlayer(byte peerId, string name)
        {
            if (Players.TryGetValue(peerId, out var existing)) return existing;

            var p = new RemotePlayer { PeerId = peerId, Name = name ?? string.Empty };
            Players[peerId] = p;
            RemotePlayerJoined?.Invoke(p);
            Log("player joined: " + peerId + " (" + p.Name + ")");
            return p;
        }

        protected void RemovePlayer(byte peerId)
        {
            if (!Players.TryGetValue(peerId, out var p)) return;
            Players.Remove(peerId);
            RemotePlayerLeft?.Invoke(p);
            Log("player left: " + peerId);
        }

        /// <summary>
        /// Applies a transform if it is newer than what we already hold.
        /// </summary>
        protected void ApplyTransform(byte peerId, uint sequence, PlayerTransformMessage transform)
        {
            if (!Players.TryGetValue(peerId, out var p)) return;

            // Unsigned wrap-safe comparison: after ~4 billion frames the counter wraps,
            // and a naive "sequence > last" would then reject everything forever.
            if (p.HasTransform && !IsNewer(sequence, p.LastTransformSequence))
                return;

            p.LastTransformSequence = sequence;
            p.LatestTransform = transform;
            RemotePlayerMoved?.Invoke(p);
        }

        /// <summary>
        /// True if sequence <paramref name="a"/> is newer than <paramref name="b"/>,
        /// tolerating counter wrap-around. Public because the wrap behaviour is subtle
        /// enough to deserve direct test coverage.
        /// </summary>
        public static bool IsNewer(uint a, uint b) => unchecked(a - b) < uint.MaxValue / 2;

        protected void RaiseEstablished() => SessionEstablished?.Invoke();
        protected void RaiseRejected(RejectMessage m) => SessionRejected?.Invoke(m);

        public virtual void Dispose()
        {
            try { Transport.Stop(); } catch { /* teardown */ }
        }
    }
}
