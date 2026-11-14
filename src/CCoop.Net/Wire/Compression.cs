using System.IO;
using System.IO.Compression;

namespace CCoop.Net.Wire
{
    public static class Compression
    {
        public static byte[] Deflate(byte[] data, int offset, int count)
        {
            using (var ms = new MemoryStream())
            {
                using (var z = new DeflateStream(ms, CompressionLevel.Optimal, true))
                    z.Write(data, offset, count);
                return ms.ToArray();
            }
        }

        /// <summary>Inflates, refusing output larger than <paramref name="maxBytes"/>.</summary>
        public static byte[] Inflate(byte[] data, int maxBytes)
        {
            using (var input = new MemoryStream(data))
            using (var z = new DeflateStream(input, CompressionMode.Decompress))
            using (var output = new MemoryStream())
            {
                var buf = new byte[81920];
                int n;
                while ((n = z.Read(buf, 0, buf.Length)) > 0)
                {
                    if (output.Length + n > maxBytes) throw new InvalidDataException("inflated data exceeds " + maxBytes + " bytes");
                    output.Write(buf, 0, n);
                }
                return output.ToArray();
            }
        }
    }
}
