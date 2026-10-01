using System;
using System.Collections.Generic;
using System.Linq;

namespace DAVFX.TextureChannelPacker
{
    public static class NamingUtility
    {
        static readonly char[] s_Separators = { '_', '-', ' ', '.' };

        static readonly string[] s_ImageExtensions =
        {
            ".png", ".tga", ".exr", ".jpg", ".jpeg", ".psd", ".tif", ".tiff", ".bmp", ".gif", ".hdr", ".iff", ".pict", ".pct", ".pic", ".dds"
        };

        /// <summary>
        /// Removes a folder and a known image extension. Texture names have no extension, and may contain dots
        /// ("T_Rock.v2_AO"), so only real image extensions are removed.
        /// </summary>
        public static string StripImageExtension(string name)
        {
            if (string.IsNullOrEmpty(name))
                return string.Empty;
            int slash = Math.Max(name.LastIndexOf('/'), name.LastIndexOf('\\'));
            if (slash >= 0)
                name = name.Substring(slash + 1);
            int dot = name.LastIndexOf('.');
            if (dot > 0 && Array.IndexOf(s_ImageExtensions, name.Substring(dot).ToLowerInvariant()) >= 0)
                name = name.Substring(0, dot);
            return name;
        }

        public static string[] Tokenize(string name)
        {
            if (string.IsNullOrEmpty(name))
                return Array.Empty<string>();
            return StripImageExtension(name)
                .Split(s_Separators, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.ToLowerInvariant())
                .ToArray();
        }

        /// <summary>
        /// Shared base name of a texture set, e.g. "T_Rock_AO" + "T_Rock_Metallic" gives "T_Rock".
        /// With a single name the last token is dropped when it is a known map keyword.
        /// </summary>
        public static string BaseName(IEnumerable<string> names)
        {
            var list = names.Where(n => !string.IsNullOrEmpty(n)).Select(StripImageExtension).Where(n => n.Length > 0).ToList();
            if (list.Count == 0)
                return "NewTexture";

            string result;
            if (list.Count == 1)
            {
                result = list[0];
                int cut = result.LastIndexOfAny(s_Separators);
                if (cut > 0)
                {
                    string last = result.Substring(cut + 1).ToLowerInvariant();
                    if (PackPresets.AllKeywords.Contains(last))
                        result = result.Substring(0, cut);
                }
            }
            else
            {
                result = list[0];
                foreach (string n in list.Skip(1))
                {
                    int i = 0;
                    while (i < result.Length && i < n.Length && char.ToLowerInvariant(result[i]) == char.ToLowerInvariant(n[i]))
                        i++;
                    result = result.Substring(0, i);
                }
                // Do not cut a word in half: go back to the last separator unless the prefix ends exactly at one.
                bool endsAtSeparator = result.Length > 0 && Array.IndexOf(s_Separators, result[result.Length - 1]) >= 0;
                if (!endsAtSeparator)
                {
                    bool allEndHere = list.All(n => n.Length == result.Length || Array.IndexOf(s_Separators, n[result.Length]) >= 0);
                    if (!allEndHere)
                    {
                        int cut = result.LastIndexOfAny(s_Separators);
                        result = cut >= 0 ? result.Substring(0, cut + 1) : string.Empty;
                    }
                }
            }

            result = result.TrimEnd(s_Separators);
            return result.Length == 0 ? "NewTexture" : result;
        }

        /// <summary>
        /// Decides which candidate goes into which slot. Returns, for every slot (the array can be longer than
        /// <paramref name="occupied"/> when new slots are needed), the candidate index or -1 to leave the slot as it is.
        /// With keywords, candidates go only where their name matches: other maps of the same set (albedo, normal...)
        /// are left out instead of landing in a slot with a different meaning. When no name matches, or the slots have
        /// no keywords, candidates fill the free slots in order and new slots are added up to <paramref name="maxSlots"/>.
        /// </summary>
        public static int[] AssignSlots(IReadOnlyList<string> candidates, IReadOnlyList<string[]> slotKeywords, IReadOnlyList<bool> occupied, int maxSlots)
        {
            int slotCount = occupied.Count;
            var result = Enumerable.Repeat(-1, slotCount).ToList();
            if (candidates == null || candidates.Count == 0)
                return result.ToArray();

            var taken = new HashSet<int>();
            if (slotKeywords != null)
            {
                for (int slot = 0; slot < slotCount && slot < slotKeywords.Count; slot++)
                {
                    if (occupied[slot] || slotKeywords[slot] == null || slotKeywords[slot].Length == 0)
                        continue;
                    int match = FindBestMatch(candidates, slotKeywords[slot], taken);
                    if (match >= 0)
                    {
                        result[slot] = match;
                        taken.Add(match);
                    }
                }
                if (taken.Count > 0)
                    return result.ToArray();
            }

            int next = 0;
            for (int slot = 0; slot < slotCount && next < candidates.Count; slot++)
            {
                if (!occupied[slot])
                    result[slot] = next++;
            }
            while (next < candidates.Count && result.Count < maxSlots)
                result.Add(next++);
            return result.ToArray();
        }

        /// <summary>Index of the candidate whose name best matches any keyword, or -1.</summary>
        public static int FindBestMatch(IReadOnlyList<string> candidateNames, IReadOnlyList<string> keywords, ICollection<int> taken)
        {
            if (keywords == null || keywords.Count == 0)
                return -1;
            // Earlier keywords win, then exact token matches over suffix matches.
            for (int k = 0; k < keywords.Count; k++)
            {
                string keyword = keywords[k];
                for (int i = 0; i < candidateNames.Count; i++)
                {
                    if (taken != null && taken.Contains(i))
                        continue;
                    var tokens = Tokenize(candidateNames[i]);
                    if (tokens.Contains(keyword))
                        return i;
                }
            }
            for (int k = 0; k < keywords.Count; k++)
            {
                string keyword = keywords[k];
                if (keyword.Length < 3)
                    continue;
                for (int i = 0; i < candidateNames.Count; i++)
                {
                    if (taken != null && taken.Contains(i))
                        continue;
                    var tokens = Tokenize(candidateNames[i]);
                    if (tokens.Length > 0 && tokens[tokens.Length - 1].EndsWith(keyword))
                        return i;
                }
            }
            return -1;
        }
    }
}
