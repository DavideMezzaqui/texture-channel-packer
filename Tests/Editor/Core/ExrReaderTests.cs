using System;
using System.Text;
using DAVFX.TextureChannelPacker.Codecs;
using NUnit.Framework;

namespace DAVFX.TextureChannelPacker.Tests
{
    public class ExrReaderTests
    {
        static uint Bits(float value)
        {
            return HalfConverter.SingleToBits(value);
        }

        [Test]
        public void OpenExrSamples_DecodeBitExact_ForEveryCompression()
        {
            foreach (var (compression, base64) in ExrSamples.Rgba)
            {
                var image = ImageEncoder.TryDecode(ExrSamples.Bytes(base64), ".exr");
                Assert.AreEqual(ExrSamples.RgbaWidth, image.Width, compression);
                Assert.AreEqual(ExrSamples.RgbaHeight, image.Height, compression);
                Assert.IsTrue(image.HasAlpha, compression);
                Assert.IsFalse(image.IsGrayscale, compression);
                Assert.AreEqual(SamplePrecision.Half, image.Precision, compression);
                StringAssert.Contains(compression == "NO" ? "(NONE)" : $"({compression})", image.SourceDescription);
                for (int y = 0; y < image.Height; y++)
                {
                    for (int x = 0; x < image.Width; x++)
                    {
                        for (int c = 0; c < 4; c++)
                        {
                            uint expected = Bits(HalfConverter.HalfToFloat(ExrSamples.RgbaHalfBits(x, y, c)));
                            uint actual = Bits(image.GetPlane(c)[y * image.Width + x]);
                            if (expected != actual)
                                Assert.Fail($"{compression} ({x}, {y}) channel {c}: expected 0x{expected:X8}, got 0x{actual:X8}");
                        }
                    }
                }
            }

            foreach (var (compression, base64) in ExrSamples.GrayFloat)
            {
                var image = ImageEncoder.TryDecode(ExrSamples.Bytes(base64), ".exr");
                Assert.AreEqual(ExrSamples.GrayWidth, image.Width, compression);
                Assert.AreEqual(ExrSamples.GrayHeight, image.Height, compression);
                Assert.IsTrue(image.IsGrayscale, compression);
                Assert.IsFalse(image.HasAlpha, compression);
                Assert.AreEqual(SamplePrecision.Float, image.Precision, compression);
                for (int y = 0; y < image.Height; y++)
                {
                    for (int x = 0; x < image.Width; x++)
                        Assert.AreEqual(ExrSamples.GrayFloatBits(x, y), Bits(image.R[y * image.Width + x]), $"{compression} ({x}, {y})");
                }
            }
        }

        static readonly float[] s_Specials = { float.NaN, float.PositiveInfinity, float.NegativeInfinity, -0f, 1e-8f, 70000f, -3.5f, 0.1f };

        static PixelImage HdrImage(int width, int height, bool gray, bool alpha, int seed)
        {
            var random = new Random(seed);
            int n = width * height;
            var planes = new float[4][];
            for (int c = 0; c < 4; c++)
            {
                planes[c] = new float[n];
                for (int i = 0; i < n; i++)
                {
                    planes[c][i] = random.Next(5) == 0
                        ? s_Specials[random.Next(s_Specials.Length)]
                        : (float)((random.NextDouble() - 0.3) * 50.0);
                }
            }
            float[] a = alpha ? planes[3] : null;
            return gray
                ? PixelImage.Gray(width, height, planes[0], a, SamplePrecision.Float)
                : new PixelImage(width, height, planes[0], planes[1], planes[2], a, SamplePrecision.Float);
        }

        [Test]
        public void WriterOutput_ReadsBackBitExact(
            [Values(OutputLayout.RGBA, OutputLayout.RGB, OutputLayout.Grayscale, OutputLayout.GrayscaleAlpha)] OutputLayout layout,
            [Values(true, false)] bool useFloat)
        {
            foreach (var size in new[] { new[] { 1, 1 }, new[] { 13, 11 }, new[] { 64, 40 } })
            {
                bool gray = PackRecipe.IsGray(layout);
                bool alpha = PackRecipe.HasAlpha(layout);
                var source = HdrImage(size[0], size[1], gray, alpha, size[0] * 31 + (useFloat ? 1 : 0));
                var image = ExrReader.Decode(ExrWriter.Encode(source, layout, useFloat));

                string label = $"{layout} {(useFloat ? "float" : "half")} {size[0]}x{size[1]}";
                Assert.AreEqual(gray, image.IsGrayscale, label + " gray");
                Assert.AreEqual(alpha, image.HasAlpha, label + " alpha");
                Assert.AreEqual(useFloat ? SamplePrecision.Float : SamplePrecision.Half, image.Precision, label);
                for (int c = 0; c < (alpha ? 4 : 3); c++)
                {
                    var expected = source.GetPlane(c);
                    var actual = image.GetPlane(c);
                    for (int i = 0; i < expected.Length; i++)
                    {
                        float want = useFloat ? expected[i] : HalfConverter.HalfToFloat(HalfConverter.FloatToHalf(expected[i]));
                        if (Bits(want) != Bits(actual[i]))
                            Assert.Fail($"{label} pixel {i} channel {c}: expected 0x{Bits(want):X8}, got 0x{Bits(actual[i]):X8}");
                    }
                }
            }
        }

        [Test]
        public void IdenticalColorChannels_WithDifferentZeroSigns_StayColor()
        {
            int n = 4;
            var r = new[] { 0f, 1f, 2f, 3f };
            var g = new[] { -0f, 1f, 2f, 3f };
            var image = ExrReader.Decode(ExrWriter.Encode(new PixelImage(n, 1, r, g, r, null, SamplePrecision.Float), OutputLayout.RGB, true));
            Assert.IsFalse(image.IsGrayscale, "-0 and +0 differ bit by bit, so G keeps its own plane");
            Assert.AreEqual(0x80000000u, Bits(image.G[0]));
        }

        /// <summary>Writes bits most significant first, like the OpenEXR Huffman coder.</summary>
        sealed class BitWriter
        {
            readonly System.Collections.Generic.List<byte> m_Bytes = new System.Collections.Generic.List<byte>();
            int m_Byte;
            int m_Count;
            public long Bits;

            public void Write(ulong value, int bits)
            {
                for (int b = bits - 1; b >= 0; b--)
                {
                    m_Byte = (m_Byte << 1) | (int)((value >> b) & 1);
                    Bits++;
                    if (++m_Count == 8)
                    {
                        m_Bytes.Add((byte)m_Byte);
                        m_Byte = 0;
                        m_Count = 0;
                    }
                }
            }

            public byte[] ToArray()
            {
                var bytes = new System.Collections.Generic.List<byte>(m_Bytes);
                if (m_Count > 0)
                    bytes.Add((byte)(m_Byte << (8 - m_Count)));
                return bytes.ToArray();
            }
        }

        static void WriteAttribute(System.IO.BinaryWriter writer, string name, string type, byte[] value)
        {
            writer.Write(Encoding.ASCII.GetBytes(name + "\0" + type + "\0"));
            writer.Write(value.Length);
            writer.Write(value);
        }

        [Test]
        public void PizBlock_With65537LongCodes_DecodesQuickly()
        {
            // Every symbol has a 58-bit code (the maximum) and the run-length symbol a 42-bit one: all codes are
            // longer than the 14-bit table, which a linear search would scan for every symbol.
            const int width = 1024, lines = 32, words = width * lines;
            var bits = new BitWriter();
            for (int symbol = 0; symbol < 65536; symbol++)
                bits.Write(58, 6);
            bits.Write(42, 6);
            var table = bits.ToArray();

            // Canonical codes: the 58-bit codes are the symbol numbers, the 42-bit run code is 1.
            var data = new BitWriter();
            data.Write(0, 58);
            for (int left = words - 1; left > 0; left -= 255)
            {
                data.Write(1, 42);
                data.Write((ulong)Math.Min(255, left), 8);
            }
            var stream = data.ToArray();

            var block = new System.IO.MemoryStream();
            using (var writer = new System.IO.BinaryWriter(block, Encoding.ASCII, true))
            {
                writer.Write((ushort)(8192 - 1)); // empty bitmap: only zeros
                writer.Write((ushort)0);
                writer.Write(20 + table.Length + stream.Length);
                writer.Write(0);                  // smallest symbol
                writer.Write(65536);              // largest symbol, the run-length code
                writer.Write(table.Length);
                writer.Write((int)data.Bits);
                writer.Write(0);
                writer.Write(table);
                writer.Write(stream);
            }
            Assert.Less(block.Length, words * 2, "the block must be smaller than its raw size to be decompressed");

            var file = new System.IO.MemoryStream();
            using (var writer = new System.IO.BinaryWriter(file, Encoding.ASCII, true))
            {
                writer.Write(20000630);
                writer.Write(2);
                var channels = new System.IO.MemoryStream();
                using (var cw = new System.IO.BinaryWriter(channels))
                {
                    cw.Write(Encoding.ASCII.GetBytes("Y\0"));
                    cw.Write(1);        // half
                    cw.Write(0);        // pLinear + reserved
                    cw.Write(1);
                    cw.Write(1);
                    cw.Write((byte)0);
                    cw.Flush();
                    WriteAttribute(writer, "channels", "chlist", channels.ToArray());
                }
                WriteAttribute(writer, "compression", "compression", new byte[] { 4 });
                var box = new byte[16];
                BitConverter.GetBytes(width - 1).CopyTo(box, 8);
                BitConverter.GetBytes(lines - 1).CopyTo(box, 12);
                WriteAttribute(writer, "dataWindow", "box2i", box);
                WriteAttribute(writer, "displayWindow", "box2i", box);
                WriteAttribute(writer, "lineOrder", "lineOrder", new byte[] { 0 });
                writer.Write((byte)0);
                writer.Flush();
                writer.Write(file.Position + 8); // offset table: one block right after it
                writer.Write(0);
                writer.Write((int)block.Length);
                writer.Write(block.ToArray());
            }

            var watch = System.Diagnostics.Stopwatch.StartNew();
            var image = ExrReader.Decode(file.ToArray());
            Assert.Less(watch.ElapsedMilliseconds, 2000);
            Assert.AreEqual(width, image.Width);
            Assert.AreEqual(lines, image.Height);
            Assert.IsTrue(image.IsGrayscale);
            foreach (float value in image.R)
                Assert.AreEqual(0u, Bits(value));
        }

        /// <summary>Offset of the value of a header attribute in an EXR file.</summary>
        static int AttributeValue(byte[] exr, string name)
        {
            int pos = 8;
            while (exr[pos] != 0)
            {
                int start = pos;
                while (exr[pos] != 0)
                    pos++;
                string attribute = Encoding.ASCII.GetString(exr, start, pos - start);
                pos++;
                while (exr[pos] != 0)
                    pos++;
                pos++;
                int size = BitConverter.ToInt32(exr, pos);
                pos += 4;
                if (attribute == name)
                    return pos;
                pos += size;
            }
            throw new ArgumentException(name + " not found");
        }

        static byte[] SmallExr()
        {
            return ExrWriter.Encode(HdrImage(9, 20, false, true, 7), OutputLayout.RGBA, false);
        }

        [Test]
        public void ValidVariantsWithoutBuiltInSupport_AreFlaggedUnsupported()
        {
            var cases = new (string label, Action<byte[]> edit)[]
            {
                ("tiled", d => d[5] |= 0x02),
                ("multi-part", d => d[5] |= 0x10),
                ("deep", d => d[5] |= 0x08),
                ("PXR24 compression", d => d[AttributeValue(d, "compression")] = 5),
                ("DWAA compression", d => d[AttributeValue(d, "compression")] = 8),
                ("ZSTD compression", d => d[AttributeValue(d, "compression")] = 13),
                ("display window", d => d[AttributeValue(d, "displayWindow") + 8] += 1),
                ("subsampled channel", d => d[AttributeValue(d, "channels") + 2 + 8] = 2),
            };
            foreach (var (label, edit) in cases)
            {
                var data = SmallExr();
                edit(data);
                var error = Assert.Throws<PackException>(() => ExrReader.Decode(data), label);
                Assert.IsTrue(error.IsUnsupported, $"{label}: {error.Message}");
            }
        }

        [Test]
        public void DamagedFiles_ThrowPackException_NotFlaggedUnsupported()
        {
            var seed = SmallExr();
            for (int length = 4; length < seed.Length; length += 7)
            {
                var data = new byte[length];
                Array.Copy(seed, data, length);
                var error = Assert.Throws<PackException>(() => ExrReader.Decode(data), $"truncated to {length}");
                Assert.IsFalse(error.IsUnsupported, $"truncated to {length}: {error.Message}");
            }

            foreach (var (compression, base64) in ExrSamples.Rgba)
            {
                var data = ExrSamples.Bytes(base64);
                // Last byte of the last block: compressed data, or the last raw sample.
                data[data.Length - 1] ^= 0x5A;
                data[data.Length - 3] ^= 0xA5;
                try
                {
                    ExrReader.Decode(data);
                }
                catch (PackException e)
                {
                    Assert.IsFalse(e.IsUnsupported, $"{compression}: {e.Message}");
                }
            }
        }
    }
}
