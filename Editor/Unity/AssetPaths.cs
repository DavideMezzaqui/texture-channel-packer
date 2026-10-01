using System.IO;
using UnityEditor;

namespace DAVFX.TextureChannelPacker
{
    internal static class AssetPaths
    {
        /// <summary>Absolute file path of an asset, including assets inside packages.</summary>
        public static string ToFullPath(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
                return null;
            if (assetPath.StartsWith("Packages/"))
            {
                var info = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(assetPath);
                if (info != null && !string.IsNullOrEmpty(info.resolvedPath))
                {
                    string relative = assetPath.Substring(info.assetPath.Length).TrimStart('/');
                    return Path.GetFullPath(Path.Combine(info.resolvedPath, relative));
                }
            }
            return Path.GetFullPath(assetPath);
        }

        /// <summary>Creates a folder (and its parents) through the AssetDatabase so .meta files are generated.</summary>
        public static void EnsureFolder(string assetFolder)
        {
            assetFolder = assetFolder.Replace('\\', '/').TrimEnd('/');
            if (AssetDatabase.IsValidFolder(assetFolder))
                return;
            int slash = assetFolder.LastIndexOf('/');
            if (slash <= 0)
                throw new PackException($"Cannot create folder '{assetFolder}'.");
            string parent = assetFolder.Substring(0, slash);
            EnsureFolder(parent);
            if (string.IsNullOrEmpty(AssetDatabase.CreateFolder(parent, assetFolder.Substring(slash + 1))))
                throw new PackException($"Cannot create folder '{assetFolder}'.");
        }

        public static string FolderOf(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
                return "Assets";
            string folder = Path.GetDirectoryName(assetPath)?.Replace('\\', '/');
            return string.IsNullOrEmpty(folder) ? "Assets" : folder;
        }

        /// <summary>Folder where outputs for a source can be written (package assets are read-only).</summary>
        public static string WritableFolderOf(string assetPath)
        {
            string folder = FolderOf(assetPath);
            return folder == "Assets" || folder.StartsWith("Assets/") ? folder : "Assets";
        }
    }
}
