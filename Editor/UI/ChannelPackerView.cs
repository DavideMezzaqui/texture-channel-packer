using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace DAVFX.TextureChannelPacker
{
    /// <summary>
    /// The whole Channel Packer interface as an embeddable IMGUI view.
    /// Host it in any EditorWindow (or an IMGUIContainer) by calling <see cref="OnGUI"/>. The state lives in a
    /// <see cref="PackerState"/> serialized by the host on <c>undoTarget</c>, which gives Undo support.
    /// </summary>
    /// <remarks>
    /// IMGUI runs every event twice (Layout, then the event itself) and both passes must draw the same controls.
    /// Every change that adds or removes controls (mode, layout, foldouts, slots...) is therefore deferred to the
    /// end of the pass, and file system checks that decide what is drawn are only done during Layout.
    /// </remarks>
    public sealed class ChannelPackerView : IDisposable
    {
        public const string PackageName = "com.davfx.texture-channel-packer";

        const float WideLayoutWidth = 820f;
        const int PreviewResolution = 512;
        const string UndoName = "Channel Packer";
        const string Title = "Texture Channel Packer";

        static readonly string[] s_LayoutNames = { "RGBA", "RGB", "Grayscale", "Grayscale + Alpha" };
        static readonly string[] s_SizeModeNames = { "Largest input", "Smallest input", "Same as input", "Custom" };
        static readonly string[] s_FilterNames = { "None (sizes must match)", "Nearest (exact pixels)", "Bilinear (smooth)", "Area (best for downscale)" };
        static readonly string[] s_FormatNames = { "PNG", "TGA", "EXR" };
        static readonly string[] s_ImportModeNames = { "Tool settings", "Copy from input", "Unity defaults" };
        static readonly string[] s_ColorSpaceNames = { "Linear (data: masks, roughness...)", "sRGB (color)" };
        static readonly string[] s_TypeNames = { "Default", "Normal Map" };
        static readonly string[] s_CompressionNames = { "Project default", "High quality", "Uncompressed" };
        static readonly string[] s_CombineNames = { "None", "Multiply", "Add", "Subtract", "Minimum", "Maximum", "Average", "Difference", "Screen" };
        static readonly string[] s_ConversionNames = { "None", "sRGB to Linear", "Linear to sRGB" };
        static readonly int[] s_CommonSizes = { 32, 64, 128, 256, 512, 1024, 2048, 4096, 8192 };

        readonly Func<PackerState> m_StateProvider;
        readonly UnityEngine.Object m_UndoTarget;
        readonly Action m_Repaint;
        readonly PreviewBuilder m_Preview = new PreviewBuilder();
        readonly List<Action> m_Deferred = new List<Action>();

        PixelImage[] m_Images = new PixelImage[0];
        string[] m_SlotErrors = new string[0];
        bool[] m_Used = new bool[0];
        PackPlan m_Plan;
        PixelImage m_PreviewImage;
        string m_PreviewError;
        bool m_PreviewTextureDirty;
        PreviewMode m_BuiltMode;

        PixelImage m_SplitImage;
        string m_SplitError;

        Texture2D m_LastFirstTexture;
        Texture2D m_LastOutput;
        string m_LastResult;
        Vector2 m_Scroll;
        bool m_OutputExists;
        string m_SplitSettingsError;
        bool m_RefreshScheduled;
        bool m_PackScheduled;
        bool m_Disposed;
        GUIStyle m_Padding;

        /// <summary>
        /// Recommended constructor. The provider is read on every use, so the view keeps working when Undo or a
        /// domain reload replaces the state object.
        /// </summary>
        public ChannelPackerView(Func<PackerState> stateProvider, UnityEngine.Object undoTarget, Action repaint)
        {
            m_StateProvider = stateProvider ?? throw new ArgumentNullException(nameof(stateProvider));
            m_UndoTarget = undoTarget;
            m_Repaint = repaint;
            var state = State ?? throw new ArgumentException("The state provider returned null.", nameof(stateProvider));
            state.EnsureInitialized();
            m_LastFirstTexture = state.inputs.Count > 0 ? state.inputs[0].texture : null;
            MarkDirty();
        }

        /// <summary>For a state object that is never replaced. Hosts with Undo should use the provider constructor.</summary>
        public ChannelPackerView(PackerState state, UnityEngine.Object undoTarget, Action repaint)
            : this(Constant(state), undoTarget, repaint)
        {
        }

        static Func<PackerState> Constant(PackerState state)
        {
            if (state == null)
                throw new ArgumentNullException(nameof(state));
            return () => state;
        }

        public PackerState State => m_StateProvider();

        /// <summary>Schedules a refresh of decoded inputs, validation and preview outside of OnGUI.</summary>
        public void MarkDirty()
        {
            if (m_RefreshScheduled || m_Disposed)
                return;
            m_RefreshScheduled = true;
            EditorApplication.delayCall += DelayedRefresh;
        }

        public void Dispose()
        {
            m_Disposed = true;
            EditorApplication.delayCall -= DelayedRefresh;
            EditorApplication.delayCall -= RunPack;
            EditorApplication.delayCall -= RunSplit;
            m_Preview.Dispose();
        }

        /// <summary>Replaces the inputs with these textures (matched by name for presets that know the maps).</summary>
        public void AssignTextures(Texture2D[] textures)
        {
            RecordUndo();
            var state = State;
            state.mode = PackerMode.Pack;
            state.AssignTextures(textures, true);
            MarkDirty();
            m_Repaint?.Invoke();
        }

        void DelayedRefresh()
        {
            m_RefreshScheduled = false;
            if (m_Disposed)
                return;
            Refresh();
            m_Repaint?.Invoke();
        }

        void RecordUndo()
        {
            if (m_UndoTarget != null)
                Undo.RecordObject(m_UndoTarget, UndoName);
        }

        /// <summary>Applies a change from outside OnGUI (menus, delayed dialogs).</summary>
        void Change(Action change)
        {
            if (m_Disposed)
                return;
            RecordUndo();
            change();
            MarkDirty();
            m_Repaint?.Invoke();
        }

        /// <summary>Applies a change that alters which controls are drawn once the current IMGUI pass is over.</summary>
        void Defer(Action change)
        {
            m_Deferred.Add(change);
        }

        void ApplyDeferred()
        {
            if (m_Deferred.Count == 0)
                return;
            var actions = m_Deferred.ToArray();
            m_Deferred.Clear();
            RecordUndo();
            foreach (var action in actions)
                action();
            MarkDirty();
            m_Repaint?.Invoke();
        }

        // ================================================================== refresh

        void Refresh()
        {
            var state = State;
            state.Sanitize();
            int n = state.inputs.Count;
            m_Images = new PixelImage[n];
            m_SlotErrors = new string[n];
            bool progress = false;
            try
            {
                for (int i = 0; i < n; i++)
                {
                    var texture = state.inputs[i].texture;
                    if (texture == null)
                        continue;
                    if (!DecodeCache.IsKnown(texture))
                    {
                        EditorUtility.DisplayProgressBar(Title, "Reading " + texture.name, (float)i / n);
                        progress = true;
                    }
                    m_Images[i] = TryDecode(texture, out m_SlotErrors[i]);
                }

                m_SplitImage = null;
                m_SplitError = null;
                if (state.mode == PackerMode.Split && state.splitSource != null)
                {
                    if (!DecodeCache.IsKnown(state.splitSource))
                    {
                        EditorUtility.DisplayProgressBar(Title, "Reading " + state.splitSource.name, 0.5f);
                        progress = true;
                    }
                    m_SplitImage = TryDecode(state.splitSource, out m_SplitError);
                }
            }
            finally
            {
                if (progress)
                    EditorUtility.ClearProgressBar();
            }

            // Presets that keep the source alpha follow the first input: RGBA when it has alpha, RGB otherwise.
            var first = n > 0 ? state.inputs[0].texture : null;
            if (first != m_LastFirstTexture)
            {
                m_LastFirstTexture = first;
                state.layoutFollowsInputPending = state.followInputAlpha;
            }
            if (state.layoutFollowsInputPending && n > 0 && m_Images[0] != null)
            {
                state.layoutFollowsInputPending = false;
                if (state.recipe.layout == OutputLayout.RGBA || state.recipe.layout == OutputLayout.RGB)
                    state.recipe.layout = m_Images[0].HasAlpha ? OutputLayout.RGBA : OutputLayout.RGB;
            }

            m_Used = ChannelPacker.UsedSlots(state.recipe, n);
            var infos = new InputInfo[n];
            var hasTexture = new bool[n];
            for (int i = 0; i < n; i++)
            {
                infos[i] = m_Used[i] ? InputInfo.From(m_Images[i]) : null;
                hasTexture[i] = state.inputs[i].texture != null;
            }
            m_Plan = PackValidator.Validate(state.recipe, infos, state.size, state.output, hasTexture);
            for (int i = n - 1; i >= 0; i--)
            {
                if (m_SlotErrors[i] != null)
                    m_Plan.Issues.Insert(0, new PackIssue(IssueSeverity.Error, $"Input {PackEngine.SlotName(i)}: {m_SlotErrors[i]}"));
            }

            BuildPreview(state);
        }

        static PixelImage TryDecode(Texture2D texture, out string error)
        {
            error = null;
            try
            {
                return DecodeCache.Get(texture);
            }
            catch (PackException e)
            {
                error = e.Message;
            }
            catch (Exception e)
            {
                error = e.GetType().Name + ": " + e.Message;
                Debug.LogException(e);
            }
            return null;
        }

        void BuildPreview(PackerState state)
        {
            m_PreviewImage = null;
            m_PreviewError = null;
            m_PreviewTextureDirty = true;
            if (m_Plan == null || m_Plan.Width <= 0 || m_Plan.Height <= 0)
            {
                m_PreviewError = "No valid output size: see the messages below.";
                return;
            }

            Resampler.FitInside(m_Plan.Width, m_Plan.Height, PreviewResolution, out int w, out int h);
            var inputs = new PixelImage[m_Images.Length];
            try
            {
                for (int i = 0; i < inputs.Length; i++)
                {
                    if (m_Images[i] != null && m_Used[i])
                        inputs[i] = DecodeCache.GetPreview(state.inputs[i].texture, PreviewResolution);
                }
                m_PreviewImage = PackEngine.Evaluate(state.recipe, inputs, w, h, ResampleFilter.Bilinear);
            }
            catch (PackException e)
            {
                m_PreviewError = e.Message;
            }
        }

        // ================================================================== GUI

        public void OnGUI()
        {
            if (m_Disposed)
                return;
            var state = State;
            if (state == null)
                return;
            state.Sanitize();
            if (m_Padding == null)
                m_Padding = new GUIStyle { padding = new RectOffset(10, 10, 4, 10) };

            var type = Event.current.type;
            if (type == EventType.Layout)
            {
                m_OutputExists = SafeFileExists(state.output.AssetPath);
                m_SplitSettingsError = state.splitSource != null ? ChannelPacker.SplitError(state.splitSource, state.split) : null;
            }
            if (type != EventType.Layout && type != EventType.Repaint)
                RecordUndo();

            bool changed = false;
            try
            {
                EditorGUI.BeginChangeCheck();
                DrawToolbar(state);
                m_Scroll = EditorGUILayout.BeginScrollView(m_Scroll);
                EditorGUILayout.BeginVertical(m_Padding);
                if (state.mode == PackerMode.Pack)
                    DrawPack(state);
                else
                    DrawSplit(state);
                EditorGUILayout.EndVertical();
                EditorGUILayout.EndScrollView();
                DrawFooter(state);
                changed = EditorGUI.EndChangeCheck();
            }
            finally
            {
                ApplyDeferred();
            }
            if (changed)
                MarkDirty();
        }

        void DrawToolbar(PackerState state)
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            var mode = (PackerMode)GUILayout.Toolbar((int)state.mode, new[] { "Pack", "Split" }, EditorStyles.toolbarButton, GUILayout.Width(130));
            if (mode != state.mode)
                Defer(() => state.mode = mode);
            GUILayout.FlexibleSpace();
            if (state.mode == PackerMode.Pack)
            {
                var content = new GUIContent("Preset: " + state.presetName, "Ready-made recipes. Applying one replaces the channel setup, inputs are kept.");
                var rect = GUILayoutUtility.GetRect(content, EditorStyles.toolbarDropDown, GUILayout.MaxWidth(320));
                if (EditorGUI.DropdownButton(rect, content, FocusType.Passive, EditorStyles.toolbarDropDown))
                    ShowPresetMenu(state, rect);
            }
            if (GUILayout.Button(new GUIContent("Help", "Open the README"), EditorStyles.toolbarButton, GUILayout.Width(40)))
            {
                var readme = AssetDatabase.LoadAssetAtPath<TextAsset>("Packages/" + PackageName + "/README.md");
                if (readme != null)
                {
                    Selection.activeObject = readme;
                    EditorGUIUtility.PingObject(readme);
                }
            }
            EditorGUILayout.EndHorizontal();
        }

        void ShowPresetMenu(PackerState state, Rect rect)
        {
            var menu = new GenericMenu();
            foreach (var preset in PackPresets.All)
            {
                var p = preset;
                menu.AddItem(new GUIContent(preset.MenuPath), preset.Name == state.presetName, () => Change(() => State.ApplyPreset(p)));
                if (string.IsNullOrEmpty(preset.Category))
                    menu.AddSeparator(string.Empty);
            }
            menu.DropDown(rect);
        }

        // ------------------------------------------------------------------ pack layout

        void DrawPack(PackerState state)
        {
            var preset = state.CurrentPreset;
            if (preset != null && !string.IsNullOrEmpty(preset.Description))
                GUILayout.Label(preset.Description, PackerStyles.Info);

            float width = EditorGUIUtility.currentViewWidth;
            if (width >= WideLayoutWidth)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.BeginVertical(GUILayout.Width(width * 0.54f));
                DrawInputs(state);
                DrawChannels(state);
                EditorGUILayout.EndVertical();
                GUILayout.Space(12);
                EditorGUILayout.BeginVertical();
                DrawPreview(state);
                DrawOutput(state);
                EditorGUILayout.EndVertical();
                EditorGUILayout.EndHorizontal();
            }
            else
            {
                DrawInputs(state);
                DrawChannels(state);
                DrawPreview(state);
                DrawOutput(state);
            }
            DrawIssues();
        }

        // ------------------------------------------------------------------ inputs

        void DrawInputs(PackerState state)
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Inputs", PackerStyles.Section);
            GUILayout.FlexibleSpace();
            var selected = Selection.objects.OfType<Texture2D>().ToArray();
            using (new EditorGUI.DisabledScope(selected.Length == 0))
            {
                if (GUILayout.Button(new GUIContent("Use Selection", "Replace the inputs with the textures selected in the Project window (matched by name when the preset knows the maps)."), EditorStyles.miniButton))
                    Defer(() => state.AssignTextures(selected, true));
            }
            EditorGUILayout.EndHorizontal();
            Separator();

            var area = EditorGUILayout.BeginVertical();
            // Before the fields: an object field under the mouse would otherwise take only the first texture.
            HandleDrop(state, area);
            for (int i = 0; i < state.inputs.Count; i++)
                DrawSlot(state, i);
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            using (new EditorGUI.DisabledScope(state.inputs.Count >= PackerState.MaxInputs))
            {
                if (GUILayout.Button(new GUIContent("+", "Add an input slot"), EditorStyles.miniButtonLeft, GUILayout.Width(26)))
                    Defer(() => state.inputs.Add(new InputSlot()));
            }
            using (new EditorGUI.DisabledScope(state.inputs.Count <= 1))
            {
                if (GUILayout.Button(new GUIContent("-", "Remove the last input slot"), EditorStyles.miniButtonRight, GUILayout.Width(26)))
                    Defer(() =>
                    {
                        if (state.inputs.Count > 1)
                            state.inputs.RemoveAt(state.inputs.Count - 1);
                        state.UpdateAutoName();
                    });
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
        }

        void DrawSlot(PackerState state, int i)
        {
            var slot = state.inputs[i];
            bool used = i < m_Used.Length && m_Used[i];

            EditorGUILayout.BeginHorizontal();
            var chip = GUILayoutUtility.GetRect(20, 18, GUILayout.Width(20), GUILayout.Height(18));
            PackerStyles.DrawChip(chip, PackEngine.SlotName(i), used ? new Color(0.45f, 0.45f, 0.45f) : new Color(0.3f, 0.3f, 0.3f, 0.6f));
            GUILayout.Space(2);
            GUILayout.Label(new GUIContent(slot.label, used ? "Used by the output" : "Not used by the current channels"),
                used ? PackerStyles.SlotLabel : PackerStyles.Info, GUILayout.Width(104), GUILayout.Height(18));
            var texture = (Texture2D)EditorGUILayout.ObjectField(slot.texture, typeof(Texture2D), false, GUILayout.Height(18));
            if (texture != slot.texture)
                Defer(() => SetSlotTexture(state, slot, texture));
            using (new EditorGUI.DisabledScope(slot.texture == null))
            {
                if (GUILayout.Button(new GUIContent("x", "Clear"), EditorStyles.miniButton, GUILayout.Width(20)))
                    Defer(() => SetSlotTexture(state, slot, null));
            }
            EditorGUILayout.EndHorizontal();

            string info;
            var image = i < m_Images.Length ? m_Images[i] : null;
            string error = i < m_SlotErrors.Length ? m_SlotErrors[i] : null;
            if (error != null)
                info = "Error: " + error;
            else if (image != null && slot.texture != null)
                info = $"{image.Width} x {image.Height}  ·  {image.SourceDescription}{(image.IsLossySource ? "  ·  lossy source" : string.Empty)}";
            else if (slot.texture != null)
                info = "Reading...";
            else
                info = used ? "Empty: the channel default value is used" : "Empty";

            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(128);
            var previous = GUI.color;
            if (error != null)
                GUI.color = new Color(1f, 0.55f, 0.5f);
            GUILayout.Label(info, PackerStyles.Info);
            GUI.color = previous;
            EditorGUILayout.EndHorizontal();
            GUILayout.Space(2);
        }

        static void SetSlotTexture(PackerState state, InputSlot slot, Texture2D texture)
        {
            // Assigning a texture again is how the user asks to retry a read that failed.
            DecodeCache.ForgetFailure(texture);
            slot.texture = texture;
            state.UpdateAutoName();
        }

        void HandleDrop(PackerState state, Rect area)
        {
            var evt = Event.current;
            if ((evt.type != EventType.DragUpdated && evt.type != EventType.DragPerform) || !area.Contains(evt.mousePosition))
                return;
            var textures = DragAndDrop.objectReferences.OfType<Texture2D>().ToArray();
            // A single texture is handled by the field under the mouse; several are distributed over the slots.
            if (textures.Length < 2)
                return;
            DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
            if (evt.type == EventType.DragPerform)
            {
                DragAndDrop.AcceptDrag();
                Defer(() => state.AssignTextures(textures, false));
            }
            evt.Use();
        }

        // ------------------------------------------------------------------ channels

        void DrawChannels(PackerState state)
        {
            GUILayout.Space(6);
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Output Channels", PackerStyles.Section);
            GUILayout.FlexibleSpace();
            GUILayout.Label("Layout", PackerStyles.Info, GUILayout.Width(40));
            var layout = (OutputLayout)EditorGUILayout.Popup((int)state.recipe.layout, s_LayoutNames, GUILayout.Width(130));
            if (layout != state.recipe.layout)
                Defer(() => state.recipe.layout = layout);
            EditorGUILayout.EndHorizontal();
            Separator();

            foreach (int c in PackRecipe.ActiveChannels(state.recipe.layout))
                DrawChannel(state, c);
            GUILayout.Label("Order of operations: source, combine, color conversion, invert, levels. Channels without changes are copied bit for bit.", PackerStyles.Info);
        }

        void DrawChannel(PackerState state, int c)
        {
            var layout = state.recipe.layout;
            var ch = state.recipe.channels[c];

            EditorGUILayout.BeginVertical(PackerStyles.Card);
            EditorGUILayout.BeginHorizontal();
            var chip = GUILayoutUtility.GetRect(22, 18, GUILayout.Width(22), GUILayout.Height(18));
            PackerStyles.DrawChip(chip, PackerStyles.ChannelLetter(layout, c), PackerStyles.ChannelColor(layout, c));
            GUILayout.Space(4);
            OperandField(state, ch.source);
            if (ch.source.kind == OperandKind.Constant)
                ConstantFields(ch.source);
            ch.invert = GUILayout.Toggle(ch.invert, new GUIContent("Invert", "1 - value"), EditorStyles.miniButton, GUILayout.Width(48), GUILayout.Height(18));
            bool expanded = state.expanded[c];
            bool toggled = GUILayout.Toggle(expanded, new GUIContent(expanded ? "Less" : "More", "Combine, color conversion and levels"),
                EditorStyles.miniButton, GUILayout.Width(42), GUILayout.Height(18));
            if (toggled != expanded)
                Defer(() => state.expanded[c] = toggled);
            EditorGUILayout.EndHorizontal();

            string note = FallbackNote(state, ch.source);
            if (note != null)
                Indented(note);

            if (expanded)
                DrawAdvanced(state, ch);
            else
            {
                string summary = Summary(ch);
                if (summary.Length > 0)
                    Indented(summary);
            }
            EditorGUILayout.EndVertical();
        }

        static string FallbackNote(PackerState state, Operand op)
        {
            if (op.kind != OperandKind.Texture)
                return null;
            bool empty = op.input < 0 || op.input >= state.inputs.Count || state.inputs[op.input].texture == null;
            if (!empty)
                return null;
            return op.hasFallback
                ? $"Input {PackEngine.SlotName(op.input)} is empty: uses {FormatValue(op.fallback)} ({ColorMath.Quantize(op.fallback, 255)}/255)"
                : $"Input {PackEngine.SlotName(op.input)} is empty";
        }

        static string FormatValue(float value)
        {
            return value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        }

        static void Indented(string text)
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(26);
            GUILayout.Label(text, PackerStyles.Info);
            EditorGUILayout.EndHorizontal();
        }

        void DrawAdvanced(PackerState state, ChannelRecipe ch)
        {
            const float labelWidth = 62;
            GUILayout.Space(2);

            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(26);
            GUILayout.Label("Combine", GUILayout.Width(labelWidth));
            var combine = (CombineOp)EditorGUILayout.Popup((int)ch.combine, s_CombineNames, GUILayout.Width(96));
            if (combine != ch.combine)
                Defer(() => ch.combine = combine);
            if (ch.combine != CombineOp.None)
            {
                GUILayout.Label("with", GUILayout.Width(30));
                OperandField(state, ch.other);
                if (ch.other.kind == OperandKind.Constant)
                    ConstantFields(ch.other);
            }
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(26);
            GUILayout.Label(new GUIContent("Color", "Converts between sRGB encoded values and linear values."), GUILayout.Width(labelWidth));
            ch.conversion = (ColorConversion)EditorGUILayout.Popup((int)ch.conversion, s_ConversionNames, GUILayout.Width(120));
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(26);
            GUILayout.Label(new GUIContent("Levels", "Remap the input range, apply gamma, remap to the output range."), GUILayout.Width(labelWidth));
            bool useLevels = EditorGUILayout.Toggle(ch.useLevels, GUILayout.Width(16));
            if (useLevels != ch.useLevels)
                Defer(() => ch.useLevels = useLevels);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(new GUIContent("Reset", "Remove combine, conversion, invert and levels"), EditorStyles.miniButton, GUILayout.Width(48)))
            {
                Defer(() =>
                {
                    ch.combine = CombineOp.None;
                    ch.conversion = ColorConversion.None;
                    ch.invert = false;
                    ch.useLevels = false;
                    ch.levels = new Levels();
                });
            }
            EditorGUILayout.EndHorizontal();

            if (ch.useLevels)
            {
                var lv = ch.levels;
                RangeRow("Input", ref lv.inMin, ref lv.inMax);
                EditorGUILayout.BeginHorizontal();
                GUILayout.Space(26 + labelWidth + 4);
                GUILayout.Label("Gamma", GUILayout.Width(46));
                lv.gamma = EditorGUILayout.Slider(lv.gamma, 0.1f, 5f);
                EditorGUILayout.EndHorizontal();
                RangeRow("Output", ref lv.outMin, ref lv.outMax);
            }
        }

        static void RangeRow(string label, ref float min, ref float max)
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(26 + 62 + 4);
            GUILayout.Label(label, GUILayout.Width(46));
            min = EditorGUILayout.FloatField(min, GUILayout.Width(44));
            EditorGUILayout.MinMaxSlider(ref min, ref max, 0f, 1f);
            max = EditorGUILayout.FloatField(max, GUILayout.Width(44));
            EditorGUILayout.EndHorizontal();
        }

        static string Summary(ChannelRecipe ch)
        {
            var parts = new List<string>();
            if (ch.combine != CombineOp.None)
                parts.Add($"{s_CombineNames[(int)ch.combine]} {OperandLabel(ch.other)}");
            if (ch.conversion != ColorConversion.None)
                parts.Add(s_ConversionNames[(int)ch.conversion]);
            if (ch.HasActiveLevels)
                parts.Add("Levels");
            return string.Join("  ·  ", parts);
        }

        void OperandField(PackerState state, Operand op)
        {
            var content = new GUIContent(OperandLabel(op), OperandTooltip(state, op));
            var rect = GUILayoutUtility.GetRect(content, EditorStyles.popup, GUILayout.MinWidth(90), GUILayout.Height(18));
            if (EditorGUI.DropdownButton(rect, content, FocusType.Keyboard, EditorStyles.popup))
                ShowOperandMenu(state, op, rect);
        }

        void ShowOperandMenu(PackerState state, Operand op, Rect rect)
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("Constant value"), op.kind == OperandKind.Constant, () => Change(() => op.kind = OperandKind.Constant));
            menu.AddSeparator(string.Empty);
            for (int i = 0; i < state.inputs.Count; i++)
            {
                var slot = state.inputs[i];
                string texture = slot.texture != null ? MenuSafe(slot.texture.name) : "empty";
                string slotName = $"{PackEngine.SlotName(i)}  {MenuSafe(slot.label)}  ({texture})";
                foreach (ChannelSource source in Enum.GetValues(typeof(ChannelSource)))
                {
                    int input = i;
                    var s = source;
                    bool on = op.kind == OperandKind.Texture && op.input == i && op.channel == s;
                    menu.AddItem(new GUIContent(slotName + "/" + SourceName(s)), on, () => Change(() =>
                    {
                        op.kind = OperandKind.Texture;
                        op.input = input;
                        op.channel = s;
                    }));
                }
            }
            menu.DropDown(rect);
        }

        /// <summary>Menu paths treat '/' as a submenu and '%', '#', '&amp;' as shortcut markers.</summary>
        static string MenuSafe(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;
            return text.Replace('/', '-').Replace("%", string.Empty).Replace("#", string.Empty).Replace("&", "+");
        }

        static void ConstantFields(Operand op)
        {
            op.constant = EditorGUILayout.Slider(op.constant, 0f, 1f, GUILayout.MinWidth(110));
            int q = ColorMath.Quantize(op.constant, 255);
            int edited = EditorGUILayout.IntField(GUIContent.none, q, GUILayout.Width(34));
            if (edited != q)
                op.constant = Mathf.Clamp(edited, 0, 255) / 255f;
        }

        static string OperandLabel(Operand op)
        {
            if (op.kind == OperandKind.Constant)
                return "Constant";
            return $"{PackEngine.SlotName(op.input)} · {SourceName(op.channel)}";
        }

        static string OperandTooltip(PackerState state, Operand op)
        {
            if (op.kind == OperandKind.Constant)
                return "A fixed value for every pixel";
            string label = op.input >= 0 && op.input < state.inputs.Count ? state.inputs[op.input].label : "missing slot";
            return $"Input {PackEngine.SlotName(op.input)} ({label}), {SourceName(op.channel)}";
        }

        static string SourceName(ChannelSource source)
        {
            switch (source)
            {
                case ChannelSource.Red: return "Red";
                case ChannelSource.Green: return "Green";
                case ChannelSource.Blue: return "Blue";
                case ChannelSource.Alpha: return "Alpha";
                case ChannelSource.Luminance: return "Luminance";
                case ChannelSource.Average: return "Average RGB";
                case ChannelSource.Minimum: return "Min RGB";
                default: return "Max RGB";
            }
        }

        // ------------------------------------------------------------------ preview

        void DrawPreview(PackerState state)
        {
            var layout = state.recipe.layout;
            bool gray = PackRecipe.IsGray(layout);
            bool alpha = PackRecipe.HasAlpha(layout);

            string[] names;
            PreviewMode[] modes;
            if (gray)
            {
                names = alpha ? new[] { "L", "A" } : new[] { "L" };
                modes = alpha ? new[] { PreviewMode.Color, PreviewMode.Alpha } : new[] { PreviewMode.Color };
            }
            else
            {
                names = alpha ? new[] { "RGB", "R", "G", "B", "A" } : new[] { "RGB", "R", "G", "B" };
                modes = alpha
                    ? new[] { PreviewMode.Color, PreviewMode.Red, PreviewMode.Green, PreviewMode.Blue, PreviewMode.Alpha }
                    : new[] { PreviewMode.Color, PreviewMode.Red, PreviewMode.Green, PreviewMode.Blue };
            }
            int current = Array.IndexOf(modes, (PreviewMode)state.previewMode);
            if (current < 0)
                current = 0;

            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Preview", PackerStyles.Section);
            GUILayout.FlexibleSpace();
            int selected = GUILayout.Toolbar(current, names, EditorStyles.miniButton, GUILayout.Width(names.Length * 34));
            EditorGUILayout.EndHorizontal();
            Separator();
            var mode = modes[Mathf.Clamp(selected, 0, modes.Length - 1)];
            state.previewMode = (int)mode;

            if (m_PreviewImage != null && (m_PreviewTextureDirty || mode != m_BuiltMode || m_Preview.Texture == null))
            {
                m_Preview.Build(m_PreviewImage, mode);
                m_BuiltMode = mode;
                m_PreviewTextureDirty = false;
            }

            float side = Mathf.Min(EditorGUIUtility.currentViewWidth - 40, 340);
            var area = GUILayoutUtility.GetRect(side, side, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(area, EditorGUIUtility.isProSkin ? new Color(0.12f, 0.12f, 0.12f) : new Color(0.75f, 0.75f, 0.75f));
            if (m_PreviewImage != null && m_Preview.Texture != null)
            {
                var fit = Fit(area, m_PreviewImage.Width, m_PreviewImage.Height);
                EditorGUI.DrawPreviewTexture(fit, m_Preview.Texture);
            }
            else
            {
                string message = m_PreviewError ?? "Add a texture or set a constant to see the result.";
                GUI.Label(area, message, PackerStyles.CenteredInfo);
            }

            if (m_Plan != null && m_Plan.Width > 0)
            {
                string depth = OutputSettings.DepthName(m_Plan.ResolvedDepth);
                GUILayout.Label($"Output {m_Plan.Width} x {m_Plan.Height}  ·  {state.output.format} {depth}  ·  {s_LayoutNames[(int)layout]}", PackerStyles.Info);
            }
            GUILayout.Label("The preview is downscaled for speed. Pack always works at full resolution.", PackerStyles.Info);
        }

        static Rect Fit(Rect area, int width, int height)
        {
            float scale = Mathf.Min(area.width / width, area.height / height);
            float w = width * scale, h = height * scale;
            return new Rect(area.x + (area.width - w) * 0.5f, area.y + (area.height - h) * 0.5f, w, h);
        }

        // ------------------------------------------------------------------ output

        void DrawOutput(PackerState state)
        {
            var output = state.output;
            var size = state.size;
            GUILayout.Space(6);
            GUILayout.Label("Output", PackerStyles.Section);
            Separator();

            // Size
            EditorGUILayout.BeginHorizontal();
            Label("Size", "Output resolution");
            var sizeMode = (SizeMode)EditorGUILayout.Popup((int)size.mode, s_SizeModeNames, GUILayout.Width(120));
            if (sizeMode != size.mode)
                Defer(() => size.mode = sizeMode);
            if (size.mode == SizeMode.FromInput)
                size.referenceInput = EditorGUILayout.Popup(size.referenceInput, SlotNames(state), GUILayout.Width(44));
            if (size.mode == SizeMode.Custom)
            {
                size.customWidth = Mathf.Clamp(EditorGUILayout.IntField(size.customWidth, GUILayout.Width(52)), 1, SizeSettings.MaxSize);
                GUILayout.Label("x", GUILayout.Width(10));
                size.customHeight = Mathf.Clamp(EditorGUILayout.IntField(size.customHeight, GUILayout.Width(52)), 1, SizeSettings.MaxSize);
                var presetRect = GUILayoutUtility.GetRect(new GUIContent("..."), EditorStyles.miniButton, GUILayout.Width(24));
                if (GUI.Button(presetRect, new GUIContent("...", "Common sizes"), EditorStyles.miniButton))
                {
                    var menu = new GenericMenu();
                    foreach (int s in s_CommonSizes)
                    {
                        int value = s;
                        menu.AddItem(new GUIContent($"{s} x {s}"), false, () => Change(() =>
                        {
                            State.size.customWidth = value;
                            State.size.customHeight = value;
                        }));
                    }
                    menu.DropDown(presetRect);
                }
            }
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            Label("Resample", "What to do when an input has a different size than the output. None keeps every pixel untouched.");
            size.filter = (ResampleFilter)EditorGUILayout.Popup((int)size.filter, s_FilterNames);
            EditorGUILayout.EndHorizontal();

            // Format
            EditorGUILayout.BeginHorizontal();
            Label("Format", "PNG and TGA store 0-1 values, EXR stores float (HDR) values. All are lossless.");
            var format = (OutputFormat)GUILayout.Toolbar((int)output.format, s_FormatNames, EditorStyles.miniButton, GUILayout.Width(150));
            if (format != output.format)
                Defer(() => output.format = format);
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            var depths = OutputSettings.DepthsFor(output.format);
            if (Array.IndexOf(depths, output.bitDepth) < 0)
                output.bitDepth = depths[0];
            var resolved = OutputSettings.ResolveDepth(output.format, output.bitDepth, m_Plan?.SourcePrecision ?? SamplePrecision.UNorm8);
            var depthNames = depths.Select(d => d == OutputBitDepth.Auto ? $"Auto ({OutputSettings.DepthName(resolved)})" : OutputSettings.DepthName(d)).ToArray();
            EditorGUILayout.BeginHorizontal();
            Label("Bit depth", "Auto picks the lowest depth that keeps every source value.");
            using (new EditorGUI.DisabledScope(depths.Length == 1))
                output.bitDepth = depths[Mathf.Clamp(EditorGUILayout.Popup(Array.IndexOf(depths, output.bitDepth), depthNames), 0, depths.Length - 1)];
            EditorGUILayout.EndHorizontal();

            if (output.format == OutputFormat.TGA)
            {
                EditorGUILayout.BeginHorizontal();
                Label("RLE", "Run length encoding: smaller files, still lossless.");
                output.tgaRle = EditorGUILayout.Toggle(output.tgaRle);
                EditorGUILayout.EndHorizontal();
            }

            // Location
            EditorGUILayout.BeginHorizontal();
            Label("Folder", "Must be inside Assets");
            EditorGUI.BeginChangeCheck();
            output.folder = EditorGUILayout.TextField(output.folder);
            if (EditorGUI.EndChangeCheck())
                state.autoFolder = false;
            if (GUILayout.Button("...", EditorStyles.miniButton, GUILayout.Width(26)))
                EditorApplication.delayCall += PickOutputFolder;
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            Label("Name", "File name without extension");
            EditorGUI.BeginChangeCheck();
            output.fileName = EditorGUILayout.TextField(output.fileName);
            if (EditorGUI.EndChangeCheck())
                state.autoName = false;
            GUILayout.Label(output.Extension, GUILayout.Width(32));
            bool auto = GUILayout.Toggle(state.autoName, new GUIContent("Auto", "Name from the inputs plus the preset suffix"), EditorStyles.miniButton, GUILayout.Width(40));
            if (auto != state.autoName)
            {
                state.autoName = auto;
                state.UpdateAutoName();
            }
            EditorGUILayout.EndHorizontal();

            // Import settings
            EditorGUILayout.BeginHorizontal();
            Label("Import", "Importer settings for a new file. An overwritten file keeps its own settings.");
            var importMode = (ImportMode)EditorGUILayout.Popup((int)output.importMode, s_ImportModeNames);
            if (importMode != output.importMode)
                Defer(() => output.importMode = importMode);
            if (output.importMode == ImportMode.CopyFromInput)
                output.importSourceInput = EditorGUILayout.Popup(output.importSourceInput, SlotNames(state), GUILayout.Width(44));
            EditorGUILayout.EndHorizontal();
            if (output.importMode == ImportMode.ToolSettings)
            {
                EditorGUILayout.BeginHorizontal();
                Label("Color space", "Masks and data must be Linear. Albedo and emission are sRGB.");
                output.colorSpace = (OutputColorSpace)EditorGUILayout.Popup((int)output.colorSpace, s_ColorSpaceNames);
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.BeginHorizontal();
                Label("Type", string.Empty);
                output.textureType = (OutputTextureType)EditorGUILayout.Popup((int)output.textureType, s_TypeNames);
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.BeginHorizontal();
                Label("Compression", "GPU compression applied by Unity at import. The file on disk stays lossless.");
                output.compression = (OutputCompression)EditorGUILayout.Popup((int)output.compression, s_CompressionNames);
                EditorGUILayout.EndHorizontal();
            }

            if (m_OutputExists)
            {
                EditorGUILayout.HelpBox("This file already exists. It will be replaced in place: GUID, import settings and material references are kept.", MessageType.Info);
                EditorGUILayout.BeginHorizontal();
                Label("Backup", "Copy the current file to " + TextureOutputWriter.BackupFolder + " before overwriting.");
                output.backupOnOverwrite = EditorGUILayout.Toggle(output.backupOnOverwrite);
                EditorGUILayout.EndHorizontal();
            }
        }

        static void Label(string text, string tooltip)
        {
            GUILayout.Label(new GUIContent(text, tooltip), GUILayout.Width(84));
        }

        static string[] SlotNames(PackerState state)
        {
            return Enumerable.Range(0, state.inputs.Count).Select(PackEngine.SlotName).ToArray();
        }

        void PickOutputFolder()
        {
            if (m_Disposed)
                return;
            string picked = PickFolder(State.output.folder);
            if (picked != null)
                Change(() =>
                {
                    State.output.folder = picked;
                    State.autoFolder = false;
                });
        }

        void PickSplitFolder()
        {
            if (m_Disposed)
                return;
            string picked = PickFolder(State.split.folder);
            if (picked != null)
                Change(() => State.split.folder = picked);
        }

        /// <summary>Shows the folder dialog. Called from delayCall: modal dialogs inside OnGUI break the layout.</summary>
        static string PickFolder(string current)
        {
            string assets = Path.GetFullPath("Assets");
            string start = assets;
            try
            {
                string full = Path.GetFullPath(OutputSettings.NormalizeFolder(current));
                if (Directory.Exists(full))
                    start = full;
            }
            catch (Exception)
            {
                // Invalid characters typed in the field: start from Assets.
            }

            string picked = EditorUtility.OpenFolderPanel("Output folder", start, string.Empty);
            if (string.IsNullOrEmpty(picked))
                return null;
            assets = assets.Replace('\\', '/');
            picked = picked.Replace('\\', '/');
            if (string.Equals(picked, assets, StringComparison.OrdinalIgnoreCase))
                return "Assets";
            if (picked.StartsWith(assets + "/", StringComparison.OrdinalIgnoreCase))
                return "Assets/" + picked.Substring(assets.Length + 1);
            EditorUtility.DisplayDialog(Title, "Pick a folder inside the project's Assets folder.", "OK");
            return null;
        }

        static bool SafeFileExists(string assetPath)
        {
            try
            {
                return File.Exists(Path.GetFullPath(assetPath));
            }
            catch (Exception)
            {
                return false; // invalid characters: the validator reports them
            }
        }

        static void Separator()
        {
            var rect = GUILayoutUtility.GetRect(1, 1, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(rect, PackerStyles.Separator);
            GUILayout.Space(4);
        }

        // ------------------------------------------------------------------ issues and footer

        void DrawIssues()
        {
            if (m_Plan == null || m_Plan.Issues.Count == 0)
                return;
            GUILayout.Space(8);
            foreach (var severity in new[] { IssueSeverity.Error, IssueSeverity.Warning, IssueSeverity.Info })
            {
                foreach (var issue in m_Plan.Issues.Where(i => i.Severity == severity))
                {
                    var type = severity == IssueSeverity.Error ? MessageType.Error : severity == IssueSeverity.Warning ? MessageType.Warning : MessageType.Info;
                    EditorGUILayout.HelpBox(issue.Message, type);
                }
            }
        }

        void DrawFooter(PackerState state)
        {
            var rect = GUILayoutUtility.GetRect(1, 1, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(rect, PackerStyles.Separator);
            EditorGUILayout.BeginVertical(m_Padding);
            if (state.mode == PackerMode.Pack)
            {
                bool blocked = m_Plan == null || m_Plan.HasErrors || m_RefreshScheduled || m_PackScheduled;
                using (new EditorGUI.DisabledScope(blocked))
                {
                    string label = m_Plan != null && m_Plan.HasErrors
                        ? "Fix the errors above to pack"
                        : "Pack  " + state.output.CleanFileName + state.output.Extension;
                    if (GUILayout.Button(label, PackerStyles.BigButton) && !m_PackScheduled)
                    {
                        m_PackScheduled = true;
                        EditorApplication.delayCall += RunPack;
                    }
                }
            }
            else
            {
                int count = SplitCount(state);
                using (new EditorGUI.DisabledScope(state.splitSource == null || m_SplitImage == null || count == 0 || m_PackScheduled || m_SplitSettingsError != null))
                {
                    if (GUILayout.Button($"Split into {count} texture{(count == 1 ? string.Empty : "s")}", PackerStyles.BigButton) && !m_PackScheduled)
                    {
                        m_PackScheduled = true;
                        EditorApplication.delayCall += RunSplit;
                    }
                }
            }

            if (!string.IsNullOrEmpty(m_LastResult))
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label(m_LastResult, PackerStyles.Info);
                if (m_LastOutput != null && GUILayout.Button("Select", EditorStyles.miniButton, GUILayout.Width(50)))
                {
                    Selection.activeObject = m_LastOutput;
                    EditorGUIUtility.PingObject(m_LastOutput);
                }
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndVertical();
        }

        void RunPack()
        {
            m_PackScheduled = false;
            if (m_Disposed)
                return;
            var request = State.ToRequest();
            string path = request.Output.AssetPath;
            if (SafeFileExists(path))
            {
                string backup = request.Output.backupOnOverwrite ? $"\n\nA backup is saved in {TextureOutputWriter.BackupFolder}." : string.Empty;
                if (!EditorUtility.DisplayDialog("Overwrite texture?",
                        $"{path} already exists.\n\nIt will be replaced in place: GUID, import settings and material references are kept.{backup}",
                        "Overwrite", "Cancel"))
                {
                    m_Repaint?.Invoke();
                    return;
                }
            }

            try
            {
                var result = ChannelPacker.Pack(request);
                m_LastOutput = result.Texture;
                m_LastResult = $"Saved {Path.GetFileName(result.AssetPath)}  ·  {result.Width} x {result.Height}  ·  {OutputSettings.DepthName(result.Depth)}";
                if (result.Texture != null)
                    EditorGUIUtility.PingObject(result.Texture);
            }
            catch (PackException e)
            {
                EditorUtility.DisplayDialog(Title, e.Message, "OK");
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                EditorUtility.DisplayDialog(Title, "Unexpected error: " + e.Message + "\nSee the Console for details.", "OK");
            }
            MarkDirty();
            m_Repaint?.Invoke();
        }

        // ------------------------------------------------------------------ split

        int SplitCount(PackerState state)
        {
            int count = 0;
            for (int c = 0; c < 4; c++)
            {
                if (!state.split.channels[c])
                    continue;
                if (c == 3 && m_SplitImage != null && !m_SplitImage.HasAlpha && state.split.skipMissingAlpha)
                    continue;
                count++;
            }
            return count;
        }

        void DrawSplit(PackerState state)
        {
            var split = state.split;
            GUILayout.Label("Writes each channel of a texture as a separate grayscale file, at the same bit depth.", PackerStyles.Info);

            GUILayout.Label("Source", PackerStyles.Section);
            Separator();
            var source = (Texture2D)EditorGUILayout.ObjectField(state.splitSource, typeof(Texture2D), false, GUILayout.Height(18));
            if (source != state.splitSource)
            {
                Defer(() =>
                {
                    DecodeCache.ForgetFailure(source);
                    state.splitSource = source;
                });
            }
            if (m_SplitError != null)
                EditorGUILayout.HelpBox(m_SplitError, MessageType.Error);
            else if (m_SplitImage != null && state.splitSource != null)
                GUILayout.Label($"{m_SplitImage.Width} x {m_SplitImage.Height}  ·  {m_SplitImage.SourceDescription}", PackerStyles.Info);

            GUILayout.Space(6);
            GUILayout.Label("Channels", PackerStyles.Section);
            Separator();
            string baseName = state.splitSource != null ? state.splitSource.name : "Texture";
            var format = split.autoFormat
                ? ChannelPacker.AutoFormat(state.splitSource != null ? AssetDatabase.GetAssetPath(state.splitSource) : null, m_SplitImage?.Precision ?? SamplePrecision.UNorm8)
                : split.format;
            for (int c = 0; c < 4; c++)
            {
                EditorGUILayout.BeginHorizontal();
                var chip = GUILayoutUtility.GetRect(22, 18, GUILayout.Width(22), GUILayout.Height(18));
                PackerStyles.DrawChip(chip, "RGBA"[c].ToString(), PackerStyles.ChannelColor(OutputLayout.RGBA, c));
                GUILayout.Space(4);
                split.channels[c] = EditorGUILayout.Toggle(split.channels[c], GUILayout.Width(16));
                using (new EditorGUI.DisabledScope(!split.channels[c]))
                {
                    GUILayout.Label("Suffix", GUILayout.Width(40));
                    split.suffixes[c] = EditorGUILayout.TextField(split.suffixes[c], GUILayout.Width(70));
                    GUILayout.Label(baseName + split.suffixes[c] + OutputSettings.ExtensionFor(format), PackerStyles.Info);
                }
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.BeginHorizontal();
            Label("Skip alpha", "Do not write an alpha file when the source has no alpha channel.");
            split.skipMissingAlpha = EditorGUILayout.Toggle(split.skipMissingAlpha);
            EditorGUILayout.EndHorizontal();

            GUILayout.Space(6);
            GUILayout.Label("Output", PackerStyles.Section);
            Separator();
            EditorGUILayout.BeginHorizontal();
            Label("Format", "Auto: EXR for float sources, TGA for TGA sources, PNG otherwise.");
            split.autoFormat = GUILayout.Toggle(split.autoFormat, "Auto", EditorStyles.miniButton, GUILayout.Width(44));
            using (new EditorGUI.DisabledScope(split.autoFormat))
                split.format = (OutputFormat)GUILayout.Toolbar((int)format, s_FormatNames, EditorStyles.miniButton, GUILayout.Width(150));
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            Label("Folder", "Empty: next to the source");
            split.folder = EditorGUILayout.TextField(split.folder);
            if (GUILayout.Button("...", EditorStyles.miniButton, GUILayout.Width(26)))
                EditorApplication.delayCall += PickSplitFolder;
            EditorGUILayout.EndHorizontal();

            if (m_SplitSettingsError != null)
                EditorGUILayout.HelpBox(m_SplitSettingsError, MessageType.Error);
        }

        void RunSplit()
        {
            m_PackScheduled = false;
            var state = State;
            if (m_Disposed || state.splitSource == null)
                return;
            try
            {
                var written = ChannelPacker.Split(state.splitSource, state.split);
                var objects = written.Select(AssetDatabase.LoadAssetAtPath<Texture2D>).Where(t => t != null).ToArray();
                m_LastOutput = objects.FirstOrDefault();
                m_LastResult = written.Count == 0
                    ? "Nothing to write"
                    : $"Created {written.Count} texture{(written.Count == 1 ? string.Empty : "s")} in {AssetPaths.FolderOf(written[0])}";
                if (objects.Length > 0)
                {
                    Selection.objects = objects;
                    EditorGUIUtility.PingObject(objects[0]);
                }
            }
            catch (PackException e)
            {
                EditorUtility.DisplayDialog(Title, e.Message, "OK");
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                EditorUtility.DisplayDialog(Title, "Unexpected error: " + e.Message + "\nSee the Console for details.", "OK");
            }
            MarkDirty();
            m_Repaint?.Invoke();
        }
    }
}
