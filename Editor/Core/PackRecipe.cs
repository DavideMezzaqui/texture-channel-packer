using System;
using System.Collections.Generic;

namespace DAVFX.TextureChannelPacker
{
    /// <summary>Which value of an input texture an operand reads.</summary>
    public enum ChannelSource
    {
        Red = 0,
        Green = 1,
        Blue = 2,
        Alpha = 3,
        /// <summary>Rec.709 weights applied to the stored RGB values.</summary>
        Luminance = 4,
        Average = 5,
        Minimum = 6,
        Maximum = 7
    }

    public enum OperandKind
    {
        Constant = 0,
        Texture = 1
    }

    /// <summary>A value source: a channel of an input slot, or a constant.</summary>
    [Serializable]
    public sealed class Operand
    {
        public OperandKind kind = OperandKind.Constant;
        public int input;
        public ChannelSource channel = ChannelSource.Red;
        public float constant;

        /// <summary>When true and the input slot is empty, <see cref="fallback"/> is used instead of failing.</summary>
        public bool hasFallback;
        public float fallback = 1f;

        public static Operand Value(float value)
        {
            return new Operand { kind = OperandKind.Constant, constant = value };
        }

        public static Operand Texture(int input, ChannelSource channel)
        {
            return new Operand { kind = OperandKind.Texture, input = input, channel = channel };
        }

        public static Operand Texture(int input, ChannelSource channel, float fallbackWhenEmpty)
        {
            return new Operand { kind = OperandKind.Texture, input = input, channel = channel, hasFallback = true, fallback = fallbackWhenEmpty };
        }

        public Operand Clone()
        {
            return (Operand)MemberwiseClone();
        }
    }

    public enum CombineOp
    {
        None = 0,
        Multiply = 1,
        Add = 2,
        Subtract = 3,
        Minimum = 4,
        Maximum = 5,
        Average = 6,
        Difference = 7,
        Screen = 8
    }

    public enum ColorConversion
    {
        None = 0,
        SrgbToLinear = 1,
        LinearToSrgb = 2
    }

    /// <summary>Photoshop style levels. Input range is clamped, gamma above 1 brightens.</summary>
    [Serializable]
    public sealed class Levels
    {
        public float inMin;
        public float inMax = 1f;
        public float gamma = 1f;
        public float outMin;
        public float outMax = 1f;

        public bool IsIdentity => inMin == 0f && inMax == 1f && gamma == 1f && outMin == 0f && outMax == 1f;

        public float Apply(float value)
        {
            float t;
            if (inMax == inMin)
                t = value >= inMin ? 1f : 0f;
            else
                t = (value - inMin) / (inMax - inMin);

            if (t < 0f) t = 0f;
            else if (t > 1f) t = 1f;

            if (gamma != 1f && gamma > 0f)
                t = (float)Math.Pow(t, 1.0 / gamma);

            return outMin + t * (outMax - outMin);
        }

        public Levels Clone()
        {
            return (Levels)MemberwiseClone();
        }
    }

    /// <summary>
    /// How one output channel is built. Evaluation order:
    /// source, combine, color conversion, invert, levels.
    /// </summary>
    [Serializable]
    public sealed class ChannelRecipe
    {
        public Operand source = Operand.Value(0f);
        public CombineOp combine = CombineOp.None;
        public Operand other = Operand.Value(1f);
        public ColorConversion conversion = ColorConversion.None;
        public bool invert;
        public bool useLevels;
        public Levels levels = new Levels();

        /// <summary>True when the value is copied from the source without any arithmetic.</summary>
        public bool IsPassthrough => combine == CombineOp.None && conversion == ColorConversion.None && !invert && !HasActiveLevels;

        public bool HasActiveLevels => useLevels && levels != null && !levels.IsIdentity;

        public static ChannelRecipe From(Operand source, bool invert = false)
        {
            return new ChannelRecipe { source = source, invert = invert };
        }

        public ChannelRecipe Clone()
        {
            var copy = (ChannelRecipe)MemberwiseClone();
            copy.source = source?.Clone() ?? Operand.Value(0f);
            copy.other = other?.Clone() ?? Operand.Value(1f);
            copy.levels = levels?.Clone() ?? new Levels();
            return copy;
        }
    }

    public enum OutputLayout
    {
        RGBA = 0,
        RGB = 1,
        Grayscale = 2,
        GrayscaleAlpha = 3
    }

    /// <summary>
    /// Complete description of an output texture. Channel slots are always R, G, B, A;
    /// grayscale layouts use slot 0 as luminance and slot 3 as alpha.
    /// </summary>
    [Serializable]
    public sealed class PackRecipe
    {
        public OutputLayout layout = OutputLayout.RGBA;
        public ChannelRecipe[] channels = CreateDefaultChannels();

        static readonly IReadOnlyList<int> s_Rgba = Array.AsReadOnly(new[] { 0, 1, 2, 3 });
        static readonly IReadOnlyList<int> s_Rgb = Array.AsReadOnly(new[] { 0, 1, 2 });
        static readonly IReadOnlyList<int> s_Gray = Array.AsReadOnly(new[] { 0 });
        static readonly IReadOnlyList<int> s_GrayAlpha = Array.AsReadOnly(new[] { 0, 3 });

        public static ChannelRecipe[] CreateDefaultChannels()
        {
            return new[]
            {
                ChannelRecipe.From(Operand.Value(0f)),
                ChannelRecipe.From(Operand.Value(0f)),
                ChannelRecipe.From(Operand.Value(0f)),
                ChannelRecipe.From(Operand.Value(1f))
            };
        }

        /// <summary>Channel slots written by a layout (shared, read-only).</summary>
        public static IReadOnlyList<int> ActiveChannels(OutputLayout layout)
        {
            switch (layout)
            {
                case OutputLayout.RGB: return s_Rgb;
                case OutputLayout.Grayscale: return s_Gray;
                case OutputLayout.GrayscaleAlpha: return s_GrayAlpha;
                default: return s_Rgba;
            }
        }

        public static bool HasAlpha(OutputLayout layout)
        {
            return layout == OutputLayout.RGBA || layout == OutputLayout.GrayscaleAlpha;
        }

        public static bool IsGray(OutputLayout layout)
        {
            return layout == OutputLayout.Grayscale || layout == OutputLayout.GrayscaleAlpha;
        }

        /// <summary>Makes sure there are exactly four valid channel recipes (Unity deserialization can leave gaps).</summary>
        public void Sanitize()
        {
            if (channels == null || channels.Length != 4)
            {
                var fixedChannels = CreateDefaultChannels();
                if (channels != null)
                {
                    for (int i = 0; i < Math.Min(4, channels.Length); i++)
                    {
                        if (channels[i] != null)
                            fixedChannels[i] = channels[i];
                    }
                }
                channels = fixedChannels;
            }
            for (int i = 0; i < 4; i++)
            {
                if (channels[i] == null)
                    channels[i] = CreateDefaultChannels()[i];
                if (channels[i].source == null) channels[i].source = Operand.Value(0f);
                if (channels[i].other == null) channels[i].other = Operand.Value(1f);
                if (channels[i].levels == null) channels[i].levels = new Levels();
            }
        }

        public PackRecipe Clone()
        {
            var copy = new PackRecipe { layout = layout, channels = new ChannelRecipe[4] };
            Sanitize();
            for (int i = 0; i < 4; i++)
                copy.channels[i] = channels[i].Clone();
            return copy;
        }

        /// <summary>Calls <paramref name="visitor"/> for every operand that is actually evaluated.</summary>
        public void ForEachActiveOperand(Action<Operand> visitor)
        {
            Sanitize();
            foreach (int c in ActiveChannels(layout))
            {
                var ch = channels[c];
                visitor(ch.source);
                if (ch.combine != CombineOp.None)
                    visitor(ch.other);
            }
        }
    }
}
