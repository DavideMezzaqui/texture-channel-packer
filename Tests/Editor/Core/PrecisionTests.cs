using DAVFX.TextureChannelPacker.Codecs;
using NUnit.Framework;

namespace DAVFX.TextureChannelPacker.Tests
{
    /// <summary>U8, U9, U12: the numeric foundation of "no quality loss".</summary>
    public class PrecisionTests
    {
        [Test]
        public void Every8BitValue_RoundTripsExactly()
        {
            for (int k = 0; k <= 255; k++)
                Assert.AreEqual(k, ColorMath.Quantize(k / 255f, 255), $"value {k}");
        }

        [Test]
        public void Every16BitValue_RoundTripsExactly()
        {
            for (int k = 0; k <= 65535; k++)
            {
                if (ColorMath.Quantize(k / 65535f, 65535) != k)
                    Assert.Fail($"value {k}");
            }
        }

        [Test]
        public void EightBitTo16Bit_IsTimes257_AndBack()
        {
            for (int k = 0; k <= 255; k++)
            {
                int wide = ColorMath.Quantize(k / 255f, 65535);
                Assert.AreEqual(k * 257, wide, $"value {k}");
                Assert.AreEqual(k, ColorMath.Quantize(wide / 65535f, 255), $"value {k} back");
            }
        }

        [Test]
        public void Every8BitValue_SurvivesHalfFloat()
        {
            for (int k = 0; k <= 255; k++)
            {
                float half = HalfConverter.HalfToFloat(HalfConverter.FloatToHalf(k / 255f));
                Assert.AreEqual(k, ColorMath.Quantize(half, 255), $"value {k}");
            }
        }

        [Test]
        public void EveryHalfValue_RoundTripsThroughFloat()
        {
            for (int h = 0; h <= 0xFFFF; h++)
            {
                int exponent = (h >> 10) & 0x1F;
                int mantissa = h & 0x3FF;
                if (exponent == 31 && mantissa != 0)
                    continue; // NaN payloads are not preserved bit for bit.
                float f = HalfConverter.HalfToFloat((ushort)h);
                ushort back = HalfConverter.FloatToHalf(f);
                if (back != h)
                    Assert.Fail($"half 0x{h:X4} -> {f} -> 0x{back:X4}");
            }
        }

        [Test]
        public void FloatToHalf_RoundsToNearestEven()
        {
            // 1 + 2^-11 is exactly halfway between 1 and the next half (1 + 2^-10): ties go to even (1).
            Assert.AreEqual((ushort)0x3C00, HalfConverter.FloatToHalf(1f + 1f / 2048f));
            // Slightly above the tie rounds up.
            Assert.AreEqual((ushort)0x3C01, HalfConverter.FloatToHalf(1f + 1f / 2048f + 1f / 65536f));
            // 1 + 3 * 2^-11 is halfway between 0x3C01 (odd) and 0x3C02 (even): goes to 0x3C02.
            Assert.AreEqual((ushort)0x3C02, HalfConverter.FloatToHalf(1f + 3f / 2048f));
            Assert.AreEqual((ushort)0x7C00, HalfConverter.FloatToHalf(1e6f), "overflow becomes infinity");
            Assert.AreEqual((ushort)0x0001, HalfConverter.FloatToHalf(5.9604645e-8f), "smallest subnormal");
            Assert.AreEqual((ushort)0x8000, HalfConverter.FloatToHalf(-0f), "negative zero");
        }

        [Test]
        public void Quantize_ClampsAndHandlesNaN()
        {
            Assert.AreEqual(0, ColorMath.Quantize(float.NaN, 255));
            Assert.AreEqual(0, ColorMath.Quantize(-3f, 255));
            Assert.AreEqual(255, ColorMath.Quantize(7f, 255));
            Assert.AreEqual(128, ColorMath.Quantize(0.5f, 255), "0.5 rounds to 128 (flat normal)");
        }

        [Test]
        public void SrgbConversions_RoundTripInFloat()
        {
            for (int k = 0; k <= 1000; k++)
            {
                float v = k / 1000f;
                float back = ColorMath.LinearToSrgb(ColorMath.SrgbToLinear(v));
                Assert.AreEqual(v, back, 1e-6f, $"value {v}");
            }
        }
    }
}
