using System;
using System.IO;
using System.IO.Compression;

namespace DAVFX.TextureChannelPacker.Codecs
{
    /// <summary>zlib (RFC 1950) wrapper around DeflateStream, plus Adler-32 and CRC-32.</summary>
    public static class Zlib
    {
        public static byte[] Compress(byte[] data, int offset, int count, CompressionLevel level = CompressionLevel.Optimal)
        {
            using (var output = new MemoryStream(count / 2 + 64))
            {
                output.WriteByte(0x78);
                output.WriteByte(level == CompressionLevel.Fastest ? (byte)0x01 : (byte)0xDA);
                using (var deflate = new DeflateStream(output, level, true))
                    deflate.Write(data, offset, count);
                uint adler = Adler32(data, offset, count);
                output.WriteByte((byte)(adler >> 24));
                output.WriteByte((byte)(adler >> 16));
                output.WriteByte((byte)(adler >> 8));
                output.WriteByte((byte)adler);
                return output.ToArray();
            }
        }

        public static byte[] Compress(byte[] data, CompressionLevel level = CompressionLevel.Optimal)
        {
            return Compress(data, 0, data.Length, level);
        }

        /// <summary>Inflates a zlib stream into exactly <paramref name="expectedLength"/> bytes.</summary>
        public static byte[] Decompress(byte[] data, int offset, int count, int expectedLength)
        {
            var result = new byte[expectedLength];
            Decompress(data, offset, count, result, expectedLength);
            return result;
        }

        /// <summary>Inflates a zlib stream into the first <paramref name="expectedLength"/> bytes of <paramref name="result"/>.</summary>
        public static void Decompress(byte[] data, int offset, int count, byte[] result, int expectedLength)
        {
            if (result == null || result.Length < expectedLength)
                throw new ArgumentException("The output buffer is too small.", nameof(result));
            if (count < 2)
                throw new PackException("Compressed data is truncated.");
            int cmf = data[offset];
            int flg = data[offset + 1];
            if ((cmf & 0x0F) != 8 || ((cmf << 8) | flg) % 31 != 0)
                throw new PackException("Invalid zlib header.");
            if ((flg & 0x20) != 0)
                throw new PackException("zlib preset dictionaries are not supported.");

            int read = 0;
            try
            {
                using (var input = new MemoryStream(data, offset + 2, count - 2, false))
                using (var inflate = new DeflateStream(input, CompressionMode.Decompress))
                {
                    while (read < expectedLength)
                    {
                        int n = inflate.Read(result, read, expectedLength - read);
                        if (n <= 0)
                            break;
                        read += n;
                    }
                }
            }
            catch (Exception e) when (e is InvalidDataException || e is IOException || e is NotSupportedException)
            {
                throw new PackException("Compressed data is corrupted.", e);
            }
            if (read != expectedLength)
                throw new PackException($"Compressed data is truncated ({read} of {expectedLength} bytes).");
        }

        public static uint Adler32(byte[] data, int offset, int count)
        {
            const uint mod = 65521;
            uint a = 1, b = 0;
            int end = offset + count;
            int i = offset;
            while (i < end)
            {
                // 5552 is the largest block that cannot overflow before the modulo.
                int block = Math.Min(5552, end - i);
                for (int k = 0; k < block; k++)
                {
                    a += data[i++];
                    b += a;
                }
                a %= mod;
                b %= mod;
            }
            return (b << 16) | a;
        }

        static readonly uint[] s_CrcTable = BuildCrcTable();

        static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[n] = c;
            }
            return table;
        }

        public static uint Crc32(byte[] data, int offset, int count, uint crc = 0)
        {
            uint c = crc ^ 0xFFFFFFFFu;
            for (int i = offset; i < offset + count; i++)
                c = s_CrcTable[(c ^ data[i]) & 0xFF] ^ (c >> 8);
            return c ^ 0xFFFFFFFFu;
        }
    }
}
