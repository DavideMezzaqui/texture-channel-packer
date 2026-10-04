using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using DAVFX.TextureChannelPacker.Codecs;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace DAVFX.TextureChannelPacker
{
    /// <summary>
    /// Reads the original pixels of a texture, ignoring the compression, max size and other
    /// settings of its importer. Never modifies the source file or its .meta.
    /// </summary>
    public static class TextureSourceLoader
    {
        internal const string TempFolder = "Assets/__DAVFX_ChannelPacker_Temp";

        /// <summary>Formats Unity's TextureImporter reads. Anything else (DDS, PSB, KTX...) is read from the texture object.</summary>
        static readonly HashSet<string> s_FileExtensions = new HashSet<string>
        {
            ".png", ".jpg", ".jpeg", ".tga", ".bmp", ".exr", ".gif", ".hdr", ".iff", ".pict", ".pct", ".pic", ".psd", ".tif", ".tiff"
        };

        // Meta files produced by a previous successful temp import, reused so the next temp import happens only once.
        static readonly Dictionary<string, string> s_MetaTemplates = new Dictionary<string, string>();
        static readonly Regex s_GuidLine = new Regex(@"^guid: [0-9a-fA-F]{32}", RegexOptions.Multiline);

        static float[] s_Lut8;

        static float[] Lut8
        {
            get
            {
                if (s_Lut8 == null)
                {
                    s_Lut8 = new float[256];
                    for (int i = 0; i < 256; i++)
                        s_Lut8[i] = i / 255f;
                }
                return s_Lut8;
            }
        }

        public static PixelImage Load(Texture2D texture)
        {
            if (texture == null)
                throw new ArgumentNullException(nameof(texture));
            string path = AssetDatabase.GetAssetPath(texture);
            if (string.IsNullOrEmpty(path) || !AssetDatabase.IsMainAsset(texture) || !IsImageFile(path))
                return ReadTextureObject(texture);
            return LoadFile(path);
        }

        internal static bool IsImageFile(string assetPath)
        {
            return s_FileExtensions.Contains(Path.GetExtension(assetPath).ToLowerInvariant());
        }

        /// <summary>Loads an image file from its asset path ("Assets/..." or "Packages/...").</summary>
        public static PixelImage LoadFile(string assetPath)
        {
            if (!IsImageFile(assetPath))
            {
                var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
                if (texture == null)
                    throw new PackException($"{assetPath} is not a texture the tool can read.");
                return ReadTextureObject(texture);
            }

            string full = AssetPaths.ToFullPath(assetPath);
            if (full == null || !File.Exists(full))
                throw new PackException($"File not found: {assetPath}");

            byte[] bytes = File.ReadAllBytes(full);
            string ext = Path.GetExtension(assetPath).ToLowerInvariant();
            bool jpeg = ext == ".jpg" || ext == ".jpeg";

            // Built-in decoders first (exact and fast). A variant they do not support falls back to Unity's importer.
            PackException nativeError = null;
            try
            {
                var native = ImageEncoder.TryDecode(bytes, ext, UseNativeExr);
                if (native != null)
                    return native;
                if (jpeg)
                    return LoadJpeg(bytes);
            }
            catch (PackException e)
            {
                nativeError = e;
            }

            try
            {
                var image = LoadThroughImporter(full, bytes, ext);
                image.IsLossySource |= jpeg;
                return image;
            }
            catch (PackException importerError) when (nativeError != null)
            {
                // Damaged file: the built-in decoder's message is the precise one.
                // Valid variant it does not implement: say so, then why the importer failed too.
                if (nativeError.IsUnsupported)
                    throw new PackException($"{nativeError.Message} Unity's importer could not read it either: {importerError.Message}", importerError);
                throw nativeError;
            }
        }

        /// <summary>Test hook: when false, OpenEXR files are read through Unity's importer like formats without a built-in decoder.</summary>
        internal static bool UseNativeExr { get; set; } = true;

        // ------------------------------------------------------------------ JPEG

        static PixelImage LoadJpeg(byte[] bytes)
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            try
            {
                if (!texture.LoadImage(bytes, false))
                    throw new PackException("Could not decode the JPEG file.");
                var image = ReadPixels(texture, SamplePrecision.UNorm8);
                image.IsLossySource = true;
                image.SourceDescription = "JPEG 8-bit " + image.LayoutName;
                return image;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        // ------------------------------------------------------------------ importer path (EXR, HDR, PSD, TIFF, BMP, GIF...)

        static PixelImage LoadThroughImporter(string fullSourcePath, byte[] bytes, string ext)
        {
            var probe = ImageProbe.Probe(bytes, ext);
            bool highPrecision = probe.Precision >= SamplePrecision.UNorm16 ||
                                 (!probe.Known && (ext == ".exr" || ext == ".hdr" || ext == ".tif" || ext == ".tiff"));
            var expectedFormat = highPrecision ? TextureFormat.RGBAFloat : TextureFormat.RGBA32;
            string templateKey = ext + (highPrecision ? "|float" : "|8bit");

            AssetPaths.EnsureFolder(TempFolder);
            string tempPath = TempFolder + "/src_" + Guid.NewGuid().ToString("N") + ext;
            string tempFull = Path.GetFullPath(tempPath);
            try
            {
                File.Copy(fullSourcePath, tempFull, true);

                bool usedTemplate = false;
                if (s_MetaTemplates.TryGetValue(templateKey, out string template))
                {
                    File.WriteAllText(tempFull + ".meta", s_GuidLine.Replace(template, "guid: " + Guid.NewGuid().ToString("N")));
                    usedTemplate = true;
                }

                AssetDatabase.ImportAsset(tempPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                var importer = AssetImporter.GetAtPath(tempPath) as TextureImporter;
                if (importer == null)
                    throw new PackException($"{ext} files are not imported by Unity's TextureImporter in this project, so they cannot be read losslessly.");

                var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(tempPath);
                string problem = usedTemplate ? Verify(texture, probe, expectedFormat) ?? VerifyImporter(importer) : "first import";
                if (problem != null)
                {
                    ConfigureLossless(importer, expectedFormat);
                    importer.SaveAndReimport();
                    texture = AssetDatabase.LoadAssetAtPath<Texture2D>(tempPath);
                    problem = Verify(texture, probe, expectedFormat) ?? VerifyImporter(importer);
                    if (problem != null)
                    {
#if UNITY_2021_2_OR_NEWER
                        int cap = EditorUserBuildSettings.overrideMaxTextureSize;
                        if (cap > 0 && probe.HasSize && Math.Max(probe.Width, probe.Height) > cap)
                            throw new PackException($"Lossless read failed ({problem}): Build Settings > Asset Import Overrides > Max Texture Size is set to {cap} and caps every import. Set it to 'No Override' to read this file at full size.");
#endif
                        throw new PackException($"Lossless read failed ({problem}). A project AssetPostprocessor, a default Preset or the Build Settings Asset Import Overrides may be changing texture import settings.");
                    }
                    s_MetaTemplates[templateKey] = File.ReadAllText(tempFull + ".meta");
                }

                var image = ReadPixels(texture, highPrecision ? probe.Precision : SamplePrecision.UNorm8);
                if (highPrecision && image.Precision < SamplePrecision.UNorm16)
                    image.Precision = SamplePrecision.Float;
                image.SourceDescription = $"{probe.Format} {PixelImage.PrecisionName(image.Precision)} {image.LayoutName}";
                return image;
            }
            finally
            {
                AssetDatabase.DeleteAsset(tempPath);
                if (File.Exists(tempFull))
                    File.Delete(tempFull);
                if (File.Exists(tempFull + ".meta"))
                    File.Delete(tempFull + ".meta");
                DeleteTempFolderIfEmpty();
            }
        }

        static string Verify(Texture2D texture, ProbeResult probe, TextureFormat expectedFormat)
        {
            if (texture == null)
                return "texture not imported";
            if (!texture.isReadable)
                return "not readable";
            if (texture.format != expectedFormat)
                return $"format {texture.format} instead of {expectedFormat}";
            if (texture.mipmapCount != 1)
                return "mipmaps present";
            if (probe.HasSize && (texture.width != probe.Width || texture.height != probe.Height))
                return $"size {texture.width}x{texture.height} instead of {probe.Width}x{probe.Height}";
            return null;
        }

        static string VerifyImporter(TextureImporter importer)
        {
            if (importer.sRGBTexture || importer.alphaIsTransparency || importer.textureType != TextureImporterType.Default)
                return "color space, alpha or type changed";
#if UNITY_2022_1_OR_NEWER
            if (importer.flipGreenChannel || importer.swizzleR != TextureImporterSwizzle.R || importer.swizzleG != TextureImporterSwizzle.G ||
                importer.swizzleB != TextureImporterSwizzle.B || importer.swizzleA != TextureImporterSwizzle.A)
                return "channel swizzle changed";
#endif
            return null;
        }

        static void ConfigureLossless(TextureImporter importer, TextureFormat format)
        {
            importer.textureType = TextureImporterType.Default;
            importer.textureShape = TextureImporterShape.Texture2D;
            importer.sRGBTexture = false;
            importer.alphaSource = TextureImporterAlphaSource.FromInput;
            importer.alphaIsTransparency = false;
            importer.ignorePngGamma = true;
            importer.convertToNormalmap = false;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.isReadable = true;
            importer.mipmapEnabled = false;
            importer.streamingMipmaps = false;
            importer.maxTextureSize = SizeSettings.MaxSize;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.crunchedCompression = false;
#if UNITY_2022_1_OR_NEWER
            // A project preset could remap channels at import; force the identity mapping.
            importer.flipGreenChannel = false;
            importer.swizzleR = TextureImporterSwizzle.R;
            importer.swizzleG = TextureImporterSwizzle.G;
            importer.swizzleB = TextureImporterSwizzle.B;
            importer.swizzleA = TextureImporterSwizzle.A;
#endif

            var importerFormat = format == TextureFormat.RGBAFloat ? TextureImporterFormat.RGBAFloat : TextureImporterFormat.RGBA32;
            foreach (string platform in PlatformNames())
            {
                var settings = importer.GetPlatformTextureSettings(platform);
                settings.name = platform;
                settings.overridden = true;
                settings.maxTextureSize = SizeSettings.MaxSize;
                settings.format = importerFormat;
                settings.textureCompression = TextureImporterCompression.Uncompressed;
                settings.crunchedCompression = false;
                importer.SetPlatformTextureSettings(settings);
            }
        }

        /// <summary>The editor imports with the active platform's settings; Standalone is added as a safe default.</summary>
        internal static IEnumerable<string> PlatformNames()
        {
            string active = ActivePlatformName();
            yield return active;
            if (active != "Standalone")
                yield return "Standalone";
        }

        internal static string ActivePlatformName()
        {
            var group = BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget);
#if UNITY_2021_2_OR_NEWER
            return UnityEditor.Build.NamedBuildTarget.FromBuildTargetGroup(group).TargetName;
#else
            return group == BuildTargetGroup.iOS ? "iPhone" : group.ToString();
#endif
        }

        static void DeleteTempFolderIfEmpty()
        {
            if (!AssetDatabase.IsValidFolder(TempFolder))
                return;
            string full = Path.GetFullPath(TempFolder);
            if (!Directory.Exists(full) || Directory.GetFileSystemEntries(full).Length == 0)
                AssetDatabase.DeleteAsset(TempFolder);
        }

        // ------------------------------------------------------------------ texture objects (.asset, runtime textures)

        static PixelImage ReadTextureObject(Texture2D texture)
        {
            bool compressed = GraphicsFormatUtility.IsCompressedFormat(texture.graphicsFormat);
            if (texture.isReadable && !compressed)
            {
                var image = ReadPixels(texture, PrecisionOf(texture.graphicsFormat));
                image.SourceDescription = "Texture asset " + image.LayoutName;
                return image;
            }

            // GPU readback. Exact for uncompressed data; compressed textures keep their compression artifacts.
            bool srgb = GraphicsFormatUtility.IsSRGBFormat(texture.graphicsFormat);
            int w = texture.width, h = texture.height;
            var rt = RenderTexture.GetTemporary(w, h, 0,
                srgb ? RenderTextureFormat.ARGB32 : RenderTextureFormat.ARGBFloat,
                srgb ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear);
            var previous = RenderTexture.active;
            var readable = new Texture2D(w, h, srgb ? TextureFormat.RGBA32 : TextureFormat.RGBAFloat, false, !srgb);
            try
            {
                Graphics.Blit(texture, rt);
                RenderTexture.active = rt;
                readable.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
                readable.Apply(false, false);
                var image = ReadPixels(readable, srgb ? SamplePrecision.UNorm8 : PrecisionOf(texture.graphicsFormat));
                image.IsLossySource = compressed;
                image.SourceDescription = (compressed ? "Compressed texture asset " : "Texture asset ") + image.LayoutName;
                return image;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
                UnityEngine.Object.DestroyImmediate(readable);
            }
        }

        static SamplePrecision PrecisionOf(GraphicsFormat format)
        {
            if (GraphicsFormatUtility.IsHalfFormat(format))
                return SamplePrecision.Half;
            if (GraphicsFormatUtility.IsIEEE754Format(format))
                return SamplePrecision.Float;
            // Packed and compressed HDR formats (BC6H, ASTC HDR, RGB9E5, B10G11R11) have "Float" in their name:
            // treat them as HDR so outputs default to EXR instead of clamping.
            if (format.ToString().IndexOf("Float", StringComparison.Ordinal) >= 0)
                return SamplePrecision.Half;
            if (GraphicsFormatUtility.IsCompressedFormat(format))
                return SamplePrecision.UNorm8;
            uint components = GraphicsFormatUtility.GetComponentCount(format);
            return components > 0 && GraphicsFormatUtility.GetBlockSize(format) / components >= 2
                ? SamplePrecision.UNorm16
                : SamplePrecision.UNorm8;
        }

        // ------------------------------------------------------------------ pixel readback

        /// <summary>Reads a readable texture into top-down planes. Gray and opaque images are stored compactly.</summary>
        static PixelImage ReadPixels(Texture2D texture, SamplePrecision precision)
        {
            int w = texture.width, h = texture.height, n = w * h;
            var r = new float[n];
            var g = new float[n];
            var b = new float[n];
            var a = new float[n];

            var format = texture.format;
            bool eightBit = format == TextureFormat.RGBA32 || format == TextureFormat.ARGB32 ||
                            format == TextureFormat.RGB24 || format == TextureFormat.BGRA32;
            if (format == TextureFormat.RGBAFloat)
            {
                // Reads the texture memory directly: no temporary Color[] (16 bytes per pixel) for large HDR images.
                var data = texture.GetPixelData<float>(0);
                for (int y = 0; y < h; y++)
                {
                    int src = (h - 1 - y) * w * 4;
                    int dst = y * w;
                    for (int x = 0; x < w; x++, src += 4)
                    {
                        r[dst + x] = data[src];
                        g[dst + x] = data[src + 1];
                        b[dst + x] = data[src + 2];
                        a[dst + x] = data[src + 3];
                    }
                }
            }
            else if (eightBit)
            {
                Color32[] pixels = texture.GetPixels32();
                var lut = Lut8;
                for (int y = 0; y < h; y++)
                {
                    int src = (h - 1 - y) * w;
                    int dst = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        var p = pixels[src + x];
                        r[dst + x] = lut[p.r];
                        g[dst + x] = lut[p.g];
                        b[dst + x] = lut[p.b];
                        a[dst + x] = lut[p.a];
                    }
                }
            }
            else
            {
                Color[] pixels = texture.GetPixels();
                for (int y = 0; y < h; y++)
                {
                    int src = (h - 1 - y) * w;
                    int dst = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        var p = pixels[src + x];
                        r[dst + x] = p.r;
                        g[dst + x] = p.g;
                        b[dst + x] = p.b;
                        a[dst + x] = p.a;
                    }
                }
            }
            return Compact(w, h, r, g, b, a, precision);
        }

        static PixelImage Compact(int w, int h, float[] r, float[] g, float[] b, float[] a, SamplePrecision precision)
        {
            bool gray = true;
            bool opaque = true;
            for (int i = 0; i < r.Length; i++)
            {
                if (gray && (r[i] != g[i] || r[i] != b[i]))
                    gray = false;
                if (opaque && a[i] != 1f)
                    opaque = false;
                if (!gray && !opaque)
                    break;
            }
            float[] alpha = opaque ? null : a;
            return gray ? PixelImage.Gray(w, h, r, alpha, precision) : new PixelImage(w, h, r, g, b, alpha, precision);
        }
    }
}
