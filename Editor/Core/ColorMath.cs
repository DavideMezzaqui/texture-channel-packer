using System;

namespace DAVFX.TextureChannelPacker
{
    public static class ColorMath
    {
        public const float LumaR = 0.2126f;
        public const float LumaG = 0.7152f;
        public const float LumaB = 0.0722f;

        /// <summary>IEC 61966-2-1 sRGB decode.</summary>
        public static float SrgbToLinear(float value)
        {
            if (value <= 0.04045f)
                return value / 12.92f;
            return (float)Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        /// <summary>IEC 61966-2-1 sRGB encode.</summary>
        public static float LinearToSrgb(float value)
        {
            if (value <= 0.0031308f)
                return value * 12.92f;
            return (float)(1.055 * Math.Pow(value, 1.0 / 2.4) - 0.055);
        }

        public static float Luminance(float r, float g, float b)
        {
            return LumaR * r + LumaG * g + LumaB * b;
        }

        public static float Combine(CombineOp op, float a, float b)
        {
            switch (op)
            {
                case CombineOp.Multiply: return a * b;
                case CombineOp.Add: return a + b;
                case CombineOp.Subtract: return a - b;
                case CombineOp.Minimum: return a < b ? a : b;
                case CombineOp.Maximum: return a > b ? a : b;
                case CombineOp.Average: return (a + b) * 0.5f;
                case CombineOp.Difference: return Math.Abs(a - b);
                case CombineOp.Screen: return 1f - (1f - a) * (1f - b);
                default: return a;
            }
        }

        /// <summary>Clamps to [0,1]; NaN becomes 0.</summary>
        public static float Saturate(float value)
        {
            if (!(value > 0f))
                return 0f;
            return value < 1f ? value : 1f;
        }

        /// <summary>Exact round-to-nearest quantization of a [0,1] value to an integer range.</summary>
        public static int Quantize(float value, int maxValue)
        {
            if (!(value > 0f))
                return 0;
            if (value >= 1f)
                return maxValue;
            // In double the product is exact, so a value just below a half step never rounds up.
            return (int)(value * (double)maxValue + 0.5);
        }
    }
}
