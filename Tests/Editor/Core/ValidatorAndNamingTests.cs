using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace DAVFX.TextureChannelPacker.Tests
{
    public class ValidatorTests
    {
        static InputInfo Info(int w, int h, SamplePrecision p = SamplePrecision.UNorm8, bool lossy = false)
        {
            return new InputInfo { Width = w, Height = h, Precision = p, IsLossy = lossy, HasAlpha = false, IsGrayscale = true };
        }

        static OutputSettings Output(OutputFormat format = OutputFormat.PNG, OutputBitDepth depth = OutputBitDepth.Auto)
        {
            return new OutputSettings { format = format, bitDepth = depth, folder = "Assets/Textures", fileName = "T_Test" };
        }

        static PackRecipe MaskMap()
        {
            return PackPresets.Find("HDRP - URP Mask Map").Recipe.Clone();
        }

        [Test]
        public void MatchingSizes_NoErrors()
        {
            var plan = PackValidator.Validate(MaskMap(), new[] { Info(512, 512), Info(512, 512), null, null }, new SizeSettings(), Output());
            Assert.IsFalse(plan.HasErrors, string.Join("\n", plan.Issues));
            Assert.AreEqual(512, plan.Width);
            Assert.AreEqual(OutputBitDepth.Bits8, plan.ResolvedDepth);
            Assert.IsTrue(plan.Issues.Any(i => i.Severity == IssueSeverity.Info), "fallback is explained");
        }

        [Test]
        public void SizeMismatch_IsAnError_UntilAFilterIsChosen()
        {
            var inputs = new[] { Info(512, 512), Info(1024, 1024), null, null };
            var size = new SizeSettings();
            var plan = PackValidator.Validate(MaskMap(), inputs, size, Output());
            Assert.IsTrue(plan.HasErrors);
            Assert.AreEqual(1024, plan.Width, "Largest by default");

            size.filter = ResampleFilter.Bilinear;
            plan = PackValidator.Validate(MaskMap(), inputs, size, Output());
            Assert.IsFalse(plan.HasErrors);
            Assert.IsTrue(plan.Issues.Any(i => i.Severity == IssueSeverity.Warning && i.Message.Contains("resized")));
        }

        [Test]
        public void SixteenBitSource_AutoPicks16_Explicit8Warns()
        {
            var inputs = new[] { Info(64, 64, SamplePrecision.UNorm16), null, null, null };
            var plan = PackValidator.Validate(MaskMap(), inputs, new SizeSettings(), Output());
            Assert.AreEqual(OutputBitDepth.Bits16, plan.ResolvedDepth);
            Assert.IsFalse(plan.Issues.Any(i => i.Severity == IssueSeverity.Warning));

            plan = PackValidator.Validate(MaskMap(), inputs, new SizeSettings(), Output(OutputFormat.PNG, OutputBitDepth.Bits8));
            Assert.IsTrue(plan.Issues.Any(i => i.Severity == IssueSeverity.Warning && i.Message.Contains("precision")));
        }

        [Test]
        public void FloatSource_IntoPng_WarnsAboutClamping_ExrDoesNot()
        {
            var inputs = new[] { Info(64, 64, SamplePrecision.Float), null, null, null };
            var plan = PackValidator.Validate(MaskMap(), inputs, new SizeSettings(), Output());
            Assert.IsTrue(plan.Issues.Any(i => i.Severity == IssueSeverity.Warning && i.Message.Contains("clamped")));
            plan = PackValidator.Validate(MaskMap(), inputs, new SizeSettings(), Output(OutputFormat.EXR));
            Assert.AreEqual(OutputBitDepth.Float, plan.ResolvedDepth);
            Assert.IsFalse(plan.Issues.Any(i => i.Severity == IssueSeverity.Warning));
        }

        [Test]
        public void EmptyUsedInput_AndBadPaths_AreErrors()
        {
            var recipe = new PackRecipe { layout = OutputLayout.Grayscale };
            recipe.channels[0] = ChannelRecipe.From(Operand.Texture(1, ChannelSource.Red));
            var plan = PackValidator.Validate(recipe, new[] { Info(8, 8), null }, new SizeSettings(), Output());
            Assert.IsTrue(plan.HasErrors);

            var output = Output();
            output.folder = "Packages/foo";
            output.fileName = "bad:name";
            plan = PackValidator.Validate(MaskMap(), new[] { Info(8, 8), null, null, null }, new SizeSettings(), output);
            Assert.AreEqual(2, plan.Issues.Count(i => i.Severity == IssueSeverity.Error));
        }

        [Test]
        public void ConstantsOnly_UseCustomSize()
        {
            var preset = PackPresets.Find("Solid Color - Constant Mask");
            var size = new SizeSettings { customWidth = 300, customHeight = 200 };
            var plan = PackValidator.Validate(preset.Recipe.Clone(), new InputInfo[4], size, Output());
            Assert.IsFalse(plan.HasErrors);
            Assert.AreEqual(300, plan.Width);
            Assert.AreEqual(200, plan.Height);
        }

        [Test]
        public void SrgbConversion_In8Bit_Warns()
        {
            var recipe = new PackRecipe { layout = OutputLayout.Grayscale };
            recipe.channels[0] = ChannelRecipe.From(Operand.Texture(0, ChannelSource.Red));
            recipe.channels[0].conversion = ColorConversion.SrgbToLinear;
            var plan = PackValidator.Validate(recipe, new[] { Info(8, 8) }, new SizeSettings(), Output());
            Assert.IsTrue(plan.Issues.Any(i => i.Message.Contains("banding")));
        }

        [Test]
        public void EveryPreset_ValidatesWithFullInputs()
        {
            foreach (var preset in PackPresets.All)
            {
                var inputs = Enumerable.Range(0, 4).Select(_ => Info(128, 128)).ToArray();
                var plan = PackValidator.Validate(preset.Recipe.Clone(), inputs, new SizeSettings(), Output());
                Assert.IsFalse(plan.HasErrors, preset.Name + ": " + string.Join("\n", plan.Issues));
            }
        }
    }

    public class NamingTests
    {
        [Test]
        public void BaseName_FromTextureSets()
        {
            Assert.AreEqual("T_Rock", NamingUtility.BaseName(new[] { "T_Rock_Metallic", "T_Rock_AO", "T_Rock_Roughness" }));
            Assert.AreEqual("Rock", NamingUtility.BaseName(new[] { "Rock_Metal", "Rock_Mask" }));
            Assert.AreEqual("T_Brick_01", NamingUtility.BaseName(new[] { "T_Brick_01_N" }));
            Assert.AreEqual("Noise", NamingUtility.BaseName(new[] { "Noise" }));
            Assert.AreEqual("NewTexture", NamingUtility.BaseName(new string[0]));
        }

        [Test]
        public void AutoAssign_MatchesByToken()
        {
            var names = new List<string> { "T_Rock_Roughness.png", "T_Rock_AO.png", "T_Rock_Metallic.tga", "T_Rock_BaseColor.png" };
            var preset = PackPresets.Find("HDRP - URP Mask Map (from Roughness)");
            var taken = new HashSet<int>();
            var assigned = new int[preset.SlotKeywords.Length];
            for (int slot = 0; slot < preset.SlotKeywords.Length; slot++)
            {
                assigned[slot] = NamingUtility.FindBestMatch(names, preset.SlotKeywords[slot], taken);
                if (assigned[slot] >= 0)
                    taken.Add(assigned[slot]);
            }
            Assert.AreEqual(2, assigned[0], "Metallic");
            Assert.AreEqual(1, assigned[1], "Occlusion");
            Assert.AreEqual(-1, assigned[2], "no detail mask");
            Assert.AreEqual(0, assigned[3], "Roughness");
        }

        [Test]
        public void AutoAssign_SingleLetterSuffixes()
        {
            var names = new List<string> { "Wall_N", "Wall_D" };
            Assert.AreEqual(0, NamingUtility.FindBestMatch(names, new[] { "normal", "n" }, null));
            Assert.AreEqual(1, NamingUtility.FindBestMatch(names, new[] { "albedo", "d" }, null));
        }
    }
}
