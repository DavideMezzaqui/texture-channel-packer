using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace DAVFX.TextureChannelPacker
{
    [Serializable]
    public sealed class InputSlot
    {
        public Texture2D texture;
        public string label = "Input";
    }

    public enum PackerMode
    {
        Pack = 0,
        Split = 1
    }

    /// <summary>
    /// Everything the Channel Packer UI shows. Serializable so it survives domain reloads and supports Undo
    /// when stored on a UnityEngine.Object (the window, or a hub).
    /// </summary>
    [Serializable]
    public sealed class PackerState
    {
        public const int MaxInputs = 8;

        public PackerMode mode = PackerMode.Pack;
        public string presetName = PackPresets.CustomName;
        public string presetSuffix = "_Packed";
        public bool followInputAlpha = true;

        public List<InputSlot> inputs = new List<InputSlot>();
        public PackRecipe recipe = new PackRecipe();
        public SizeSettings size = new SizeSettings();
        public OutputSettings output = new OutputSettings();

        public bool autoName = true;
        public bool autoFolder = true;
        public int previewMode;
        public bool[] expanded = new bool[4];

        public Texture2D splitSource;
        public SplitSettings split = new SplitSettings();

        /// <summary>Set when the layout must follow the alpha of input A as soon as it is decoded (presets like Swap or Invert).</summary>
        [NonSerialized] public bool layoutFollowsInputPending;

        [SerializeField] bool m_Initialized;

        public void EnsureInitialized()
        {
            if (!m_Initialized)
            {
                m_Initialized = true;
                ApplyPreset(PackPresets.Find(PackPresets.CustomName));
            }
            Sanitize();
        }

        public void Sanitize()
        {
            if (inputs == null)
                inputs = new List<InputSlot>();
            if (inputs.Count == 0)
                inputs.Add(new InputSlot());
            for (int i = 0; i < inputs.Count; i++)
            {
                if (inputs[i] == null)
                    inputs[i] = new InputSlot();
            }
            if (recipe == null)
                recipe = new PackRecipe();
            recipe.Sanitize();
            if (size == null)
                size = new SizeSettings();
            if (output == null)
                output = new OutputSettings();
            if (split == null)
                split = new SplitSettings();
            split.Sanitize();
            if (expanded == null || expanded.Length != 4)
                expanded = new bool[4];
        }

        public void ApplyPreset(PackPreset preset)
        {
            if (preset == null)
                return;
            Sanitize();
            presetName = preset.Name;
            presetSuffix = preset.Suffix ?? string.Empty;
            followInputAlpha = preset.FollowInputAlpha;
            layoutFollowsInputPending = preset.FollowInputAlpha;
            recipe = preset.Recipe.Clone();

            int slots = Mathf.Clamp(preset.SlotLabels.Length, 1, MaxInputs);
            if (preset.Name == PackPresets.CustomName)
                slots = Mathf.Max(slots, inputs.Count);
            while (inputs.Count < slots)
                inputs.Add(new InputSlot());
            if (inputs.Count > slots)
            {
                // Keep extra slots only if they hold a texture, so nothing the user picked disappears.
                for (int i = inputs.Count - 1; i >= slots; i--)
                {
                    if (inputs[i].texture == null)
                        inputs.RemoveAt(i);
                }
            }
            for (int i = 0; i < inputs.Count; i++)
                inputs[i].label = i < preset.SlotLabels.Length ? preset.SlotLabels[i] : "Input";

            output.importMode = preset.ImportMode;
            output.importSourceInput = 0;
            output.colorSpace = preset.ColorSpace;
            output.textureType = preset.TextureType;
            if (preset.UsesCustomSize)
                size.mode = SizeMode.Custom;
            else if (size.mode == SizeMode.Custom)
                size.mode = SizeMode.Largest;

            for (int i = 0; i < expanded.Length; i++)
                expanded[i] = false;
            UpdateAutoName();
        }

        public PackPreset CurrentPreset => PackPresets.Find(presetName);

        /// <summary>
        /// Puts textures into slots: by name when the preset knows the maps, otherwise in order.
        /// <paramref name="replaceAll"/> clears every slot first (Use Selection, Open in Channel Packer);
        /// otherwise only empty slots are filled (drag and drop).
        /// </summary>
        public void AssignTextures(IList<Texture2D> textures, bool replaceAll)
        {
            if (textures == null || textures.Count == 0)
                return;
            Sanitize();
            var candidates = textures.Where(t => t != null).Distinct().ToList();
            foreach (var texture in candidates)
                DecodeCache.ForgetFailure(texture); // assigning again retries a read that failed
            if (replaceAll)
            {
                foreach (var slot in inputs)
                    slot.texture = null;
            }
            else
            {
                candidates = candidates.Where(t => !inputs.Any(s => s.texture == t)).ToList();
            }

            if (candidates.Count > 0)
            {
                var preset = CurrentPreset;
                var keywords = preset != null ? preset.SlotKeywords : Array.Empty<string[]>();
                var occupied = inputs.Select(s => s.texture != null).ToArray();
                var assignment = NamingUtility.AssignSlots(candidates.Select(t => t.name).ToList(), keywords, occupied, MaxInputs);
                for (int i = 0; i < assignment.Length; i++)
                {
                    if (i >= inputs.Count)
                        inputs.Add(new InputSlot());
                    if (assignment[i] >= 0)
                        inputs[i].texture = candidates[assignment[i]];
                }
            }
            UpdateAutoName();
        }

        public void UpdateAutoName()
        {
            var textures = inputs.Where(s => s.texture != null).Select(s => s.texture).ToList();
            if (autoName)
            {
                string baseName = textures.Count > 0 ? NamingUtility.BaseName(textures.Select(t => t.name)) : "NewTexture";
                output.fileName = baseName + presetSuffix;
            }
            if (autoFolder && textures.Count > 0)
            {
                string path = UnityEditor.AssetDatabase.GetAssetPath(textures[0]);
                output.folder = AssetPaths.WritableFolderOf(path);
            }
        }

        public PackRequest ToRequest()
        {
            Sanitize();
            return new PackRequest
            {
                Recipe = recipe.Clone(),
                Inputs = inputs.Select(s => s.texture).ToList(),
                Size = size.Clone(),
                Output = output.Clone()
            };
        }
    }
}
