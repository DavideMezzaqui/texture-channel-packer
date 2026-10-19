using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using UnityEditor;
using UnityEngine;

namespace DAVFX.TextureChannelPacker
{
    /// <summary>
    /// Keeps recently decoded sources in memory (LRU with a byte budget) so previews and packs
    /// do not decode the same file twice. Keys include the file time and size, so edits invalidate them.
    /// </summary>
    public static class DecodeCache
    {
        /// <summary>Memory budget for cached images. Older entries are dropped first.</summary>
        public static long BudgetBytes = 1024L * 1024 * 1024;

        /// <summary>
        /// A failed read is remembered for this long. Reading through Unity's importer changes the project, which
        /// refreshes the window, which would read again: without this the editor could re-import forever.
        /// </summary>
        public static double FailureRetrySeconds = 30;

        /// <summary>
        /// Images used this recently are never evicted, even over budget: the window needs all its inputs at once,
        /// and evicting one would make the next refresh read it again (and, through the importer, refresh again).
        /// </summary>
        public static double KeepRecentSeconds = 60;

        sealed class Entry
        {
            public string Key;
            public PixelImage Image;
            public PixelImage Preview;
            public int PreviewSize;
            public string Error;
            public double FailedAt;
            public double LastUsed;
        }

        static readonly LinkedList<Entry> s_Entries = new LinkedList<Entry>();

        /// <summary>Reads a texture; replaced by tests to simulate failures.</summary>
        internal static Func<Texture2D, PixelImage> Loader = TextureSourceLoader.Load;
        static long s_Bytes;

        public static PixelImage Get(Texture2D texture)
        {
            return GetEntry(texture).Image;
        }

        /// <summary>Downscaled copy for previews (area filtered, fits in <paramref name="maxSize"/>).</summary>
        public static PixelImage GetPreview(Texture2D texture, int maxSize)
        {
            var entry = GetEntry(texture);
            if (entry.Preview == null || entry.PreviewSize != maxSize)
            {
                Resampler.FitInside(entry.Image.Width, entry.Image.Height, maxSize, out int w, out int h);
                entry.Preview = Resampler.ResizeImage(entry.Image, w, h, ResampleFilter.Area);
                entry.PreviewSize = maxSize;
            }
            return entry.Preview;
        }

        /// <summary>Returns the cached image without decoding, or null.</summary>
        public static PixelImage Peek(Texture2D texture)
        {
            return Find(texture)?.Image;
        }

        /// <summary>True when reading this texture now would not touch the disk (decoded, or failed recently).</summary>
        public static bool IsKnown(Texture2D texture)
        {
            var entry = Find(texture);
            return entry != null && (entry.Error == null || !Expired(entry));
        }

        public static void Clear()
        {
            s_Entries.Clear();
            s_Bytes = 0;
        }

        /// <summary>Memory held by decoded images.</summary>
        public static long CachedBytes => s_Bytes;

        internal static int EntryCount => s_Entries.Count;

        /// <summary>
        /// Drops everything cached for a file. Called after the tool writes it: the file time and size are not enough
        /// on file systems with one or two second timestamps (HFS+, FAT, exFAT) when a file is rewritten quickly.
        /// </summary>
        public static void Invalidate(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
                return;
            string prefix = assetPath + "|";
            for (var node = s_Entries.First; node != null;)
            {
                var next = node.Next;
                if (node.Value.Key.StartsWith(prefix, StringComparison.Ordinal))
                {
                    s_Bytes -= node.Value.Image?.ByteSize ?? 0;
                    s_Entries.Remove(node);
                }
                node = next;
            }
        }

        /// <summary>Forgets a failed read so the next request tries again (the user assigned the texture again).</summary>
        public static void ForgetFailure(Texture2D texture)
        {
            var entry = texture != null ? Find(texture) : null;
            if (entry != null && entry.Error != null)
                s_Entries.Remove(entry); // failed entries hold no image: the byte count does not change
        }

        static Entry Find(Texture2D texture)
        {
            if (texture == null)
                return null;
            string key = KeyOf(texture);
            foreach (var entry in s_Entries)
            {
                if (entry.Key == key)
                    return entry;
            }
            return null;
        }

        static bool Expired(Entry failed)
        {
            return EditorApplication.timeSinceStartup - failed.FailedAt > FailureRetrySeconds;
        }

        static Entry GetEntry(Texture2D texture)
        {
            string key = KeyOf(texture);
            for (var node = s_Entries.First; node != null; node = node.Next)
            {
                if (node.Value.Key != key)
                    continue;
                if (node.Value.Error != null)
                {
                    if (!Expired(node.Value))
                        throw new PackException(node.Value.Error);
                    s_Entries.Remove(node);
                    break;
                }
                s_Entries.Remove(node);
                s_Entries.AddFirst(node);
                node.Value.LastUsed = EditorApplication.timeSinceStartup;
                return node.Value;
            }

            PixelImage image;
            try
            {
                image = Loader(texture);
            }
            catch (Exception e)
            {
                // Remember every failure: retrying on each repaint would re-import the source in a loop.
                string message = e is PackException ? e.Message : $"{e.GetType().Name}: {e.Message}";
                s_Entries.AddFirst(new Entry { Key = key, Error = message, FailedAt = EditorApplication.timeSinceStartup });
                throw;
            }

            double now = EditorApplication.timeSinceStartup;
            var created = new Entry { Key = key, Image = image, LastUsed = now };
            s_Entries.AddFirst(created);
            s_Bytes += image.ByteSize;

            // Evict the least recently used entries that have not been needed for a while.
            var candidate = s_Entries.Last;
            while (s_Bytes > BudgetBytes && candidate != null)
            {
                var previous = candidate.Previous;
                var entry = candidate.Value;
                if (entry != created && (entry.Error != null || now - entry.LastUsed > KeepRecentSeconds))
                {
                    s_Bytes -= entry.Image?.ByteSize ?? 0;
                    s_Entries.Remove(candidate);
                }
                candidate = previous;
            }
            return created;
        }

        static string KeyOf(Texture2D texture)
        {
            if (texture == null)
                throw new ArgumentNullException(nameof(texture));
            string path = AssetDatabase.GetAssetPath(texture);
            // Image files are decoded from disk, so the file identifies the data. Anything else is read from the
            // imported texture, which changes with its import settings: the key then follows the texture object.
            if (!string.IsNullOrEmpty(path) && AssetDatabase.IsMainAsset(texture) && TextureSourceLoader.IsImageFile(path))
            {
                string full = AssetPaths.ToFullPath(path);
                if (full != null && File.Exists(full))
                {
                    var info = new FileInfo(full);
                    return path + "|" + info.LastWriteTimeUtc.Ticks + "|" + info.Length;
                }
            }
            // Textures without a file: identify the object itself (GetInstanceID is removed in recent Unity 6 versions).
            if (!s_ObjectIds.TryGetValue(texture, out var id))
            {
                id = new ObjectId { Value = ++s_NextObjectId };
                s_ObjectIds.Add(texture, id);
            }
            return $"object|{id.Value}|{texture.updateCount}|{texture.width}x{texture.height}|{texture.graphicsFormat}";
        }

        sealed class ObjectId
        {
            public long Value;
        }

        static readonly ConditionalWeakTable<Texture2D, ObjectId> s_ObjectIds = new ConditionalWeakTable<Texture2D, ObjectId>();
        static long s_NextObjectId;
    }
}
