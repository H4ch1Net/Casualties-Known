using System;
using System.Linq;
using CCoop.Core;
using CCoop.Protocol;
using CCoop.Protocol.Messages;
using Xunit;
using Xunit.Abstractions;

namespace CCoop.Transport.Tests
{
    public class WorldSnapshotTests
    {
        private readonly ITestOutputHelper _log;
        public WorldSnapshotTests(ITestOutputHelper log) => _log = log;

        // The real world measured in-game: 16x16 chunks of 64 = 1024x1024 blocks.
        private const int ChunkW = 16, ChunkH = 16, ChunkSize = 64;
        private const int Width = ChunkW * ChunkSize;
        private const int Height = ChunkH * ChunkSize;
        private const int RawBytes = Width * Height * sizeof(ushort);

        /// <summary>
        /// Terrain-like data: large contiguous runs of a few block ids. Random bytes
        /// would be incompressible and make the size assertions meaningless.
        /// </summary>
        private static byte[] FakeTerrain(int seed = 1)
        {
            var rng = new Random(seed);
            var blocks = new ushort[Width * Height];
            ushort current = 0;
            int run = 0;
            for (int i = 0; i < blocks.Length; i++)
            {
                if (run == 0)
                {
                    current = (ushort)rng.Next(0, 17); // 17 distinct ids, as measured
                    run = rng.Next(20, 400);
                }
                blocks[i] = current;
                run--;
            }

            var raw = new byte[RawBytes];
            Buffer.BlockCopy(blocks, 0, raw, 0, raw.Length);
            return raw;
        }

        /// <summary>
        /// Fluid-like data: one byte per cell, mostly empty with pooled runs. Real layers
        /// look like this — a few lakes in a mostly dry world — and it is what makes the
        /// megabyte grid cheap to ship.
        /// </summary>
        private static byte[] FakeFluid(int seed = 2)
        {
            var rng = new Random(seed);
            var fluid = new byte[Width * Height];
            for (int i = 0; i < fluid.Length;)
            {
                int run = rng.Next(200, 4000);
                byte type = rng.Next(0, 10) == 0 ? (byte)rng.Next(1, 7) : (byte)0;
                for (int j = 0; j < run && i < fluid.Length; j++, i++) fluid[i] = type;
            }
            return fluid;
        }

        private static LayerPrepareMessage PrepareFor(byte[] raw, byte[] compressed, uint transferId = 42)
        {
            var chunks = WorldSnapshotCodec.Split(compressed);
            return new LayerPrepareMessage
            {
                TransferId = transferId,
                ChunkWidth = ChunkW,
                ChunkHeight = ChunkH,
                ChunkSize = ChunkSize,
                BiomeDepth = 0,
                SpawnX = 1f,
                SpawnY = 2f,
                CompressedLength = compressed.Length,
                ChunkCount = (ushort)chunks.Count,
                CompressedHash = WorldSnapshotCodec.Sha256Hex(compressed),
                UncompressedLength = raw.Length,
                TerrainLength = RawBytes,
                FluidLength = 0,
            };
        }

        // ---------------------------------------------------------------- codec

        [Fact]
        public void Terrain_round_trips_through_compression()
        {
            var raw = FakeTerrain();
            var compressed = WorldSnapshotCodec.Compress(raw);

            Assert.True(WorldSnapshotCodec.TryDecompress(compressed, raw.Length, out var back, out var err),
                        "decompress failed: " + err);
            Assert.Equal(raw, back);

            _log.WriteLine($"raw {raw.Length:N0} B -> deflate {compressed.Length:N0} B " +
                           $"({100.0 * compressed.Length / raw.Length:F1}%)");

            // Sanity-check against the ~145 KiB measured in-game.
            Assert.True(compressed.Length < raw.Length / 4,
                        "terrain should compress well, got " + compressed.Length);
        }

        [Fact]
        public void Decompress_rejects_a_wrong_expected_length()
        {
            var raw = FakeTerrain();
            var compressed = WorldSnapshotCodec.Compress(raw);

            Assert.False(WorldSnapshotCodec.TryDecompress(compressed, raw.Length - 1, out _, out var err));
            _log.WriteLine("rejected: " + err);
        }

        [Fact]
        public void Decompress_rejects_garbage_without_throwing()
        {
            var junk = new byte[4096];
            new Random(9).NextBytes(junk);

            Assert.False(WorldSnapshotCodec.TryDecompress(junk, RawBytes, out _, out var err));
            Assert.NotNull(err);
        }

        [Fact]
        public void Decompress_refuses_an_implausible_length()
        {
            // A decompression-bomb shape: tiny input claiming an enormous output.
            var compressed = WorldSnapshotCodec.Compress(new byte[64]);
            Assert.False(WorldSnapshotCodec.TryDecompress(
                compressed, LayerPrepareMessage.MaxTransferBytes + 1, out _, out var err));
            _log.WriteLine("rejected: " + err);
        }

        [Fact]
        public void Split_covers_the_payload_exactly()
        {
            var compressed = WorldSnapshotCodec.Compress(FakeTerrain());
            var chunks = WorldSnapshotCodec.Split(compressed);

            Assert.Equal(compressed.Length, chunks.Sum(c => c.Length));
            Assert.All(chunks, c => Assert.True(c.Length <= WorldSnapshotChunkMessage.MaxDataPerChunk));
            _log.WriteLine($"{compressed.Length:N0} B -> {chunks.Count} chunks");
        }

        // ---------------------------------------------------------------- assembly

        [Fact]
        public void Assembler_reconstructs_the_terrain()
        {
            var raw = FakeTerrain();
            var compressed = WorldSnapshotCodec.Compress(raw);
            var prepare = PrepareFor(raw, compressed);
            var chunks = WorldSnapshotCodec.Split(compressed);

            var asm = new WorldSnapshotAssembler(prepare);
            for (int i = 0; i < chunks.Count; i++)
            {
                Assert.True(asm.Accept(new WorldSnapshotChunkMessage
                {
                    TransferId = prepare.TransferId,
                    ChunkIndex = (ushort)i,
                    Data = chunks[i],
                }, out var err), err);
            }

            Assert.True(asm.IsComplete);
            Assert.True(asm.TryFinish(out var result, out var error), error);
            Assert.Equal(raw, result);
        }

        [Fact]
        public void Assembler_tolerates_out_of_order_and_duplicate_chunks()
        {
            var raw = FakeTerrain(3);
            var compressed = WorldSnapshotCodec.Compress(raw);
            var prepare = PrepareFor(raw, compressed);
            var chunks = WorldSnapshotCodec.Split(compressed);

            var asm = new WorldSnapshotAssembler(prepare);
            var order = Enumerable.Range(0, chunks.Count).OrderBy(_ => Guid.NewGuid()).ToList();

            foreach (int i in order)
            {
                var m = new WorldSnapshotChunkMessage
                {
                    TransferId = prepare.TransferId, ChunkIndex = (ushort)i, Data = chunks[i],
                };
                Assert.True(asm.Accept(m, out _));
                Assert.True(asm.Accept(m, out _)); // duplicate must be harmless
            }

            Assert.True(asm.IsComplete);
            Assert.True(asm.TryFinish(out var result, out var error), error);
            Assert.Equal(raw, result);
        }

        [Fact]
        public void Assembler_rejects_a_chunk_from_another_transfer()
        {
            var raw = FakeTerrain();
            var compressed = WorldSnapshotCodec.Compress(raw);
            var prepare = PrepareFor(raw, compressed, transferId: 100);
            var asm = new WorldSnapshotAssembler(prepare);

            Assert.False(asm.Accept(new WorldSnapshotChunkMessage
            {
                TransferId = 999, ChunkIndex = 0, Data = new byte[10],
            }, out var err));
            _log.WriteLine("rejected: " + err);
        }

        [Fact]
        public void Assembler_rejects_an_out_of_range_index()
        {
            var raw = FakeTerrain();
            var compressed = WorldSnapshotCodec.Compress(raw);
            var prepare = PrepareFor(raw, compressed);
            var asm = new WorldSnapshotAssembler(prepare);

            Assert.False(asm.Accept(new WorldSnapshotChunkMessage
            {
                TransferId = prepare.TransferId,
                ChunkIndex = (ushort)(prepare.ChunkCount + 5),
                Data = new byte[10],
            }, out var err));
            _log.WriteLine("rejected: " + err);
        }

        [Fact]
        public void Assembler_detects_corruption_via_the_hash()
        {
            var raw = FakeTerrain(5);
            var compressed = WorldSnapshotCodec.Compress(raw);
            var prepare = PrepareFor(raw, compressed);
            var chunks = WorldSnapshotCodec.Split(compressed);

            // Flip one bit in the middle of the transfer.
            chunks[chunks.Count / 2][0] ^= 0x01;

            var asm = new WorldSnapshotAssembler(prepare);
            for (int i = 0; i < chunks.Count; i++)
            {
                asm.Accept(new WorldSnapshotChunkMessage
                {
                    TransferId = prepare.TransferId, ChunkIndex = (ushort)i, Data = chunks[i],
                }, out _);
            }

            Assert.True(asm.IsComplete);
            Assert.False(asm.TryFinish(out _, out var error));
            Assert.Contains("hash mismatch", error);
            _log.WriteLine(error);
        }

        [Fact]
        public void Assembler_refuses_to_finish_early()
        {
            var raw = FakeTerrain();
            var compressed = WorldSnapshotCodec.Compress(raw);
            var prepare = PrepareFor(raw, compressed);
            var chunks = WorldSnapshotCodec.Split(compressed);

            var asm = new WorldSnapshotAssembler(prepare);
            asm.Accept(new WorldSnapshotChunkMessage
            {
                TransferId = prepare.TransferId, ChunkIndex = 0, Data = chunks[0],
            }, out _);

            Assert.False(asm.IsComplete);
            Assert.False(asm.TryFinish(out _, out var error));
            Assert.Contains("incomplete", error);
        }

        // ---------------------------------------------------------------- messages

        [Fact]
        public void LayerPrepare_round_trips()
        {
            var raw = FakeTerrain();
            var compressed = WorldSnapshotCodec.Compress(raw);
            var sent = PrepareFor(raw, compressed);

            var packet = sent.Encode(1, 1, 1);
            Assert.Equal(DecodeResult.Ok, PacketHeader.TryRead(packet, 0, packet.Length, out _));
            var r = PacketHeader.PayloadReader(packet, 0, packet.Length);

            Assert.True(LayerPrepareMessage.TryDecode(ref r, out var got));
            Assert.Equal(sent.TransferId, got.TransferId);
            Assert.Equal(sent.ChunkCount, got.ChunkCount);
            Assert.Equal(sent.CompressedHash, got.CompressedHash);
            Assert.Equal(sent.UncompressedLength, got.UncompressedLength);
            Assert.Equal(sent.SpawnX, got.SpawnX, 3);
        }

        [Fact]
        public void LayerPrepare_carries_the_layer_modifier()
        {
            // The client cannot re-roll this: the game picks it from the unseeded global
            // RNG deep inside the coroutine clients suppress.
            var raw = FakeTerrain();
            var sent = PrepareFor(raw, WorldSnapshotCodec.Compress(raw));
            sent.LayerModifier = 4;    // Freezing

            var packet = sent.Encode(1, 1, 1);
            var r = PacketHeader.PayloadReader(packet, 0, packet.Length);

            Assert.True(LayerPrepareMessage.TryDecode(ref r, out var got));
            Assert.Equal(4, got.LayerModifier);
        }

        [Fact]
        public void LayerPrepare_defaults_to_no_modifier()
        {
            var raw = FakeTerrain();
            var sent = PrepareFor(raw, WorldSnapshotCodec.Compress(raw));

            var packet = sent.Encode(1, 1, 1);
            var r = PacketHeader.PayloadReader(packet, 0, packet.Length);

            Assert.True(LayerPrepareMessage.TryDecode(ref r, out var got));
            Assert.Equal(LayerPrepareMessage.NoLayerModifier, got.LayerModifier);
        }

        [Fact]
        public void LayerPrepare_rejects_a_modifier_this_build_does_not_have()
        {
            // Looked up and not found would leave the client quietly in the wrong
            // climate, which is exactly the failure this whole field exists to stop.
            var raw = FakeTerrain();
            var sent = PrepareFor(raw, WorldSnapshotCodec.Compress(raw));
            sent.LayerModifier = LayerPrepareMessage.LayerModifierCount;

            var packet = sent.Encode(1, 1, 1);
            var r = PacketHeader.PayloadReader(packet, 0, packet.Length);

            Assert.False(LayerPrepareMessage.TryDecode(ref r, out _));
        }

        [Theory]
        [InlineData(0, 16, 64)]       // zero chunk width
        [InlineData(16, 0, 64)]       // zero chunk height
        [InlineData(16, 16, 0)]       // zero chunk size
        [InlineData(16, 16, 513)]     // implausible chunk size
        [InlineData(4096, 4096, 64)]  // world far beyond MaxDimension
        public void LayerPrepare_rejects_implausible_dimensions(int cw, int ch, int cs)
        {
            // Every one of these bounds an allocation or loop on the receiving side.
            var w = new PacketWriter(256);
            int lp = PacketHeader.Write(w, MessageType.LayerPrepare, 0, 1, 1, 1);
            w.WriteUInt32(1);
            w.WriteUInt16((ushort)cw);
            w.WriteUInt16((ushort)ch);
            w.WriteUInt16((ushort)cs);
            w.WriteInt32(0);
            w.WriteSingle(0f); w.WriteSingle(0f);
            w.WriteInt32(1000);
            w.WriteUInt16(1);
            w.WriteString(new string('a', 64));
            w.WriteInt32(1000);
            w.WriteInt32(1000);
            w.WriteByte(LayerPrepareMessage.NoLayerModifier);
            PacketHeader.PatchPayloadLength(w, lp);

            var packet = w.ToArray();
            var r = PacketHeader.PayloadReader(packet, 0, packet.Length);
            Assert.False(LayerPrepareMessage.TryDecode(ref r, out _));
        }

        [Fact]
        public void LayerPrepare_rejects_a_length_that_disagrees_with_the_dimensions()
        {
            // Uncompressed size must be exactly width*height*2; anything else means the
            // sender and receiver disagree about the world, so the apply step would
            // read past the end of the grid.
            var w = new PacketWriter(256);
            int lp = PacketHeader.Write(w, MessageType.LayerPrepare, 0, 1, 1, 1);
            w.WriteUInt32(1);
            w.WriteUInt16(ChunkW); w.WriteUInt16(ChunkH); w.WriteUInt16(ChunkSize);
            w.WriteInt32(0);
            w.WriteSingle(0f); w.WriteSingle(0f);
            w.WriteInt32(1000);
            w.WriteUInt16(1);
            w.WriteString(new string('a', 64));
            w.WriteInt32(RawBytes + 2);
            w.WriteInt32(RawBytes + 2); // terrain length wrong by one block
            w.WriteByte(LayerPrepareMessage.NoLayerModifier);
            PacketHeader.PatchPayloadLength(w, lp);

            var packet = w.ToArray();
            var r = PacketHeader.PayloadReader(packet, 0, packet.Length);
            Assert.False(LayerPrepareMessage.TryDecode(ref r, out _));
        }

        // ---------------------------------------------------------------- entities

        private static EntityManifest SampleManifest(int count = 2256)
        {
            // Shaped like a real layer: ~46 distinct prefabs, heavily repeated.
            var names = new[]
            {
                "glowplant", "geotree", "leadbush", "geyser", "jumppad", "droppings",
                "corpse", "spikestabber", "hydreed", "medcrate", "dropcapsule", "bandage",
            };
            var m = new EntityManifest();
            var rng = new Random(5);
            for (int i = 0; i < count; i++)
                m.Add(names[rng.Next(names.Length)], rng.Next(-500, 500), rng.Next(-500, 500), rng.Next(0, 360));
            return m;
        }

        [Fact]
        public void Entity_manifest_round_trips()
        {
            var sent = SampleManifest();
            var bytes = sent.Serialize();

            Assert.True(EntityManifest.TryDeserialize(bytes, out var got, out var err), err);
            Assert.Equal(sent.Entities.Count, got.Entities.Count);
            Assert.Equal(sent.Keys.Count, got.Keys.Count);

            for (int i = 0; i < sent.Entities.Count; i++)
            {
                Assert.Equal(sent.KeyFor(sent.Entities[i]), got.KeyFor(got.Entities[i]));
                Assert.Equal(sent.Entities[i].X, got.Entities[i].X, 3);
                Assert.Equal(sent.Entities[i].Y, got.Entities[i].Y, 3);
                Assert.Equal(sent.Entities[i].Rotation, got.Entities[i].Rotation, 3);
            }

            var compressed = WorldSnapshotCodec.Compress(bytes);
            _log.WriteLine($"{sent.Entities.Count} entities -> {bytes.Length:N0} B " +
                           $"-> {compressed.Length:N0} B compressed");

            // The string table must actually pay for itself against ~46 repeated names.
            Assert.True(bytes.Length < sent.Entities.Count * 20,
                        "manifest larger than expected: " + bytes.Length);
        }

        [Fact]
        public void Entity_manifest_deduplicates_keys()
        {
            var m = new EntityManifest();
            for (int i = 0; i < 500; i++) m.Add("glowplant", i, i, 0f);

            Assert.Single(m.Keys);
            Assert.Equal(500, m.Entities.Count);
        }

        [Fact]
        public void Entity_manifest_rejects_an_out_of_range_key_index()
        {
            // A key index past the table would silently produce null prefabs much later.
            var w = new PacketWriter();
            w.WriteUInt16(1);
            w.WriteString("glowplant");
            w.WriteInt32(1);
            w.WriteUInt16(7);         // key 7 with only 1 key present
            w.WriteSingle(0f); w.WriteSingle(0f); w.WriteSingle(0f);

            Assert.False(EntityManifest.TryDeserialize(w.ToArray(), out _, out var err));
            Assert.Contains("references key", err);
        }

        [Fact]
        public void Entity_manifest_rejects_a_count_beyond_the_hard_cap()
        {
            var w = new PacketWriter();
            w.WriteUInt16(1);
            w.WriteString("glowplant");
            w.WriteInt32(EntityManifest.MaxEntities + 1);

            Assert.False(EntityManifest.TryDeserialize(w.ToArray(), out _, out var err));
            Assert.Contains("implausible entity count", err);
        }

        [Fact]
        public void Entity_manifest_rejects_a_count_the_payload_cannot_hold()
        {
            // Under the hard cap, but far more records than there are bytes — the case
            // that would otherwise allocate a large list from a remote-supplied number.
            var w = new PacketWriter();
            w.WriteUInt16(1);
            w.WriteString("glowplant");
            w.WriteInt32(100000);

            Assert.False(EntityManifest.TryDeserialize(w.ToArray(), out _, out var err));
            Assert.Contains("exceeds remaining", err);
        }

        [Fact]
        public void Entity_manifest_rejects_trailing_bytes()
        {
            var m = SampleManifest(4);
            var bytes = m.Serialize();
            var padded = new byte[bytes.Length + 4];
            Buffer.BlockCopy(bytes, 0, padded, 0, bytes.Length);

            Assert.False(EntityManifest.TryDeserialize(padded, out _, out var err));
            Assert.Contains("trailing", err);
        }

        [Fact]
        public void World_payload_splits_terrain_fluid_and_entities()
        {
            var terrain = FakeTerrain();
            var fluid = FakeFluid();
            var entityBytes = SampleManifest(100).Serialize();
            var combined = WorldPayload.Combine(terrain, fluid, entityBytes);

            Assert.Equal(terrain.Length + fluid.Length + entityBytes.Length, combined.Length);
            Assert.True(WorldPayload.TrySplit(combined, terrain.Length, fluid.Length,
                                              out var t, out var f, out var e, out var err), err);
            Assert.Equal(terrain, t);
            Assert.Equal(fluid, f);
            Assert.Equal(entityBytes, e);
        }

        [Fact]
        public void World_payload_still_splits_when_there_is_no_fluid()
        {
            // A layer with no water sends none rather than a megabyte of zeroes.
            var terrain = FakeTerrain();
            var entityBytes = SampleManifest(10).Serialize();
            var combined = WorldPayload.Combine(terrain, null, entityBytes);

            Assert.True(WorldPayload.TrySplit(combined, terrain.Length, 0,
                                              out var t, out var f, out var e, out var err), err);
            Assert.Equal(terrain, t);
            Assert.Empty(f);
            Assert.Equal(entityBytes, e);
        }

        [Fact]
        public void World_payload_rejects_sections_that_overrun_it()
        {
            // Each length can look plausible alone and still run past the end together,
            // which is why the check is on the sum.
            var combined = new byte[100];
            Assert.False(WorldPayload.TrySplit(combined, 60, 60, out _, out _, out _, out var err));
            Assert.Contains("exceed payload", err);
        }

        [Fact]
        public void World_payload_rejects_a_negative_section_length()
        {
            var combined = new byte[100];
            Assert.False(WorldPayload.TrySplit(combined, 10, -1, out _, out _, out _, out var err));
            Assert.Contains("negative", err);
        }

        [Fact]
        public void Full_layer_payload_stays_a_reasonable_size()
        {
            // Terrain plus entities is what actually crosses the wire per layer.
            var terrain = FakeTerrain();
            var fluid = FakeFluid();
            var entityBytes = SampleManifest().Serialize();
            var combined = WorldPayload.Combine(terrain, fluid, entityBytes);
            var compressed = WorldSnapshotCodec.Compress(combined);
            var chunks = WorldSnapshotCodec.Split(compressed);

            _log.WriteLine($"terrain {terrain.Length:N0} B + fluid {fluid.Length:N0} B + " +
                           $"entities {entityBytes.Length:N0} B = {combined.Length:N0} B " +
                           $"-> {compressed.Length:N0} B in {chunks.Count} chunks");

            Assert.True(compressed.Length < 400 * 1024,
                        "layer payload unexpectedly large: " + compressed.Length);
        }

        [Fact]
        public void WorldSnapshotChunk_round_trips()
        {
            var data = new byte[WorldSnapshotChunkMessage.MaxDataPerChunk];
            new Random(11).NextBytes(data);

            var sent = new WorldSnapshotChunkMessage { TransferId = 7, ChunkIndex = 3, Data = data };
            var packet = sent.Encode(1, 1, 1);

            Assert.Equal(DecodeResult.Ok, PacketHeader.TryRead(packet, 0, packet.Length, out _));
            var r = PacketHeader.PayloadReader(packet, 0, packet.Length);

            Assert.True(WorldSnapshotChunkMessage.TryDecode(ref r, out var got));
            Assert.Equal(7u, got.TransferId);
            Assert.Equal(3, got.ChunkIndex);
            Assert.Equal(data, got.Data);
        }

        [Fact]
        public void A_full_transfer_fits_the_packet_budget()
        {
            // Each encoded chunk must stay within MaxPacketBytes or the transport will
            // reject it at send time.
            var raw = FakeTerrain();
            var compressed = WorldSnapshotCodec.Compress(raw);
            var chunks = WorldSnapshotCodec.Split(compressed);

            int largest = 0;
            for (int i = 0; i < chunks.Count; i++)
            {
                var packet = new WorldSnapshotChunkMessage
                {
                    TransferId = 1, ChunkIndex = (ushort)i, Data = chunks[i],
                }.Encode(1, (uint)i, 1);
                largest = Math.Max(largest, packet.Length);
                Assert.True(packet.Length <= ProtocolConstants.MaxPacketBytes,
                            "packet " + packet.Length + " exceeds MaxPacketBytes");
            }

            _log.WriteLine($"{chunks.Count} packets, largest {largest} B, " +
                           $"budget {ProtocolConstants.MaxPacketBytes} B");
        }
    }
}
