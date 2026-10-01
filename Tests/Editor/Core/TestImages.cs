using System;
using NUnit.Framework;

namespace DAVFX.TextureChannelPacker.Tests
{
    /// <summary>Deterministic test images and exact comparison helpers.</summary>
    public static class TestImages
    {
        public static float[] RandomPlane(int count, int maxValue, Random random)
        {
            var plane = new float[count];
            for (int i = 0; i < count; i++)
                plane[i] = random.Next(maxValue + 1) / (float)maxValue;
            return plane;
        }

        public static PixelImage Noise(int width, int height, int bits, bool gray, bool alpha, int seed)
        {
            var random = new Random(seed);
            int max = (1 << bits) - 1;
            int count = width * height;
            var precision = bits == 16 ? SamplePrecision.UNorm16 : SamplePrecision.UNorm8;
            float[] a = alpha ? RandomPlane(count, max, random) : null;
            if (gray)
                return PixelImage.Gray(width, height, RandomPlane(count, max, random), a, precision);
            return new PixelImage(width, height, RandomPlane(count, max, random), RandomPlane(count, max, random), RandomPlane(count, max, random), a, precision);
        }

        public static PixelImage GrayNoise(int width, int height, int seed)
        {
            return Noise(width, height, 8, true, false, seed);
        }

        public static PixelImage Constant(int width, int height, float r, float g, float b, float? a = null)
        {
            int n = width * height;
            return new PixelImage(width, height, Fill(n, r), Fill(n, g), Fill(n, b), a.HasValue ? Fill(n, a.Value) : null, SamplePrecision.UNorm8);
        }

        public static float[] Fill(int count, float value)
        {
            var plane = new float[count];
            for (int i = 0; i < count; i++)
                plane[i] = value;
            return plane;
        }

        public static int[] Quantize(float[] plane, int maxValue, int count)
        {
            var q = new int[count];
            for (int i = 0; i < count; i++)
                q[i] = plane == null ? maxValue : ColorMath.Quantize(plane[i], maxValue);
            return q;
        }

        /// <summary>Asserts two planes store exactly the same integer values at a bit depth (null = opaque).</summary>
        public static void AssertPlaneExact(float[] expected, float[] actual, int maxValue, int count, string label)
        {
            var e = Quantize(expected, maxValue, count);
            var a = Quantize(actual, maxValue, count);
            for (int i = 0; i < count; i++)
            {
                if (e[i] != a[i])
                    Assert.Fail($"{label}: pixel {i} expected {e[i]} but was {a[i]} (max {maxValue}).");
            }
        }

        public static void AssertImageExact(PixelImage expected, PixelImage actual, int maxValue, bool compareAlpha = true)
        {
            Assert.AreEqual(expected.Width, actual.Width, "width");
            Assert.AreEqual(expected.Height, actual.Height, "height");
            int n = expected.PixelCount;
            AssertPlaneExact(expected.R, actual.R, maxValue, n, "R");
            AssertPlaneExact(expected.G, actual.G, maxValue, n, "G");
            AssertPlaneExact(expected.B, actual.B, maxValue, n, "B");
            if (compareAlpha)
                AssertPlaneExact(expected.A, actual.A, maxValue, n, "A");
        }

        public static void AssertAllEqual(float[] plane, float value, string label)
        {
            for (int i = 0; i < plane.Length; i++)
            {
                if (plane[i] != value)
                    Assert.Fail($"{label}: pixel {i} expected {value} but was {plane[i]}.");
            }
        }
    }
}
