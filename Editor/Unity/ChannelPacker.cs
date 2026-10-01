using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace DAVFX.TextureChannelPacker
{
    /// <summary>Everything needed to produce one texture.</summary>
    public sealed class PackRequest
    {
        public PackRecipe Recipe = new PackRecipe();
        public List<Texture2D> Inputs = new List<Texture2D>();
        public SizeSettings Size = new SizeSettings();
        public OutputSettings Output = new OutputSettings();
    }

    public sealed class PackResult
    {
        public string AssetPath;
        public Texture2D Texture;
        public int Width;
        public int Height;
        public OutputBitDepth Depth;
        public string BackupPath;
        public List<PackIssue> Issues = new List<PackIssue>();
    }

    [Serializable]
    public sealed class SplitSettings
    {
        public bool[] channels = { true, true, true, true };
        public string[] suffixes = { "_R", "_G", "_B", "_A" };

        /// <summary>Empty = next to the source (or Assets for package sources).</summary>
        public string folder = string.Empty;

        /// <summary>Auto picks EXR for float sources, TGA for TGA sources, PNG otherwise.</summary>
        public bool autoFormat = true;
        public OutputFormat format = OutputFormat.PNG;
        public OutputBitDepth bitDepth = OutputBitDepth.Auto;

        /// <summary>Do not write an alpha file when the source has no alpha.</summary>
        public bool skipMissingAlpha = true;

        /// <summary>When a file with the same name exists, pick a new name instead of replacing it.</summary>
        public bool neverOverwrite;

        public void Sanitize()
        {
            if (channels == null || channels.Length != 4)
                channels = new[] { true, true, true, true };
            if (suffixes == null || suffixes.Length != 4)
                suffixes = new[] { "_R", "_G", "_B", "_A" };
        }
    }

    /// <summary>
    /// Public entry point of the tool. The window, the context menu actions and any external hub
    /// go through these methods.
    /// </summary>
    public static class ChannelPacker
    {
        const string ProgressTitle = "Texture Channel Packer";

        /// <summary>Decodes the inputs the recipe reads and checks the request without writing anything.</summary>
        public static PackPlan Plan(PackRequest request, out PixelImage[] images)
        {
            CheckRequest(request);
            images = LoadUsedInputs(request, false);
            var infos = images.Select(InputInfo.From).ToArray();
            return PackValidator.Validate(request.Recipe, infos, request.Size, request.Output, SlotPresence(request));
        }

        public static PackResult Pack(PackRequest request, bool showProgress = true)
        {
            CheckRequest(request);
            try
            {
                var images = LoadUsedInputs(request, showProgress);
                var infos = images.Select(InputInfo.From).ToArray();
                var plan = PackValidator.Validate(request.Recipe, infos, request.Size, request.Output, SlotPresence(request));
                if (plan.HasErrors)
                    throw new PackException(string.Join("\n", plan.Issues.Where(i => i.Severity == IssueSeverity.Error).Select(i => i.Message)));

                if (showProgress)
                    EditorUtility.DisplayProgressBar(ProgressTitle, "Packing channels...", 0.7f);
                var image = PackEngine.Evaluate(request.Recipe, images, plan.Width, plan.Height, request.Size.filter);

                if (showProgress)
                    EditorUtility.DisplayProgressBar(ProgressTitle, "Writing " + request.Output.AssetPath, 0.85f);
                Texture2D reference = null;
                if (request.Output.importMode == ImportMode.CopyFromInput)
                {
                    int index = request.Output.importSourceInput;
                    reference = index >= 0 && index < request.Inputs.Count ? request.Inputs[index] : null;
                }
                var settings = request.Output;
                if (settings.importMode == ImportMode.CopyFromInput && reference == null)
                {
                    settings = settings.Clone();
                    settings.importMode = ImportMode.ToolSettings;
                }

                string path = TextureOutputWriter.Write(image, request.Recipe.layout, settings, plan.ResolvedDepth, reference, out string backup);
                return new PackResult
                {
                    AssetPath = path,
                    Texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path),
                    Width = plan.Width,
                    Height = plan.Height,
                    Depth = plan.ResolvedDepth,
                    BackupPath = backup,
                    Issues = plan.Issues
                };
            }
            finally
            {
                if (showProgress)
                    EditorUtility.ClearProgressBar();
            }
        }

        /// <summary>Writes each channel of <paramref name="source"/> as a grayscale texture. Returns the written paths.</summary>
        public static List<string> Split(Texture2D source, SplitSettings settings, bool showProgress = true)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));
            if (settings == null)
                settings = new SplitSettings();
            settings.Sanitize();
            var written = new List<string>();
            try
            {
                if (showProgress)
                    EditorUtility.DisplayProgressBar(ProgressTitle, "Reading " + source.name, 0.1f);
                string error = SplitError(source, settings);
                if (error != null)
                    throw new PackException(error);
                string sourcePath = AssetDatabase.GetAssetPath(source);
                string folder = string.IsNullOrWhiteSpace(settings.folder) ? AssetPaths.WritableFolderOf(sourcePath) : settings.folder;
                var image = DecodeCache.Get(source);
                var format = settings.autoFormat ? AutoFormat(sourcePath, image.Precision) : settings.format;
                var depth = OutputSettings.ResolveDepth(format, settings.bitDepth, image.Precision);

                for (int c = 0; c < 4; c++)
                {
                    if (!settings.channels[c])
                        continue;
                    if (c == 3 && !image.HasAlpha && settings.skipMissingAlpha)
                        continue;
                    if (showProgress)
                        EditorUtility.DisplayProgressBar(ProgressTitle, "Writing channel " + "RGBA"[c], 0.2f + 0.2f * c);

                    var recipe = new PackRecipe { layout = OutputLayout.Grayscale };
                    recipe.channels[0] = ChannelRecipe.From(Operand.Texture(0, (ChannelSource)c));
                    var plane = PackEngine.Evaluate(recipe, new[] { image }, image.Width, image.Height, ResampleFilter.None);
                    string fileName = source.name + settings.suffixes[c];
                    if (settings.neverOverwrite)
                        fileName = Path.GetFileNameWithoutExtension(AssetDatabase.GenerateUniqueAssetPath(folder + "/" + fileName + OutputSettings.ExtensionFor(format)));
                    var output = new OutputSettings
                    {
                        format = format,
                        bitDepth = depth,
                        folder = folder,
                        fileName = fileName,
                        importMode = ImportMode.ToolSettings,
                        colorSpace = OutputColorSpace.Linear
                    };
                    written.Add(TextureOutputWriter.Write(plane, OutputLayout.Grayscale, output, depth, null, out _));
                }
            }
            finally
            {
                if (showProgress)
                    EditorUtility.ClearProgressBar();
            }
            return written;
        }

        /// <summary>Why a split cannot run with these settings (folder, suffixes), or null when it can.</summary>
        public static string SplitError(Texture2D source, SplitSettings settings)
        {
            if (source == null)
                return "Pick a source texture.";
            settings.Sanitize();
            string folder = string.IsNullOrWhiteSpace(settings.folder)
                ? AssetPaths.WritableFolderOf(AssetDatabase.GetAssetPath(source))
                : settings.folder;
            string folderError = PackValidator.FolderError(folder);
            if (folderError != null)
                return folderError;

            var usedSuffixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int c = 0; c < 4; c++)
            {
                if (!settings.channels[c])
                    continue;
                string suffix = settings.suffixes[c] ?? string.Empty;
                // An empty suffix would give the source's own name and overwrite it.
                if (suffix.Trim().Length == 0)
                    return $"Channel {"RGBA"[c]}: the suffix is empty, the file would replace the source texture.";
                if (!usedSuffixes.Add(suffix))
                    return $"Channel {"RGBA"[c]}: the suffix '{suffix}' is used twice, one file would replace the other.";
                string nameError = PackValidator.FileNameError(source.name + suffix);
                if (nameError != null)
                    return $"Channel {"RGBA"[c]}: {nameError}";
            }
            return null;
        }

        /// <summary>Lossless format for derived files: EXR for float data, TGA for TGA sources, PNG otherwise.</summary>
        public static OutputFormat AutoFormat(string sourcePath, SamplePrecision precision)
        {
            if (precision >= SamplePrecision.Half)
                return OutputFormat.EXR;
            if (!string.IsNullOrEmpty(sourcePath) && Path.GetExtension(sourcePath).ToLowerInvariant() == ".tga")
                return OutputFormat.TGA;
            return OutputFormat.PNG;
        }

        /// <summary>Decoded input slots; slots the recipe does not read stay null.</summary>
        static PixelImage[] LoadUsedInputs(PackRequest request, bool showProgress)
        {
            var used = UsedSlots(request.Recipe, request.Inputs.Count);
            var images = new PixelImage[request.Inputs.Count];
            for (int i = 0; i < images.Length; i++)
            {
                var texture = request.Inputs[i];
                if (!used[i] || texture == null)
                    continue;
                if (showProgress)
                    EditorUtility.DisplayProgressBar(ProgressTitle, $"Reading {PackEngine.SlotName(i)}: {texture.name}", 0.1f + 0.5f * i / images.Length);
                images[i] = DecodeCache.Get(texture);
            }
            return images;
        }

        static void CheckRequest(PackRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));
            if (request.Recipe == null || request.Size == null || request.Output == null)
                throw new ArgumentException("PackRequest needs a Recipe, Size and Output.", nameof(request));
            if (request.Inputs == null)
                request.Inputs = new List<Texture2D>();
        }

        static bool[] SlotPresence(PackRequest request)
        {
            return request.Inputs.Select(t => t != null).ToArray();
        }

        public static bool[] UsedSlots(PackRecipe recipe, int slotCount)
        {
            var used = new bool[slotCount];
            recipe.ForEachActiveOperand(op =>
            {
                if (op.kind == OperandKind.Texture && op.input >= 0 && op.input < slotCount)
                    used[op.input] = true;
            });
            return used;
        }
    }
}
