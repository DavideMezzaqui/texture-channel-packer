using System;
using System.Collections.Generic;

namespace DAVFX.TextureChannelPacker
{
    public enum IssueSeverity
    {
        Info = 0,
        Warning = 1,
        Error = 2
    }

    public readonly struct PackIssue
    {
        public readonly IssueSeverity Severity;
        public readonly string Message;

        public PackIssue(IssueSeverity severity, string message)
        {
            Severity = severity;
            Message = message;
        }

        public override string ToString()
        {
            return $"{Severity}: {Message}";
        }
    }

    /// <summary>What the validator needs to know about an input, without its pixels.</summary>
    public sealed class InputInfo
    {
        public int Width;
        public int Height;
        public SamplePrecision Precision;
        public bool IsLossy;
        public bool HasAlpha;
        public bool IsGrayscale;

        public static InputInfo From(PixelImage image)
        {
            if (image == null)
                return null;
            return new InputInfo
            {
                Width = image.Width,
                Height = image.Height,
                Precision = image.Precision,
                IsLossy = image.IsLossySource,
                HasAlpha = image.HasAlpha,
                IsGrayscale = image.IsGrayscale
            };
        }
    }

    public sealed class PackPlan
    {
        public readonly List<PackIssue> Issues = new List<PackIssue>();
        public int Width;
        public int Height;
        public SamplePrecision SourcePrecision;
        public OutputBitDepth ResolvedDepth;
        public bool[] UsedInputs = new bool[0];

        public bool HasErrors
        {
            get
            {
                foreach (var issue in Issues)
                {
                    if (issue.Severity == IssueSeverity.Error)
                        return true;
                }
                return false;
            }
        }

        public void Add(IssueSeverity severity, string message)
        {
            foreach (var issue in Issues)
            {
                if (issue.Severity == severity && issue.Message == message)
                    return;
            }
            Issues.Add(new PackIssue(severity, message));
        }
    }

    /// <summary>
    /// Checks a pack before running it and explains every step that could change the data.
    /// The goal: nothing is ever degraded silently.
    /// </summary>
    public static class PackValidator
    {
        // Invalid on at least one OS. Checked everywhere so projects stay portable.
        static readonly char[] s_InvalidNameChars = { '<', '>', ':', '"', '/', '\\', '|', '?', '*', '\0' };
        static readonly char[] s_InvalidFolderChars = { '<', '>', ':', '"', '|', '?', '*', '\0' };

        /// <param name="inputs">Decoded slots. Slots the recipe does not read may be null.</param>
        /// <param name="slotHasTexture">Which slots hold a texture, used for settings that refer to a slot (copy import settings).
        /// When null, a slot counts as filled when its info is not null.</param>
        public static PackPlan Validate(PackRecipe recipe, IReadOnlyList<InputInfo> inputs, SizeSettings size, OutputSettings output,
            IReadOnlyList<bool> slotHasTexture = null)
        {
            var plan = new PackPlan();
            recipe.Sanitize();
            int slotCount = inputs?.Count ?? 0;
            plan.UsedInputs = new bool[slotCount];

            var precision = SamplePrecision.UNorm8;
            bool anyLossy = false;

            recipe.ForEachActiveOperand(op =>
            {
                if (op.kind != OperandKind.Texture)
                    return;
                InputInfo info = op.input >= 0 && op.input < slotCount ? inputs[op.input] : null;
                if (info == null)
                {
                    if (op.hasFallback)
                    {
                        plan.Add(IssueSeverity.Info, $"Input {PackEngine.SlotName(op.input)} is empty: its default value {Format255(op.fallback)} is used.");
                    }
                    else
                    {
                        plan.Add(IssueSeverity.Error, $"Input {PackEngine.SlotName(op.input)} is used but empty.");
                    }
                    return;
                }
                plan.UsedInputs[op.input] = true;
                if (info.Precision > precision)
                    precision = info.Precision;
                anyLossy |= info.IsLossy;
                if (op.channel == ChannelSource.Alpha && !info.HasAlpha)
                    plan.Add(IssueSeverity.Info, $"Input {PackEngine.SlotName(op.input)} has no alpha: its alpha reads as white (1.0).");
            });

            plan.SourcePrecision = precision;

            // Size.
            var sizes = new List<(int width, int height)?>(slotCount);
            for (int i = 0; i < slotCount; i++)
                sizes.Add(plan.UsedInputs[i] && inputs[i] != null ? ((int, int)?)(inputs[i].Width, inputs[i].Height) : null);

            if (!size.TryResolve(sizes, out plan.Width, out plan.Height, out string sizeError))
            {
                plan.Add(IssueSeverity.Error, sizeError);
            }
            else
            {
                var mismatched = new List<string>();
                for (int i = 0; i < slotCount; i++)
                {
                    if (sizes[i].HasValue && (sizes[i].Value.width != plan.Width || sizes[i].Value.height != plan.Height))
                        mismatched.Add($"{PackEngine.SlotName(i)} ({sizes[i].Value.width}x{sizes[i].Value.height})");
                }
                if (mismatched.Count > 0)
                {
                    string list = string.Join(", ", mismatched);
                    if (size.filter == ResampleFilter.None)
                        plan.Add(IssueSeverity.Error, $"Input sizes differ from the output size {plan.Width}x{plan.Height}: {list}. Pick a resample filter to allow resizing.");
                    else
                        plan.Add(IssueSeverity.Warning, $"{list} will be resized to {plan.Width}x{plan.Height} with {size.filter}. Resized pixels are new values, not copies.");
                }
            }

            // Depth and format.
            plan.ResolvedDepth = OutputSettings.ResolveDepth(output.format, output.bitDepth, precision);
            var capacity = OutputSettings.CapacityOf(plan.ResolvedDepth);
            bool integerOutput = output.format != OutputFormat.EXR;
            if (integerOutput && precision >= SamplePrecision.Half)
                plan.Add(IssueSeverity.Warning, $"Float (HDR) source into {output.format}: values are clamped to 0-1 and quantized to {OutputSettings.DepthName(plan.ResolvedDepth)}. Use EXR to keep HDR data.");
            else if (capacity < precision)
                plan.Add(IssueSeverity.Warning, $"Source is {PixelImage.PrecisionName(precision)}, output is {OutputSettings.DepthName(plan.ResolvedDepth)}: precision is reduced.");
            if (output.format == OutputFormat.EXR && plan.ResolvedDepth == OutputBitDepth.Half && precision == SamplePrecision.UNorm16)
                plan.Add(IssueSeverity.Warning, "Half float cannot store every 16-bit value exactly. Use Float for a lossless result.");

            // The PNG encoder builds the filtered image in a single array, limited to 2 GB.
            if (output.format == OutputFormat.PNG && plan.Width > 0 && plan.Height > 0)
            {
                int sampleBytes = plan.ResolvedDepth == OutputBitDepth.Bits16 ? 2 : 1;
                long pngBytes = (long)plan.Height * ((long)plan.Width * PackRecipe.ActiveChannels(recipe.layout).Count * sampleBytes + 1);
                if (pngBytes > int.MaxValue)
                    plan.Add(IssueSeverity.Error, $"{plan.Width}x{plan.Height} at {OutputSettings.DepthName(plan.ResolvedDepth)} is too large for a single PNG (over 2 GB of pixel data). Use EXR, 8-bit, fewer channels or a smaller size.");
            }
            else if (output.format == OutputFormat.EXR && plan.Width > 0 && plan.Height > 0)
            {
                int sampleBytes = plan.ResolvedDepth == OutputBitDepth.Float ? 4 : 2;
                long exrBytes = (long)plan.Width * plan.Height * PackRecipe.ActiveChannels(recipe.layout).Count * sampleBytes;
                if (exrBytes > int.MaxValue)
                    plan.Add(IssueSeverity.Warning, $"{plan.Width}x{plan.Height} at {OutputSettings.DepthName(plan.ResolvedDepth)} is over 2 GB uncompressed: the EXR can be written only if it compresses below 2 GB.");
            }

            if (anyLossy)
                plan.Add(IssueSeverity.Info, "A source is already lossy (JPEG or GPU compressed). The tool keeps its current pixels but cannot restore lost detail.");

            // Modifiers that change values in a way 8-bit storage cannot undo.
            if (integerOutput && plan.ResolvedDepth == OutputBitDepth.Bits8)
            {
                foreach (int c in PackRecipe.ActiveChannels(recipe.layout))
                {
                    if (recipe.channels[c].conversion != ColorConversion.None)
                    {
                        plan.Add(IssueSeverity.Warning, "sRGB/Linear conversion stored in 8-bit causes banding in dark tones. 16-bit PNG or EXR keeps it smooth.");
                        break;
                    }
                }
            }

            // File name and folder.
            string nameError = FileNameError(output.fileName);
            if (nameError != null)
                plan.Add(IssueSeverity.Error, nameError);
            string folderError = FolderError(output.folder);
            if (folderError != null)
                plan.Add(IssueSeverity.Error, folderError);

            if (output.importMode == ImportMode.CopyFromInput)
            {
                int source = output.importSourceInput;
                bool filled = source >= 0 && source < slotCount &&
                              (slotHasTexture != null ? source < slotHasTexture.Count && slotHasTexture[source] : inputs[source] != null);
                if (!filled)
                    plan.Add(IssueSeverity.Warning, $"Import settings source {PackEngine.SlotName(source)} is empty: tool settings are used instead.");
            }

            return plan;
        }

        /// <summary>Why a file name (without extension) cannot be used, or null when it is fine.</summary>
        public static string FileNameError(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return "Output file name is empty.";
            if (fileName.IndexOfAny(s_InvalidNameChars) >= 0 || fileName.Trim() != fileName || fileName.EndsWith(".", StringComparison.Ordinal))
                return "Output file name contains invalid characters, or starts or ends with a space or a dot.";
            string clean = new OutputSettings { fileName = fileName }.CleanFileName;
            if (IsIgnoredByUnity(clean))
                return "Unity ignores files whose name starts with '.' or ends with '~': pick another name.";
            if (IsReservedOnWindows(clean))
                return $"'{clean}' is a reserved name on Windows: pick another name.";
            return null;
        }

        /// <summary>CON, PRN, AUX, NUL, COM1-9 and LPT1-9 cannot be file or folder names on Windows, with any extension.</summary>
        static bool IsReservedOnWindows(string name)
        {
            int dot = name.IndexOf('.');
            string stem = (dot >= 0 ? name.Substring(0, dot) : name).TrimEnd().ToUpperInvariant();
            if (stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL")
                return true;
            return stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] >= '1' && stem[3] <= '9';
        }

        /// <summary>Why an output folder cannot be used, or null when it is fine. Empty means Assets.</summary>
        public static string FolderError(string folder)
        {
            folder = OutputSettings.NormalizeFolder(folder);
            if (folder != "Assets" && !folder.StartsWith("Assets/", StringComparison.Ordinal))
                return "Output folder must be inside Assets.";
            if (("/" + folder + "/").Contains("/../") || ("/" + folder + "/").Contains("/./"))
                return "Output folder cannot contain '.' or '..' segments.";
            if (folder.IndexOfAny(s_InvalidFolderChars) >= 0)
                return "Output folder contains invalid characters.";
            foreach (string segment in folder.Split('/'))
            {
                if (segment.Length == 0 || IsIgnoredByUnity(segment) || segment.Trim() != segment)
                    return $"Output folder segment '{segment}' is empty, has extra spaces, or is ignored by Unity (starts with '.' or ends with '~').";
                // Windows drops a trailing dot when it creates the folder, so the path would not match the disk.
                if (segment.EndsWith(".", StringComparison.Ordinal))
                    return $"Output folder segment '{segment}' ends with a dot.";
                if (IsReservedOnWindows(segment))
                    return $"Output folder segment '{segment}' is a reserved name on Windows.";
            }
            return null;
        }

        /// <summary>Unity does not import hidden names: starting with a dot or ending with a tilde.</summary>
        static bool IsIgnoredByUnity(string name)
        {
            return name.StartsWith(".", StringComparison.Ordinal) || name.EndsWith("~", StringComparison.Ordinal);
        }

        static string Format255(float value)
        {
            return $"{value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} ({ColorMath.Quantize(value, 255)}/255)";
        }
    }
}
