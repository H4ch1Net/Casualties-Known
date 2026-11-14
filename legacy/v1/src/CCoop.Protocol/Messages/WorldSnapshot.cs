using System;

namespace CCoop.Protocol.Messages
{
    /// <summary>
    /// Host -> client. Announces the layer about to be transferred.
    ///
    /// Sent before any <see cref="WorldSnapshotChunkMessage"/> so the client can size
    /// its reassembly buffer once from a value it has validated, rather than growing it
    /// from whatever the next packet claims.
    /// </summary>
    public sealed class LayerPrepareMessage
    {
        /// <summary>Guards against mixing chunks from two different transfers.</summary>
        public uint TransferId;

        public ushort ChunkWidth;
        public ushort ChunkHeight;
        public ushort ChunkSize;

        /// <summary>Layer index (<c>WorldGeneration.biomeDepth</c>).</summary>
        public int BiomeDepth;

        /// <summary>Where the host wants this client's body placed.</summary>
        public float SpawnX;
        public float SpawnY;

        /// <summary>Total compressed bytes across all chunks.</summary>
        public int CompressedLength;

        /// <summary>Number of <see cref="WorldSnapshotChunkMessage"/> to expect.</summary>
        public ushort ChunkCount;

        /// <summary>
        /// SHA-256 of the compressed payload, lowercase hex. Deflate detects most
        /// corruption on its own, but this also catches a mismatched or maliciously
        /// crafted transfer before any of it reaches world state.
        /// </summary>
        public string CompressedHash = string.Empty;

        /// <summary>Total uncompressed size: terrain followed by the entity manifest.</summary>
        public int UncompressedLength;

        /// <summary>
        /// Bytes of the payload that are terrain; the remainder is the entity manifest.
        /// Both travel in one transfer so a client can never end up with a layer's
        /// terrain but not its contents.
        /// </summary>
        public int TerrainLength;

        /// <summary>
        /// Bytes of the payload that are the fluid grid, 1 byte per cell, following the
        /// terrain. Zero when the host had none to send.
        ///
        /// Fluid is placed by <c>PlaceLiquids</c> during generation, which clients
        /// suppress — so without this a client's world is bone dry while the host's has
        /// lakes, and one player wades where the other sees ground.
        /// </summary>
        public int FluidLength;

        /// <summary>
        /// The layer's modifier — Foggy, Freezing, Infested and so on — or
        /// <see cref="NoLayerModifier"/> for a plain layer.
        ///
        /// Travels with the layer because the game rolls it inside <c>GenerateWorld</c>,
        /// which clients suppress. Without it a client stands in the host's terrain at
        /// the host's coordinates and yet is in a different climate: no fog, no cold, and
        /// none of the hazards the layer is supposed to have.
        /// </summary>
        public byte LayerModifier = NoLayerModifier;

        /// <summary>Sentinel for "this layer has no modifier".</summary>
        public const byte NoLayerModifier = 255;

        /// <summary>How many modifiers the supported build defines.</summary>
        public const byte LayerModifierCount = 11;

        public byte[] Encode(uint sessionId, uint sequence, uint tick)
        {
            var w = new PacketWriter(256);
            int lp = PacketHeader.Write(w, MessageType.LayerPrepare, ProtocolConstants.HostPeerId,
                                        sessionId, sequence, tick);
            w.WriteUInt32(TransferId);
            w.WriteUInt16(ChunkWidth);
            w.WriteUInt16(ChunkHeight);
            w.WriteUInt16(ChunkSize);
            w.WriteInt32(BiomeDepth);
            w.WriteSingle(SpawnX);
            w.WriteSingle(SpawnY);
            w.WriteInt32(CompressedLength);
            w.WriteUInt16(ChunkCount);
            w.WriteString(CompressedHash);
            w.WriteInt32(UncompressedLength);
            w.WriteInt32(TerrainLength);
            w.WriteInt32(FluidLength);
            w.WriteByte(LayerModifier);
            PacketHeader.PatchPayloadLength(w, lp);
            return w.ToArray();
        }

        /// <summary>Largest world we will accept, as a guard on remote-supplied sizes.</summary>
        public const int MaxDimension = 4096;

        /// <summary>32 MiB ceiling on any single transfer.</summary>
        public const int MaxTransferBytes = 32 * 1024 * 1024;

        public static bool TryDecode(ref PacketReader r, out LayerPrepareMessage msg)
        {
            msg = null;
            var m = new LayerPrepareMessage
            {
                TransferId = r.ReadUInt32(),
                ChunkWidth = r.ReadUInt16(),
                ChunkHeight = r.ReadUInt16(),
                ChunkSize = r.ReadUInt16(),
                BiomeDepth = r.ReadInt32(),
                SpawnX = r.ReadSingle(),
                SpawnY = r.ReadSingle(),
                CompressedLength = r.ReadInt32(),
                ChunkCount = r.ReadUInt16(),
                CompressedHash = r.ReadString(),
                UncompressedLength = r.ReadInt32(),
                TerrainLength = r.ReadInt32(),
                FluidLength = r.ReadInt32(),
                LayerModifier = r.ReadByte(),
            };
            if (r.Failed || !r.FullyConsumed) return false;

            // Anything outside the known set would be looked up and silently ignored,
            // leaving the client quietly in the wrong climate.
            if (m.LayerModifier != NoLayerModifier && m.LayerModifier >= LayerModifierCount)
                return false;

            // Every one of these bounds a later allocation or loop. A world claiming
            // 65535x65535 chunks would otherwise ask us for terabytes.
            if (m.ChunkWidth == 0 || m.ChunkHeight == 0 || m.ChunkSize == 0) return false;
            if (m.ChunkSize > 512) return false;

            long width = (long)m.ChunkWidth * m.ChunkSize;
            long height = (long)m.ChunkHeight * m.ChunkSize;
            if (width > MaxDimension || height > MaxDimension) return false;

            if (m.CompressedLength <= 0 || m.CompressedLength > MaxTransferBytes) return false;
            if (m.UncompressedLength <= 0 || m.UncompressedLength > MaxTransferBytes) return false;

            // Terrain must be exactly the block grid, 2 bytes per block; the entity
            // manifest is whatever follows it. Pinning the terrain length to the declared
            // dimensions is what stops a mismatched payload from being copied into
            // worldBlocks at the wrong stride.
            if (m.TerrainLength != width * height * sizeof(ushort)) return false;

            // Fluid is one byte per cell, or absent entirely. Pinned to the same
            // dimensions for the same reason: a grid of the wrong size copied into
            // FluidManager's array would smear water across unrelated coordinates.
            if (m.FluidLength != 0 && m.FluidLength != width * height) return false;

            if (m.UncompressedLength < (long)m.TerrainLength + m.FluidLength) return false;

            if (m.ChunkCount == 0) return false;
            if (m.CompressedHash.Length != ProtocolConstants.Sha256HexLength) return false;

            msg = m;
            return true;
        }
    }

    /// <summary>
    /// Host -> client. One slice of the compressed terrain.
    ///
    /// Sent reliable-ordered on the Bulk channel, which has its own transport channel so
    /// a ~145 KiB transfer cannot head-of-line block movement or handshake traffic.
    /// </summary>
    public sealed class WorldSnapshotChunkMessage
    {
        public uint TransferId;
        public ushort ChunkIndex;
        public byte[] Data = new byte[0];

        /// <summary>
        /// Bytes of terrain per message. Kept under
        /// <see cref="ProtocolConstants.MaxBlobBytes"/> with room for the header and the
        /// blob's own length prefix.
        /// </summary>
        public const int MaxDataPerChunk = 7000;

        public byte[] Encode(uint sessionId, uint sequence, uint tick)
        {
            if (Data == null) Data = new byte[0];
            if (Data.Length > MaxDataPerChunk)
                throw new ArgumentException("chunk too large: " + Data.Length);

            var w = new PacketWriter(Data.Length + 64);
            int lp = PacketHeader.Write(w, MessageType.WorldSnapshot, ProtocolConstants.HostPeerId,
                                        sessionId, sequence, tick);
            w.WriteUInt32(TransferId);
            w.WriteUInt16(ChunkIndex);
            w.WriteBlob(Data);
            PacketHeader.PatchPayloadLength(w, lp);
            return w.ToArray();
        }

        public static bool TryDecode(ref PacketReader r, out WorldSnapshotChunkMessage msg)
        {
            msg = null;
            var m = new WorldSnapshotChunkMessage
            {
                TransferId = r.ReadUInt32(),
                ChunkIndex = r.ReadUInt16(),
                Data = r.ReadBlob(),
            };
            if (r.Failed || !r.FullyConsumed) return false;
            if (m.Data.Length == 0 || m.Data.Length > MaxDataPerChunk) return false;
            msg = m;
            return true;
        }
    }

    /// <summary>Client -> host. Snapshot applied; this peer is ready to play.</summary>
    public sealed class LayerReadyMessage
    {
        public uint TransferId;
        public bool Success;

        /// <summary>Failure reason, for the host's log.</summary>
        public string Detail = string.Empty;

        public byte[] Encode(byte peerId, uint sessionId, uint sequence, uint tick)
        {
            var w = new PacketWriter(128);
            int lp = PacketHeader.Write(w, MessageType.LayerReady, peerId, sessionId, sequence, tick);
            w.WriteUInt32(TransferId);
            w.WriteBool(Success);
            w.WriteString(Detail);
            PacketHeader.PatchPayloadLength(w, lp);
            return w.ToArray();
        }

        public static bool TryDecode(ref PacketReader r, out LayerReadyMessage msg)
        {
            msg = null;
            var m = new LayerReadyMessage
            {
                TransferId = r.ReadUInt32(),
                Success = r.ReadBool(),
                Detail = r.ReadString(),
            };
            if (r.Failed || !r.FullyConsumed) return false;
            msg = m;
            return true;
        }
    }
}
