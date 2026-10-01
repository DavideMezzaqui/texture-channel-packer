using System;
using System.Collections.Generic;

namespace DAVFX.TextureChannelPacker
{
    public enum OutputFormat
    {
        PNG = 0,
        TGA = 1,
        EXR = 2
    }

    public enum OutputBitDepth
    {
        /// <summary>Lowest depth that keeps every source value.</summary>
        Auto = 0,
        Bits8 = 1,
        Bits16 = 2,
        Half = 3,
        Float = 4
    }

    public enum ImportMode
    {
        /// <summary>Apply color space, type and compression chosen in the tool.</summary>
        ToolSettings = 0,
        /// <summary>Copy the importer settings of an input texture.</summary>
        CopyFromInput = 1,
        /// <summary>Leave whatever Unity (or the project's presets) assigns.</summary>
        UnityDefaults = 2
    }

    public enum OutputColorSpace
    {
        /// <summary>Data textures: masks, roughness, metallic... (sRGB off).</summary>
        Linear = 0,
        /// <summary>Color textures: albedo, emission (sRGB on).</summary>
        Srgb = 1
    }

    public enum OutputTextureType
    {
        Default = 0,
        NormalMap = 1
    }

    public enum OutputCompression
    {
        ProjectDefault = 0,
        HighQuality = 1,
        Uncompressed = 2
    }

    [Serializable]
    public sealed class OutputSettings
    {
        public OutputFormat format = OutputFormat.PNG;
        public OutputBitDepth bitDepth = OutputBitDepth.Auto;
        public bool tgaRle;
        public string folder = "Assets";
        public string fileName = "NewPackedTexture";

        public ImportMode importMode = ImportMode.ToolSettings;
        public int importSourceInput;
        public OutputColorSpace colorSpace = OutputColorSpace.Linear;
        public OutputTextureType textureType = OutputTextureType.Default;
        public OutputCompression compression = OutputCompression.ProjectDefault;

        /// <summary>When overwriting an existing file, copy it to Library/DAVFX_ChannelPacker/Backups first.</summary>
        public bool backupOnOverwrite = true;

        public string Extension => ExtensionFor(format);

        public string AssetPath
        {
            get
            {
                string dir = string.IsNullOrEmpty(folder) ? "Assets" : folder.Replace('\\', '/').TrimEnd('/');
                return dir + "/" + fileName + Extension;
            }
        }

        public static string ExtensionFor(OutputFormat format)
        {
            switch (format)
            {
                case OutputFormat.TGA: return ".tga";
                case OutputFormat.EXR: return ".exr";
                default: return ".png";
            }
        }

        public OutputSettings Clone()
        {
            return (OutputSettings)MemberwiseClone();
        }

        /// <summary>Bit depths that make sense for a format (Auto first).</summary>
        public static OutputBitDepth[] DepthsFor(OutputFormat format)
        {
            switch (format)
            {
                case OutputFormat.PNG: return new[] { OutputBitDepth.Auto, OutputBitDepth.Bits8, OutputBitDepth.Bits16 };
                case OutputFormat.TGA: return new[] { OutputBitDepth.Bits8 };
                default: return new[] { OutputBitDepth.Auto, OutputBitDepth.Half, OutputBitDepth.Float };
            }
        }

        /// <summary>Concrete depth for a format given the highest source precision. Never returns Auto.</summary>
        public static OutputBitDepth ResolveDepth(OutputFormat format, OutputBitDepth requested, SamplePrecision sourcePrecision)
        {
            switch (format)
            {
                case OutputFormat.TGA:
                    return OutputBitDepth.Bits8;
                case OutputFormat.PNG:
                    if (requested == OutputBitDepth.Bits8 || requested == OutputBitDepth.Bits16)
                        return requested;
                    return sourcePrecision >= SamplePrecision.UNorm16 ? OutputBitDepth.Bits16 : OutputBitDepth.Bits8;
                default:
                    if (requested == OutputBitDepth.Half || requested == OutputBitDepth.Float)
                        return requested;
                    // Half holds every 8-bit value exactly but not every 16-bit value.
                    return sourcePrecision == SamplePrecision.UNorm8 || sourcePrecision == SamplePrecision.Half
                        ? OutputBitDepth.Half
                        : OutputBitDepth.Float;
            }
        }

        public static string DepthName(OutputBitDepth depth)
        {
            switch (depth)
            {
                case OutputBitDepth.Bits8: return "8-bit";
                case OutputBitDepth.Bits16: return "16-bit";
                case OutputBitDepth.Half: return "Half (16-bit float)";
                case OutputBitDepth.Float: return "Float (32-bit)";
                default: return "Auto";
            }
        }

        /// <summary>Precision that a resolved output depth can hold losslessly.</summary>
        public static SamplePrecision CapacityOf(OutputBitDepth depth)
        {
            switch (depth)
            {
                case OutputBitDepth.Bits16: return SamplePrecision.UNorm16;
                case OutputBitDepth.Half: return SamplePrecision.Half;
                case OutputBitDepth.Float: return SamplePrecision.Float;
                default: return SamplePrecision.UNorm8;
            }
        }
    }

    public enum SizeMode
    {
        Largest = 0,
        Smallest = 1,
        FromInput = 2,
        Custom = 3
    }

    [Serializable]
    public sealed class SizeSettings
    {
        public SizeMode mode = SizeMode.Largest;
        public int referenceInput;
        public int customWidth = 1024;
        public int customHeight = 1024;
        public ResampleFilter filter = ResampleFilter.None;

        public const int MaxSize = 16384;

        public SizeSettings Clone()
        {
            return (SizeSettings)MemberwiseClone();
        }

        /// <summary>
        /// Resolves the output size. <paramref name="usedSizes"/> holds the size of every input slot
        /// the recipe actually reads (null entries for unused or empty slots).
        /// Falls back to the custom size when no texture is read.
        /// </summary>
        public bool TryResolve(IReadOnlyList<(int width, int height)?> usedSizes, out int width, out int height, out string error)
        {
            error = null;
            width = 0;
            height = 0;

            bool any = false;
            if (usedSizes != null)
            {
                foreach (var s in usedSizes)
                    any |= s.HasValue;
            }

            SizeMode effective = any ? mode : SizeMode.Custom;
            switch (effective)
            {
                case SizeMode.Custom:
                    width = customWidth;
                    height = customHeight;
                    break;
                case SizeMode.FromInput:
                    if (usedSizes == null || referenceInput < 0 || referenceInput >= usedSizes.Count || !usedSizes[referenceInput].HasValue)
                    {
                        error = $"Size reference input {PackEngine.SlotName(referenceInput)} is not used or empty.";
                        return false;
                    }
                    width = usedSizes[referenceInput].Value.width;
                    height = usedSizes[referenceInput].Value.height;
                    break;
                default:
                    long best = effective == SizeMode.Largest ? long.MinValue : long.MaxValue;
                    foreach (var s in usedSizes)
                    {
                        if (!s.HasValue)
                            continue;
                        long area = (long)s.Value.width * s.Value.height;
                        bool better = effective == SizeMode.Largest ? area > best : area < best;
                        if (better)
                        {
                            best = area;
                            width = s.Value.width;
                            height = s.Value.height;
                        }
                    }
                    break;
            }

            if (width < 1 || height < 1 || width > MaxSize || height > MaxSize)
            {
                error = $"Output size {width}x{height} is outside 1..{MaxSize}.";
                return false;
            }
            return true;
        }
    }
}
