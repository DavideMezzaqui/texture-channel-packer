using System;
using System.Collections.Generic;
using System.IO;
using DAVFX.TextureChannelPacker.Codecs;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace DAVFX.TextureChannelPacker.Tests
{
    /// <summary>
    /// End-to-end tests with the real AssetDatabase (use cases U1-U3, U6, U7, U11, U14, U19-U23).
    /// They create files under Assets/__ChannelPackerTests and delete them afterwards.
    /// </summary>
    public class IntegrationTests
    {
        const string Folder = "Assets/__ChannelPackerTests";

        [SetUp]
        public void SetUp()
        {
            AssetPaths.EnsureFolder(Folder);
            DecodeCache.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            AssetDatabase.DeleteAsset(Folder);
            if (AssetDatabase.IsValidFolder(TextureSourceLoader.TempFolder))
                AssetDatabase.DeleteAsset(TextureSourceLoader.TempFolder);
            DecodeCache.Clear();
        }

        static string PathOf(string name)
        {
            return Folder + "/" + name;
        }

        static Texture2D WriteAndImport(string name, byte[] bytes, Action<TextureImporter> configure = null)
        {
            string path = PathOf(name);
            File.WriteAllBytes(Path.GetFullPath(path), bytes);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            if (configure != null)
            {
                var importer = (TextureImporter)AssetImporter.GetAtPath(path);
                configure(importer);
                importer.SaveAndReimport();
            }
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            Assert.IsNotNull(texture, "import of " + name);
            return texture;
        }

        static void Degrade(TextureImporter importer)
        {
            // The worst settings a source can have: tiny max size, low quality compression, alpha dilation, sRGB.
            importer.maxTextureSize = 32;
            importer.textureCompression = TextureImporterCompression.CompressedLQ;
            importer.alphaIsTransparency = true;
            importer.sRGBTexture = true;
            importer.mipmapEnabled = true;
        }

        static PixelImage ReadOutput(string assetPath)
        {
            return ImageEncoder.TryDecode(File.ReadAllBytes(Path.GetFullPath(assetPath)), Path.GetExtension(assetPath));
        }

        // ------------------------------------------------------------------ reading sources

        [Test]
        public void U20_Png8_WithDegradedImportSettings_IsReadAtFullQuality()
        {
            var source = TestImages.Noise(300, 200, 8, false, true, 1);
            var texture = WriteAndImport("noise8.png", PngCodec.Encode(source, OutputLayout.RGBA, 8), Degrade);
            Assert.Less(texture.width, 300, "Unity's imported copy is degraded");
            string metaBefore = File.ReadAllText(Path.GetFullPath(PathOf("noise8.png.meta")));

            var loaded = TextureSourceLoader.Load(texture);
            TestImages.AssertImageExact(source, loaded, 255);
            Assert.AreEqual(metaBefore, File.ReadAllText(Path.GetFullPath(PathOf("noise8.png.meta"))), "source .meta untouched");
        }

        [Test]
        public void U2_Png16_IsReadExactly()
        {
            var source = TestImages.Noise(64, 48, 16, false, false, 2);
            var texture = WriteAndImport("noise16.png", PngCodec.Encode(source, OutputLayout.RGB, 16), Degrade);
            var loaded = TextureSourceLoader.Load(texture);
            Assert.AreEqual(SamplePrecision.UNorm16, loaded.Precision);
            TestImages.AssertImageExact(source, loaded, 65535);
        }

        [Test]
        public void U21_Exr_ThroughTemporaryImport_IsBitExact_AndLeavesNoTrace([Values(true, false)] bool useFloat)
        {
            var random = new System.Random(3);
            int w = 70, h = 33, n = w * h;
            var planes = new float[4][];
            for (int c = 0; c < 4; c++)
            {
                planes[c] = new float[n];
                for (int i = 0; i < n; i++)
                {
                    float v = (float)((random.NextDouble() - 0.2) * 20.0);
                    planes[c][i] = useFloat ? v : HalfConverter.HalfToFloat(HalfConverter.FloatToHalf(v));
                }
            }
            var source = new PixelImage(w, h, planes[0], planes[1], planes[2], planes[3], useFloat ? SamplePrecision.Float : SamplePrecision.Half);
            var texture = WriteAndImport("hdr.exr", ExrWriter.Encode(source, OutputLayout.RGBA, useFloat), Degrade);
            string metaBefore = File.ReadAllText(Path.GetFullPath(PathOf("hdr.exr.meta")));

            for (int pass = 0; pass < 2; pass++)
            {
                // Second pass exercises the cached importer template path.
                DecodeCache.Clear();
                var loaded = TextureSourceLoader.Load(texture);
                Assert.AreEqual(useFloat ? SamplePrecision.Float : SamplePrecision.Half, loaded.Precision);
                CollectionAssert.AreEqual(source.R, loaded.R, "R pass " + pass);
                CollectionAssert.AreEqual(source.G, loaded.G, "G pass " + pass);
                CollectionAssert.AreEqual(source.B, loaded.B, "B pass " + pass);
                CollectionAssert.AreEqual(source.A, loaded.A, "A pass " + pass);
            }

            Assert.IsFalse(AssetDatabase.IsValidFolder(TextureSourceLoader.TempFolder), "temporary folder removed");
            Assert.AreEqual(metaBefore, File.ReadAllText(Path.GetFullPath(PathOf("hdr.exr.meta"))), "source .meta untouched");
        }

        [Test]
        public void U19_Tga_RleAndRaw_AreReadExactly([Values(false, true)] bool rle)
        {
            var source = TestImages.Noise(45, 31, 8, false, true, 4);
            var texture = WriteAndImport(rle ? "rle.tga" : "raw.tga", TgaCodec.Encode(source, OutputLayout.RGBA, rle), Degrade);
            TestImages.AssertImageExact(source, TextureSourceLoader.Load(texture), 255);
        }

        [Test]
        public void Jpeg_MatchesUnityDecoder_AndIsFlaggedLossy()
        {
            var temp = new Texture2D(40, 24, TextureFormat.RGBA32, false);
            var pixels = new Color32[40 * 24];
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = new Color32((byte)(i * 7), (byte)(i * 3), (byte)(i * 11), 255);
            temp.SetPixels32(pixels);
            temp.Apply();
            byte[] jpg = temp.EncodeToJPG(90);
            UnityEngine.Object.DestroyImmediate(temp);

            var texture = WriteAndImport("photo.jpg", jpg, Degrade);
            var loaded = TextureSourceLoader.Load(texture);
            Assert.IsTrue(loaded.IsLossySource);

            var reference = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            reference.LoadImage(jpg);
            var expected = reference.GetPixels32();
            UnityEngine.Object.DestroyImmediate(reference);
            for (int y = 0; y < 24; y++)
            {
                for (int x = 0; x < 40; x++)
                {
                    var e = expected[(23 - y) * 40 + x];
                    Assert.AreEqual(e.r, ColorMath.Quantize(loaded.R[y * 40 + x], 255));
                    Assert.AreEqual(e.g, ColorMath.Quantize(loaded.G[y * 40 + x], 255));
                }
            }
        }

        // ------------------------------------------------------------------ packing

        [Test]
        public void U3_U23_MaskMap_EndToEnd()
        {
            var maps = new List<PixelImage>();
            var textures = new List<Texture2D>();
            string[] names = { "T_Rock_Metallic.png", "T_Rock_AO.png", "T_Rock_DetailMask.png", "T_Rock_Smoothness.png" };
            for (int i = 0; i < 4; i++)
            {
                var map = TestImages.GrayNoise(600, 300, 10 + i);
                maps.Add(map);
                textures.Add(WriteAndImport(names[i], PngCodec.Encode(map, OutputLayout.Grayscale, 8), Degrade));
            }

            var state = new PackerState();
            state.EnsureInitialized();
            state.ApplyPreset(PackPresets.Find("HDRP Mask Map"));
            state.AssignTextures(textures.ToArray(), true);
            Assert.AreEqual("T_Rock_MaskMap", state.output.fileName, "automatic name");
            Assert.AreEqual(Folder, state.output.folder, "automatic folder");

            var result = ChannelPacker.Pack(state.ToRequest(), false);
            var output = ReadOutput(result.AssetPath);
            int n = 600 * 300;
            TestImages.AssertPlaneExact(maps[0].R, output.R, 255, n, "Metallic");
            TestImages.AssertPlaneExact(maps[1].R, output.G, 255, n, "AO");
            TestImages.AssertPlaneExact(maps[2].R, output.B, 255, n, "Detail");
            TestImages.AssertPlaneExact(maps[3].R, output.A, 255, n, "Smoothness");

            var importer = (TextureImporter)AssetImporter.GetAtPath(result.AssetPath);
            Assert.IsFalse(importer.sRGBTexture, "mask maps are linear");
            Assert.IsFalse(importer.alphaIsTransparency, "RGB under transparent pixels is not altered");
            Assert.GreaterOrEqual(importer.maxTextureSize, 1024, "not downscaled by the default max size");
        }

        [Test]
        public void U6_U22_OverwriteInPlace_KeepsGuid_MakesBackup_AndFlipsTwiceToOriginal()
        {
            var normal = TestImages.Noise(64, 64, 8, false, false, 20);
            var texture = WriteAndImport("T_Wall_N.png", PngCodec.Encode(normal, OutputLayout.RGB, 8),
                importer => importer.textureType = TextureImporterType.NormalMap);
            string path = AssetDatabase.GetAssetPath(texture);
            string guid = AssetDatabase.AssetPathToGUID(path);

            var recipe = PackPresets.Find("Flip Green (DirectX - OpenGL)").Recipe.Clone();
            recipe.layout = OutputLayout.RGB; // the source has no alpha (the window and the menu action do this automatically)
            var request = new PackRequest
            {
                Recipe = recipe,
                Inputs = new List<Texture2D> { texture },
                Output = new OutputSettings { folder = Folder, fileName = "T_Wall_N", format = OutputFormat.PNG, backupOnOverwrite = true }
            };

            var first = ChannelPacker.Pack(request, false);
            Assert.AreEqual(path, first.AssetPath);
            Assert.AreEqual(guid, AssetDatabase.AssetPathToGUID(path), "GUID kept");
            Assert.IsTrue(File.Exists(first.BackupPath), "backup written");
            Assert.AreEqual(TextureImporterType.NormalMap, ((TextureImporter)AssetImporter.GetAtPath(path)).textureType, "import settings kept");
            var flipped = ReadOutput(path);
            for (int i = 0; i < normal.PixelCount; i++)
                Assert.AreEqual(255 - ColorMath.Quantize(normal.G[i], 255), ColorMath.Quantize(flipped.G[i], 255));

            request.Inputs[0] = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            var second = ChannelPacker.Pack(request, false);
            TestImages.AssertImageExact(normal, ReadOutput(path), 255);

            // Two overwrites in a row keep two distinct backups: the original is never lost.
            Assert.AreNotEqual(first.BackupPath, second.BackupPath);
            TestImages.AssertImageExact(normal, PngCodec.Decode(File.ReadAllBytes(first.BackupPath)), 255);
            Directory.Delete(Path.GetDirectoryName(first.BackupPath), true);
            Directory.Delete(Path.GetDirectoryName(second.BackupPath), true);
        }

        [Test]
        public void TextureAsset_ReadableUncompressed_IsReadExactly()
        {
            var source = TestImages.Noise(16, 12, 8, false, true, 41);
            var texture = new Texture2D(16, 12, TextureFormat.RGBA32, false, true);
            var pixels = new Color32[16 * 12];
            for (int y = 0; y < 12; y++)
            {
                for (int x = 0; x < 16; x++)
                {
                    int i = y * 16 + x;
                    pixels[(11 - y) * 16 + x] = new Color32(
                        (byte)ColorMath.Quantize(source.R[i], 255), (byte)ColorMath.Quantize(source.G[i], 255),
                        (byte)ColorMath.Quantize(source.B[i], 255), (byte)ColorMath.Quantize(source.A[i], 255));
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply();
            AssetDatabase.CreateAsset(texture, PathOf("generated.asset"));

            var loaded = TextureSourceLoader.Load(AssetDatabase.LoadAssetAtPath<Texture2D>(PathOf("generated.asset")));
            TestImages.AssertImageExact(source, loaded, 255);
            Assert.IsFalse(loaded.IsLossySource);
        }

        [Test]
        public void UseSelection_WholeMaterialSet_FillsOnlyMatchingSlots()
        {
            var textures = new List<Texture2D>();
            foreach (string name in new[] { "T_Wall_BaseColor.png", "T_Wall_Normal.png", "T_Wall_Roughness.png", "T_Wall_AO.png", "T_Wall_Metallic.png" })
                textures.Add(WriteAndImport(name, PngCodec.Encode(TestImages.GrayNoise(8, 8, name.Length), OutputLayout.Grayscale, 8)));

            var state = new PackerState();
            state.EnsureInitialized();
            state.ApplyPreset(PackPresets.Find("HDRP Mask Map (from Roughness)"));
            state.inputs[2].texture = textures[0]; // a leftover from a previous set must be cleared
            state.AssignTextures(textures, true);

            Assert.AreEqual(textures[4], state.inputs[0].texture, "Metallic");
            Assert.AreEqual(textures[3], state.inputs[1].texture, "Occlusion");
            Assert.IsNull(state.inputs[2].texture, "Detail Mask stays empty: albedo and normal are not detail masks");
            Assert.AreEqual(textures[2], state.inputs[3].texture, "Roughness");
            Assert.AreEqual(4, state.inputs.Count, "no extra slots for unrelated maps");
            Assert.AreEqual("T_Wall_MaskMap", state.output.fileName);
        }

        [Test]
        public void U7_SplitThenRepack_IsIdentical()
        {
            var source = TestImages.Noise(50, 40, 8, false, true, 30);
            var texture = WriteAndImport("T_Fx_Mask.png", PngCodec.Encode(source, OutputLayout.RGBA, 8), Degrade);
            var parts = ChannelPacker.Split(texture, new SplitSettings(), false);
            Assert.AreEqual(4, parts.Count);

            var request = new PackRequest
            {
                Recipe = PackPresets.Find("4 Grayscale Masks into RGBA").Recipe.Clone(),
                Output = new OutputSettings { folder = Folder, fileName = "T_Fx_Mask_Repacked" }
            };
            foreach (var part in parts)
                request.Inputs.Add(AssetDatabase.LoadAssetAtPath<Texture2D>(part));
            var result = ChannelPacker.Pack(request, false);
            TestImages.AssertImageExact(source, ReadOutput(result.AssetPath), 255);
        }

        [Test]
        public void Split_EmptyOrDuplicateSuffix_IsRefused_AndTheSourceIsUntouched()
        {
            var source = TestImages.Noise(8, 8, 8, false, false, 33);
            byte[] original = PngCodec.Encode(source, OutputLayout.RGB, 8);
            var texture = WriteAndImport("T_Split.png", original);

            var empty = new SplitSettings();
            empty.suffixes[0] = "";
            Assert.Throws<PackException>(() => ChannelPacker.Split(texture, empty, false));

            var duplicate = new SplitSettings();
            duplicate.suffixes[1] = "_R";
            Assert.Throws<PackException>(() => ChannelPacker.Split(texture, duplicate, false));

            var hidden = new SplitSettings { folder = Folder + "/Hidden~" };
            Assert.Throws<PackException>(() => ChannelPacker.Split(texture, hidden, false));

            CollectionAssert.AreEqual(original, File.ReadAllBytes(Path.GetFullPath(PathOf("T_Split.png"))), "source file untouched");
            Assert.IsFalse(File.Exists(Path.GetFullPath(PathOf("T_Split_G.png"))), "nothing written");
        }

        [Test]
        public void Split_16BitSource_Writes16BitGrayscale()
        {
            var source = TestImages.Noise(20, 20, 16, false, false, 31);
            var texture = WriteAndImport("T_Height16.png", PngCodec.Encode(source, OutputLayout.RGB, 16));
            var parts = ChannelPacker.Split(texture, new SplitSettings(), false);
            Assert.AreEqual(3, parts.Count, "no alpha file for an opaque source");
            var red = ReadOutput(parts[0]);
            Assert.AreEqual(SamplePrecision.UNorm16, red.Precision);
            Assert.IsTrue(red.IsGrayscale);
            TestImages.AssertPlaneExact(source.R, red.R, 65535, source.PixelCount, "R");
        }

        [Test]
        public void U11_HdrChannels_PackedToExr_AreBitExact()
        {
            var r = new float[16];
            for (int i = 0; i < r.Length; i++)
                r[i] = i * 1.37f - 3f;
            var source = PixelImage.Gray(4, 4, r, null, SamplePrecision.Float);
            var texture = WriteAndImport("hdr_gray.exr", ExrWriter.Encode(source, OutputLayout.Grayscale, true));

            var recipe = new PackRecipe { layout = OutputLayout.RGB };
            recipe.channels[0] = ChannelRecipe.From(Operand.Texture(0, ChannelSource.Red));
            recipe.channels[1] = ChannelRecipe.From(Operand.Value(0f));
            recipe.channels[2] = ChannelRecipe.From(Operand.Texture(0, ChannelSource.Red));
            var result = ChannelPacker.Pack(new PackRequest
            {
                Recipe = recipe,
                Inputs = new List<Texture2D> { texture },
                Output = new OutputSettings { folder = Folder, fileName = "hdr_packed", format = OutputFormat.EXR }
            }, false);
            Assert.AreEqual(OutputBitDepth.Float, result.Depth);

            DecodeCache.Clear();
            var loaded = TextureSourceLoader.Load(result.Texture);
            CollectionAssert.AreEqual(r, loaded.R);
            CollectionAssert.AreEqual(r, loaded.B);
        }

        [Test]
        public void U14_SizeMismatch_FailsAndWritesNothing()
        {
            var a = WriteAndImport("a.png", PngCodec.Encode(TestImages.GrayNoise(16, 16, 1), OutputLayout.Grayscale, 8));
            var b = WriteAndImport("b.png", PngCodec.Encode(TestImages.GrayNoise(32, 32, 2), OutputLayout.Grayscale, 8));
            var request = new PackRequest
            {
                Recipe = PackPresets.Find("3 Grayscale Masks into RGB").Recipe.Clone(),
                Inputs = new List<Texture2D> { a, b, null },
                Output = new OutputSettings { folder = Folder, fileName = "mismatch" }
            };
            Assert.Throws<PackException>(() => ChannelPacker.Pack(request, false));
            Assert.IsFalse(File.Exists(Path.GetFullPath(PathOf("mismatch.png"))));
        }

        [Test]
        public void U10_NewTexture_FromConstants()
        {
            var state = new PackerState();
            state.EnsureInitialized();
            state.ApplyPreset(PackPresets.Find("Solid Color - Constant Mask"));
            state.size.customWidth = 33;
            state.size.customHeight = 17;
            foreach (var channel in state.recipe.channels)
                channel.source.constant = 64f / 255f;
            state.output.folder = Folder;
            state.output.fileName = "solid";

            var result = ChannelPacker.Pack(state.ToRequest(), false);
            var output = ReadOutput(result.AssetPath);
            Assert.AreEqual(33, output.Width);
            Assert.AreEqual(17, output.Height);
            Assert.AreEqual(64, ColorMath.Quantize(output.G[100], 255));
            Assert.AreEqual(64, ColorMath.Quantize(output.A[100], 255));
        }
    }
}
