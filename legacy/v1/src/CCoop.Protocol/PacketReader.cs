using System;
using System.Text;

namespace CCoop.Protocol
{
    /// <summary>
    /// Bounds-checked little-endian reader over untrusted bytes.
    ///
    /// **This type never throws.** Every read is bounds-checked; a violation sets
    /// <see cref="Failed"/> and subsequent reads return defaults. Callers do the work
    /// then check <see cref="Failed"/> once, which keeps decoders readable without
    /// letting a malformed packet raise an exception into game code — a throw inside a
    /// Unity update loop is exactly the crash a hostile peer would aim for.
    /// </summary>
    public struct PacketReader
    {
        private readonly byte[] _buffer;
        private readonly int _end;
        private int _pos;

        public PacketReader(byte[] buffer, int offset, int count)
        {
            _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));

            if (offset < 0 || count < 0 || offset > buffer.Length || offset + count > buffer.Length)
            {
                // Refuse to trust the caller's framing; present an empty, failed reader.
                _pos = 0;
                _end = 0;
                Failed = true;
                return;
            }

            _pos = offset;
            _end = offset + count;
            Failed = false;
        }

        /// <summary>True once any read has run past the end or violated a limit.</summary>
        public bool Failed { get; private set; }

        public int Remaining => Failed ? 0 : _end - _pos;

        /// <summary>True when every byte was consumed and nothing failed.</summary>
        public bool FullyConsumed => !Failed && _pos == _end;

        private bool Take(int n)
        {
            if (Failed) return false;
            if (n < 0 || _end - _pos < n)
            {
                Failed = true;
                return false;
            }
            return true;
        }

        public byte ReadByte()
        {
            if (!Take(1)) return 0;
            return _buffer[_pos++];
        }

        public bool ReadBool()
        {
            byte b = ReadByte();
            if (Failed) return false;
            if (b > 1) { Failed = true; return false; } // reject non-canonical booleans
            return b == 1;
        }

        public ushort ReadUInt16()
        {
            if (!Take(2)) return 0;
            ushort v = (ushort)(_buffer[_pos] | (_buffer[_pos + 1] << 8));
            _pos += 2;
            return v;
        }

        public uint ReadUInt32()
        {
            if (!Take(4)) return 0;
            uint v = (uint)_buffer[_pos]
                   | ((uint)_buffer[_pos + 1] << 8)
                   | ((uint)_buffer[_pos + 2] << 16)
                   | ((uint)_buffer[_pos + 3] << 24);
            _pos += 4;
            return v;
        }

        public int ReadInt32() => unchecked((int)ReadUInt32());

        public ulong ReadUInt64()
        {
            uint lo = ReadUInt32();
            uint hi = ReadUInt32();
            if (Failed) return 0;
            return lo | ((ulong)hi << 32);
        }

        public float ReadSingle()
        {
            if (!Take(4)) return 0f;
            var tmp = new byte[4];
            Buffer.BlockCopy(_buffer, _pos, tmp, 0, 4);
            _pos += 4;
            if (!BitConverter.IsLittleEndian) Array.Reverse(tmp);
            float v = BitConverter.ToSingle(tmp, 0);

            // NaN/Infinity are never legitimate gameplay values and propagate through
            // physics and interpolation; reject rather than quarantine them later.
            if (float.IsNaN(v) || float.IsInfinity(v))
            {
                Failed = true;
                return 0f;
            }
            return v;
        }

        /// <summary>
        /// Reads a UTF-8 string with a 16-bit byte-length prefix. The declared length
        /// is validated against <see cref="ProtocolConstants.MaxStringBytes"/> AND
        /// against the bytes actually remaining, before anything is allocated.
        /// </summary>
        public string ReadString()
        {
            ushort n = ReadUInt16();
            if (Failed) return string.Empty;

            if (n > ProtocolConstants.MaxStringBytes) { Failed = true; return string.Empty; }
            if (!Take(n)) return string.Empty;

            string s;
            try
            {
                // Throws on invalid UTF-8 because of the throwing decoder below.
                s = StrictUtf8.GetString(_buffer, _pos, n);
            }
            catch (ArgumentException)
            {
                Failed = true;
                return string.Empty;
            }

            _pos += n;
            return s;
        }

        /// <summary>
        /// Reads a blob with a 32-bit byte-length prefix. Guards against negative and
        /// oversized lengths before allocating.
        /// </summary>
        public byte[] ReadBlob()
        {
            int n = ReadInt32();
            if (Failed) return Empty;

            if (n < 0 || n > ProtocolConstants.MaxBlobBytes) { Failed = true; return Empty; }
            if (n == 0) return Empty;
            if (!Take(n)) return Empty;

            var data = new byte[n];
            Buffer.BlockCopy(_buffer, _pos, data, 0, n);
            _pos += n;
            return data;
        }

        /// <summary>Reads exactly <paramref name="n"/> raw bytes.</summary>
        public byte[] ReadBytesRaw(int n)
        {
            if (n < 0 || n > ProtocolConstants.MaxBlobBytes) { Failed = true; return Empty; }
            if (n == 0) return Empty;
            if (!Take(n)) return Empty;

            var data = new byte[n];
            Buffer.BlockCopy(_buffer, _pos, data, 0, n);
            _pos += n;
            return data;
        }

        /// <summary>Marks the reader failed — for semantic validation by a decoder.</summary>
        public void Invalidate() => Failed = true;

        private static readonly byte[] Empty = new byte[0];

        /// <summary>UTF-8 that throws on malformed input rather than inserting U+FFFD.</summary>
        private static readonly Encoding StrictUtf8 =
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    }
}
