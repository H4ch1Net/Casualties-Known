using System;
using System.Text;

namespace CCoop.Net.Wire
{
    /// <summary>
    /// Bounds-checked little-endian reader. Never throws on malformed input: any bad
    /// read sets <see cref="Failed"/> and returns a default, so a decoder checks once
    /// at the end instead of after every field.
    /// </summary>
    public struct PacketReader
    {
        private readonly byte[] _buffer;
        private readonly int _end;
        private int _pos;

        public PacketReader(byte[] buffer, int offset, int count)
        {
            _buffer = buffer ?? new byte[0];
            if (offset < 0 || count < 0 || offset + count > _buffer.Length)
            {
                _pos = _end = 0;
                Failed = true;
                return;
            }
            _pos = offset;
            _end = offset + count;
            Failed = false;
        }

        public PacketReader(ArraySegment<byte> segment) : this(segment.Array, segment.Offset, segment.Count) { }

        public bool Failed { get; private set; }
        public int Remaining => Failed ? 0 : _end - _pos;
        public bool AtEnd => !Failed && _pos == _end;
        public int Position => _pos;

        public void Fail() => Failed = true;

        private bool Take(int n)
        {
            if (Failed) return false;
            if (n < 0 || _end - _pos < n) { Failed = true; return false; }
            return true;
        }

        public byte ReadByte() => Take(1) ? _buffer[_pos++] : (byte)0;
        public sbyte ReadSByte() => unchecked((sbyte)ReadByte());

        public bool ReadBool()
        {
            byte b = ReadByte();
            if (b > 1) { Failed = true; return false; }
            return b == 1;
        }

        public ushort ReadUInt16()
        {
            if (!Take(2)) return 0;
            ushort v = (ushort)(_buffer[_pos] | (_buffer[_pos + 1] << 8));
            _pos += 2;
            return v;
        }

        public short ReadInt16() => unchecked((short)ReadUInt16());

        public uint ReadUInt32()
        {
            if (!Take(4)) return 0;
            uint v = _buffer[_pos]
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
            return lo | ((ulong)hi << 32);
        }

        public unsafe float ReadSingle()
        {
            uint bits = ReadUInt32();
            float v = *(float*)&bits;
            // NaN/Infinity are never legitimate and poison physics and interpolation.
            if (float.IsNaN(v) || float.IsInfinity(v)) { Failed = true; return 0f; }
            return v;
        }

        public float ReadQuantized(float range)
        {
            ushort q = ReadUInt16();
            return q / (float)ushort.MaxValue * 2f * range - range;
        }

        public float ReadAngle() => ReadUInt16() / (float)ushort.MaxValue * 360f;
        public float ReadUnit() => ReadByte() / 255f;

        public string ReadString()
        {
            ushort n = ReadUInt16();
            if (n > WireLimits.MaxStringBytes) { Failed = true; return string.Empty; }
            if (!Take(n)) return string.Empty;
            try
            {
                var s = StrictUtf8.GetString(_buffer, _pos, n);
                _pos += n;
                return s;
            }
            catch (ArgumentException)
            {
                Failed = true;
                return string.Empty;
            }
        }

        public byte[] ReadBlob()
        {
            int n = ReadInt32();
            if (n < 0 || n > WireLimits.MaxBlobBytes) { Failed = true; return Empty; }
            return ReadRaw(n);
        }

        public byte[] ReadRaw(int n)
        {
            if (n == 0) return Empty;
            if (n < 0 || !Take(n)) { Failed = true; return Empty; }
            var data = new byte[n];
            System.Buffer.BlockCopy(_buffer, _pos, data, 0, n);
            _pos += n;
            return data;
        }

        /// <summary>Reads a count and rejects it if it exceeds <paramref name="max"/>.</summary>
        public int ReadCount(int max)
        {
            int n = ReadUInt16();
            if (n > max) { Failed = true; return 0; }
            return n;
        }

        private static readonly byte[] Empty = new byte[0];
        private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    }
}
