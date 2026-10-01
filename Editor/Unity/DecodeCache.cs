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

        sealed class Entry
        {
            public string Key;
            public PixelImage Image;
            public PixelImage Preview;
            public int PreviewSize;
        }

        static readonly LinkedList<Entry> s_Entries = new LinkedList<Entry>();
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
            if (texture == null)
                return null;
            string key = KeyOf(texture);
            foreach (var entry in s_Entries)
            {
                if (entry.Key == key)
                    return entry.Image;
            }
            return null;
        }

        public static void Clear()
        {
            s_Entries.Clear();
            s_Bytes = 0;
        }

        static Entry GetEntry(Texture2D texture)
        {
            string key = KeyOf(texture);
            for (var node = s_Entries.First; node != null; node = node.Next)
            {
                if (node.Value.Key == key)
                {
                    s_Entries.Remove(node);
                    s_Entries.AddFirst(node);
                    return node.Value;
                }
            }

            var image = TextureSourceLoader.Load(texture);
            var created = new Entry { Key = key, Image = image };
            s_Entries.AddFirst(created);
            s_Bytes += image.ByteSize;
            while (s_Bytes > BudgetBytes && s_Entries.Count > 1)
            {
                var last = s_Entries.Last;
                s_Bytes -= last.Value.Image.ByteSize;
                s_Entries.RemoveLast();
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
