using UnityEditor;
using UnityEngine;

namespace DAVFX.TextureChannelPacker
{
    static class PackerStyles
    {
        public static readonly Color Red = new Color(0.86f, 0.33f, 0.33f);
        public static readonly Color Green = new Color(0.36f, 0.72f, 0.38f);
        public static readonly Color Blue = new Color(0.36f, 0.55f, 0.93f);
        public static readonly Color Alpha = new Color(0.62f, 0.62f, 0.62f);
        public static readonly Color Gray = new Color(0.78f, 0.78f, 0.78f);
        public static readonly Color Slot = new Color(0.32f, 0.32f, 0.32f);

        static GUIStyle s_Section;
        static GUIStyle s_Chip;
        static GUIStyle s_Info;
        static GUIStyle s_Card;
        static GUIStyle s_Title;
        static GUIStyle s_BigButton;
        static GUIStyle s_SlotLabel;
        static GUIStyle s_Wrap;

        public static Color Separator => EditorGUIUtility.isProSkin ? new Color(1f, 1f, 1f, 0.08f) : new Color(0f, 0f, 0f, 0.12f);
        public static Color ChipText => Color.white;

        public static GUIStyle Section => s_Section ?? (s_Section = new GUIStyle(EditorStyles.boldLabel) { fontSize = 12, margin = new RectOffset(0, 0, 6, 2) });

        public static GUIStyle Title => s_Title ?? (s_Title = new GUIStyle(EditorStyles.boldLabel) { fontSize = 14 });

        public static GUIStyle Chip => s_Chip ?? (s_Chip = new GUIStyle(EditorStyles.boldLabel)
        {
            alignment = TextAnchor.MiddleCenter,
            normal = { textColor = Color.white },
            fontSize = 11
        });

        public static GUIStyle Info => s_Info ?? (s_Info = new GUIStyle(EditorStyles.miniLabel)
        {
            wordWrap = true,
            normal = { textColor = EditorGUIUtility.isProSkin ? new Color(0.62f, 0.62f, 0.62f) : new Color(0.38f, 0.38f, 0.38f) }
        });

        public static GUIStyle Wrap => s_Wrap ?? (s_Wrap = new GUIStyle(EditorStyles.label) { wordWrap = true });

        public static GUIStyle SlotLabel => s_SlotLabel ?? (s_SlotLabel = new GUIStyle(EditorStyles.label) { clipping = TextClipping.Clip });

        public static GUIStyle Card => s_Card ?? (s_Card = new GUIStyle(EditorStyles.helpBox) { padding = new RectOffset(8, 8, 6, 6), margin = new RectOffset(0, 0, 2, 4) });

        public static GUIStyle BigButton => s_BigButton ?? (s_BigButton = new GUIStyle(GUI.skin.button) { fontSize = 13, fontStyle = FontStyle.Bold, fixedHeight = 32 });

        public static Color ChannelColor(OutputLayout layout, int channel)
        {
            if (PackRecipe.IsGray(layout))
                return channel == 3 ? Alpha : Gray;
            switch (channel)
            {
                case 0: return Red;
                case 1: return Green;
                case 2: return Blue;
                default: return Alpha;
            }
        }

        public static string ChannelLetter(OutputLayout layout, int channel)
        {
            if (PackRecipe.IsGray(layout))
                return channel == 3 ? "A" : "L";
            return "RGBA"[channel].ToString();
        }

        public static void DrawChip(Rect rect, string text, Color color)
        {
            EditorGUI.DrawRect(rect, color);
            var previous = GUI.color;
            GUI.color = Color.white;
            GUI.Label(rect, text, Chip);
            GUI.color = previous;
        }

        public static void SectionHeader(string text)
        {
            GUILayout.Space(4);
            GUILayout.Label(text, Section);
            var rect = GUILayoutUtility.GetRect(1, 1, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(rect, Separator);
            GUILayout.Space(4);
        }
    }
}
