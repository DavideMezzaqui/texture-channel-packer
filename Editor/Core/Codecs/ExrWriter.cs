using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace DAVFX.TextureChannelPacker.Codecs
{
    /// <summary>Float to half conversion with IEEE round-to-nearest-even.</summary>
    public static class HalfConverter
    {
        [StructLayout(LayoutKind.Explicit)]
        struct FloatBits
        {
            [FieldOffset(0)] public float F;
            [FieldOffset(0)] public uint U;
        }

        public static uint SingleToBits(float value)
        {
            return new FloatBits { F = value }.U;
        }

        public static float BitsToSingle(uint bits)
        {
            return new FloatBits { U = bits }.F;
        }

        public static ushort FloatToHalf(float value)
        {
            uint x = new FloatBits { F = value }.U;
            uint sign = (x >> 16) & 0x8000u;
            int exponent = (int)((x >> 23) & 0xFF);
            uint mantissa = x & 0x7FFFFFu;

            if (exponent == 255)
                return (ushort)(sign | 0x7C00u | (mantissa != 0 ? 0x200u | (mantissa >> 13) : 0u));

            int e = exponent - 127 + 15;
            if (e >= 31)
                return (ushort)(sign | 0x7C00u);

            if (e <= 0)
            {
                if (e < -10)
                    return (ushort)sign;
                mantissa |= 0x800000u;
                int shift = 14 - e;
                uint half = mantissa >> shift;
                uint remainder = mantissa & ((1u << shift) - 1u);
                uint halfway = 1u << (shift - 1);
                if (remainder > halfway || (remainder == halfway && (half & 1u) != 0))
                    half++;
                return (ushort)(sign | half);
            }

            uint h = sign | ((uint)e << 10) | (mantissa >> 13);
            uint rest = mantissa & 0x1FFFu;
            if (rest > 0x1000u || (rest == 0x1000u && (h & 1u) != 0))
                h++;
            return (ushort)h;
        }

        public static float HalfToFloat(ushort half)
        {
            uint sign = (uint)(half & 0x8000) << 16;
            int exponent = (half >> 10) & 0x1F;
            uint mantissa = (uint)(half & 0x3FF);
            uint bits;
            if (exponent == 0)
            {
                if (mantissa == 0)
                {
                    bits = sign;
                }
                else
                {
                    // Subnormal: normalize.
                    int e = -1;
                    do
                    {
                        e++;
                        mantissa <<= 1;
                    } while ((mantissa & 0x400u) == 0);
                    bits = sign | (uint)(127 - 15 - e) << 23 | ((mantissa & 0x3FFu) << 13);
                }
            }
            else if (exponent == 31)
            {
                bits = sign | 0x7F800000u | (mantissa << 13);
            }
            else
            {
                bits = sign | (uint)(exponent - 15 + 127) << 23 | (mantissa << 13);
            }
            return new FloatBits { U = bits }.F;
        }
    }

    /// <summary>
    /// Minimal OpenEXR 2 writer: single part scanline image, half or float channels, ZIP (lossless) compression.
    /// Gray layouts are written as R=G=B so every application imports them the same way.
    /// </summary>
    public static class ExrWriter
    {
        const int LinesPerBlock = 16;

        public static byte[] Encode(PixelImage image, OutputLayout layout, bool useFloat)
        {
            if (image == null)
                throw new ArgumentNullException(nameof(image));

            int width = image.Width;
            int height = image.Height;
            bool alpha = PackRecipe.HasAlpha(layout);

            // OpenEXR requires channels sorted by name.
            var channels = new List<KeyValuePair<string, float[]>>();
            if (alpha)
                channels.Add(new KeyValuePair<string, float[]>("A", image.A));
            channels.Add(new KeyValuePair<string, float[]>("B", image.B));
            channels.Add(new KeyValuePair<string, float[]>("G", image.G));
            channels.Add(new KeyValuePair<string, float[]>("R", image.R));

            int sampleBytes = useFloat ? 4 : 2;
            int pixelType = useFloat ? 2 : 1;
            int lineBytes = width * channels.Count * sampleBytes;
            int blockCount = (height + LinesPerBlock - 1) / LinesPerBlock;

            // Build compressed blocks in parallel.
            var blocks = new byte[blockCount][];
            var blockRaw = new bool[blockCount];
            Parallel.For(0, blockCount, block =>
            {
                int y0 = block * LinesPerBlock;
                int lines = Math.Min(LinesPerBlock, height - y0);
                var raw = new byte[lines * lineBytes];
                int o = 0;
                for (int line = 0; line < lines; line++)
                {
                    int row = (y0 + line) * width;
                    foreach (var channel in channels)
                    {
                        var plane = channel.Value;
                        for (int x = 0; x < width; x++)
                        {
                            float v = plane == null ? 1f : plane[row + x];
                            if (useFloat)
                            {
                                uint bits = FloatToBits(v);
                                raw[o++] = (byte)bits;
                                raw[o++] = (byte)(bits >> 8);
                                raw[o++] = (byte)(bits >> 16);
                                raw[o++] = (byte)(bits >> 24);
                            }
                            else
                            {
                                ushort h = HalfConverter.FloatToHalf(v);
                                raw[o++] = (byte)h;
                                raw[o++] = (byte)(h >> 8);
                            }
                        }
                    }
                }

                byte[] compressed = CompressZip(raw);
                if (compressed.Length < raw.Length)
                {
                    blocks[block] = compressed;
                }
                else
                {
                    // OpenEXR rule: when compression does not help, the block is stored raw.
                    blocks[block] = raw;
                    blockRaw[block] = true;
                }
            });

            // The file is assembled in one MemoryStream, limited to 2 GB (header and offsets are a few KB at most).
            long dataBytes = 0;
            foreach (var block in blocks)
                dataBytes += block.Length + 8L + 8L;
            if (dataBytes > int.MaxValue - (1 << 20))
                throw new PackException($"{width}x{height} does not fit in a single EXR file under 2 GB with this data. Use Half instead of Float, fewer channels or a smaller size.");

            using (var output = new MemoryStream())
            using (var writer = new BinaryWriter(output, Encoding.ASCII, true))
            {
                writer.Write(20000630); // magic 0x762f3101
                writer.Write(2);        // version 2, single part scanline

                // channels
                var chlist = new MemoryStream();
                using (var cw = new BinaryWriter(chlist, Encoding.ASCII, true))
                {
                    foreach (var channel in channels)
                    {
                        WriteCString(cw, channel.Key);
                        cw.Write(pixelType);
                        cw.Write((byte)0); // pLinear
                        cw.Write((byte)0);
                        cw.Write((byte)0);
                        cw.Write((byte)0);
                        cw.Write(1); // xSampling
                        cw.Write(1); // ySampling
                    }
                    cw.Write((byte)0);
                }
                WriteAttribute(writer, "channels", "chlist", chlist.ToArray());
                WriteAttribute(writer, "compression", "compression", new byte[] { 3 }); // ZIP_COMPRESSION
                WriteAttribute(writer, "dataWindow", "box2i", Box(width, height));
                WriteAttribute(writer, "displayWindow", "box2i", Box(width, height));
                WriteAttribute(writer, "lineOrder", "lineOrder", new byte[] { 0 }); // INCREASING_Y
                WriteAttribute(writer, "pixelAspectRatio", "float", BitConverter.GetBytes(1f));
                WriteAttribute(writer, "screenWindowCenter", "v2f", Concat(BitConverter.GetBytes(0f), BitConverter.GetBytes(0f)));
                WriteAttribute(writer, "screenWindowWidth", "float", BitConverter.GetBytes(1f));
                writer.Write((byte)0); // end of header

                long tableStart = output.Position;
                long offset = tableStart + 8L * blockCount;
                for (int block = 0; block < blockCount; block++)
                {
                    writer.Write(offset);
                    offset += 8 + blocks[block].Length;
                }
                for (int block = 0; block < blockCount; block++)
                {
                    writer.Write(block * LinesPerBlock);
                    writer.Write(blocks[block].Length);
                    writer.Write(blocks[block]);
                }
                writer.Flush();
                return output.ToArray();
            }
        }

        /// <summary>OpenEXR ZIP: interleave bytes, delta predictor, zlib.</summary>
        static byte[] CompressZip(byte[] raw)
        {
            int n = raw.Length;
            var tmp = new byte[n];
            int t1 = 0;
            int t2 = (n + 1) / 2;
            int r = 0;
            while (true)
            {
                if (r < n) tmp[t1++] = raw[r++];
                else break;
                if (r < n) tmp[t2++] = raw[r++];
                else break;
            }

            int previous = tmp.Length > 0 ? tmp[0] : 0;
            for (int i = 1; i < n; i++)
            {
                int current = tmp[i];
                tmp[i] = (byte)(current - previous + 128 + 256);
                previous = current;
            }
            return Zlib.Compress(tmp, 0, n, CompressionLevel.Optimal);
        }

        static uint FloatToBits(float value)
        {
            return HalfConverter.SingleToBits(value);
        }

        static byte[] Box(int width, int height)
        {
            var data = new byte[16];
            Buffer.BlockCopy(BitConverter.GetBytes(0), 0, data, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(0), 0, data, 4, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(width - 1), 0, data, 8, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(height - 1), 0, data, 12, 4);
            return data;
        }

        static byte[] Concat(byte[] a, byte[] b)
        {
            var result = new byte[a.Length + b.Length];
            Buffer.BlockCopy(a, 0, result, 0, a.Length);
            Buffer.BlockCopy(b, 0, result, a.Length, b.Length);
            return result;
        }

        static void WriteCString(BinaryWriter writer, string value)
        {
            writer.Write(Encoding.ASCII.GetBytes(value));
            writer.Write((byte)0);
        }

        static void WriteAttribute(BinaryWriter writer, string name, string type, byte[] value)
        {
            WriteCString(writer, name);
            WriteCString(writer, type);
            writer.Write(value.Length);
            writer.Write(value);
        }
    }
}
