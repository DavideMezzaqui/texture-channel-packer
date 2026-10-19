using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace DAVFX.TextureChannelPacker
{
    /// <summary>Right click actions on textures in the Project window. They always create new files.</summary>
    static class QuickActions
    {
        const string Root = "Assets/DAVFX Channel Packer/";
        const int Priority = 1100;

        static Texture2D[] SelectedTextures()
        {
            return Selection.objects.OfType<Texture2D>().Where(t => !string.IsNullOrEmpty(AssetDatabase.GetAssetPath(t))).ToArray();
        }

        static bool HasSelection()
        {
            return SelectedTextures().Length > 0;
        }

        [MenuItem(Root + "Open in Channel Packer", false, Priority)]
        static void Open()
        {
            ChannelPackerWindow.OpenWith(SelectedTextures());
        }

        [MenuItem(Root + "Split Channels", false, Priority + 20)]
        static void Split()
        {
            var created = new List<string>();
            Run("Split Channels", texture => created.AddRange(ChannelPacker.Split(texture, new SplitSettings { neverOverwrite = true }, false)));
            Finish(created);
        }

        [MenuItem(Root + "Flip Normal Green (DirectX - OpenGL)", false, Priority + 21)]
        static void FlipGreen()
        {
            RunPreset("Flip Green (DirectX - OpenGL)");
        }

        [MenuItem(Root + "Remove Alpha", false, Priority + 22)]
        static void RemoveAlpha()
        {
            RunPreset("Remove Alpha");
        }

        [MenuItem(Root + "Invert RGB", false, Priority + 23)]
        static void Invert()
        {
            RunPreset("Invert RGB");
        }

        [MenuItem(Root + "Open in Channel Packer", true)]
        [MenuItem(Root + "Split Channels", true)]
        [MenuItem(Root + "Flip Normal Green (DirectX - OpenGL)", true)]
        [MenuItem(Root + "Remove Alpha", true)]
        [MenuItem(Root + "Invert RGB", true)]
        static bool Validate()
        {
            return HasSelection();
        }

        static void RunPreset(string presetName)
        {
            var preset = PackPresets.Find(presetName);
            var created = new List<string>();
            Run(preset.Name, texture =>
            {
                var image = DecodeCache.Get(texture);
                var recipe = preset.Recipe.Clone();
                if (preset.FollowInputAlpha)
                    recipe.layout = image.HasAlpha ? OutputLayout.RGBA : OutputLayout.RGB;

                string sourcePath = AssetDatabase.GetAssetPath(texture);
                var format = ChannelPacker.AutoFormat(sourcePath, image.Precision);
                string folder = AssetPaths.WritableFolderOf(sourcePath);
                string unique = AssetDatabase.GenerateUniqueAssetPath(folder + "/" + texture.name + preset.Suffix + OutputSettings.ExtensionFor(format));

                var request = new PackRequest
                {
                    Recipe = recipe,
                    Inputs = new List<Texture2D> { texture },
                    Size = new SizeSettings { mode = SizeMode.FromInput, referenceInput = 0 },
                    Output = new OutputSettings
                    {
                        format = format,
                        bitDepth = OutputBitDepth.Auto,
                        folder = folder,
                        fileName = Path.GetFileNameWithoutExtension(unique),
                        importMode = ImportMode.CopyFromInput,
                        importSourceInput = 0,
                        colorSpace = preset.ColorSpace,
                        textureType = preset.TextureType
                    }
                };
                created.Add(ChannelPacker.Pack(request, false).AssetPath);
            });
            Finish(created);
        }

        static void Run(string title, Action<Texture2D> action)
        {
            var textures = SelectedTextures();
            try
            {
                for (int i = 0; i < textures.Length; i++)
                {
                    if (EditorUtility.DisplayCancelableProgressBar(title, textures[i].name, (float)i / textures.Length))
                        break;
                    try
                    {
                        action(textures[i]);
                    }
                    catch (PackException e)
                    {
                        Debug.LogError($"[Channel Packer] {title} failed on {textures[i].name}: {e.Message}", textures[i]);
                    }
                    catch (Exception e)
                    {
                        // Keep going with the other textures; the full error is in the Console.
                        Debug.LogError($"[Channel Packer] {title} failed on {textures[i].name}: {e.GetType().Name}: {e.Message}", textures[i]);
                        Debug.LogException(e);
                    }
                    finally
                    {
                        // A batch of large textures would otherwise stay decoded in memory until the cache expires.
                        DecodeCache.Invalidate(AssetDatabase.GetAssetPath(textures[i]));
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        static void Finish(List<string> created)
        {
            if (created.Count == 0)
                return;
            var objects = created.Select(AssetDatabase.LoadAssetAtPath<Texture2D>).Where(t => t != null).ToArray();
            Selection.objects = objects;
            if (objects.Length > 0)
                EditorGUIUtility.PingObject(objects[0]);
            Debug.Log("[Channel Packer] Created:\n" + string.Join("\n", created));
        }
    }
}
