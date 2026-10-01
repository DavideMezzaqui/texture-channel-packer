using System;

namespace DAVFX.TextureChannelPacker
{
    /// <summary>Precision of the stored samples, ordered from lowest to highest.</summary>
    public enum SamplePrecision
    {
        UNorm8 = 0,
        UNorm16 = 1,
        Half = 2,
        Float = 3
    }

    /// <summary>
    /// Decoded image held as planar float channels, rows ordered top to bottom.
    /// Integer sources are stored as k / (2^bits - 1), so quantizing back to the same depth is exact.
    /// Instances are treated as immutable: planes can be shared between images and must never be written after creation.
    /// </summary>
    public sealed class PixelImage
    {
        public int Width { get; }
        public int Height { get; }

        public float[] R { get; }
        public float[] G { get; }
        public float[] B { get; }

        /// <summary>Alpha plane, or null when the image is fully opaque.</summary>
        public float[] A { get; }

        public SamplePrecision Precision { get; set; }

        /// <summary>Human readable origin, e.g. "PNG 16-bit RGBA".</summary>
        public string SourceDescription { get; set; } = string.Empty;

        /// <summary>True when the source already lost data before reaching us (JPEG, GPU compressed texture).</summary>
        public bool IsLossySource { get; set; }

        public PixelImage(int width, int height, float[] r, float[] g, float[] b, float[] a, SamplePrecision precision)
        {
            if (width <= 0 || height <= 0)
                throw new ArgumentException($"Invalid image size {width}x{height}.");
            long count = (long)width * height;
            if (count > int.MaxValue)
                throw new ArgumentException($"Image {width}x{height} is too large.");
            CheckPlane(r, count, nameof(r));
            CheckPlane(g, count, nameof(g));
            CheckPlane(b, count, nameof(b));
            if (a != null)
                CheckPlane(a, count, nameof(a));

            Width = width;
            Height = height;
            R = r;
            G = g;
            B = b;
            A = a;
            Precision = precision;
        }

        public static PixelImage Gray(int width, int height, float[] luminance, float[] alpha, SamplePrecision precision)
        {
            return new PixelImage(width, height, luminance, luminance, luminance, alpha, precision);
        }

        public int PixelCount => Width * Height;
        public bool HasAlpha => A != null;
        public bool IsGrayscale => ReferenceEquals(R, G) && ReferenceEquals(R, B);

        /// <summary>Approximate memory used by the planes, counting shared planes once.</summary>
        public long ByteSize
        {
            get
            {
                int planes = IsGrayscale ? 1 : 3;
                if (A != null)
                    planes++;
                return (long)planes * PixelCount * sizeof(float);
            }
        }

        public string LayoutName => IsGrayscale ? (HasAlpha ? "Gray+Alpha" : "Gray") : (HasAlpha ? "RGBA" : "RGB");

        /// <summary>Returns plane 0..3 (R, G, B, A). Alpha may be null.</summary>
        public float[] GetPlane(int channel)
        {
            switch (channel)
            {
                case 0: return R;
                case 1: return G;
                case 2: return B;
                case 3: return A;
                default: throw new ArgumentOutOfRangeException(nameof(channel));
            }
        }

        static void CheckPlane(float[] plane, long count, string name)
        {
            if (plane == null)
                throw new ArgumentNullException(name);
            if (plane.Length != count)
                throw new ArgumentException($"Plane '{name}' has {plane.Length} samples, expected {count}.");
        }

        public static string PrecisionName(SamplePrecision precision)
        {
            switch (precision)
            {
                case SamplePrecision.UNorm8: return "8-bit";
                case SamplePrecision.UNorm16: return "16-bit";
                case SamplePrecision.Half: return "Half float";
                default: return "32-bit float";
            }
        }
    }
}
