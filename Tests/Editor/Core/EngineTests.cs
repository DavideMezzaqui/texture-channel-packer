using System.Collections.Generic;
using DAVFX.TextureChannelPacker.Codecs;
using NUnit.Framework;

namespace DAVFX.TextureChannelPacker.Tests
{
    /// <summary>Use cases U1 to U17 at engine level.</summary>
    public class EngineTests
    {
        static PackRecipe Recipe(OutputLayout layout, params ChannelRecipe[] channels)
        {
            var recipe = new PackRecipe { layout = layout };
            for (int i = 0; i < channels.Length; i++)
                recipe.channels[PackRecipe.ActiveChannels(layout)[i]] = channels[i];
            return recipe;
        }

        static ChannelRecipe Tex(int input, ChannelSource channel, bool invert = false)
        {
            return ChannelRecipe.From(Operand.Texture(input, channel), invert);
        }

        static PixelImage Run(PackRecipe recipe, params PixelImage[] inputs)
        {
            var first = inputs.Length > 0 && inputs[0] != null ? inputs[0] : null;
            int w = first?.Width ?? 4, h = first?.Height ?? 4;
            return PackEngine.Evaluate(recipe, inputs, w, h, ResampleFilter.None);
        }

        [Test]
        public void U1_RgbaCopy_IsBitIdentical_AndReusesPlanes()
        {
            var source = TestImages.Noise(31, 17, 8, false, true, 1);
            var recipe = Recipe(OutputLayout.RGBA, Tex(0, ChannelSource.Red), Tex(0, ChannelSource.Green), Tex(0, ChannelSource.Blue), Tex(0, ChannelSource.Alpha));
            var result = Run(recipe, source);
            Assert.AreSame(source.R, result.R, "no arithmetic on passthrough channels");
            Assert.AreSame(source.A, result.A);

            var png = PngCodec.Decode(PngCodec.Encode(result, OutputLayout.RGBA, 8));
            TestImages.AssertImageExact(source, png, 255);
        }

        [Test]
        public void U2_16BitCopy_IsExact_AndAutoDepthKeeps16Bit()
        {
            var source = TestImages.Noise(20, 20, 16, false, false, 2);
            var recipe = Recipe(OutputLayout.RGB, Tex(0, ChannelSource.Red), Tex(0, ChannelSource.Green), Tex(0, ChannelSource.Blue));
            var result = Run(recipe, source);
            Assert.AreEqual(SamplePrecision.UNorm16, result.Precision);
            var depth = OutputSettings.ResolveDepth(OutputFormat.PNG, OutputBitDepth.Auto, result.Precision);
            Assert.AreEqual(OutputBitDepth.Bits16, depth);
            var png = PngCodec.Decode(PngCodec.Encode(result, OutputLayout.RGB, 16));
            TestImages.AssertImageExact(source, png, 65535);
        }

        [Test]
        public void U3_MaskMap_FromFourGrayscale_IsExactPerChannel()
        {
            var metallic = TestImages.GrayNoise(64, 32, 10);
            var ao = TestImages.GrayNoise(64, 32, 11);
            var detail = TestImages.GrayNoise(64, 32, 12);
            var smooth = TestImages.GrayNoise(64, 32, 13);
            var preset = PackPresets.Find("HDRP - URP Mask Map");
            var result = Run(preset.Recipe.Clone(), metallic, ao, detail, smooth);
            var decoded = PngCodec.Decode(PngCodec.Encode(result, OutputLayout.RGBA, 8));

            int n = metallic.PixelCount;
            TestImages.AssertPlaneExact(metallic.R, decoded.R, 255, n, "Metallic in R");
            TestImages.AssertPlaneExact(ao.R, decoded.G, 255, n, "AO in G");
            TestImages.AssertPlaneExact(detail.R, decoded.B, 255, n, "Detail in B");
            TestImages.AssertPlaneExact(smooth.R, decoded.A, 255, n, "Smoothness in A");
        }

        [Test]
        public void U4_MaskMap_WithMissingInputs_UsesUnityDefaults()
        {
            var metallic = TestImages.GrayNoise(8, 8, 20);
            var preset = PackPresets.Find("HDRP - URP Mask Map");
            var result = PackEngine.Evaluate(preset.Recipe.Clone(), new PixelImage[] { metallic, null, null, null }, 8, 8, ResampleFilter.None);
            TestImages.AssertAllEqual(result.G, 1f, "AO default");
            TestImages.AssertAllEqual(result.B, 1f, "Detail default");
            Assert.AreEqual(128, ColorMath.Quantize(result.A[0], 255), "Smoothness default 0.5");
        }

        [Test]
        public void U4b_EmptyInputWithoutFallback_Throws()
        {
            var recipe = Recipe(OutputLayout.Grayscale, Tex(1, ChannelSource.Red));
            Assert.Throws<PackException>(() => PackEngine.Evaluate(recipe, new PixelImage[] { TestImages.GrayNoise(4, 4, 1), null }, 4, 4, ResampleFilter.None));
        }

        [Test]
        public void U5_RoughnessToSmoothness_Is255MinusValue()
        {
            var rough = TestImages.GrayNoise(16, 16, 30);
            var preset = PackPresets.Find("HDRP - URP Mask Map (from Roughness)");
            var result = PackEngine.Evaluate(preset.Recipe.Clone(), new PixelImage[] { null, null, null, rough }, 16, 16, ResampleFilter.None);
            for (int i = 0; i < rough.PixelCount; i++)
                Assert.AreEqual(255 - ColorMath.Quantize(rough.R[i], 255), ColorMath.Quantize(result.A[i], 255));
        }

        [Test]
        public void U6_FlipGreenTwice_RestoresTheOriginal()
        {
            var normal = TestImages.Noise(24, 24, 8, false, false, 40);
            var preset = PackPresets.Find("Flip Green (DirectX - OpenGL)");
            var once = PngCodec.Decode(PngCodec.Encode(Run(preset.Recipe.Clone(), normal), OutputLayout.RGB, 8));
            for (int i = 0; i < normal.PixelCount; i++)
                Assert.AreEqual(255 - ColorMath.Quantize(normal.G[i], 255), ColorMath.Quantize(once.G[i], 255));
            var twice = PngCodec.Decode(PngCodec.Encode(Run(preset.Recipe.Clone(), once), OutputLayout.RGB, 8));
            TestImages.AssertImageExact(normal, twice, 255);
        }

        [Test]
        public void U7_SplitThenRepack_IsIdentical()
        {
            var source = TestImages.Noise(19, 23, 8, false, true, 50);
            var parts = new List<PixelImage>();
            for (int c = 0; c < 4; c++)
            {
                var split = Run(Recipe(OutputLayout.Grayscale, Tex(0, (ChannelSource)c)), source);
                parts.Add(PngCodec.Decode(PngCodec.Encode(split, OutputLayout.Grayscale, 8)));
            }
            var repack = Run(PackPresets.Find("4 Grayscale Masks into RGBA").Recipe.Clone(), parts.ToArray());
            TestImages.AssertImageExact(source, PngCodec.Decode(PngCodec.Encode(repack, OutputLayout.RGBA, 8)), 255);
        }

        [Test]
        public void U10_FlatNormal_IsExactly128_128_255()
        {
            var preset = PackPresets.Find("Flat Normal Map");
            var result = PackEngine.Evaluate(preset.Recipe.Clone(), new PixelImage[0], 4, 4, ResampleFilter.None);
            var png = PngCodec.Decode(PngCodec.Encode(result, OutputLayout.RGB, 8));
            Assert.AreEqual(128, ColorMath.Quantize(png.R[5], 255));
            Assert.AreEqual(128, ColorMath.Quantize(png.G[5], 255));
            Assert.AreEqual(255, ColorMath.Quantize(png.B[5], 255));
            Assert.IsFalse(png.HasAlpha);
        }

        [Test]
        public void U11_FloatChannels_AreCopiedBitExact()
        {
            var r = new[] { -1.5f, 0f, 3.25f, 1e5f };
            var g = new[] { 0.1f, 0.2f, 0.3f, 0.4f };
            var hdr = new PixelImage(2, 2, r, g, g, null, SamplePrecision.Float);
            var recipe = Recipe(OutputLayout.RGB, Tex(0, ChannelSource.Green), Tex(0, ChannelSource.Red), Tex(0, ChannelSource.Blue));
            var result = Run(recipe, hdr);
            CollectionAssert.AreEqual(r, result.G);
            var depth = OutputSettings.ResolveDepth(OutputFormat.EXR, OutputBitDepth.Auto, result.Precision);
            Assert.AreEqual(OutputBitDepth.Float, depth);
            var exr = ExrTestReader.Read(ExrWriter.Encode(result, OutputLayout.RGB, true));
            CollectionAssert.AreEqual(r, exr["G"]);
            CollectionAssert.AreEqual(g, exr["R"]);
        }

        [Test]
        public void U13_DetailMap_ChannelsLandInTheRightPlace()
        {
            var albedo = TestImages.Constant(4, 4, 0.5f, 0.5f, 0.5f);
            var normal = TestImages.Constant(4, 4, 0.2f, 0.7f, 1f);
            var smooth = PixelImage.Gray(4, 4, TestImages.Fill(16, 0.9f), null, SamplePrecision.UNorm8);
            var result = Run(PackPresets.Find("HDRP Detail Map").Recipe.Clone(), albedo, normal, smooth);
            Assert.AreEqual(0.5f, result.R[0], 1e-6f, "R = albedo luminance");
            Assert.AreEqual(0.7f, result.G[0], "G = normal Y");
            Assert.AreEqual(0.9f, result.B[0], "B = smoothness");
            Assert.AreEqual(0.2f, result.A[0], "A = normal X");
        }

        [Test]
        public void U14_SizeMismatch_WithoutFilter_Throws()
        {
            var a = TestImages.GrayNoise(8, 8, 1);
            var b = TestImages.GrayNoise(16, 16, 2);
            var recipe = Recipe(OutputLayout.RGB, Tex(0, ChannelSource.Red), Tex(1, ChannelSource.Red), ChannelRecipe.From(Operand.Value(0f)));
            Assert.Throws<PackException>(() => PackEngine.Evaluate(recipe, new[] { a, b }, 16, 16, ResampleFilter.None));
            Assert.DoesNotThrow(() => PackEngine.Evaluate(recipe, new[] { a, b }, 16, 16, ResampleFilter.Bilinear));
        }

        [Test]
        public void U15_NearestUpThenDown_IsExact()
        {
            var source = TestImages.GrayNoise(10, 6, 60);
            var up = Resampler.Resample(source.R, 10, 6, 40, 24, ResampleFilter.Nearest);
            var down = Resampler.Resample(up, 40, 24, 10, 6, ResampleFilter.Nearest);
            CollectionAssert.AreEqual(source.R, down);
        }

        [Test]
        public void U16_AreaAndBilinear_KeepConstantsConstant([Values(ResampleFilter.Area, ResampleFilter.Bilinear)] ResampleFilter filter)
        {
            var plane = TestImages.Fill(37 * 23, 0.3f);
            var resized = Resampler.Resample(plane, 37, 23, 11, 50, filter);
            foreach (float v in resized)
                Assert.AreEqual(0.3f, v, 1e-6f);
        }

        [Test]
        public void Area_DownscaleByTwo_AveragesBlocks()
        {
            var plane = new[] { 0f, 1f, 0.5f, 0.5f };
            var result = Resampler.Resample(plane, 2, 2, 1, 1, ResampleFilter.Area);
            Assert.AreEqual(0.5f, result[0], 1e-6f);
        }

        [Test]
        public void U17_IdentityLevels_AndDoubleInvert_ChangeNothing()
        {
            var source = TestImages.GrayNoise(32, 32, 70);
            var levels = ChannelRecipe.From(Operand.Texture(0, ChannelSource.Red));
            levels.useLevels = true;
            Assert.IsTrue(levels.IsPassthrough, "identity levels are a passthrough");
            var result = Run(Recipe(OutputLayout.Grayscale, levels), source);
            Assert.AreSame(source.R, result.R);

            var once = Run(Recipe(OutputLayout.Grayscale, Tex(0, ChannelSource.Red, true)), source);
            var twice = Run(Recipe(OutputLayout.Grayscale, Tex(0, ChannelSource.Red, true)), once);
            TestImages.AssertPlaneExact(source.R, twice.R, 255, source.PixelCount, "double invert");
        }

        [Test]
        public void Levels_RemapAndGamma()
        {
            var levels = new Levels { inMin = 0.25f, inMax = 0.75f, outMin = 0.1f, outMax = 0.9f };
            Assert.AreEqual(0.1f, levels.Apply(0f), 1e-6f);
            Assert.AreEqual(0.5f, levels.Apply(0.5f), 1e-6f);
            Assert.AreEqual(0.9f, levels.Apply(1f), 1e-6f);
            var gamma = new Levels { gamma = 2f };
            Assert.AreEqual(System.Math.Sqrt(0.25), gamma.Apply(0.25f), 1e-6, "gamma 2 brightens");
        }

        [Test]
        public void Combine_Operations()
        {
            var a = PixelImage.Gray(1, 1, new[] { 0.6f }, null, SamplePrecision.UNorm8);
            var b = PixelImage.Gray(1, 1, new[] { 0.5f }, null, SamplePrecision.UNorm8);
            var expected = new Dictionary<CombineOp, float>
            {
                { CombineOp.Multiply, 0.3f }, { CombineOp.Add, 1.1f }, { CombineOp.Subtract, 0.1f },
                { CombineOp.Minimum, 0.5f }, { CombineOp.Maximum, 0.6f }, { CombineOp.Average, 0.55f },
                { CombineOp.Difference, 0.1f }, { CombineOp.Screen, 0.8f }
            };
            foreach (var pair in expected)
            {
                var ch = ChannelRecipe.From(Operand.Texture(0, ChannelSource.Red));
                ch.combine = pair.Key;
                ch.other = Operand.Texture(1, ChannelSource.Red);
                var result = PackEngine.Evaluate(Recipe(OutputLayout.Grayscale, ch), new[] { a, b }, 1, 1, ResampleFilter.None);
                Assert.AreEqual(pair.Value, result.R[0], 1e-6f, pair.Key.ToString());
            }
        }

        [Test]
        public void DerivedChannels_OnGrayInput_AreExactCopies()
        {
            var gray = TestImages.GrayNoise(16, 16, 80);
            foreach (var source in new[] { ChannelSource.Luminance, ChannelSource.Average, ChannelSource.Minimum, ChannelSource.Maximum })
            {
                var result = Run(Recipe(OutputLayout.Grayscale, Tex(0, source)), gray);
                Assert.AreSame(gray.R, result.R, source.ToString());
            }
        }

        [Test]
        public void MissingAlpha_ReadsAsOne()
        {
            var rgb = TestImages.Noise(4, 4, 8, false, false, 90);
            var result = Run(Recipe(OutputLayout.Grayscale, Tex(0, ChannelSource.Alpha)), rgb);
            TestImages.AssertAllEqual(result.R, 1f, "alpha of an opaque image");
        }

        [Test]
        public void ConstantsAndConversions_OnConstants_StayScalarUntilTheEnd()
        {
            var ch = ChannelRecipe.From(Operand.Value(0.5f));
            ch.conversion = ColorConversion.SrgbToLinear;
            var result = PackEngine.Evaluate(Recipe(OutputLayout.Grayscale, ch), new PixelImage[0], 3, 2, ResampleFilter.None);
            TestImages.AssertAllEqual(result.R, ColorMath.SrgbToLinear(0.5f), "converted constant");
        }
    }
}
