using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using DAVFX.TextureChannelPacker.Codecs;
using NUnit.Framework;

namespace DAVFX.TextureChannelPacker.Tests
{
    public class PngCodecTests
    {
        static readonly int[][] s_Sizes = { new[] { 1, 1 }, new[] { 3, 5 }, new[] { 17, 9 }, new[] { 64, 64 } };

        [Test]
        public void RoundTrip_AllLayouts_8And16Bit([Values(8, 16)] int bits,
            [Values(OutputLayout.RGBA, OutputLayout.RGB, OutputLayout.Grayscale, OutputLayout.GrayscaleAlpha)] OutputLayout layout)
        {
            int max = (1 << bits) - 1;
            foreach (var size in s_Sizes)
            {
                bool gray = PackRecipe.IsGray(layout);
                bool alpha = PackRecipe.HasAlpha(layout);
                var source = TestImages.Noise(size[0], size[1], bits, gray, alpha, 1234 + size[0]);
                byte[] png = PngCodec.Encode(source, layout, bits);
                var decoded = PngCodec.Decode(png);

                Assert.AreEqual(bits == 16 ? SamplePrecision.UNorm16 : SamplePrecision.UNorm8, decoded.Precision);
                Assert.AreEqual(gray, decoded.IsGrayscale, "grayscale stays grayscale");
                Assert.AreEqual(alpha, decoded.HasAlpha, "alpha presence");
                TestImages.AssertImageExact(source, decoded, max);
            }
        }

        [Test]
        public void Decode_GivesExactNormalizedFloats()
        {
            var source = TestImages.Noise(16, 16, 8, false, true, 7);
            var decoded = PngCodec.Decode(PngCodec.Encode(source, OutputLayout.RGBA, 8));
            // Not just the same integers: the very same floats, so passthrough planes are bit identical.
            CollectionAssert.AreEqual(source.R, decoded.R);
            CollectionAssert.AreEqual(source.A, decoded.A);
        }

        [Test]
        public void Encode_HandlesConstantAndGradientImages()
        {
            var flat = TestImages.Constant(300, 7, 0.5f, 0.5f, 1f);
            TestImages.AssertImageExact(flat, PngCodec.Decode(PngCodec.Encode(flat, OutputLayout.RGB, 8)), 255);

            int w = 256, h = 4;
            var ramp = new float[w * h];
            for (int i = 0; i < ramp.Length; i++)
                ramp[i] = (i % w) / 255f;
            var gradient = PixelImage.Gray(w, h, ramp, null, SamplePrecision.UNorm8);
            TestImages.AssertImageExact(gradient, PngCodec.Decode(PngCodec.Encode(gradient, OutputLayout.Grayscale, 8)), 255);
        }

        [Test]
        public void Decode_HandWritten1BitPalettePng()
        {
            // 2x2, 1-bit palette (red, blue), tRNS makes index 1 half transparent.
            var raw = new byte[] { 0, 0x40, 0, 0x80 }; // row0: [0,1], row1: [1,0]
            byte[] png = BuildPng(2, 2, 1, 3, raw,
                ("PLTE", new byte[] { 255, 0, 0, 0, 0, 255 }),
                ("tRNS", new byte[] { 255, 128 }));
            var image = PngCodec.Decode(png);
            Assert.AreEqual(1f, image.R[0]);
            Assert.AreEqual(1f, image.B[1]);
            Assert.AreEqual(128, ColorMath.Quantize(image.A[1], 255));
            Assert.AreEqual(1f, image.A[0]);
            Assert.AreEqual(1f, image.B[2]);
            Assert.AreEqual(1f, image.R[3]);
        }

        [Test]
        public void Decode_RejectsGarbage()
        {
            Assert.Throws<PackException>(() => PngCodec.Decode(new byte[] { 1, 2, 3 }));
            var png = PngCodec.Encode(TestImages.GrayNoise(8, 8, 1), OutputLayout.Grayscale, 8);
            var truncated = new byte[png.Length / 2];
            Array.Copy(png, truncated, truncated.Length);
            Assert.Throws<PackException>(() => PngCodec.Decode(truncated));
        }

        /// <summary>Builds a PNG from already filtered scanlines (filter bytes included).</summary>
        internal static byte[] BuildPng(int width, int height, int bitDepth, int colorType, byte[] filteredRows, params (string type, byte[] data)[] extra)
        {
            using (var ms = new MemoryStream())
            {
                ms.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, 0, 8);
                var ihdr = new byte[13];
                WriteBE(ihdr, 0, width);
                WriteBE(ihdr, 4, height);
                ihdr[8] = (byte)bitDepth;
                ihdr[9] = (byte)colorType;
                Chunk(ms, "IHDR", ihdr);
                foreach (var (type, data) in extra)
                    Chunk(ms, type, data);
                Chunk(ms, "IDAT", Zlib.Compress(filteredRows));
                Chunk(ms, "IEND", new byte[0]);
                return ms.ToArray();
            }
        }

        static void Chunk(Stream s, string type, byte[] data)
        {
            var len = new byte[4];
            WriteBE(len, 0, data.Length);
            s.Write(len, 0, 4);
            var typeBytes = Encoding.ASCII.GetBytes(type);
            s.Write(typeBytes, 0, 4);
            s.Write(data, 0, data.Length);
            uint crc = Zlib.Crc32(typeBytes, 0, 4);
            crc = Zlib.Crc32(data, 0, data.Length, crc);
            var c = new byte[4];
            WriteBE(c, 0, (int)crc);
            s.Write(c, 0, 4);
        }

        static void WriteBE(byte[] d, int p, int v)
        {
            d[p] = (byte)(v >> 24);
            d[p + 1] = (byte)(v >> 16);
            d[p + 2] = (byte)(v >> 8);
            d[p + 3] = (byte)v;
        }
    }

    public class TgaCodecTests
    {
        [Test]
        public void RoundTrip_AllLayouts_RawAndRle([Values(false, true)] bool rle,
            [Values(OutputLayout.RGBA, OutputLayout.RGB, OutputLayout.Grayscale, OutputLayout.GrayscaleAlpha)] OutputLayout layout)
        {
            bool gray = PackRecipe.IsGray(layout);
            bool alpha = PackRecipe.HasAlpha(layout);
            var source = TestImages.Noise(37, 21, 8, gray, alpha, 99);
            var decoded = TgaCodec.Decode(TgaCodec.Encode(source, layout, rle));
            TestImages.AssertImageExact(source, decoded, 255);
            Assert.AreEqual(alpha, decoded.HasAlpha);
        }

        [Test]
        public void Rle_CompressesFlatAreasAndStaysExact()
        {
            var flat = TestImages.Constant(500, 3, 0.2f, 0.4f, 0.6f, 1f);
            byte[] raw = TgaCodec.Encode(flat, OutputLayout.RGBA, false);
            byte[] rle = TgaCodec.Encode(flat, OutputLayout.RGBA, true);
            Assert.Less(rle.Length, raw.Length / 10);
            TestImages.AssertImageExact(flat, TgaCodec.Decode(rle), 255);
        }

        [Test]
        public void Decode_TopLeftOrigin_And16Bit()
        {
            // 2x1 image, 24-bit, top-left origin: first pixel red, second green.
            var data = new byte[18 + 6];
            data[2] = 2;
            data[12] = 2;
            data[14] = 1;
            data[16] = 24;
            data[17] = 0x20;
            data[18 + 2] = 255;     // pixel 0: B G R = 0 0 255
            data[18 + 4] = 255;     // pixel 1: G = 255
            var image = TgaCodec.Decode(data);
            Assert.AreEqual(1f, image.R[0]);
            Assert.AreEqual(1f, image.G[1]);

            // 1x2 image, 16-bit A1R5G5B5, bottom-left origin: file row 0 is the bottom row.
            var d16 = new byte[18 + 4];
            d16[2] = 2;
            d16[12] = 1;
            d16[14] = 2;
            d16[16] = 16;
            d16[17] = 1;
            d16[18] = 0x00; d16[19] = 0xFC; // bottom: A=1, R=31
            d16[20] = 0x1F; d16[21] = 0x00; // top: A=0, B=31
            var image16 = TgaCodec.Decode(d16);
            Assert.AreEqual(1f, image16.B[0], "top pixel blue");
            Assert.AreEqual(0f, image16.A[0], "top pixel transparent");
            Assert.AreEqual(1f, image16.R[1], "bottom pixel red");
            Assert.AreEqual(1f, image16.A[1], "bottom pixel opaque");
        }

        [Test]
        public void Decode_32BitWithUnusedZeroAlpha_IsOpaque()
        {
            var data = new byte[18 + 4];
            data[2] = 2;
            data[12] = 1;
            data[14] = 1;
            data[16] = 32;
            data[17] = 0; // no alpha bits declared, alpha bytes all zero
            data[18] = 10; data[19] = 20; data[20] = 30; data[21] = 0;
            var image = TgaCodec.Decode(data);
            Assert.IsFalse(image.HasAlpha);
        }
    }

    public class ExrWriterTests
    {
        [Test]
        public void Float_RoundTripsBitExact([Values(OutputLayout.RGBA, OutputLayout.RGB)] OutputLayout layout)
        {
            var random = new Random(5);
            int w = 33, h = 40, n = w * h;
            var planes = new float[4][];
            for (int c = 0; c < 4; c++)
            {
                planes[c] = new float[n];
                for (int i = 0; i < n; i++)
                    planes[c][i] = (float)((random.NextDouble() - 0.25) * 100.0); // HDR, negative values included
            }
            var image = new PixelImage(w, h, planes[0], planes[1], planes[2], PackRecipe.HasAlpha(layout) ? planes[3] : null, SamplePrecision.Float);
            byte[] exr = ExrWriter.Encode(image, layout, true);

            var probe = ImageProbe.Probe(exr, ".exr");
            Assert.AreEqual(w, probe.Width);
            Assert.AreEqual(h, probe.Height);
            Assert.AreEqual(SamplePrecision.Float, probe.Precision);

            var decoded = ExrTestReader.Read(exr);
            CollectionAssert.AreEqual(image.R, decoded["R"]);
            CollectionAssert.AreEqual(image.G, decoded["G"]);
            CollectionAssert.AreEqual(image.B, decoded["B"]);
            if (PackRecipe.HasAlpha(layout))
                CollectionAssert.AreEqual(image.A, decoded["A"]);
            else
                Assert.IsFalse(decoded.ContainsKey("A"));
        }

        [Test]
        public void Half_StoresNearestHalf()
        {
            var image = TestImages.Noise(20, 20, 8, false, true, 3);
            byte[] exr = ExrWriter.Encode(image, OutputLayout.RGBA, false);
            Assert.AreEqual(SamplePrecision.Half, ImageProbe.Probe(exr, ".exr").Precision);
            var decoded = ExrTestReader.Read(exr);
            for (int i = 0; i < image.PixelCount; i++)
            {
                Assert.AreEqual(HalfConverter.HalfToFloat(HalfConverter.FloatToHalf(image.R[i])), decoded["R"][i]);
                Assert.AreEqual(ColorMath.Quantize(image.A[i], 255), ColorMath.Quantize(decoded["A"][i], 255));
            }
        }

        [Test]
        public void IncompressibleBlocks_AreStoredRaw()
        {
            // Random float bits barely compress; the writer must fall back to raw blocks and still read back.
            var random = new Random(11);
            int w = 8, h = 3, n = w * h;
            var r = new float[n];
            for (int i = 0; i < n; i++)
                r[i] = (float)random.NextDouble();
            var image = PixelImage.Gray(w, h, r, null, SamplePrecision.Float);
            var decoded = ExrTestReader.Read(ExrWriter.Encode(image, OutputLayout.Grayscale, true));
            CollectionAssert.AreEqual(r, decoded["R"]);
            CollectionAssert.AreEqual(r, decoded["G"]);
        }
    }

    /// <summary>Independent reader for the subset ExrWriter produces (scanline, ZIP or raw blocks).</summary>
    public static class ExrTestReader
    {
        public static Dictionary<string, float[]> Read(byte[] d)
        {
            Assert.AreEqual(20000630, BitConverter.ToInt32(d, 0), "magic");
            int pos = 8;
            var names = new List<string>();
            var types = new List<int>();
            int width = 0, height = 0, compression = -1;
            while (true)
            {
                string name = CString(d, ref pos);
                if (name.Length == 0)
                    break;
                string type = CString(d, ref pos);
                int size = BitConverter.ToInt32(d, pos);
                pos += 4;
                int start = pos;
                if (type == "chlist")
                {
                    int p = start;
                    while (true)
                    {
                        string ch = CString(d, ref p);
                        if (ch.Length == 0)
                            break;
                        names.Add(ch);
                        types.Add(BitConverter.ToInt32(d, p));
                        p += 16;
                    }
                }
                else if (name == "dataWindow")
                {
                    width = BitConverter.ToInt32(d, start + 8) + 1;
                    height = BitConverter.ToInt32(d, start + 12) + 1;
                }
                else if (name == "compression")
                {
                    compression = d[start];
                }
                pos = start + size;
            }
            Assert.AreEqual(3, compression, "ZIP compression");

            var result = new Dictionary<string, float[]>();
            foreach (var n in names)
                result[n] = new float[width * height];

            int lineBytes = 0;
            foreach (int t in types)
                lineBytes += width * (t == 1 ? 2 : 4);

            int blocks = (height + 15) / 16;
            for (int b = 0; b < blocks; b++)
            {
                long offset = BitConverter.ToInt64(d, pos + b * 8);
                int y0 = BitConverter.ToInt32(d, (int)offset);
                int size = BitConverter.ToInt32(d, (int)offset + 4);
                int lines = Math.Min(16, height - y0);
                int expected = lines * lineBytes;
                byte[] raw;
                if (size == expected)
                {
                    raw = new byte[size];
                    Array.Copy(d, offset + 8, raw, 0, size);
                }
                else
                {
                    byte[] tmp = Zlib.Decompress(d, (int)offset + 8, size, expected);
                    for (int i = 1; i < tmp.Length; i++)
                        tmp[i] = (byte)(tmp[i - 1] + tmp[i] - 128);
                    raw = new byte[expected];
                    int half = (expected + 1) / 2;
                    for (int i = 0; i < expected; i++)
                        raw[i] = (i & 1) == 0 ? tmp[i / 2] : tmp[half + i / 2];
                }

                int o = 0;
                for (int line = 0; line < lines; line++)
                {
                    int row = (y0 + line) * width;
                    for (int c = 0; c < names.Count; c++)
                    {
                        var plane = result[names[c]];
                        for (int x = 0; x < width; x++)
                        {
                            if (types[c] == 1)
                            {
                                plane[row + x] = HalfConverter.HalfToFloat(BitConverter.ToUInt16(raw, o));
                                o += 2;
                            }
                            else
                            {
                                plane[row + x] = BitConverter.ToSingle(raw, o);
                                o += 4;
                            }
                        }
                    }
                }
            }
            return result;
        }

        static string CString(byte[] d, ref int pos)
        {
            int start = pos;
            while (d[pos] != 0)
                pos++;
            string s = Encoding.ASCII.GetString(d, start, pos - start);
            pos++;
            return s;
        }
    }

    public class ProbeTests
    {
        [Test]
        public void Psd_And_Tiff_Headers()
        {
            var psd = new byte[32];
            Encoding.ASCII.GetBytes("8BPS").CopyTo(psd, 0);
            psd[5] = 1;
            psd[13] = 3;                 // channels
            psd[16] = 0x02; psd[17] = 0x00; // height 512
            psd[20] = 0x04; psd[21] = 0x00; // width 1024
            psd[23] = 16;                // depth
            var p = ImageProbe.Probe(psd, ".psd");
            Assert.AreEqual(1024, p.Width);
            Assert.AreEqual(512, p.Height);
            Assert.AreEqual(SamplePrecision.UNorm16, p.Precision);

            // Little endian TIFF, IFD at 8 with 3 entries: width 300, height 200, bits 16 (count 1).
            var tif = new byte[64];
            tif[0] = (byte)'I'; tif[1] = (byte)'I'; tif[2] = 42;
            tif[4] = 8;
            tif[8] = 3;
            Entry(tif, 10, 256, 3, 1, 300);
            Entry(tif, 22, 257, 4, 1, 200);
            Entry(tif, 34, 258, 3, 1, 16);
            var t = ImageProbe.Probe(tif, ".tif");
            Assert.AreEqual(300, t.Width);
            Assert.AreEqual(200, t.Height);
            Assert.AreEqual(SamplePrecision.UNorm16, t.Precision);
        }

        [Test]
        public void Hdr_Header()
        {
            byte[] hdr = Encoding.ASCII.GetBytes("#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n-Y 64 +X 128\n");
            var p = ImageProbe.Probe(hdr, ".hdr");
            Assert.AreEqual(128, p.Width);
            Assert.AreEqual(64, p.Height);
            Assert.AreEqual(SamplePrecision.Float, p.Precision);
        }

        static void Entry(byte[] d, int p, int tag, int type, int count, int value)
        {
            d[p] = (byte)tag; d[p + 1] = (byte)(tag >> 8);
            d[p + 2] = (byte)type;
            d[p + 4] = (byte)count;
            d[p + 8] = (byte)value; d[p + 9] = (byte)(value >> 8);
        }
    }

    public class ZlibTests
    {
        [Test]
        public void RoundTrip_And_KnownChecksums()
        {
            byte[] text = Encoding.ASCII.GetBytes("Wikipedia");
            Assert.AreEqual(0x11E60398u, Zlib.Adler32(text, 0, text.Length));
            byte[] check = Encoding.ASCII.GetBytes("123456789");
            Assert.AreEqual(0xCBF43926u, Zlib.Crc32(check, 0, check.Length));

            var random = new Random(1);
            var data = new byte[100000];
            random.NextBytes(data);
            for (int i = 0; i < 50000; i++)
                data[i] = (byte)(i % 7);
            byte[] compressed = Zlib.Compress(data, CompressionLevel.Optimal);
            CollectionAssert.AreEqual(data, Zlib.Decompress(compressed, 0, compressed.Length, data.Length));
        }
    }
}
