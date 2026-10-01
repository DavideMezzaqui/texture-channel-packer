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
            return PackPresets.Find("HDRP Mask Map").Recipe.Clone();
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
        public void EveryEmptySlot_IsReportedOnce()
        {
            // C and D are both empty: both must be named, each once, even if read by several channels.
            var recipe = MaskMap();
            recipe.channels[0].combine = CombineOp.Multiply;
            recipe.channels[0].other = Operand.Texture(3, ChannelSource.Red, 0.5f);
            var plan = PackValidator.Validate(recipe, new[] { Info(8, 8), Info(8, 8), null, null }, new SizeSettings(), Output());
            Assert.AreEqual(1, plan.Issues.Count(i => i.Message.StartsWith("Input C is empty")));
            Assert.AreEqual(1, plan.Issues.Count(i => i.Message.StartsWith("Input D is empty")));
        }

        [Test]
        public void ImportSource_UsesSlotPresence_NotOnlyUsedSlots()
        {
            // Slot B holds a texture the recipe does not read: copying its import settings is still possible.
            var output = Output();
            output.importMode = ImportMode.CopyFromInput;
            output.importSourceInput = 1;
            var recipe = new PackRecipe { layout = OutputLayout.Grayscale };
            recipe.channels[0] = ChannelRecipe.From(Operand.Texture(0, ChannelSource.Red));
            var plan = PackValidator.Validate(recipe, new[] { Info(8, 8), null }, new SizeSettings(), output, new[] { true, true });
            Assert.IsFalse(plan.Issues.Any(i => i.Message.Contains("Import settings source")), string.Join("\n", plan.Issues));
            plan = PackValidator.Validate(recipe, new[] { Info(8, 8), null }, new SizeSettings(), output, new[] { true, false });
            Assert.IsTrue(plan.Issues.Any(i => i.Message.Contains("Import settings source")));
        }

        [Test]
        public void Folders_AndNames_ThatUnityIgnoresOrThatEscapeAssets_AreErrors()
        {
            foreach (string folder in new[] { "Assets/../Outside", "Assets/./X", "Assets/.hidden", "Assets/Backup~", "Assets//X", "Assets/ X", "C:/Temp", "Assets/a|b" })
            {
                var output = Output();
                output.folder = folder;
                var plan = PackValidator.Validate(MaskMap(), new[] { Info(8, 8), null, null, null }, new SizeSettings(), output);
                Assert.IsTrue(plan.HasErrors, "folder " + folder);
            }
            foreach (string name in new[] { ".png", ".hidden", "Mask~", "Mask.", " Mask", "a/b", "a*b" })
            {
                var output = Output();
                output.fileName = name;
                var plan = PackValidator.Validate(MaskMap(), new[] { Info(8, 8), null, null, null }, new SizeSettings(), output);
                Assert.IsTrue(plan.HasErrors, "name " + name);
            }
            foreach (string folder in new[] { "", "Assets", "Assets/", "Assets\\Textures\\Rock", "Assets/My Textures/v1.2" })
            {
                var output = Output();
                output.folder = folder;
                var plan = PackValidator.Validate(MaskMap(), new[] { Info(8, 8), null, null, null }, new SizeSettings(), output);
                Assert.IsFalse(plan.HasErrors, "folder '" + folder + "': " + string.Join("\n", plan.Issues));
            }
        }

        [Test]
        public void TypedExtension_IsNotDoubled()
        {
            var output = new OutputSettings { folder = "Assets/T", fileName = "T_Rock.PNG", format = OutputFormat.PNG };
            Assert.AreEqual("Assets/T/T_Rock.png", output.AssetPath);
            output.fileName = "T_Rock.v2";
            Assert.AreEqual("Assets/T/T_Rock.v2.png", output.AssetPath);
            output.folder = "";
            Assert.AreEqual("Assets/T_Rock.v2.png", output.AssetPath);
        }

        [Test]
        public void TgaDepth_DoesNotStickWhenSwitchingBackToPng()
        {
            var depths = OutputSettings.DepthsFor(OutputFormat.TGA);
            Assert.AreEqual(1, depths.Length);
            Assert.AreEqual(OutputBitDepth.Auto, depths[0], "TGA stores Auto so PNG keeps choosing 16-bit for 16-bit sources");
            Assert.AreEqual(OutputBitDepth.Bits8, OutputSettings.ResolveDepth(OutputFormat.TGA, OutputBitDepth.Auto, SamplePrecision.UNorm16));
        }

        [Test]
        public void TerrainMaskMap_HasHeightInBlue_WithTerrainDefaults()
        {
            var preset = PackPresets.Find("Terrain Layer Mask Map");
            Assert.IsNotNull(preset);
            var result = PackEngine.Evaluate(preset.Recipe.Clone(), new PixelImage[4], 2, 2, ResampleFilter.None);
            Assert.AreEqual(0f, result.R[0], "metallic default");
            Assert.AreEqual(1f, result.G[0], "occlusion default");
            Assert.AreEqual(0.5f, result.B[0], "height default (neutral)");
            Assert.AreEqual(0.5f, result.A[0], "smoothness default");
            Assert.AreEqual("Height", preset.SlotLabels[2]);
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
            var preset = PackPresets.Find("HDRP Mask Map (from Roughness)");
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
        public void NamesWithDots_AreNotTreatedAsExtensions()
        {
            Assert.AreEqual(new[] { "t", "rock", "v2", "ao" }, NamingUtility.Tokenize("T_Rock.v2_AO"));
            Assert.AreEqual(new[] { "t", "rock", "ao" }, NamingUtility.Tokenize("Assets/Tex/T_Rock_AO.png"));
            Assert.AreEqual("T_Rock.v2", NamingUtility.BaseName(new[] { "T_Rock.v2_Metallic", "T_Rock.v2_AO" }));
            Assert.AreEqual("T_Rock.v2", NamingUtility.BaseName(new[] { "T_Rock.v2_AO" }));
        }

        [Test]
        public void AssignSlots_KeywordPresets_LeaveOtherMapsOut()
        {
            // A whole material set selected for a mask map: albedo and normal must not land in the Detail Mask slot.
            var names = new List<string> { "T_Rock_BaseColor", "T_Rock_Normal", "T_Rock_Roughness", "T_Rock_AO", "T_Rock_Metallic" };
            var preset = PackPresets.Find("HDRP Mask Map (from Roughness)");
            var result = NamingUtility.AssignSlots(names, preset.SlotKeywords, new[] { false, false, false, false }, 8);
            CollectionAssert.AreEqual(new[] { 4, 3, -1, 2 }, result);
        }

        [Test]
        public void AssignSlots_WithoutMatches_FillsInOrder()
        {
            var preset = PackPresets.Find("HDRP Mask Map");
            var result = NamingUtility.AssignSlots(new List<string> { "Noise01", "Noise02" }, preset.SlotKeywords, new[] { false, false, false, false }, 8);
            CollectionAssert.AreEqual(new[] { 0, 1, -1, -1 }, result);
        }

        [Test]
        public void AssignSlots_NoKeywords_FillsFreeSlots_AndAddsSlotsUpToTheLimit()
        {
            var result = NamingUtility.AssignSlots(new List<string> { "a", "b", "c", "d", "e" }, new string[0][], new[] { true, false, true }, 5);
            CollectionAssert.AreEqual(new[] { -1, 0, -1, 1, 2 }, result, "slot B gets a, then two new slots; d and e exceed the limit");
            Assert.AreEqual(0, NamingUtility.AssignSlots(new List<string>(), new string[0][], new bool[0], 8).Length);
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
