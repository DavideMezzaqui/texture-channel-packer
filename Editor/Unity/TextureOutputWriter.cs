using System;
using System.IO;
using DAVFX.TextureChannelPacker.Codecs;
using UnityEditor;
using UnityEngine;

namespace DAVFX.TextureChannelPacker
{
    /// <summary>Writes encoded images into the project and sets up their importer.</summary>
    public static class TextureOutputWriter
    {
        public const string BackupFolder = "Library/DAVFX_ChannelPacker/Backups";

        /// <summary>
        /// Encodes and writes <paramref name="image"/>. When the file already exists it is replaced in place,
        /// so its .meta (GUID and importer settings) and every reference to it are kept.
        /// </summary>
        /// <returns>The asset path that was written.</returns>
        public static string Write(PixelImage image, OutputLayout layout, OutputSettings settings, OutputBitDepth resolvedDepth, Texture2D importReference, out string backupPath)
        {
            backupPath = null;
            string pathError = PackValidator.FolderError(settings.folder) ?? PackValidator.FileNameError(settings.fileName);
            if (pathError != null)
                throw new PackException(pathError);
            string assetPath = settings.AssetPath;
            string folder = AssetPaths.FolderOf(assetPath);

            byte[] bytes = ImageEncoder.Encode(image, layout, settings.format, resolvedDepth, settings.tgaRle);

            AssetPaths.EnsureFolder(folder);
            string full = Path.GetFullPath(assetPath);
            bool existed = File.Exists(full);
            if (existed && settings.backupOnOverwrite)
                backupPath = Backup(full);

            File.WriteAllBytes(full, bytes);
            DecodeCache.Invalidate(assetPath);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);

            if (!existed)
                ApplyImportSettings(assetPath, image, layout, settings, importReference);
            return assetPath;
        }

        static string Backup(string fullPath)
        {
            // One folder per backup: two overwrites in the same second must not replace the first backup.
            string root = Path.GetFullPath(BackupFolder);
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", System.Globalization.CultureInfo.InvariantCulture);
            string folder = Path.Combine(root, stamp);
            for (int i = 1; Directory.Exists(folder); i++)
                folder = Path.Combine(root, stamp + "_" + i);
            Directory.CreateDirectory(folder);
            string target = Path.Combine(folder, Path.GetFileName(fullPath));
            File.Copy(fullPath, target, true);
            if (File.Exists(fullPath + ".meta"))
                File.Copy(fullPath + ".meta", target + ".meta", true);
            return target;
        }

        public static void ApplyImportSettings(string assetPath, PixelImage image, OutputLayout layout, OutputSettings settings, Texture2D importReference)
        {
            var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer == null || settings.importMode == ImportMode.UnityDefaults)
                return;

            var referenceImporter = importReference != null
                ? AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(importReference)) as TextureImporter
                : null;

            if (settings.importMode == ImportMode.CopyFromInput && referenceImporter != null)
            {
                var textureSettings = new TextureImporterSettings();
                referenceImporter.ReadTextureSettings(textureSettings);
                importer.SetTextureSettings(textureSettings);
                importer.SetPlatformTextureSettings(referenceImporter.GetDefaultPlatformTextureSettings());
                foreach (string platform in KnownPlatforms)
                {
                    var platformSettings = referenceImporter.GetPlatformTextureSettings(platform);
                    if (platformSettings.overridden)
                        importer.SetPlatformTextureSettings(platformSettings);
                }
            }
            else
            {
                importer.textureType = settings.textureType == OutputTextureType.NormalMap ? TextureImporterType.NormalMap : TextureImporterType.Default;
                importer.sRGBTexture = settings.colorSpace == OutputColorSpace.Srgb;
                switch (settings.compression)
                {
                    case OutputCompression.HighQuality:
                        importer.textureCompression = TextureImporterCompression.CompressedHQ;
                        break;
                    case OutputCompression.Uncompressed:
                        importer.textureCompression = TextureImporterCompression.Uncompressed;
                        break;
                }
            }

            // Whatever the source: alpha must follow the file, Unity must not touch RGB under transparent pixels,
            // and the texture must not be downscaled by the default 2048 max size.
            importer.alphaSource = PackRecipe.HasAlpha(layout) ? TextureImporterAlphaSource.FromInput : TextureImporterAlphaSource.None;
            importer.alphaIsTransparency = false;
            int needed = NeededMaxSize(image.Width, image.Height);
            if (importer.maxTextureSize < needed)
                importer.maxTextureSize = needed;
            importer.SaveAndReimport();
        }

        public static int NeededMaxSize(int width, int height)
        {
            int size = 32;
            int largest = Math.Max(width, height);
            while (size < largest && size < SizeSettings.MaxSize)
                size *= 2;
            return size;
        }

        static readonly string[] KnownPlatforms =
        {
            "Standalone", "Android", "iPhone", "WebGL", "tvOS", "PS4", "PS5", "XboxOne", "GameCoreXboxOne",
            "GameCoreScarlett", "Switch", "Server", "EmbeddedLinux", "LinuxHeadlessSimulation", "VisionOS"
        };
    }
}
