using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DAVFX.TextureChannelPacker
{
    /// <summary>
    /// Evaluates a <see cref="PackRecipe"/> against decoded inputs.
    /// Passthrough channels reuse the source plane untouched, so copying a channel is exact by construction.
    /// </summary>
    public static class PackEngine
    {
        public static PixelImage Evaluate(PackRecipe recipe, IReadOnlyList<PixelImage> inputs, int width, int height, ResampleFilter filter)
        {
            if (recipe == null)
                throw new ArgumentNullException(nameof(recipe));
            if (width <= 0 || height <= 0)
                throw new PackException($"Invalid output size {width}x{height}.");

            recipe.Sanitize();
            var context = new Context(inputs, width, height, filter);
            var planes = new float[4][];
            foreach (int c in PackRecipe.ActiveChannels(recipe.layout))
                planes[c] = context.EvaluateChannel(recipe.channels[c]);

            var precision = context.UsedAnyInput ? context.MaxPrecision : SamplePrecision.UNorm8;
            PixelImage result;
            if (PackRecipe.IsGray(recipe.layout))
                result = PixelImage.Gray(width, height, planes[0], planes[3], precision);
            else
                result = new PixelImage(width, height, planes[0], planes[1], planes[2], planes[3], precision);
            result.IsLossySource = context.AnyLossyInput;
            result.SourceDescription = "Packed";
            return result;
        }

        /// <summary>Highest precision among the inputs a recipe reads (UNorm8 if none).</summary>
        public static SamplePrecision UsedPrecision(PackRecipe recipe, IReadOnlyList<PixelImage> inputs)
        {
            var max = SamplePrecision.UNorm8;
            recipe.ForEachActiveOperand(op =>
            {
                if (op.kind != OperandKind.Texture || inputs == null)
                    return;
                if (op.input < 0 || op.input >= inputs.Count || inputs[op.input] == null)
                    return;
                if (inputs[op.input].Precision > max)
                    max = inputs[op.input].Precision;
            });
            return max;
        }

        /// <summary>A plane or a constant. Constants stay scalar until the very end to save memory and time.</summary>
        readonly struct Signal
        {
            public readonly float[] Data;
            public readonly float Value;

            public Signal(float[] data)
            {
                Data = data;
                Value = 0f;
            }

            public Signal(float value)
            {
                Data = null;
                Value = value;
            }

            public bool IsConstant => Data == null;
        }

        sealed class Context
        {
            readonly IReadOnlyList<PixelImage> m_Inputs;
            readonly int m_Width;
            readonly int m_Height;
            readonly ResampleFilter m_Filter;
            readonly Dictionary<long, Signal> m_Cache = new Dictionary<long, Signal>();

            public SamplePrecision MaxPrecision = SamplePrecision.UNorm8;
            public bool UsedAnyInput;
            public bool AnyLossyInput;

            public Context(IReadOnlyList<PixelImage> inputs, int width, int height, ResampleFilter filter)
            {
                m_Inputs = inputs ?? Array.Empty<PixelImage>();
                m_Width = width;
                m_Height = height;
                m_Filter = filter;
            }

            int Count => m_Width * m_Height;

            public float[] EvaluateChannel(ChannelRecipe recipe)
            {
                Signal value = Sample(recipe.source);
                if (recipe.combine != CombineOp.None)
                    value = Combine(value, Sample(recipe.other), recipe.combine);

                var steps = new List<Func<float, float>>();
                if (recipe.conversion == ColorConversion.SrgbToLinear)
                    steps.Add(ColorMath.SrgbToLinear);
                else if (recipe.conversion == ColorConversion.LinearToSrgb)
                    steps.Add(ColorMath.LinearToSrgb);
                if (recipe.invert)
                    steps.Add(v => 1f - v);
                if (recipe.HasActiveLevels)
                {
                    var levels = recipe.levels.Clone();
                    steps.Add(levels.Apply);
                }

                if (steps.Count > 0)
                    value = Map(value, steps);

                return Materialize(value);
            }

            Signal Sample(Operand op)
            {
                if (op == null || op.kind == OperandKind.Constant)
                    return new Signal(op?.constant ?? 0f);

                PixelImage image = op.input >= 0 && op.input < m_Inputs.Count ? m_Inputs[op.input] : null;
                if (image == null)
                {
                    if (op.hasFallback)
                        return new Signal(op.fallback);
                    throw new PackException($"Input {SlotName(op.input)} is empty.");
                }

                UsedAnyInput = true;
                AnyLossyInput |= image.IsLossySource;
                if (image.Precision > MaxPrecision)
                    MaxPrecision = image.Precision;

                long key = ((long)op.input << 8) | (long)op.channel;
                if (m_Cache.TryGetValue(key, out var cached))
                    return cached;

                Signal native = Extract(image, op.channel);
                Signal result = native;
                if (!native.IsConstant && (image.Width != m_Width || image.Height != m_Height))
                {
                    if (m_Filter == ResampleFilter.None)
                        throw new PackException($"Input {SlotName(op.input)} is {image.Width}x{image.Height} but the output is {m_Width}x{m_Height}. Choose a resample filter or matching textures.");
                    result = new Signal(Resampler.Resample(native.Data, image.Width, image.Height, m_Width, m_Height, m_Filter));
                }
                m_Cache[key] = result;
                return result;
            }

            static Signal Extract(PixelImage image, ChannelSource channel)
            {
                switch (channel)
                {
                    case ChannelSource.Red: return new Signal(image.R);
                    case ChannelSource.Green: return new Signal(image.G);
                    case ChannelSource.Blue: return new Signal(image.B);
                    case ChannelSource.Alpha: return image.A != null ? new Signal(image.A) : new Signal(1f);
                }

                // Every derived value of a gray pixel is the gray value itself; skip the math to stay exact.
                if (image.IsGrayscale)
                    return new Signal(image.R);

                var r = image.R;
                var g = image.G;
                var b = image.B;
                var result = new float[image.PixelCount];
                ForRange(result.Length, (start, end) =>
                {
                    for (int i = start; i < end; i++)
                    {
                        float vr = r[i], vg = g[i], vb = b[i];
                        float v;
                        switch (channel)
                        {
                            case ChannelSource.Luminance: v = ColorMath.Luminance(vr, vg, vb); break;
                            case ChannelSource.Average: v = (vr + vg + vb) / 3f; break;
                            case ChannelSource.Minimum: v = Math.Min(vr, Math.Min(vg, vb)); break;
                            default: v = Math.Max(vr, Math.Max(vg, vb)); break;
                        }
                        result[i] = v;
                    }
                });
                return new Signal(result);
            }

            Signal Combine(Signal a, Signal b, CombineOp op)
            {
                if (a.IsConstant && b.IsConstant)
                    return new Signal(ColorMath.Combine(op, a.Value, b.Value));

                var result = new float[Count];
                var da = a.Data;
                var db = b.Data;
                float ca = a.Value;
                float cb = b.Value;
                ForRange(result.Length, (start, end) =>
                {
                    for (int i = start; i < end; i++)
                        result[i] = ColorMath.Combine(op, da != null ? da[i] : ca, db != null ? db[i] : cb);
                });
                return new Signal(result);
            }

            Signal Map(Signal input, List<Func<float, float>> steps)
            {
                if (input.IsConstant)
                {
                    float v = input.Value;
                    foreach (var step in steps)
                        v = step(v);
                    return new Signal(v);
                }

                var src = input.Data;
                var result = new float[Count];
                var array = steps.ToArray();
                ForRange(result.Length, (start, end) =>
                {
                    for (int i = start; i < end; i++)
                    {
                        float v = src[i];
                        for (int s = 0; s < array.Length; s++)
                            v = array[s](v);
                        result[i] = v;
                    }
                });
                return new Signal(result);
            }

            float[] Materialize(Signal signal)
            {
                if (!signal.IsConstant)
                    return signal.Data;
                var plane = new float[Count];
                float v = signal.Value;
                for (int i = 0; i < plane.Length; i++)
                    plane[i] = v;
                return plane;
            }
        }

        public static string SlotName(int index)
        {
            return index >= 0 && index < 26 ? ((char)('A' + index)).ToString() : $"#{index}";
        }

        /// <summary>Runs <paramref name="body"/> over [0,count) in parallel chunks.</summary>
        internal static void ForRange(int count, Action<int, int> body)
        {
            const int chunk = 1 << 16;
            if (count <= chunk)
            {
                body(0, count);
                return;
            }
            int chunks = (count + chunk - 1) / chunk;
            Parallel.For(0, chunks, c =>
            {
                int start = c * chunk;
                body(start, Math.Min(count, start + chunk));
            });
        }
    }
}
