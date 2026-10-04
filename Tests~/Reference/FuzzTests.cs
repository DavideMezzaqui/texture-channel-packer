using System;
using System.Collections.Generic;
using System.Diagnostics;
using DAVFX.TextureChannelPacker.Codecs;
using NUnit.Framework;

namespace DAVFX.TextureChannelPacker.Tests
{
    /// <summary>
    /// Feeds corrupted files to the decoders and the header probe. A decoder may succeed or throw
    /// PackException, nothing else: any other exception would surface as an unexpected error in Unity.
    /// </summary>
    public class FuzzTests
    {
        static IEnumerable<(string name, byte[] data, string ext)> Seeds()
        {
            yield return ("png8", PngCodec.Encode(TestImages.Noise(13, 7, 8, false, true, 1), OutputLayout.RGBA, 8), ".png");
            yield return ("png16", PngCodec.Encode(TestImages.Noise(9, 11, 16, true, false, 2), OutputLayout.Grayscale, 16), ".png");
            yield return ("pngGA", PngCodec.Encode(TestImages.Noise(5, 5, 8, true, true, 3), OutputLayout.GrayscaleAlpha, 8), ".png");
            yield return ("tga", TgaCodec.Encode(TestImages.Noise(17, 9, 8, false, true, 4), OutputLayout.RGBA, false), ".tga");
            yield return ("tgaRle", TgaCodec.Encode(TestImages.Constant(40, 4, 0.1f, 0.2f, 0.3f), OutputLayout.RGB, true), ".tga");
            yield return ("tgaGray", TgaCodec.Encode(TestImages.GrayNoise(6, 6, 5), OutputLayout.Grayscale, true), ".tga");
            yield return ("exr", ExrWriter.Encode(TestImages.Noise(8, 20, 8, false, true, 6), OutputLayout.RGBA, false), ".exr");
            foreach (var (compression, base64) in ExrSamples.Rgba)
                yield return ("exr" + compression, ExrSamples.Bytes(base64), ".exr");
            foreach (var (compression, base64) in ExrSamples.GrayFloat)
                yield return ("exrGray" + compression, ExrSamples.Bytes(base64), ".exr");
        }

        static void Check(byte[] data, string ext, string label)
        {
            var watch = Stopwatch.StartNew();
            try
            {
                ImageEncoder.TryDecode(data, ext);
            }
            catch (PackException)
            {
            }
            catch (Exception e)
            {
                Assert.Fail($"{label}: decoder threw {e.GetType().Name}: {e.Message}");
            }

            try
            {
                ImageProbe.Probe(data, ext);
            }
            catch (Exception e)
            {
                Assert.Fail($"{label}: probe threw {e.GetType().Name}: {e.Message}");
            }
            Assert.Less(watch.ElapsedMilliseconds, 5000, $"{label}: took too long (possible infinite loop)");
        }

        [Test]
        public void RandomByteCorruption_OnlyThrowsPackException()
        {
            var random = new Random(1234);
            foreach (var (name, seed, ext) in Seeds())
            {
                for (int iteration = 0; iteration < 1500; iteration++)
                {
                    var data = (byte[])seed.Clone();
                    int edits = 1 + random.Next(6);
                    for (int e = 0; e < edits; e++)
                    {
                        // Headers are where most parsing decisions happen: corrupt them more often.
                        int pos = random.Next(4) == 0 ? random.Next(data.Length) : random.Next(Math.Min(data.Length, 64));
                        data[pos] = (byte)random.Next(256);
                    }
                    Check(data, ext, $"{name} #{iteration}");
                }
            }
        }

        [Test]
        public void Truncation_OnlyThrowsPackException()
        {
            foreach (var (name, seed, ext) in Seeds())
            {
                for (int length = 0; length < seed.Length; length += Math.Max(1, seed.Length / 200))
                {
                    var data = new byte[length];
                    Array.Copy(seed, data, length);
                    Check(data, ext, $"{name} truncated to {length}");
                }
            }
        }

        [Test]
        public void HugeDeclaredSizes_AreRejectedCleanly()
        {
            // PNG header that declares 100000 x 100000 pixels.
            var png = PngCodec.Encode(TestImages.GrayNoise(4, 4, 1), OutputLayout.Grayscale, 8);
            png[16] = 0; png[17] = 1; png[18] = 0x86; png[19] = 0xA0;
            png[20] = 0; png[21] = 1; png[22] = 0x86; png[23] = 0xA0;
            Assert.Throws<PackException>(() => PngCodec.Decode(png));

            // TGA header that declares 65535 x 65535 pixels.
            var tga = TgaCodec.Encode(TestImages.GrayNoise(4, 4, 1), OutputLayout.Grayscale, false);
            tga[12] = 0xFF; tga[13] = 0xFF; tga[14] = 0xFF; tga[15] = 0xFF;
            Assert.Throws<PackException>(() => TgaCodec.Decode(tga));
        }
    }
}
