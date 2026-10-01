using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DAVFX.TextureChannelPacker
{
    public static class NamingUtility
    {
        static readonly char[] s_Separators = { '_', '-', ' ', '.' };

        public static string[] Tokenize(string name)
        {
            if (string.IsNullOrEmpty(name))
                return Array.Empty<string>();
            return Path.GetFileNameWithoutExtension(name)
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
            var list = names.Where(n => !string.IsNullOrEmpty(n)).Select(Path.GetFileNameWithoutExtension).ToList();
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
