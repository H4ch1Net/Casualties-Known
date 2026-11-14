using System;
using System.Text;

namespace CCoop.Net.Wire
{
    /// <summary>Hard ceilings for anything length-prefixed on the wire.</summary>
    public static class WireLimits
    {
        /// <summary>Largest UTF-8 string, in bytes.</summary>
        public const int MaxStringBytes = 4096;

        /// <summary>Largest length-prefixed blob inside one message.</summary>
        public const int MaxBlobBytes = 256 * 1024;

        /// <summary>Largest whole packet a transport will be asked to carry.</summary>
        public const int MaxPacketBytes = MaxBlobBytes + 1024;
    }

    /// <summary>Little-endian binary writer with a growable buffer.</summary>
    public sealed class PacketWriter
    {
        private byte[] _buffer;
        private int _length;

        public PacketWriter(int capacity = 256)
        {
            _buffer = new byte[Math.Max(capacity, 16)];
        }

        public int Length => _length;
        public byte[] Buffer => _buffer;
        public ArraySegment<byte> Segment => new ArraySegment<byte>(_buffer, 0, _length);

        public void Reset() => _length = 0;

        private void Need(int extra)
        {
            int required = _length + extra;
            if (required <= _buffer.Length) return;
            int cap = _buffer.Length;
            while (cap < required) cap *= 2;
            Array.Resize(ref _buffer, cap);
        }

        public void WriteByte(byte v)
        {
            Need(1);
            _buffer[_length++] = v;
        }

        public void WriteSByte(sbyte v) => WriteByte(unchecked((byte)v));
        public void WriteBool(bool v) => WriteByte(v ? (byte)1 : (byte)0);

        public void WriteUInt16(ushort v)
        {
            Need(2);
            _buffer[_length++] = (byte)v;
            _buffer[_length++] = (byte)(v >> 8);
        }

        public void WriteInt16(short v) => WriteUInt16(unchecked((ushort)v));

        public void WriteUInt32(uint v)
        {
            Need(4);
            _buffer[_length++] = (byte)v;
            _buffer[_length++] = (byte)(v >> 8);
            _buffer[_length++] = (byte)(v >> 16);
            _buffer[_length++] = (byte)(v >> 24);
        }

        public void WriteInt32(int v) => WriteUInt32(unchecked((uint)v));

        public void WriteUInt64(ulong v)
        {
            WriteUInt32((uint)(v & 0xFFFFFFFF));
            WriteUInt32((uint)(v >> 32));
        }

        public unsafe void WriteSingle(float v)
        {
            WriteUInt32(*(uint*)&v);
        }

        /// <summary>A float in [-range, range] quantised to 16 bits.</summary>
        public void WriteQuantized(float v, float range)
        {
            float t = (Clamp(v, -range, range) + range) / (2f * range);
            WriteUInt16((ushort)Math.Round(t * ushort.MaxValue));
        }

        /// <summary>An angle in degrees, quantised to 16 bits.</summary>
        public void WriteAngle(float degrees)
        {
            float d = degrees % 360f;
            if (d < 0) d += 360f;
            WriteUInt16((ushort)Math.Round(d / 360f * ushort.MaxValue));
        }

        /// <summary>A value in [0, 1] as one byte.</summary>
        public void WriteUnit(float v) => WriteByte((byte)Math.Round(Clamp(v, 0f, 1f) * 255f));

        public void WriteString(string s)
        {
            if (s == null) s = string.Empty;
            var bytes = Encoding.UTF8.GetBytes(s);
            if (bytes.Length > WireLimits.MaxStringBytes)
                throw new ArgumentException("string exceeds " + WireLimits.MaxStringBytes + " bytes");
            WriteUInt16((ushort)bytes.Length);
            WriteRaw(bytes, 0, bytes.Length);
        }

        public void WriteBlob(byte[] data) => WriteBlob(data, 0, data?.Length ?? 0);

        public void WriteBlob(byte[] data, int offset, int count)
        {
            if (count > WireLimits.MaxBlobBytes)
                throw new ArgumentException("blob exceeds " + WireLimits.MaxBlobBytes + " bytes");
            WriteInt32(count);
            if (count > 0) WriteRaw(data, offset, count);
        }

        public void WriteRaw(byte[] data, int offset, int count)
        {
            Need(count);
            System.Buffer.BlockCopy(data, offset, _buffer, _length, count);
            _length += count;
        }

        public byte[] ToArray()
        {
            var result = new byte[_length];
            System.Buffer.BlockCopy(_buffer, 0, result, 0, _length);
            return result;
        }

        private static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);
    }
}
