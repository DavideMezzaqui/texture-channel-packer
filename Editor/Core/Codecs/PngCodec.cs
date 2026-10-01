using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;

namespace DAVFX.TextureChannelPacker.Codecs
{
    /// <summary>
    /// Lossless PNG reader and writer.
    /// Reads every standard variant: gray/RGB/palette/gray+alpha/RGBA, 1 to 16 bits, Adam7 interlacing, tRNS transparency.
    /// Ancillary chunks (gamma, ICC, sRGB) are ignored on purpose: the stored values are what shaders sample.
    /// </summary>
    public static class PngCodec
    {
        static readonly byte[] s_Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

        static readonly int[] s_PassX = { 0, 4, 0, 2, 0, 1, 0 };
        static readonly int[] s_PassY = { 0, 0, 4, 0, 2, 0, 1 };
        static readonly int[] s_PassDx = { 8, 8, 4, 4, 2, 2, 1 };
        static readonly int[] s_PassDy = { 8, 8, 8, 4, 4, 2, 2 };

        public static bool IsPng(byte[] data)
        {
            if (data == null || data.Length < 8)
                return false;
            for (int i = 0; i < 8; i++)
            {
                if (data[i] != s_Signature[i])
                    return false;
            }
            return true;
        }

        // ------------------------------------------------------------------ decode

        public static PixelImage Decode(byte[] data)
        {
            if (!IsPng(data))
                throw new PackException("Not a PNG file.");
            try
            {
                return DecodeChecked(data);
            }
            catch (AggregateException e) when (e.InnerException is PackException inner)
            {
                throw new PackException(inner.Message, inner);
            }
            catch (Exception e) when (!(e is PackException) && !(e is OutOfMemoryException))
            {
                throw new PackException($"The PNG file is damaged ({e.GetType().Name}).", e);
            }
        }

        static PixelImage DecodeChecked(byte[] data)
        {

            int width = 0, height = 0, bitDepth = 0, colorType = -1, interlace = 0;
            byte[] palette = null;
            byte[] trns = null;
            bool seenHeader = false;
            var idat = new MemoryStream();

            int pos = 8;
            while (pos + 8 <= data.Length)
            {
                int length = ReadInt32BE(data, pos);
                string type = Encoding.ASCII.GetString(data, pos + 4, 4);
                int start = pos + 8;
                if (length < 0 || (long)start + length + 4 > data.Length)
                    throw new PackException("PNG file is truncated.");

                if (type == "IHDR")
                {
                    if (length != 13)
                        throw new PackException("Invalid PNG header (IHDR).");
                    width = ReadInt32BE(data, start);
                    height = ReadInt32BE(data, start + 4);
                    bitDepth = data[start + 8];
                    colorType = data[start + 9];
                    if (data[start + 10] != 0 || data[start + 11] != 0)
                        throw new PackException("Unsupported PNG compression or filter method.");
                    interlace = data[start + 12];
                    seenHeader = true;
                }
                else if (type == "PLTE")
                {
                    palette = Slice(data, start, length);
                }
                else if (type == "tRNS")
                {
                    trns = Slice(data, start, length);
                }
                else if (type == "IDAT")
                {
                    idat.Write(data, start, length);
                }
                else if (type == "IEND")
                {
                    break;
                }
                pos = start + length + 4;
            }

            if (!seenHeader)
                throw new PackException("PNG header (IHDR) not found.");
            if (width <= 0 || height <= 0)
                throw new PackException($"Invalid PNG size {width}x{height}.");
            CheckSize("PNG", width, height);
            if (interlace > 1)
                throw new PackException("Unknown PNG interlace method.");

            int channels;
            switch (colorType)
            {
                case 0: channels = 1; CheckDepth(bitDepth, 1, 2, 4, 8, 16); break;
                case 2: channels = 3; CheckDepth(bitDepth, 8, 16); break;
                case 3: channels = 1; CheckDepth(bitDepth, 1, 2, 4, 8); break;
                case 4: channels = 2; CheckDepth(bitDepth, 8, 16); break;
                case 6: channels = 4; CheckDepth(bitDepth, 8, 16); break;
                default: throw new PackException($"Unknown PNG color type {colorType}.");
            }
            if (colorType == 3 && palette == null)
                throw new PackException("Palette PNG without a PLTE chunk.");

            int bitsPerPixel = channels * bitDepth;
            int bpp = Math.Max(1, bitsPerPixel / 8);

            long expected = 0;
            if (interlace == 0)
            {
                expected = (long)height * (1 + RowBytes(width, bitsPerPixel));
            }
            else
            {
                for (int p = 0; p < 7; p++)
                {
                    PassSize(width, height, p, out int pw, out int ph);
                    if (pw > 0 && ph > 0)
                        expected += (long)ph * (1 + RowBytes(pw, bitsPerPixel));
                }
            }
            if (expected > int.MaxValue)
                throw new PackException("PNG is too large.");

            byte[] compressed = idat.ToArray();
            byte[] raw = Zlib.Decompress(compressed, 0, compressed.Length, (int)expected);

            var target = new DecodeTarget(width, height, colorType, bitDepth, palette, trns);
            int rawPos = 0;
            if (interlace == 0)
            {
                int rowBytes = RowBytes(width, bitsPerPixel);
                int stride = rowBytes + 1;
                for (int y = 0; y < height; y++)
                    Unfilter(raw, y * stride + 1, y > 0 ? (y - 1) * stride + 1 : -1, rowBytes, bpp, raw[y * stride]);
                Parallel.For(0, height, y => target.WriteRow(raw, y * stride + 1, width, y, 0, 1, channels));
            }
            else
            {
                for (int p = 0; p < 7; p++)
                {
                    PassSize(width, height, p, out int pw, out int ph);
                    if (pw > 0 && ph > 0)
                        DecodePass(raw, ref rawPos, pw, ph, s_PassX[p], s_PassY[p], s_PassDx[p], s_PassDy[p], bitsPerPixel, bpp, channels, target);
                }
            }

            var image = target.Build();
            string layout = colorType == 3 ? "Indexed" : image.LayoutName;
            image.SourceDescription = $"PNG {bitDepth}-bit {layout}";
            return image;
        }

        /// <summary>Rejects images larger than Unity's texture limit before allocating anything.</summary>
        internal static void CheckSize(string format, int width, int height)
        {
            if (width > SizeSettings.MaxSize || height > SizeSettings.MaxSize)
                throw new PackException($"The {format} file is {width}x{height}: the maximum supported size is {SizeSettings.MaxSize} pixels per side, like Unity.");
        }

        static void CheckDepth(int depth, params int[] allowed)
        {
            foreach (int a in allowed)
            {
                if (a == depth)
                    return;
            }
            throw new PackException($"Invalid PNG bit depth {depth} for its color type.");
        }

        static int RowBytes(int width, int bitsPerPixel)
        {
            return (int)(((long)width * bitsPerPixel + 7) / 8);
        }

        static void PassSize(int width, int height, int pass, out int pw, out int ph)
        {
            pw = width > s_PassX[pass] ? (width - s_PassX[pass] + s_PassDx[pass] - 1) / s_PassDx[pass] : 0;
            ph = height > s_PassY[pass] ? (height - s_PassY[pass] + s_PassDy[pass] - 1) / s_PassDy[pass] : 0;
        }

        static void DecodePass(byte[] raw, ref int pos, int pw, int ph, int x0, int y0, int dx, int dy,
            int bitsPerPixel, int bpp, int channels, DecodeTarget target)
        {
            int rowBytes = RowBytes(pw, bitsPerPixel);
            int prev = -1;
            for (int j = 0; j < ph; j++)
            {
                int filter = raw[pos];
                int cur = pos + 1;
                Unfilter(raw, cur, prev, rowBytes, bpp, filter);
                target.WriteRow(raw, cur, pw, y0 + j * dy, x0, dx, channels);
                prev = cur;
                pos = cur + rowBytes;
            }
        }

        static void Unfilter(byte[] d, int cur, int prev, int n, int bpp, int filter)
        {
            switch (filter)
            {
                case 0:
                    return;
                case 1:
                    for (int i = bpp; i < n; i++)
                        d[cur + i] = (byte)(d[cur + i] + d[cur + i - bpp]);
                    return;
                case 2:
                    if (prev < 0)
                        return;
                    for (int i = 0; i < n; i++)
                        d[cur + i] = (byte)(d[cur + i] + d[prev + i]);
                    return;
                case 3:
                    for (int i = 0; i < n; i++)
                    {
                        int left = i >= bpp ? d[cur + i - bpp] : 0;
                        int up = prev >= 0 ? d[prev + i] : 0;
                        d[cur + i] = (byte)(d[cur + i] + ((left + up) >> 1));
                    }
                    return;
                case 4:
                    for (int i = 0; i < n; i++)
                    {
                        int left = i >= bpp ? d[cur + i - bpp] : 0;
                        int up = prev >= 0 ? d[prev + i] : 0;
                        int upLeft = i >= bpp && prev >= 0 ? d[prev + i - bpp] : 0;
                        d[cur + i] = (byte)(d[cur + i] + Paeth(left, up, upLeft));
                    }
                    return;
                default:
                    throw new PackException($"Invalid PNG filter type {filter}.");
            }
        }

        static int Paeth(int a, int b, int c)
        {
            int p = a + b - c;
            int pa = Math.Abs(p - a);
            int pb = Math.Abs(p - b);
            int pc = Math.Abs(p - c);
            if (pa <= pb && pa <= pc)
                return a;
            return pb <= pc ? b : c;
        }

        /// <summary>Receives unfiltered rows and writes normalized samples into planes.</summary>
        sealed class DecodeTarget
        {
            readonly int m_Width;
            readonly int m_Height;
            readonly int m_ColorType;
            readonly int m_BitDepth;
            readonly int m_Mask;
            readonly float[] m_Lut;
            readonly float[] m_R, m_G, m_B, m_A;
            readonly float[] m_PalR, m_PalG, m_PalB, m_PalA;
            readonly int m_KeyR = -1, m_KeyG = -1, m_KeyB = -1, m_KeyGray = -1;

            public DecodeTarget(int width, int height, int colorType, int bitDepth, byte[] palette, byte[] trns)
            {
                m_Width = width;
                m_Height = height;
                m_ColorType = colorType;
                m_BitDepth = bitDepth;
                m_Mask = (1 << bitDepth) - 1;
                int count = width * height;

                m_Lut = Lut(colorType == 3 ? 8 : bitDepth);

                bool gray = colorType == 0 || colorType == 4;
                m_R = new float[count];
                if (!gray)
                {
                    m_G = new float[count];
                    m_B = new float[count];
                }

                bool alpha = colorType == 4 || colorType == 6 || trns != null;
                if (alpha)
                    m_A = new float[count];

                if (colorType == 3)
                {
                    int entries = palette.Length / 3;
                    m_PalR = new float[256];
                    m_PalG = new float[256];
                    m_PalB = new float[256];
                    m_PalA = new float[256];
                    var lut8 = Lut(8);
                    for (int i = 0; i < 256; i++)
                    {
                        if (i < entries)
                        {
                            m_PalR[i] = lut8[palette[i * 3]];
                            m_PalG[i] = lut8[palette[i * 3 + 1]];
                            m_PalB[i] = lut8[palette[i * 3 + 2]];
                        }
                        m_PalA[i] = trns != null && i < trns.Length ? lut8[trns[i]] : 1f;
                    }
                }
                else if (trns != null)
                {
                    if (colorType == 0 && trns.Length >= 2)
                        m_KeyGray = ((trns[0] << 8) | trns[1]) & m_Mask;
                    else if (colorType == 2 && trns.Length >= 6)
                    {
                        m_KeyR = ((trns[0] << 8) | trns[1]) & m_Mask;
                        m_KeyG = ((trns[2] << 8) | trns[3]) & m_Mask;
                        m_KeyB = ((trns[4] << 8) | trns[5]) & m_Mask;
                    }
                }
            }

            static float[] Lut(int bitDepth)
            {
                int max = (1 << bitDepth) - 1;
                var lut = new float[max + 1];
                for (int i = 0; i <= max; i++)
                    lut[i] = i / (float)max;
                return lut;
            }

            int Sample(byte[] d, int row, int index)
            {
                switch (m_BitDepth)
                {
                    case 8:
                        return d[row + index];
                    case 16:
                        return (d[row + index * 2] << 8) | d[row + index * 2 + 1];
                    default:
                        int bit = index * m_BitDepth;
                        int shift = 8 - m_BitDepth - (bit & 7);
                        return (d[row + (bit >> 3)] >> shift) & m_Mask;
                }
            }

            public void WriteRow(byte[] d, int row, int pixels, int y, int x0, int dx, int channels)
            {
                int baseIndex = y * m_Width;
                // Fast path: contiguous row, samples map 1:1 to planes (no palette, no color key alpha).
                bool alphaInData = m_ColorType == 4 || m_ColorType == 6;
                if (dx == 1 && x0 == 0 && m_ColorType != 3 && (alphaInData || m_A == null))
                {
                    if (m_BitDepth == 8)
                    {
                        WriteRow8(d, row, pixels, baseIndex, channels);
                        return;
                    }
                    if (m_BitDepth == 16)
                    {
                        WriteRow16(d, row, pixels, baseIndex, channels);
                        return;
                    }
                }
                for (int i = 0; i < pixels; i++)
                {
                    int dst = baseIndex + x0 + i * dx;
                    int s = i * channels;
                    switch (m_ColorType)
                    {
                        case 0:
                        {
                            int v = Sample(d, row, s);
                            m_R[dst] = m_Lut[v];
                            if (m_A != null)
                                m_A[dst] = v == m_KeyGray ? 0f : 1f;
                            break;
                        }
                        case 2:
                        {
                            int r = Sample(d, row, s), g = Sample(d, row, s + 1), b = Sample(d, row, s + 2);
                            m_R[dst] = m_Lut[r];
                            m_G[dst] = m_Lut[g];
                            m_B[dst] = m_Lut[b];
                            if (m_A != null)
                                m_A[dst] = r == m_KeyR && g == m_KeyG && b == m_KeyB ? 0f : 1f;
                            break;
                        }
                        case 3:
                        {
                            int index = Sample(d, row, s);
                            m_R[dst] = m_PalR[index];
                            m_G[dst] = m_PalG[index];
                            m_B[dst] = m_PalB[index];
                            if (m_A != null)
                                m_A[dst] = m_PalA[index];
                            break;
                        }
                        case 4:
                            m_R[dst] = m_Lut[Sample(d, row, s)];
                            m_A[dst] = m_Lut[Sample(d, row, s + 1)];
                            break;
                        default:
                            m_R[dst] = m_Lut[Sample(d, row, s)];
                            m_G[dst] = m_Lut[Sample(d, row, s + 1)];
                            m_B[dst] = m_Lut[Sample(d, row, s + 2)];
                            m_A[dst] = m_Lut[Sample(d, row, s + 3)];
                            break;
                    }
                }
            }

            void WriteRow8(byte[] d, int row, int pixels, int dst, int channels)
            {
                var lut = m_Lut;
                switch (channels)
                {
                    case 1:
                        for (int i = 0; i < pixels; i++)
                            m_R[dst + i] = lut[d[row + i]];
                        break;
                    case 2:
                        for (int i = 0, o = row; i < pixels; i++, o += 2)
                        {
                            m_R[dst + i] = lut[d[o]];
                            m_A[dst + i] = lut[d[o + 1]];
                        }
                        break;
                    case 3:
                        for (int i = 0, o = row; i < pixels; i++, o += 3)
                        {
                            m_R[dst + i] = lut[d[o]];
                            m_G[dst + i] = lut[d[o + 1]];
                            m_B[dst + i] = lut[d[o + 2]];
                        }
                        break;
                    default:
                        for (int i = 0, o = row; i < pixels; i++, o += 4)
                        {
                            m_R[dst + i] = lut[d[o]];
                            m_G[dst + i] = lut[d[o + 1]];
                            m_B[dst + i] = lut[d[o + 2]];
                            m_A[dst + i] = lut[d[o + 3]];
                        }
                        break;
                }
            }

            void WriteRow16(byte[] d, int row, int pixels, int dst, int channels)
            {
                var lut = m_Lut;
                for (int i = 0, o = row; i < pixels; i++)
                {
                    int p = dst + i;
                    m_R[p] = lut[(d[o] << 8) | d[o + 1]];
                    o += 2;
                    if (channels == 2)
                    {
                        m_A[p] = lut[(d[o] << 8) | d[o + 1]];
                        o += 2;
                        continue;
                    }
                    if (channels >= 3)
                    {
                        m_G[p] = lut[(d[o] << 8) | d[o + 1]];
                        m_B[p] = lut[(d[o + 2] << 8) | d[o + 3]];
                        o += 4;
                    }
                    if (channels == 4)
                    {
                        m_A[p] = lut[(d[o] << 8) | d[o + 1]];
                        o += 2;
                    }
                }
            }

            public PixelImage Build()
            {
                var precision = m_BitDepth == 16 ? SamplePrecision.UNorm16 : SamplePrecision.UNorm8;
                if (m_G == null)
                    return PixelImage.Gray(m_Width, m_Height, m_R, m_A, precision);
                return new PixelImage(m_Width, m_Height, m_R, m_G, m_B, m_A, precision);
            }
        }

        // ------------------------------------------------------------------ encode

        /// <summary>Encodes an image as PNG. <paramref name="bitDepth"/> is 8 or 16. Missing alpha is written as opaque.</summary>
        public static byte[] Encode(PixelImage image, OutputLayout layout, int bitDepth, CompressionLevel level = CompressionLevel.Optimal)
        {
            if (image == null)
                throw new ArgumentNullException(nameof(image));
            if (bitDepth != 8 && bitDepth != 16)
                throw new ArgumentException("PNG bit depth must be 8 or 16.", nameof(bitDepth));

            int colorType;
            float[][] planes;
            switch (layout)
            {
                case OutputLayout.Grayscale:
                    colorType = 0;
                    planes = new[] { image.R };
                    break;
                case OutputLayout.GrayscaleAlpha:
                    colorType = 4;
                    planes = new[] { image.R, image.A };
                    break;
                case OutputLayout.RGB:
                    colorType = 2;
                    planes = new[] { image.R, image.G, image.B };
                    break;
                default:
                    colorType = 6;
                    planes = new[] { image.R, image.G, image.B, image.A };
                    break;
            }

            int width = image.Width;
            int height = image.Height;
            int channels = planes.Length;
            int bytesPerSample = bitDepth / 8;
            int bpp = channels * bytesPerSample;
            int rowBytes = width * bpp;
            int maxValue = bitDepth == 16 ? 65535 : 255;

            if ((long)height * (rowBytes + 1) > int.MaxValue)
                throw new PackException("Image is too large for a single PNG buffer.");

            // Quantize into unfiltered scanlines.
            var raw = new byte[height * rowBytes];
            Parallel.For(0, height, y =>
            {
                int src = y * width;
                int dst = y * rowBytes;
                for (int x = 0; x < width; x++)
                {
                    for (int c = 0; c < channels; c++)
                    {
                        var plane = planes[c];
                        int q = plane == null ? maxValue : ColorMath.Quantize(plane[src + x], maxValue);
                        if (bytesPerSample == 1)
                        {
                            raw[dst++] = (byte)q;
                        }
                        else
                        {
                            raw[dst++] = (byte)(q >> 8);
                            raw[dst++] = (byte)q;
                        }
                    }
                }
            });

            // Adaptive filtering: pick the filter with the smallest sum of absolute differences per row.
            int stride = rowBytes + 1;
            var filtered = new byte[height * stride];
            Parallel.For(0, height,
                () => new[] { new byte[rowBytes], new byte[rowBytes], new byte[rowBytes], new byte[rowBytes], new byte[rowBytes] },
                (y, state, buffers) =>
                {
                    int cur = y * rowBytes;
                    int prev = y > 0 ? cur - rowBytes : -1;
                    long bestScore = long.MaxValue;
                    int bestFilter = 0;
                    for (int f = 0; f < 5; f++)
                    {
                        long score = ApplyFilter(raw, cur, prev, rowBytes, bpp, f, buffers[f]);
                        if (score < bestScore)
                        {
                            bestScore = score;
                            bestFilter = f;
                        }
                    }
                    filtered[y * stride] = (byte)bestFilter;
                    Buffer.BlockCopy(buffers[bestFilter], 0, filtered, y * stride + 1, rowBytes);
                    return buffers;
                },
                _ => { });

            byte[] compressed = Zlib.Compress(filtered, 0, filtered.Length, level);

            using (var output = new MemoryStream(compressed.Length + 128))
            {
                output.Write(s_Signature, 0, 8);

                var header = new byte[13];
                WriteInt32BE(header, 0, width);
                WriteInt32BE(header, 4, height);
                header[8] = (byte)bitDepth;
                header[9] = (byte)colorType;
                WriteChunk(output, "IHDR", header, 0, header.Length);

                const int chunkSize = 1 << 20;
                for (int offset = 0; offset < compressed.Length; offset += chunkSize)
                    WriteChunk(output, "IDAT", compressed, offset, Math.Min(chunkSize, compressed.Length - offset));
                if (compressed.Length == 0)
                    WriteChunk(output, "IDAT", compressed, 0, 0);

                WriteChunk(output, "IEND", Array.Empty<byte>(), 0, 0);
                return output.ToArray();
            }
        }

        static long ApplyFilter(byte[] raw, int cur, int prev, int n, int bpp, int filter, byte[] dst)
        {
            long score = 0;
            for (int i = 0; i < n; i++)
            {
                int x = raw[cur + i];
                int left = i >= bpp ? raw[cur + i - bpp] : 0;
                int up = prev >= 0 ? raw[prev + i] : 0;
                int upLeft = i >= bpp && prev >= 0 ? raw[prev + i - bpp] : 0;
                int predicted;
                switch (filter)
                {
                    case 0: predicted = 0; break;
                    case 1: predicted = left; break;
                    case 2: predicted = up; break;
                    case 3: predicted = (left + up) >> 1; break;
                    default: predicted = Paeth(left, up, upLeft); break;
                }
                byte value = (byte)(x - predicted);
                dst[i] = value;
                score += value < 128 ? value : 256 - value;
            }
            return score;
        }

        // ------------------------------------------------------------------ helpers

        static void WriteChunk(Stream stream, string type, byte[] data, int offset, int length)
        {
            var header = new byte[8];
            WriteInt32BE(header, 0, length);
            Encoding.ASCII.GetBytes(type, 0, 4, header, 4);
            stream.Write(header, 0, 8);
            stream.Write(data, offset, length);
            uint crc = Zlib.Crc32(header, 4, 4);
            crc = Zlib.Crc32(data, offset, length, crc);
            var crcBytes = new byte[4];
            WriteInt32BE(crcBytes, 0, (int)crc);
            stream.Write(crcBytes, 0, 4);
        }

        static byte[] Slice(byte[] data, int start, int length)
        {
            var result = new byte[length];
            Buffer.BlockCopy(data, start, result, 0, length);
            return result;
        }

        internal static int ReadInt32BE(byte[] d, int p)
        {
            return (d[p] << 24) | (d[p + 1] << 16) | (d[p + 2] << 8) | d[p + 3];
        }

        static void WriteInt32BE(byte[] d, int p, int v)
        {
            d[p] = (byte)(v >> 24);
            d[p + 1] = (byte)(v >> 16);
            d[p + 2] = (byte)(v >> 8);
            d[p + 3] = (byte)v;
        }
    }
}
