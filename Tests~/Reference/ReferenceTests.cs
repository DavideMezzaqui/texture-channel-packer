using System;
using System.IO;
using DAVFX.TextureChannelPacker.Codecs;
using NUnit.Framework;

namespace DAVFX.TextureChannelPacker.Tests
{
    /// <summary>
    /// Driven by reference_check.py: decodes fixtures written by pypng/Pillow and writes files
    /// that the script verifies with pypng/Pillow/OpenEXR. Ignored when run without the script.
    /// </summary>
    public class ReferenceTests
    {
        static string Folder(string variable)
        {
            string path = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrEmpty(path))
                Assert.Ignore($"{variable} not set: run Reference/reference_check.py.");
            return path;
        }

        [Test]
        public void DecodeFixtures_MatchReferenceExactly()
        {
            string folder = Folder("CP_FIXTURES");
            int checkedFiles = 0;
            foreach (string file in Directory.GetFiles(folder))
            {
                string ext = Path.GetExtension(file);
                if (ext != ".png" && ext != ".tga")
                    continue;
                var image = ImageEncoder.TryDecode(File.ReadAllBytes(file), ext);
                Assert.IsNotNull(image, file);

                using (var reader = new BinaryReader(File.OpenRead(Path.ChangeExtension(file, ".expected"))))
                {
                    int w = reader.ReadInt32();
                    int h = reader.ReadInt32();
                    int max = reader.ReadInt32();
                    Assert.AreEqual(w, image.Width, file);
                    Assert.AreEqual(h, image.Height, file);
                    int n = w * h;
                    for (int i = 0; i < n; i++)
                    {
                        for (int c = 0; c < 4; c++)
                        {
                            int expected = reader.ReadUInt16();
                            var plane = image.GetPlane(c);
                            int actual = plane == null ? max : ColorMath.Quantize(plane[i], max);
                            if (expected != actual)
                                Assert.Fail($"{Path.GetFileName(file)} pixel {i} channel {c}: expected {expected}, got {actual}");
                        }
                    }
                }
                checkedFiles++;
            }
            Assert.Greater(checkedFiles, 100);
            TestContext.WriteLine($"Decoded {checkedFiles} reference files exactly.");
        }

        [Test]
        public void DecodeExrFixtures_MatchOpenExrExactly()
        {
            string folder = Folder("CP_FIXTURES");
            int decoded = 0, rejected = 0;
            foreach (string file in Directory.GetFiles(folder, "*.exr"))
            {
                string name = Path.GetFileName(file);
                byte[] bytes = File.ReadAllBytes(file);
                if (File.Exists(Path.ChangeExtension(file, ".exrunsupported")))
                {
                    var error = Assert.Throws<PackException>(() => ImageEncoder.TryDecode(bytes, ".exr"), name);
                    Assert.IsTrue(error.IsUnsupported, $"{name}: should be reported as unsupported, got: {error.Message}");
                    rejected++;
                    continue;
                }

                var image = ImageEncoder.TryDecode(bytes, ".exr");
                Assert.IsNotNull(image, name);
                using (var reader = new BinaryReader(File.OpenRead(Path.ChangeExtension(file, ".exrexpected"))))
                {
                    int w = reader.ReadInt32();
                    int h = reader.ReadInt32();
                    int flags = reader.ReadInt32();
                    var precision = (SamplePrecision)reader.ReadInt32();
                    Assert.AreEqual(w, image.Width, name);
                    Assert.AreEqual(h, image.Height, name);
                    Assert.AreEqual((flags & 1) != 0, image.HasAlpha, name + " alpha");
                    Assert.AreEqual((flags & 2) != 0, image.IsGrayscale, name + " grayscale");
                    Assert.AreEqual(precision, image.Precision, name + " precision");
                    int planes = image.HasAlpha ? 4 : 3;
                    for (int c = 0; c < planes; c++)
                    {
                        var plane = image.GetPlane(c);
                        for (int i = 0; i < w * h; i++)
                        {
                            uint expected = reader.ReadUInt32();
                            uint actual = HalfConverter.SingleToBits(plane[i]);
                            if (expected != actual)
                                Assert.Fail($"{name} pixel {i} channel {c}: expected 0x{expected:X8}, got 0x{actual:X8}");
                        }
                    }
                }
                decoded++;
            }
            Assert.Greater(decoded, 200);
            Assert.Greater(rejected, 10);
            TestContext.WriteLine($"Decoded {decoded} OpenEXR files bit-exactly, rejected {rejected} unsupported variants as expected.");
        }

        [Test]
        public void EncodeOutputs_ForExternalVerification()
        {
            string folder = Folder("CP_OUTPUTS");
            var layouts = new[] { OutputLayout.RGBA, OutputLayout.RGB, OutputLayout.Grayscale, OutputLayout.GrayscaleAlpha };
            int seed = 0;
            foreach (var size in new[] { new[] { 1, 1 }, new[] { 13, 11 }, new[] { 200, 37 } })
            {
                foreach (var layout in layouts)
                {
                    bool gray = PackRecipe.IsGray(layout);
                    bool alpha = PackRecipe.HasAlpha(layout);
                    foreach (int bits in new[] { 8, 16 })
                    {
                        var image = TestImages.Noise(size[0], size[1], bits, gray, alpha, ++seed);
                        string name = $"cs_{layout}_{bits}_{size[0]}x{size[1]}";
                        File.WriteAllBytes(Path.Combine(folder, name + ".png"), PngCodec.Encode(image, layout, bits));
                        WriteExpected(Path.Combine(folder, name + ".expected"), image, (1 << bits) - 1);
                    }

                    var image8 = TestImages.Noise(size[0], size[1], 8, gray, alpha, ++seed);
                    foreach (bool rle in new[] { false, true })
                    {
                        string name = $"cs_{layout}_{(rle ? "rle" : "raw")}_{size[0]}x{size[1]}";
                        File.WriteAllBytes(Path.Combine(folder, name + ".tga"), TgaCodec.Encode(image8, layout, rle));
                        WriteExpected(Path.Combine(folder, name + ".expected"), image8, 255);
                    }

                    var random = new Random(++seed);
                    int count = size[0] * size[1];
                    var planes = new float[4][];
                    for (int c = 0; c < 4; c++)
                    {
                        planes[c] = new float[count];
                        for (int i = 0; i < count; i++)
                            planes[c][i] = (float)((random.NextDouble() - 0.3) * 40.0);
                    }
                    var hdr = gray
                        ? PixelImage.Gray(size[0], size[1], planes[0], alpha ? planes[3] : null, SamplePrecision.Float)
                        : new PixelImage(size[0], size[1], planes[0], planes[1], planes[2], alpha ? planes[3] : null, SamplePrecision.Float);
                    foreach (bool useFloat in new[] { true, false })
                    {
                        string name = $"cs_{layout}_{(useFloat ? "float" : "half")}_{size[0]}x{size[1]}";
                        File.WriteAllBytes(Path.Combine(folder, name + ".exr"), ExrWriter.Encode(hdr, layout, useFloat));
                        WriteFloatExpected(Path.Combine(folder, name + ".fexpected"), hdr, alpha);
                    }
                }
            }
        }

        static void WriteExpected(string path, PixelImage image, int max)
        {
            using (var writer = new BinaryWriter(File.Create(path)))
            {
                writer.Write(image.Width);
                writer.Write(image.Height);
                writer.Write(max);
                for (int i = 0; i < image.PixelCount; i++)
                {
                    for (int c = 0; c < 4; c++)
                    {
                        var plane = image.GetPlane(c);
                        writer.Write((ushort)(plane == null ? max : ColorMath.Quantize(plane[i], max)));
                    }
                }
            }
        }

        static void WriteFloatExpected(string path, PixelImage image, bool alpha)
        {
            int channels = alpha ? 4 : 3;
            using (var writer = new BinaryWriter(File.Create(path)))
            {
                writer.Write(image.Width);
                writer.Write(image.Height);
                writer.Write(channels);
                for (int c = 0; c < channels; c++)
                {
                    var plane = image.GetPlane(c);
                    for (int i = 0; i < image.PixelCount; i++)
                        writer.Write(plane[i]);
                }
            }
        }
    }
}
