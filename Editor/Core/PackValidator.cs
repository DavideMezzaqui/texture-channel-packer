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

        public static PackPlan Validate(PackRecipe recipe, IReadOnlyList<InputInfo> inputs, SizeSettings size, OutputSettings output)
        {
            var plan = new PackPlan();
            recipe.Sanitize();
            int slotCount = inputs?.Count ?? 0;
            plan.UsedInputs = new bool[slotCount];

            var precision = SamplePrecision.UNorm8;
            bool anyLossy = false;
            bool anyFallback = false;

            recipe.ForEachActiveOperand(op =>
            {
                if (op.kind != OperandKind.Texture)
                    return;
                InputInfo info = op.input >= 0 && op.input < slotCount ? inputs[op.input] : null;
                if (info == null)
                {
                    if (op.hasFallback)
                    {
                        if (!anyFallback)
                            plan.Add(IssueSeverity.Info, $"Input {PackEngine.SlotName(op.input)} is empty: its default value ({Format255(op.fallback)}) is used.");
                        anyFallback = true;
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

            // File name.
            if (string.IsNullOrWhiteSpace(output.fileName))
                plan.Add(IssueSeverity.Error, "Output file name is empty.");
            else if (output.fileName.IndexOfAny(s_InvalidNameChars) >= 0 || output.fileName.Trim() != output.fileName)
                plan.Add(IssueSeverity.Error, "Output file name contains invalid characters.");

            string folder = (output.folder ?? string.Empty).Replace('\\', '/').TrimEnd('/');
            if (folder != "Assets" && !folder.StartsWith("Assets/"))
                plan.Add(IssueSeverity.Error, "Output folder must be inside Assets.");

            if (output.importMode == ImportMode.CopyFromInput &&
                (output.importSourceInput < 0 || output.importSourceInput >= slotCount || inputs[output.importSourceInput] == null))
                plan.Add(IssueSeverity.Warning, $"Import settings source {PackEngine.SlotName(output.importSourceInput)} is empty: tool settings are used instead.");

            return plan;
        }

        static string Format255(float value)
        {
            return $"{value:0.###} / {ColorMath.Quantize(value, 255)}";
        }
    }
}
