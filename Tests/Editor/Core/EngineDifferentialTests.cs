using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace DAVFX.TextureChannelPacker.Tests
{
    /// <summary>
    /// Compares PackEngine with a deliberately naive per pixel implementation on thousands of random recipes,
    /// and checks that the validator and the engine agree on what is an error.
    /// </summary>
    public class EngineDifferentialTests
    {
        const int Width = 7;
        const int Height = 5;

        static Operand RandomOperand(Random random, int slots)
        {
            if (random.Next(4) == 0)
                return Operand.Value((float)random.NextDouble() * 1.2f - 0.1f);
            var op = Operand.Texture(random.Next(slots + 1), (ChannelSource)random.Next(8));
            if (random.Next(2) == 0)
            {
                op.hasFallback = true;
                op.fallback = (float)random.NextDouble();
            }
            return op;
        }

        static ChannelRecipe RandomChannel(Random random, int slots)
        {
            var ch = ChannelRecipe.From(RandomOperand(random, slots), random.Next(3) == 0);
            if (random.Next(3) == 0)
            {
                ch.combine = (CombineOp)random.Next(9);
                ch.other = RandomOperand(random, slots);
            }
            if (random.Next(4) == 0)
                ch.conversion = (ColorConversion)random.Next(3);
            if (random.Next(3) == 0)
            {
                ch.useLevels = true;
                ch.levels = new Levels
                {
                    inMin = (float)random.NextDouble() * 0.4f,
                    inMax = 0.6f + (float)random.NextDouble() * 0.4f,
                    gamma = random.Next(2) == 0 ? 1f : 0.3f + (float)random.NextDouble() * 3f,
                    outMin = (float)random.NextDouble() * 0.3f,
                    outMax = 0.7f + (float)random.NextDouble() * 0.3f
                };
            }
            return ch;
        }

        static PixelImage RandomImage(Random random, int w, int h)
        {
            int kind = random.Next(4);
            bool alpha = random.Next(2) == 0;
            switch (kind)
            {
                case 0: return TestImages.Noise(w, h, 8, true, alpha, random.Next());
                case 1: return TestImages.Noise(w, h, 16, false, alpha, random.Next());
                case 2:
                {
                    var r = new float[w * h];
                    for (int i = 0; i < r.Length; i++)
                        r[i] = (float)(random.NextDouble() * 4.0 - 1.0);
                    return new PixelImage(w, h, r, TestImages.RandomPlane(w * h, 255, random), TestImages.RandomPlane(w * h, 255, random), null, SamplePrecision.Float);
                }
                default: return TestImages.Noise(w, h, 8, false, alpha, random.Next());
            }
        }

        // ------------------------------------------------------------------ naive reference

        sealed class MissingInput : Exception
        {
        }

        static float SampleNaive(Operand op, PixelImage[] inputs, int i)
        {
            if (op.kind == OperandKind.Constant)
                return op.constant;
            var image = op.input >= 0 && op.input < inputs.Length ? inputs[op.input] : null;
            if (image == null)
            {
                if (op.hasFallback)
                    return op.fallback;
                throw new MissingInput();
            }
            float r = image.R[i], g = image.G[i], b = image.B[i];
            switch (op.channel)
            {
                case ChannelSource.Red: return r;
                case ChannelSource.Green: return g;
                case ChannelSource.Blue: return b;
                case ChannelSource.Alpha: return image.A != null ? image.A[i] : 1f;
            }
            if (image.IsGrayscale)
                return r; // every derived value of a gray pixel is the gray value
            switch (op.channel)
            {
                case ChannelSource.Luminance: return 0.2126f * r + 0.7152f * g + 0.0722f * b;
                case ChannelSource.Average: return (r + g + b) / 3f;
                case ChannelSource.Minimum: return Math.Min(r, Math.Min(g, b));
                default: return Math.Max(r, Math.Max(g, b));
            }
        }

        static float Naive(ChannelRecipe ch, PixelImage[] inputs, int i)
        {
            float v = SampleNaive(ch.source, inputs, i);
            if (ch.combine != CombineOp.None)
            {
                float o = SampleNaive(ch.other, inputs, i);
                switch (ch.combine)
                {
                    case CombineOp.Multiply: v = v * o; break;
                    case CombineOp.Add: v = v + o; break;
                    case CombineOp.Subtract: v = v - o; break;
                    case CombineOp.Minimum: v = Math.Min(v, o); break;
                    case CombineOp.Maximum: v = Math.Max(v, o); break;
                    case CombineOp.Average: v = (v + o) * 0.5f; break;
                    case CombineOp.Difference: v = Math.Abs(v - o); break;
                    case CombineOp.Screen: v = 1f - (1f - v) * (1f - o); break;
                }
            }
            if (ch.conversion == ColorConversion.SrgbToLinear)
                v = v <= 0.04045f ? v / 12.92f : (float)Math.Pow((v + 0.055) / 1.055, 2.4);
            else if (ch.conversion == ColorConversion.LinearToSrgb)
                v = v <= 0.0031308f ? v * 12.92f : (float)(1.055 * Math.Pow(v, 1.0 / 2.4) - 0.055);
            if (ch.invert)
                v = 1f - v;
            if (ch.useLevels && !ch.levels.IsIdentity)
            {
                var l = ch.levels;
                float t = l.inMax == l.inMin ? (v >= l.inMin ? 1f : 0f) : (v - l.inMin) / (l.inMax - l.inMin);
                t = t < 0f ? 0f : (t > 1f ? 1f : t);
                if (l.gamma != 1f && l.gamma > 0f)
                    t = (float)Math.Pow(t, 1.0 / l.gamma);
                v = l.outMin + t * (l.outMax - l.outMin);
            }
            return v;
        }

        [Test]
        public void RandomRecipes_MatchNaiveReference_AndValidatorAgrees()
        {
            var random = new Random(20261001);
            int evaluated = 0, rejected = 0;
            for (int iteration = 0; iteration < 3000; iteration++)
            {
                int slots = 1 + random.Next(4);
                var inputs = new PixelImage[slots];
                for (int s = 0; s < slots; s++)
                    inputs[s] = random.Next(5) == 0 ? null : RandomImage(random, Width, Height);

                var recipe = new PackRecipe { layout = (OutputLayout)random.Next(4) };
                for (int c = 0; c < 4; c++)
                    recipe.channels[c] = RandomChannel(random, slots);

                // Expected behavior from the naive implementation.
                bool expectError = false;
                var expected = new float[4][];
                foreach (int c in PackRecipe.ActiveChannels(recipe.layout))
                {
                    expected[c] = new float[Width * Height];
                    try
                    {
                        for (int i = 0; i < Width * Height; i++)
                            expected[c][i] = Naive(recipe.channels[c], inputs, i);
                    }
                    catch (MissingInput)
                    {
                        expectError = true;
                    }
                }

                // The validator must agree with the engine about errors.
                var infos = new InputInfo[slots];
                for (int s = 0; s < slots; s++)
                    infos[s] = InputInfo.From(inputs[s]);
                var plan = PackValidator.Validate(recipe, infos,
                    new SizeSettings { mode = SizeMode.Custom, customWidth = Width, customHeight = Height },
                    new OutputSettings { format = OutputFormat.EXR, folder = "Assets", fileName = "T" });
                Assert.AreEqual(expectError, plan.HasErrors, $"iteration {iteration}: validator disagrees. {string.Join(" | ", plan.Issues)}");

                if (expectError)
                {
                    Assert.Throws<PackException>(() => PackEngine.Evaluate(recipe, inputs, Width, Height, ResampleFilter.None), $"iteration {iteration}");
                    rejected++;
                    continue;
                }

                var result = PackEngine.Evaluate(recipe, inputs, Width, Height, ResampleFilter.None);
                bool gray = PackRecipe.IsGray(recipe.layout);
                for (int c = 0; c < 4; c++)
                {
                    float[] actual;
                    if (gray)
                        actual = c == 0 ? result.R : c == 3 ? result.A : null;
                    else
                        actual = result.GetPlane(c);
                    if (expected[c] == null)
                    {
                        if (c == 3)
                            Assert.IsNull(result.A, $"iteration {iteration}: unexpected alpha");
                        continue;
                    }
                    Assert.IsNotNull(actual, $"iteration {iteration}: channel {c} missing");
                    for (int i = 0; i < expected[c].Length; i++)
                    {
                        if (!SameFloat(expected[c][i], actual[i]))
                            Assert.Fail($"iteration {iteration} channel {c} pixel {i}: expected {expected[c][i]:R}, got {actual[i]:R}");
                    }
                }
                if (gray)
                    Assert.IsTrue(result.IsGrayscale, $"iteration {iteration}: grayscale layout must give a grayscale image");
                evaluated++;
            }
            Assert.Greater(evaluated, 1000);
            Assert.Greater(rejected, 50);
        }

        static bool SameFloat(float a, float b)
        {
            if (float.IsNaN(a) && float.IsNaN(b))
                return true;
            return a == b;
        }

        [Test]
        public void NearestResample_MatchesNaiveMapping()
        {
            var random = new Random(5);
            for (int iteration = 0; iteration < 200; iteration++)
            {
                int sw = 1 + random.Next(20), sh = 1 + random.Next(20);
                int dw = 1 + random.Next(20), dh = 1 + random.Next(20);
                var src = TestImages.RandomPlane(sw * sh, 255, random);
                var dst = Resampler.Resample(src, sw, sh, dw, dh, ResampleFilter.Nearest);
                for (int y = 0; y < dh; y++)
                {
                    for (int x = 0; x < dw; x++)
                    {
                        int sx = Math.Min(sw - 1, (int)Math.Floor((x + 0.5) * sw / dw));
                        int sy = Math.Min(sh - 1, (int)Math.Floor((y + 0.5) * sh / dh));
                        Assert.AreEqual(src[sy * sw + sx], dst[y * dw + x], $"{sw}x{sh} -> {dw}x{dh} at {x},{y}");
                    }
                }
            }
        }

        [Test]
        public void AreaAndBilinear_PreserveTheMean_AndStayInRange()
        {
            var random = new Random(9);
            for (int iteration = 0; iteration < 200; iteration++)
            {
                int sw = 1 + random.Next(30), sh = 1 + random.Next(30);
                int dw = 1 + random.Next(30), dh = 1 + random.Next(30);
                var src = TestImages.RandomPlane(sw * sh, 255, random);
                foreach (var filter in new[] { ResampleFilter.Area, ResampleFilter.Bilinear })
                {
                    var dst = Resampler.Resample(src, sw, sh, dw, dh, filter);
                    Assert.AreEqual(dw * dh, dst.Length);
                    foreach (float v in dst)
                        Assert.IsTrue(v >= -1e-6f && v <= 1f + 1e-6f, $"{filter} {sw}x{sh}->{dw}x{dh}: {v} out of range");
                }

                // Area downscale by an exact integer factor keeps the mean exactly (up to float rounding).
                int factor = 1 + random.Next(4);
                int bw = sw * factor, bh = sh * factor;
                var big = TestImages.RandomPlane(bw * bh, 255, random);
                var small = Resampler.Resample(big, bw, bh, sw, sh, ResampleFilter.Area);
                double meanBig = 0, meanSmall = 0;
                foreach (float v in big) meanBig += v;
                foreach (float v in small) meanSmall += v;
                Assert.AreEqual(meanBig / big.Length, meanSmall / small.Length, 1e-5);
            }
        }
    }
}
