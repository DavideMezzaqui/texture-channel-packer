using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

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
                var names = candidates.Select(Analyze).ToList();
                // Strong matches first for every slot (the map suffix of the name), then weak ones, so a material
                // name that contains a keyword ("T_Metal_Plate_AO") cannot steal a slot from a real suffix match.
                for (int pass = 0; pass < 2; pass++)
                {
                    for (int slot = 0; slot < slotCount && slot < slotKeywords.Count; slot++)
                    {
                        if (occupied[slot] || result[slot] >= 0 || slotKeywords[slot] == null || slotKeywords[slot].Length == 0)
                            continue;
                        int match = pass == 0 ? FindStrong(names, slotKeywords[slot], taken) : FindWeak(names, slotKeywords[slot], taken);
                        if (match >= 0)
                        {
                            result[slot] = match;
                            taken.Add(match);
                        }
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

        /// <summary>Index of the candidate whose name best matches any keyword for a single slot, or -1.</summary>
        public static int FindBestMatch(IReadOnlyList<string> candidateNames, IReadOnlyList<string> keywords, ICollection<int> taken)
        {
            if (keywords == null || keywords.Count == 0 || candidateNames == null)
                return -1;
            var names = candidateNames.Select(Analyze).ToList();
            int strong = FindStrong(names, keywords, taken);
            return strong >= 0 ? strong : FindWeak(names, keywords, taken);
        }

        /// <summary>Trailing tokens that describe the file, not the map: resolution, version, normal convention...</summary>
        static readonly Regex s_NoiseToken = new Regex(@"^(\d+k?|\d+x\d+|v\d+|dx|gl|ogl|directx|opengl|final|lod\d*|tex|texture|map)$", RegexOptions.CultureInvariant);

        sealed class NameInfo
        {
            public string[] Tokens;
            /// <summary>Last meaningful token, e.g. "ao" in "T_Rock_AO_2K".</summary>
            public string MapToken;
            /// <summary>Map token joined with the previous one, e.g. "basecolor" in "T_Rock_Base_Color".</summary>
            public string MapPair;
            /// <summary>The map token is a known map keyword: the name says clearly what the map is.</summary>
            public bool KnownMap;
        }

        static NameInfo Analyze(string name)
        {
            var tokens = Tokenize(name);
            int last = tokens.Length - 1;
            while (last > 0 && s_NoiseToken.IsMatch(tokens[last]))
                last--;
            var info = new NameInfo
            {
                Tokens = tokens,
                MapToken = last >= 0 ? tokens[last] : string.Empty,
                MapPair = last >= 1 ? tokens[last - 1] + tokens[last] : string.Empty
            };
            var known = PackPresets.AllKeywords;
            info.KnownMap = known.Contains(info.MapToken) || known.Contains(info.MapPair);
            return info;
        }

        /// <summary>The map suffix of the name is the keyword. Earlier keywords win.</summary>
        static int FindStrong(IReadOnlyList<NameInfo> names, IReadOnlyList<string> keywords, ICollection<int> taken)
        {
            foreach (string keyword in keywords)
            {
                for (int i = 0; i < names.Count; i++)
                {
                    if (taken != null && taken.Contains(i))
                        continue;
                    if (names[i].MapToken == keyword || names[i].MapPair == keyword)
                        return i;
                }
            }
            return -1;
        }

        /// <summary>
        /// Keyword elsewhere in the name ("T_Rock_Normal_Strong") or glued to it ("RockMetallic"). Only for names whose
        /// suffix is not already a known map, and glued matches need a long keyword so "T_Helmet" is not metallic.
        /// </summary>
        static int FindWeak(IReadOnlyList<NameInfo> names, IReadOnlyList<string> keywords, ICollection<int> taken)
        {
            foreach (string keyword in keywords)
            {
                for (int i = 0; i < names.Count; i++)
                {
                    if ((taken != null && taken.Contains(i)) || names[i].KnownMap)
                        continue;
                    if (names[i].Tokens.Contains(keyword) || (keyword.Length >= 6 && names[i].MapToken.EndsWith(keyword, StringComparison.Ordinal)))
                        return i;
                }
            }
            return -1;
        }
    }
}
