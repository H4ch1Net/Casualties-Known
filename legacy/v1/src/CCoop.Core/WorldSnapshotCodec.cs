using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using CCoop.Protocol.Messages;

namespace CCoop.Core
{
    /// <summary>
    /// Compresses, splits and reassembles the terrain payload.
    ///
    /// The game has no world seed (<c>UnityEngine.Random.InitState</c> is never called
    /// and every noise generator is seeded from the unseeded global RNG), so a client
    /// cannot regenerate the host's layer — it has to be transferred. Measured on the
    /// real game: 1024x1024 blocks, 2 MiB raw, ~145 KiB deflated.
    ///
    /// Run-length encoding was measured and dropped: it beat plain deflate by about 4%,
    /// which does not justify a second format to get wrong.
    /// </summary>
    public static class WorldSnapshotCodec
    {
        public static byte[] Compress(byte[] raw)
        {
            if (raw == null) throw new ArgumentNullException(nameof(raw));

            using (var ms = new MemoryStream())
            {
                using (var ds = new DeflateStream(ms, CompressionMode.Compress, leaveOpen: true))
                    ds.Write(raw, 0, raw.Length);
                return ms.ToArray();
            }
        }

        /// <summary>
        /// Inflates to exactly <paramref name="expectedLength"/> bytes.
        ///
        /// The expected length is required rather than inferred: inflating an
        /// attacker-supplied stream into a growing buffer is a decompression bomb. Here
        /// the output buffer is pre-sized from a value already validated against the
        /// world dimensions, and a stream that produces more than that is rejected.
        /// </summary>
        public static bool TryDecompress(byte[] compressed, int expectedLength, out byte[] raw, out string error)
        {
            raw = null;
            error = null;

            if (compressed == null || compressed.Length == 0) { error = "empty payload"; return false; }
            if (expectedLength <= 0 || expectedLength > LayerPrepareMessage.MaxTransferBytes)
            {
                error = "implausible uncompressed length " + expectedLength;
                return false;
            }

            var buffer = new byte[expectedLength];
            try
            {
                using (var ms = new MemoryStream(compressed, writable: false))
                using (var ds = new DeflateStream(ms, CompressionMode.Decompress))
                {
                    int total = 0;
                    while (total < expectedLength)
                    {
                        int read = ds.Read(buffer, total, expectedLength - total);
                        if (read <= 0) break;
                        total += read;
                    }

                    if (total != expectedLength)
                    {
                        error = "inflated " + total + " bytes, expected " + expectedLength;
                        return false;
                    }

                    // Anything beyond the declared size means the sender disagrees with
                    // us about the world, or is probing for a buffer overrun.
                    if (ds.ReadByte() != -1)
                    {
                        error = "stream contains more data than declared";
                        return false;
                    }
                }
            }
            catch (Exception e)
            {
                error = "inflate failed: " + e.Message;
                return false;
            }

            raw = buffer;
            return true;
        }

        public static string Sha256Hex(byte[] data)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>Splits a compressed payload into transferable slices.</summary>
        public static List<byte[]> Split(byte[] compressed, int maxChunkBytes = WorldSnapshotChunkMessage.MaxDataPerChunk)
        {
            if (compressed == null) throw new ArgumentNullException(nameof(compressed));
            if (maxChunkBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxChunkBytes));

            var chunks = new List<byte[]>((compressed.Length / maxChunkBytes) + 1);
            for (int offset = 0; offset < compressed.Length; offset += maxChunkBytes)
            {
                int size = Math.Min(maxChunkBytes, compressed.Length - offset);
                var slice = new byte[size];
                Buffer.BlockCopy(compressed, offset, slice, 0, size);
                chunks.Add(slice);
            }

            // A zero-length payload would otherwise produce zero chunks and a transfer
            // that can never complete.
            if (chunks.Count == 0) chunks.Add(new byte[0]);
            return chunks;
        }
    }

    /// <summary>
    /// Reassembles a chunked world transfer on the receiving side.
    ///
    /// Chunks arrive reliable-ordered, but this tolerates duplicates and out-of-order
    /// delivery anyway: a transfer that silently mis-assembles would surface much later
    /// as inexplicable terrain corruption.
    /// </summary>
    public sealed class WorldSnapshotAssembler
    {
        private readonly byte[][] _chunks;
        private readonly bool[] _received;
        private int _receivedCount;
        private int _receivedBytes;

        public WorldSnapshotAssembler(LayerPrepareMessage prepare)
        {
            Prepare = prepare ?? throw new ArgumentNullException(nameof(prepare));
            _chunks = new byte[prepare.ChunkCount][];
            _received = new bool[prepare.ChunkCount];
        }

        public LayerPrepareMessage Prepare { get; }
        public bool IsComplete => _receivedCount == _chunks.Length;
        public int ReceivedChunks => _receivedCount;
        public int TotalChunks => _chunks.Length;

        public float Progress => _chunks.Length == 0 ? 1f : (float)_receivedCount / _chunks.Length;

        /// <summary>
        /// Accepts a chunk. Returns false (with a reason) if it does not belong to this
        /// transfer or would overrun the declared total.
        /// </summary>
        public bool Accept(WorldSnapshotChunkMessage chunk, out string error)
        {
            error = null;
            if (chunk == null) { error = "null chunk"; return false; }

            if (chunk.TransferId != Prepare.TransferId)
            {
                error = "chunk from a different transfer";
                return false;
            }

            if (chunk.ChunkIndex >= _chunks.Length)
            {
                error = "chunk index " + chunk.ChunkIndex + " outside 0.." + (_chunks.Length - 1);
                return false;
            }

            if (_received[chunk.ChunkIndex]) return true; // duplicate: harmless, ignore

            int newTotal = _receivedBytes + chunk.Data.Length;
            if (newTotal > Prepare.CompressedLength)
            {
                error = "chunks exceed declared compressed length";
                return false;
            }

            _chunks[chunk.ChunkIndex] = chunk.Data;
            _received[chunk.ChunkIndex] = true;
            _receivedCount++;
            _receivedBytes = newTotal;
            return true;
        }

        /// <summary>
        /// Concatenates, verifies the hash, and inflates. Only call once complete.
        /// </summary>
        public bool TryFinish(out byte[] raw, out string error)
        {
            raw = null;
            error = null;

            if (!IsComplete)
            {
                error = "transfer incomplete (" + _receivedCount + "/" + _chunks.Length + ")";
                return false;
            }

            if (_receivedBytes != Prepare.CompressedLength)
            {
                error = "assembled " + _receivedBytes + " bytes, expected " + Prepare.CompressedLength;
                return false;
            }

            var compressed = new byte[_receivedBytes];
            int offset = 0;
            foreach (var c in _chunks)
            {
                Buffer.BlockCopy(c, 0, compressed, offset, c.Length);
                offset += c.Length;
            }

            // Verify BEFORE inflating: cheaper to reject here, and it keeps a corrupt
            // stream away from the decompressor entirely.
            var actualHash = WorldSnapshotCodec.Sha256Hex(compressed);
            if (!string.Equals(actualHash, Prepare.CompressedHash, StringComparison.Ordinal))
            {
                error = "hash mismatch (expected " + Prepare.CompressedHash.Substring(0, 12) +
                        "..., got " + actualHash.Substring(0, 12) + "...)";
                return false;
            }

            return WorldSnapshotCodec.TryDecompress(compressed, Prepare.UncompressedLength, out raw, out error);
        }
    }
}
