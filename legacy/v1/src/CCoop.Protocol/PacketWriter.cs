using System;
using System.Text;

namespace CCoop.Protocol
{
    /// <summary>
    /// Little-endian binary writer. Explicit byte order rather than
    /// <see cref="BitConverter"/> for integers so the wire format is identical
    /// regardless of host endianness.
    /// </summary>
    public sealed class PacketWriter
    {
        private byte[] _buffer;
        private int _length;

        public PacketWriter(int capacity = 256)
        {
            if (capacity < 16) capacity = 16;
            _buffer = new byte[capacity];
        }

        public int Length => _length;

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

        public void WriteBool(bool v) => WriteByte(v ? (byte)1 : (byte)0);

        public void WriteUInt16(ushort v)
        {
            Need(2);
            _buffer[_length++] = (byte)v;
            _buffer[_length++] = (byte)(v >> 8);
        }

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

        public void WriteSingle(float v)
        {
            // Reinterpret through the integer path so byte order is explicit.
            var bytes = BitConverter.GetBytes(v);
            if (!BitConverter.IsLittleEndian) Array.Reverse(bytes);
            WriteBytesRaw(bytes, 0, 4);
        }

        /// <summary>
        /// Writes a UTF-8 string with a 16-bit byte-length prefix.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// If the encoded form exceeds <see cref="ProtocolConstants.MaxStringBytes"/>.
        /// Thrown on the *send* side deliberately: an over-long string is a local
        /// programming error, whereas the receive side must never throw.
        /// </exception>
        public void WriteString(string s)
        {
            if (s == null) s = string.Empty;
            var bytes = Encoding.UTF8.GetBytes(s);
            if (bytes.Length > ProtocolConstants.MaxStringBytes)
                throw new ArgumentException(
                    "string exceeds MaxStringBytes (" + bytes.Length + " > " +
                    ProtocolConstants.MaxStringBytes + ")", nameof(s));

            WriteUInt16((ushort)bytes.Length);
            WriteBytesRaw(bytes, 0, bytes.Length);
        }

        /// <summary>Writes a blob with a 32-bit byte-length prefix.</summary>
        public void WriteBlob(byte[] data)
        {
            int n = data?.Length ?? 0;
            if (n > ProtocolConstants.MaxBlobBytes)
                throw new ArgumentException(
                    "blob exceeds MaxBlobBytes (" + n + " > " +
                    ProtocolConstants.MaxBlobBytes + ")", nameof(data));

            WriteInt32(n);
            if (n > 0) WriteBytesRaw(data, 0, n);
        }

        public void WriteBytesRaw(byte[] data, int offset, int count)
        {
            Need(count);
            Buffer.BlockCopy(data, offset, _buffer, _length, count);
            _length += count;
        }

        /// <summary>Copies the written bytes into a right-sized array.</summary>
        public byte[] ToArray()
        {
            var result = new byte[_length];
            Buffer.BlockCopy(_buffer, 0, result, 0, _length);
            return result;
        }

        /// <summary>
        /// The internal buffer, to allow a zero-copy send. Only the first
        /// <see cref="Length"/> bytes are meaningful.
        /// </summary>
        public byte[] UnsafeBuffer => _buffer;

        /// <summary>
        /// Overwrites 2 bytes at an earlier position — used to backfill the payload
        /// length in the header once the body has been written.
        /// </summary>
        public void PatchUInt16(int position, ushort value)
        {
            if (position < 0 || position + 2 > _length)
                throw new ArgumentOutOfRangeException(nameof(position));
            _buffer[position] = (byte)value;
            _buffer[position + 1] = (byte)(value >> 8);
        }
    }
}
