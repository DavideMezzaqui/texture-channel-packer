using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace DAVFX.TextureChannelPacker
{
    /// <summary>Tools > DAVFX > Texture Channel Packer. A thin host around <see cref="ChannelPackerView"/>.</summary>
    public sealed class ChannelPackerWindow : EditorWindow
    {
        [SerializeField] PackerState m_State = new PackerState();
        ChannelPackerView m_View;

        [MenuItem("Tools/DAVFX/Texture Channel Packer", false, 100)]
        public static ChannelPackerWindow Open()
        {
            var window = GetWindow<ChannelPackerWindow>();
            var icon = EditorGUIUtility.IconContent("PreTextureRGB");
            window.titleContent = new GUIContent("Channel Packer", icon?.image);
            window.minSize = new Vector2(440, 520);
            window.Show();
            return window;
        }

        public static void OpenWith(IList<Texture2D> textures)
        {
            var window = Open();
            if (textures != null && textures.Count > 0)
                window.m_View.AssignTextures(textures.ToArray());
        }

        void OnEnable()
        {
            if (m_State == null)
                m_State = new PackerState();
            m_View = new ChannelPackerView(() => m_State, this, Repaint);
            wantsMouseMove = true; // live pixel readout in the preview
            Undo.undoRedoPerformed += OnUndoRedo;
        }

        void OnDisable()
        {
            Undo.undoRedoPerformed -= OnUndoRedo;
            m_View?.Dispose();
            m_View = null;
        }

        void OnGUI()
        {
            m_View?.OnGUI();
        }

        void OnFocus()
        {
            m_View?.MarkDirty();
        }

        void OnProjectChange()
        {
            m_View?.MarkDirty();
        }

        void OnSelectionChange()
        {
            Repaint();
        }

        void OnUndoRedo()
        {
            if (m_State == null)
                m_State = new PackerState();
            m_View?.MarkDirty();
            Repaint();
        }
    }
}
