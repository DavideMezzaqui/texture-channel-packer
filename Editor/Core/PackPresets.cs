using System;
using System.Collections.Generic;
using System.Linq;

namespace DAVFX.TextureChannelPacker
{
    public sealed class PackPreset
    {
        public string Name;
        public string Category;
        public string Description;
        public string Suffix;
        public string[] SlotLabels = Array.Empty<string>();
        public string[][] SlotKeywords = Array.Empty<string[]>();
        public PackRecipe Recipe;
        public ImportMode ImportMode = ImportMode.ToolSettings;
        public OutputColorSpace ColorSpace = OutputColorSpace.Linear;
        public OutputTextureType TextureType = OutputTextureType.Default;

        /// <summary>The output is created from constants only, so the custom size is used.</summary>
        public bool UsesCustomSize;

        /// <summary>RGBA when the first input has alpha, RGB otherwise.</summary>
        public bool FollowInputAlpha;

        /// <summary>Menu path, e.g. "Unity/HDRP Mask Map".</summary>
        public string MenuPath => string.IsNullOrEmpty(Category) ? Name : Category + "/" + Name;
    }

    public static class PackPresets
    {
        public const string CustomName = "Custom";

        static readonly string[] s_Metallic = { "metallic", "metalness", "metal", "met", "mtl", "m" };
        static readonly string[] s_Occlusion = { "ao", "occlusion", "ambientocclusion", "occ", "ambient" };
        static readonly string[] s_Detail = { "detailmask", "detail", "dmask", "mask" };
        static readonly string[] s_Smoothness = { "smoothness", "smooth", "glossiness", "gloss", "s" };
        static readonly string[] s_Roughness = { "roughness", "rough", "rgh", "r" };
        static readonly string[] s_Albedo = { "albedo", "basecolor", "base", "color", "colour", "diffuse", "diff", "col", "bc", "d" };
        static readonly string[] s_Normal = { "normal", "normals", "nrm", "norm", "nml", "nor", "n" };
        static readonly string[] s_Opacity = { "opacity", "alpha", "transparency", "opac", "op", "mask" };
        static readonly string[] s_Height = { "height", "heightmap", "displacement", "disp", "bump", "h" };

        static List<PackPreset> s_All;
        static HashSet<string> s_Keywords;

        public static IReadOnlyList<PackPreset> All => s_All ?? (s_All = Build());

        public static HashSet<string> AllKeywords
        {
            get
            {
                if (s_Keywords == null)
                {
                    s_Keywords = new HashSet<string>(s_Metallic.Concat(s_Occlusion).Concat(s_Detail).Concat(s_Smoothness)
                        .Concat(s_Roughness).Concat(s_Albedo).Concat(s_Normal).Concat(s_Opacity).Concat(s_Height)
                        .Concat(new[] { "orm", "maskmap", "packed", "height", "emission", "emissive" }));
                }
                return s_Keywords;
            }
        }

        public static PackPreset Find(string name)
        {
            return All.FirstOrDefault(p => p.Name == name);
        }

        static Operand T(int input, ChannelSource channel, float fallback)
        {
            return Operand.Texture(input, channel, fallback);
        }

        static Operand C(float value)
        {
            return Operand.Value(value);
        }

        static ChannelRecipe Ch(Operand source, bool invert = false)
        {
            return ChannelRecipe.From(source, invert);
        }

        static PackRecipe Recipe(OutputLayout layout, ChannelRecipe r, ChannelRecipe g = null, ChannelRecipe b = null, ChannelRecipe a = null)
        {
            var recipe = new PackRecipe { layout = layout };
            recipe.channels[0] = r;
            if (g != null) recipe.channels[1] = g;
            if (b != null) recipe.channels[2] = b;
            if (a != null) recipe.channels[3] = a;
            return recipe;
        }

        static List<PackPreset> Build()
        {
            var list = new List<PackPreset>();

            list.Add(new PackPreset
            {
                Name = CustomName,
                Description = "Start from a copy of input A and change what you need.",
                Suffix = "_Packed",
                SlotLabels = new[] { "Input", "Input", "Input", "Input" },
                Recipe = Recipe(OutputLayout.RGBA,
                    Ch(T(0, ChannelSource.Red, 0f)), Ch(T(0, ChannelSource.Green, 0f)),
                    Ch(T(0, ChannelSource.Blue, 0f)), Ch(T(0, ChannelSource.Alpha, 1f))),
                FollowInputAlpha = true
            });

            // Unity
            list.Add(new PackPreset
            {
                Name = "HDRP Mask Map",
                Category = "Unity",
                Description = "HDRP Lit: R Metallic, G Ambient Occlusion, B Detail Mask, A Smoothness. Empty inputs use HDRP's defaults.",
                Suffix = "_MaskMap",
                SlotLabels = new[] { "Metallic", "Occlusion", "Detail Mask", "Smoothness" },
                SlotKeywords = new[] { s_Metallic, s_Occlusion, s_Detail, s_Smoothness },
                Recipe = Recipe(OutputLayout.RGBA,
                    Ch(T(0, ChannelSource.Red, 0f)), Ch(T(1, ChannelSource.Red, 1f)),
                    Ch(T(2, ChannelSource.Red, 1f)), Ch(T(3, ChannelSource.Red, 0.5f)))
            });
            list.Add(new PackPreset
            {
                Name = "HDRP Mask Map (from Roughness)",
                Category = "Unity",
                Description = "Same as HDRP Mask Map, smoothness is computed as 1 - roughness.",
                Suffix = "_MaskMap",
                SlotLabels = new[] { "Metallic", "Occlusion", "Detail Mask", "Roughness" },
                SlotKeywords = new[] { s_Metallic, s_Occlusion, s_Detail, s_Roughness },
                Recipe = Recipe(OutputLayout.RGBA,
                    Ch(T(0, ChannelSource.Red, 0f)), Ch(T(1, ChannelSource.Red, 1f)),
                    Ch(T(2, ChannelSource.Red, 1f)), Ch(T(3, ChannelSource.Red, 0.5f), true))
            });
            list.Add(new PackPreset
            {
                Name = "Terrain Layer Mask Map",
                Category = "Unity",
                Description = "Terrain Layers in URP and HDRP: R Metallic, G Ambient Occlusion, B Height, A Smoothness. Empty inputs use the terrain defaults.",
                Suffix = "_MaskMap",
                SlotLabels = new[] { "Metallic", "Occlusion", "Height", "Smoothness" },
                SlotKeywords = new[] { s_Metallic, s_Occlusion, s_Height, s_Smoothness },
                Recipe = Recipe(OutputLayout.RGBA,
                    Ch(T(0, ChannelSource.Red, 0f)), Ch(T(1, ChannelSource.Red, 1f)),
                    Ch(T(2, ChannelSource.Red, 0.5f)), Ch(T(3, ChannelSource.Red, 0.5f)))
            });
            list.Add(new PackPreset
            {
                Name = "Terrain Layer Mask Map (from Roughness)",
                Category = "Unity",
                Description = "Same as Terrain Layer Mask Map, smoothness is computed as 1 - roughness.",
                Suffix = "_MaskMap",
                SlotLabels = new[] { "Metallic", "Occlusion", "Height", "Roughness" },
                SlotKeywords = new[] { s_Metallic, s_Occlusion, s_Height, s_Roughness },
                Recipe = Recipe(OutputLayout.RGBA,
                    Ch(T(0, ChannelSource.Red, 0f)), Ch(T(1, ChannelSource.Red, 1f)),
                    Ch(T(2, ChannelSource.Red, 0.5f)), Ch(T(3, ChannelSource.Red, 0.5f), true))
            });
            list.Add(new PackPreset
            {
                Name = "URP - Built-in Metallic + Smoothness",
                Category = "Unity",
                Description = "RGB Metallic, A Smoothness (Smoothness Source: Metallic Alpha).",
                Suffix = "_MetallicSmoothness",
                SlotLabels = new[] { "Metallic", "Smoothness" },
                SlotKeywords = new[] { s_Metallic, s_Smoothness },
                Recipe = Recipe(OutputLayout.RGBA,
                    Ch(T(0, ChannelSource.Red, 0f)), Ch(T(0, ChannelSource.Red, 0f)),
                    Ch(T(0, ChannelSource.Red, 0f)), Ch(T(1, ChannelSource.Red, 0.5f)))
            });
            list.Add(new PackPreset
            {
                Name = "HDRP Detail Map",
                Category = "Unity",
                Description = "R desaturated Albedo, G Normal Y, B Smoothness, A Normal X. Albedo should be centered on 0.5.",
                Suffix = "_DetailMap",
                SlotLabels = new[] { "Detail Albedo", "Detail Normal", "Detail Smoothness" },
                SlotKeywords = new[] { s_Albedo, s_Normal, s_Smoothness },
                Recipe = Recipe(OutputLayout.RGBA,
                    Ch(T(0, ChannelSource.Luminance, 0.5f)), Ch(T(1, ChannelSource.Green, 0.5f)),
                    Ch(T(2, ChannelSource.Red, 0.5f)), Ch(T(1, ChannelSource.Red, 0.5f)))
            });
            list.Add(new PackPreset
            {
                Name = "Albedo + Alpha",
                Category = "Unity",
                Description = "RGB from the color texture, A from an opacity mask.",
                Suffix = "_AlbedoAlpha",
                SlotLabels = new[] { "Albedo", "Opacity" },
                SlotKeywords = new[] { s_Albedo, s_Opacity },
                ColorSpace = OutputColorSpace.Srgb,
                Recipe = Recipe(OutputLayout.RGBA,
                    Ch(T(0, ChannelSource.Red, 0.5f)), Ch(T(0, ChannelSource.Green, 0.5f)),
                    Ch(T(0, ChannelSource.Blue, 0.5f)), Ch(T(1, ChannelSource.Red, 1f)))
            });
            list.Add(new PackPreset
            {
                Name = "Albedo + Smoothness",
                Category = "Unity",
                Description = "RGB Albedo, A Smoothness (Smoothness Source: Albedo Alpha).",
                Suffix = "_AlbedoSmoothness",
                SlotLabels = new[] { "Albedo", "Smoothness" },
                SlotKeywords = new[] { s_Albedo, s_Smoothness },
                ColorSpace = OutputColorSpace.Srgb,
                Recipe = Recipe(OutputLayout.RGBA,
                    Ch(T(0, ChannelSource.Red, 0.5f)), Ch(T(0, ChannelSource.Green, 0.5f)),
                    Ch(T(0, ChannelSource.Blue, 0.5f)), Ch(T(1, ChannelSource.Red, 0.5f)))
            });

            // Other engines
            list.Add(new PackPreset
            {
                Name = "ORM (Occlusion, Roughness, Metallic)",
                Category = "Unreal - Other",
                Description = "R Ambient Occlusion, G Roughness, B Metallic.",
                Suffix = "_ORM",
                SlotLabels = new[] { "Occlusion", "Roughness", "Metallic" },
                SlotKeywords = new[] { s_Occlusion, s_Roughness, s_Metallic },
                Recipe = Recipe(OutputLayout.RGB,
                    Ch(T(0, ChannelSource.Red, 1f)), Ch(T(1, ChannelSource.Red, 0.5f)),
                    Ch(T(2, ChannelSource.Red, 0f)))
            });

            // Normal maps
            list.Add(new PackPreset
            {
                Name = "Flip Green (DirectX - OpenGL)",
                Category = "Normal Map",
                Description = "Inverts the green channel. Unity uses OpenGL style (Y+) normal maps. Alpha (often height) is kept.",
                Suffix = "_FlipG",
                SlotLabels = new[] { "Normal Map" },
                SlotKeywords = new[] { s_Normal },
                ImportMode = ImportMode.CopyFromInput,
                TextureType = OutputTextureType.NormalMap,
                FollowInputAlpha = true,
                Recipe = Recipe(OutputLayout.RGBA,
                    Ch(T(0, ChannelSource.Red, 0.5f)), Ch(T(0, ChannelSource.Green, 0.5f), true),
                    Ch(T(0, ChannelSource.Blue, 1f)), Ch(T(0, ChannelSource.Alpha, 1f)))
            });

            // Channels
            list.Add(new PackPreset
            {
                Name = "4 Grayscale Masks into RGBA",
                Category = "Channels",
                Description = "One grayscale texture per channel. Ideal for VFX masks (erosion, noise, gradients).",
                Suffix = "_Packed",
                SlotLabels = new[] { "Mask R", "Mask G", "Mask B", "Mask A" },
                Recipe = Recipe(OutputLayout.RGBA,
                    Ch(T(0, ChannelSource.Red, 0f)), Ch(T(1, ChannelSource.Red, 0f)),
                    Ch(T(2, ChannelSource.Red, 0f)), Ch(T(3, ChannelSource.Red, 1f)))
            });
            list.Add(new PackPreset
            {
                Name = "3 Grayscale Masks into RGB",
                Category = "Channels",
                Description = "One grayscale texture per channel, no alpha.",
                Suffix = "_Packed",
                SlotLabels = new[] { "Mask R", "Mask G", "Mask B" },
                Recipe = Recipe(OutputLayout.RGB,
                    Ch(T(0, ChannelSource.Red, 0f)), Ch(T(1, ChannelSource.Red, 0f)),
                    Ch(T(2, ChannelSource.Red, 0f)))
            });
            list.Add(new PackPreset
            {
                Name = "Swap Red - Blue",
                Category = "Channels",
                Description = "Exchanges red and blue (RGB - BGR). Green and alpha are kept.",
                Suffix = "_SwapRB",
                SlotLabels = new[] { "Texture" },
                ImportMode = ImportMode.CopyFromInput,
                FollowInputAlpha = true,
                Recipe = Recipe(OutputLayout.RGBA,
                    Ch(T(0, ChannelSource.Blue, 0f)), Ch(T(0, ChannelSource.Green, 0f)),
                    Ch(T(0, ChannelSource.Red, 0f)), Ch(T(0, ChannelSource.Alpha, 1f)))
            });
            list.Add(new PackPreset
            {
                Name = "Remove Alpha",
                Category = "Channels",
                Description = "Keeps RGB exactly and drops the alpha channel.",
                Suffix = "_NoAlpha",
                SlotLabels = new[] { "Texture" },
                ImportMode = ImportMode.CopyFromInput,
                Recipe = Recipe(OutputLayout.RGB,
                    Ch(T(0, ChannelSource.Red, 0f)), Ch(T(0, ChannelSource.Green, 0f)),
                    Ch(T(0, ChannelSource.Blue, 0f)))
            });
            list.Add(new PackPreset
            {
                Name = "Extract Channel to Grayscale",
                Category = "Channels",
                Description = "Saves one channel as a grayscale texture. Pick the channel in the L row.",
                Suffix = "_Channel",
                SlotLabels = new[] { "Texture" },
                Recipe = Recipe(OutputLayout.Grayscale, Ch(T(0, ChannelSource.Red, 0f)))
            });
            list.Add(new PackPreset
            {
                Name = "Invert RGB",
                Category = "Channels",
                Description = "1 - value on R, G and B. Alpha is kept.",
                Suffix = "_Inverted",
                SlotLabels = new[] { "Texture" },
                ImportMode = ImportMode.CopyFromInput,
                FollowInputAlpha = true,
                Recipe = Recipe(OutputLayout.RGBA,
                    Ch(T(0, ChannelSource.Red, 0f), true), Ch(T(0, ChannelSource.Green, 0f), true),
                    Ch(T(0, ChannelSource.Blue, 0f), true), Ch(T(0, ChannelSource.Alpha, 1f)))
            });
            list.Add(new PackPreset
            {
                Name = "Roughness - Smoothness",
                Category = "Channels",
                Description = "Grayscale output, 1 - value. Works in both directions.",
                Suffix = "_Inverted",
                SlotLabels = new[] { "Roughness or Smoothness" },
                SlotKeywords = new[] { s_Roughness.Concat(s_Smoothness).ToArray() },
                ImportMode = ImportMode.CopyFromInput,
                Recipe = Recipe(OutputLayout.Grayscale, Ch(T(0, ChannelSource.Red, 0f), true))
            });

            // New textures
            list.Add(new PackPreset
            {
                Name = "Solid Color - Constant Mask",
                Category = "New Texture",
                Description = "A texture made of constant values. Set the size in Output.",
                Suffix = "_Solid",
                UsesCustomSize = true,
                Recipe = Recipe(OutputLayout.RGBA, Ch(C(1f)), Ch(C(1f)), Ch(C(1f)), Ch(C(1f)))
            });
            list.Add(new PackPreset
            {
                Name = "Flat Normal Map",
                Category = "New Texture",
                Description = "(128, 128, 255): a normal map that does not change the surface.",
                Suffix = "_FlatNormal",
                UsesCustomSize = true,
                TextureType = OutputTextureType.NormalMap,
                Recipe = Recipe(OutputLayout.RGB, Ch(C(0.5f)), Ch(C(0.5f)), Ch(C(1f)))
            });

            return list;
        }
    }
}
