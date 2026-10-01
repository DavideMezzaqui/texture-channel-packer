using System;
using System.Threading.Tasks;

namespace DAVFX.TextureChannelPacker
{
    public enum ResampleFilter
    {
        /// <summary>Sizes must match; a mismatch is an error.</summary>
        None = 0,
        /// <summary>Copies the closest pixel. Exact for integer scale factors.</summary>
        Nearest = 1,
        /// <summary>Smooth, best for upscaling.</summary>
        Bilinear = 2,
        /// <summary>Averages covered pixels, best for downscaling.</summary>
        Area = 3
    }

    public static class Resampler
    {
        /// <summary>Resamples one plane. Returns the same array when sizes already match.</summary>
        public static float[] Resample(float[] src, int srcWidth, int srcHeight, int dstWidth, int dstHeight, ResampleFilter filter)
        {
            if (src == null)
                throw new ArgumentNullException(nameof(src));
            if (srcWidth == dstWidth && srcHeight == dstHeight)
                return src;
            if (filter == ResampleFilter.None)
                throw new PackException($"Size mismatch: {srcWidth}x{srcHeight} cannot be used for a {dstWidth}x{dstHeight} output without resampling.");

            if (filter == ResampleFilter.Nearest)
                return Nearest(src, srcWidth, srcHeight, dstWidth, dstHeight);

            var xTaps = filter == ResampleFilter.Bilinear ? LinearTaps(srcWidth, dstWidth) : AreaTaps(srcWidth, dstWidth);
            var yTaps = filter == ResampleFilter.Bilinear ? LinearTaps(srcHeight, dstHeight) : AreaTaps(srcHeight, dstHeight);

            // Horizontal pass: srcWidth x srcHeight -> dstWidth x srcHeight.
            var temp = new float[dstWidth * srcHeight];
            Parallel.For(0, srcHeight, y =>
            {
                int srcRow = y * srcWidth;
                int dstRow = y * dstWidth;
                for (int x = 0; x < dstWidth; x++)
                {
                    var tap = xTaps[x];
                    double sum = 0;
                    for (int k = 0; k < tap.Weights.Length; k++)
                        sum += tap.Weights[k] * src[srcRow + tap.Start + k];
                    temp[dstRow + x] = (float)sum;
                }
            });

            // Vertical pass: dstWidth x srcHeight -> dstWidth x dstHeight.
            var dst = new float[dstWidth * dstHeight];
            Parallel.For(0, dstHeight, y =>
            {
                var tap = yTaps[y];
                int dstRow = y * dstWidth;
                for (int x = 0; x < dstWidth; x++)
                {
                    double sum = 0;
                    for (int k = 0; k < tap.Weights.Length; k++)
                        sum += tap.Weights[k] * temp[(tap.Start + k) * dstWidth + x];
                    dst[dstRow + x] = (float)sum;
                }
            });
            return dst;
        }

        static float[] Nearest(float[] src, int sw, int sh, int dw, int dh)
        {
            var xs = new int[dw];
            for (int x = 0; x < dw; x++)
                xs[x] = Math.Min(sw - 1, (int)((x + 0.5) * sw / dw));
            var dst = new float[dw * dh];
            Parallel.For(0, dh, y =>
            {
                int sy = Math.Min(sh - 1, (int)((y + 0.5) * sh / dh));
                int srcRow = sy * sw;
                int dstRow = y * dw;
                for (int x = 0; x < dw; x++)
                    dst[dstRow + x] = src[srcRow + xs[x]];
            });
            return dst;
        }

        struct Tap
        {
            public int Start;
            public double[] Weights;
        }

        static Tap[] LinearTaps(int srcSize, int dstSize)
        {
            var taps = new Tap[dstSize];
            double scale = (double)srcSize / dstSize;
            for (int i = 0; i < dstSize; i++)
            {
                double u = (i + 0.5) * scale - 0.5;
                int i0 = (int)Math.Floor(u);
                double f = u - i0;
                int a = Clamp(i0, 0, srcSize - 1);
                int b = Clamp(i0 + 1, 0, srcSize - 1);
                if (a == b || f <= 0)
                    taps[i] = new Tap { Start = a, Weights = new[] { 1.0 } };
                else
                    taps[i] = new Tap { Start = a, Weights = new[] { 1.0 - f, f } };
            }
            return taps;
        }

        static Tap[] AreaTaps(int srcSize, int dstSize)
        {
            var taps = new Tap[dstSize];
            double scale = (double)srcSize / dstSize;
            for (int i = 0; i < dstSize; i++)
            {
                double start = i * scale;
                double end = (i + 1) * scale;
                int first = Clamp((int)Math.Floor(start), 0, srcSize - 1);
                int last = Clamp((int)Math.Ceiling(end) - 1, first, srcSize - 1);
                var weights = new double[last - first + 1];
                double total = 0;
                for (int j = first; j <= last; j++)
                {
                    double overlap = Math.Min(end, j + 1) - Math.Max(start, j);
                    if (overlap < 0) overlap = 0;
                    weights[j - first] = overlap;
                    total += overlap;
                }
                for (int k = 0; k < weights.Length; k++)
                    weights[k] /= total;
                taps[i] = new Tap { Start = first, Weights = weights };
            }
            return taps;
        }

        static int Clamp(int v, int min, int max)
        {
            return v < min ? min : (v > max ? max : v);
        }

        /// <summary>Largest size that fits in a box of <paramref name="maxSize"/> keeping the aspect ratio. Never upscales.</summary>
        public static void FitInside(int width, int height, int maxSize, out int fitWidth, out int fitHeight)
        {
            if (width <= maxSize && height <= maxSize)
            {
                fitWidth = width;
                fitHeight = height;
                return;
            }
            double scale = (double)maxSize / Math.Max(width, height);
            fitWidth = Math.Max(1, (int)Math.Round(width * scale));
            fitHeight = Math.Max(1, (int)Math.Round(height * scale));
        }

        /// <summary>Downscales a whole image (used by previews).</summary>
        public static PixelImage ResizeImage(PixelImage image, int width, int height, ResampleFilter filter)
        {
            if (image.Width == width && image.Height == height)
                return image;
            float[] r = Resample(image.R, image.Width, image.Height, width, height, filter);
            float[] g = image.IsGrayscale ? r : Resample(image.G, image.Width, image.Height, width, height, filter);
            float[] b = image.IsGrayscale ? r : Resample(image.B, image.Width, image.Height, width, height, filter);
            float[] a = image.A == null ? null : Resample(image.A, image.Width, image.Height, width, height, filter);
            return new PixelImage(width, height, r, g, b, a, image.Precision)
            {
                SourceDescription = image.SourceDescription,
                IsLossySource = image.IsLossySource
            };
        }
    }
}
