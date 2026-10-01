using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace DAVFX.TextureChannelPacker.Codecs
{
    /// <summary>
    /// Lossless TGA reader and writer.
    /// Reads color-mapped, true color and gray images, raw or RLE, 8/15/16/24/32 bits, any origin.
    /// Writes 8-bit gray, 24-bit or 32-bit, raw or RLE, bottom-left origin (the most compatible).
    /// </summary>
    public static class TgaCodec
    {
        // ------------------------------------------------------------------ decode

        public static PixelImage Decode(byte[] data)
        {
            if (data == null || data.Length < 18)
                throw new PackException("TGA file is truncated.");
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
                throw new PackException($"The TGA file is damaged ({e.GetType().Name}).", e);
            }
        }

        static PixelImage DecodeChecked(byte[] data)
        {

            int idLength = data[0];
            int colorMapType = data[1];
            int imageType = data[2];
            int mapFirst = U16(data, 3);
            int mapLength = U16(data, 5);
            int mapEntryBits = data[7];
            int width = U16(data, 12);
            int height = U16(data, 14);
            int depth = data[16];
            int descriptor = data[17];

            int baseType = imageType & 7;
            bool rle = (imageType & 8) != 0;
            if (baseType != 1 && baseType != 2 && baseType != 3)
                throw new PackException($"Unsupported TGA image type {imageType}.");
            if (width <= 0 || height <= 0)
                throw new PackException($"Invalid TGA size {width}x{height}.");
            PngCodec.CheckSize("TGA", width, height);

            int alphaBits = descriptor & 0x0F;
            bool rightToLeft = (descriptor & 0x10) != 0;
            bool topToBottom = (descriptor & 0x20) != 0;

            int pos = 18 + idLength;

            // Color map.
            float[] palR = null, palG = null, palB = null, palA = null;
            bool paletteHasAlpha = false;
            if (colorMapType == 1)
            {
                int entryBytes = (mapEntryBits + 7) / 8;
                int total = mapFirst + mapLength;
                palR = new float[Math.Max(total, 1)];
                palG = new float[palR.Length];
                palB = new float[palR.Length];
                palA = new float[palR.Length];
                for (int i = 0; i < mapLength; i++)
                {
                    int p = pos + i * entryBytes;
                    if (p + entryBytes > data.Length)
                        throw new PackException("TGA color map is truncated.");
                    ReadColor(data, p, mapEntryBits, alphaBits, out palR[mapFirst + i], out palG[mapFirst + i], out palB[mapFirst + i], out palA[mapFirst + i], out bool hasA);
                    paletteHasAlpha |= hasA;
                }
                pos += mapLength * entryBytes;
            }
            else if (baseType == 1)
            {
                throw new PackException("Color-mapped TGA without a color map.");
            }

            bool depthValid;
            switch (baseType)
            {
                case 1: depthValid = depth == 8 || depth == 16; break;
                case 2: depthValid = depth == 15 || depth == 16 || depth == 24 || depth == 32; break;
                default: depthValid = depth == 8 || depth == 16; break;
            }
            if (!depthValid)
                throw new PackException($"Unsupported TGA pixel depth {depth} for image type {imageType}.");
            int pixelBytes = (depth + 7) / 8;

            int count = width * height;
            byte[] pixels = new byte[count * pixelBytes];
            if (rle)
                DecodeRle(data, pos, pixels, pixelBytes);
            else
            {
                if (pos + pixels.Length > data.Length)
                    throw new PackException("TGA pixel data is truncated.");
                Buffer.BlockCopy(data, pos, pixels, 0, pixels.Length);
            }

            bool gray = baseType == 3;
            var r = new float[count];
            float[] g = gray ? r : new float[count];
            float[] b = gray ? r : new float[count];
            var a = new float[count];
            bool hasAlpha;

            switch (baseType)
            {
                case 3:
                    hasAlpha = depth == 16;
                    break;
                case 1:
                    hasAlpha = paletteHasAlpha;
                    break;
                default:
                    hasAlpha = depth == 32 || (depth == 16 && alphaBits > 0);
                    break;
            }

            var lut = new float[256];
            for (int i = 0; i < 256; i++)
                lut[i] = i / 255f;

            int nonZeroAlpha = 0;
            Parallel.For(0, height, fileRow =>
            {
                int y = topToBottom ? fileRow : height - 1 - fileRow;
                int rowStart = y * width;
                int src = fileRow * width * pixelBytes;
                bool rowAlpha = false;
                for (int fileCol = 0; fileCol < width; fileCol++, src += pixelBytes)
                {
                    int dst = rowStart + (rightToLeft ? width - 1 - fileCol : fileCol);
                    float va;
                    if (baseType == 2 && depth >= 24)
                    {
                        b[dst] = lut[pixels[src]];
                        g[dst] = lut[pixels[src + 1]];
                        r[dst] = lut[pixels[src + 2]];
                        va = depth == 32 ? lut[pixels[src + 3]] : 1f;
                    }
                    else if (baseType == 3)
                    {
                        r[dst] = lut[pixels[src]];
                        va = depth == 16 ? lut[pixels[src + 1]] : 1f;
                    }
                    else if (baseType == 1)
                    {
                        int index = pixelBytes == 1 ? pixels[src] : pixels[src] | (pixels[src + 1] << 8);
                        bool valid = index < palR.Length;
                        r[dst] = valid ? palR[index] : 0f;
                        g[dst] = valid ? palG[index] : 0f;
                        b[dst] = valid ? palB[index] : 0f;
                        va = valid ? palA[index] : 1f;
                    }
                    else
                    {
                        ReadColor(pixels, src, depth, alphaBits, out r[dst], out g[dst], out b[dst], out va, out _);
                    }
                    a[dst] = va;
                    rowAlpha |= va != 0f;
                }
                if (rowAlpha)
                    System.Threading.Interlocked.Exchange(ref nonZeroAlpha, 1);
            });
            bool anyAlphaNonZero = nonZeroAlpha != 0;

            // Many writers store 32-bit pixels with an unused zero alpha and no alpha bits declared.
            if (hasAlpha && depth == 32 && baseType == 2 && alphaBits == 0 && !anyAlphaNonZero)
                hasAlpha = false;

            var image = new PixelImage(width, height, r, g, b, hasAlpha ? a : null, SamplePrecision.UNorm8);
            image.SourceDescription = $"TGA {depth}-bit {(rle ? "RLE " : string.Empty)}{image.LayoutName}";
            return image;
        }

        static void ReadColor(byte[] d, int p, int bits, int alphaBits, out float r, out float g, out float b, out float a, out bool hasAlpha)
        {
            switch (bits)
            {
                case 8:
                    r = g = b = d[p] / 255f;
                    a = 1f;
                    hasAlpha = false;
                    return;
                case 15:
                case 16:
                {
                    int v = d[p] | (d[p + 1] << 8);
                    r = ((v >> 10) & 31) / 31f;
                    g = ((v >> 5) & 31) / 31f;
                    b = (v & 31) / 31f;
                    hasAlpha = bits == 16 && alphaBits > 0;
                    a = hasAlpha ? ((v >> 15) & 1) : 1f;
                    return;
                }
                case 24:
                    b = d[p] / 255f;
                    g = d[p + 1] / 255f;
                    r = d[p + 2] / 255f;
                    a = 1f;
                    hasAlpha = false;
                    return;
                case 32:
                    b = d[p] / 255f;
                    g = d[p + 1] / 255f;
                    r = d[p + 2] / 255f;
                    a = d[p + 3] / 255f;
                    hasAlpha = true;
                    return;
                default:
                    throw new PackException($"Unsupported TGA color depth {bits}.");
            }
        }

        static void DecodeRle(byte[] data, int pos, byte[] pixels, int pixelBytes)
        {
            int written = 0;
            while (written < pixels.Length)
            {
                if (pos >= data.Length)
                    throw new PackException("TGA RLE data is truncated.");
                int header = data[pos++];
                int run = (header & 0x7F) + 1;
                if ((header & 0x80) != 0)
                {
                    if (pos + pixelBytes > data.Length)
                        throw new PackException("TGA RLE data is truncated.");
                    for (int i = 0; i < run && written < pixels.Length; i++)
                    {
                        Buffer.BlockCopy(data, pos, pixels, written, pixelBytes);
                        written += pixelBytes;
                    }
                    pos += pixelBytes;
                }
                else
                {
                    int bytes = Math.Min(run * pixelBytes, pixels.Length - written);
                    if (pos + bytes > data.Length)
                        throw new PackException("TGA RLE data is truncated.");
                    Buffer.BlockCopy(data, pos, pixels, written, bytes);
                    written += bytes;
                    pos += run * pixelBytes;
                }
            }
        }

        // ------------------------------------------------------------------ encode

        /// <summary>Encodes an image as 8-bit per channel TGA. Missing alpha is written as opaque.</summary>
        public static byte[] Encode(PixelImage image, OutputLayout layout, bool rle)
        {
            if (image == null)
                throw new ArgumentNullException(nameof(image));
            if (image.Width > 65535 || image.Height > 65535)
                throw new PackException("TGA supports at most 65535 pixels per side.");

            int width = image.Width;
            int height = image.Height;
            int imageType;
            int depth;
            int descriptor;
            switch (layout)
            {
                case OutputLayout.Grayscale:
                    imageType = 3;
                    depth = 8;
                    descriptor = 0;
                    break;
                case OutputLayout.RGB:
                    imageType = 2;
                    depth = 24;
                    descriptor = 0;
                    break;
                default:
                    // RGBA and Gray+Alpha (written as BGRA with equal color channels).
                    imageType = 2;
                    depth = 32;
                    descriptor = 8;
                    break;
            }
            if (rle)
                imageType |= 8;
            int pixelBytes = depth / 8;

            using (var output = new MemoryStream(width * height * pixelBytes + 64))
            {
                var header = new byte[18];
                header[2] = (byte)imageType;
                header[12] = (byte)width;
                header[13] = (byte)(width >> 8);
                header[14] = (byte)height;
                header[15] = (byte)(height >> 8);
                header[16] = (byte)depth;
                header[17] = (byte)descriptor;
                output.Write(header, 0, header.Length);

                var row = new byte[width * pixelBytes];
                bool gray = layout == OutputLayout.Grayscale || layout == OutputLayout.GrayscaleAlpha;
                for (int fileRow = 0; fileRow < height; fileRow++)
                {
                    int y = height - 1 - fileRow;
                    int src = y * width;
                    for (int x = 0; x < width; x++)
                    {
                        int o = x * pixelBytes;
                        int i = src + x;
                        if (depth == 8)
                        {
                            row[o] = Q(image.R[i]);
                            continue;
                        }
                        byte r = Q(image.R[i]);
                        row[o] = gray ? r : Q(image.B[i]);
                        row[o + 1] = gray ? r : Q(image.G[i]);
                        row[o + 2] = r;
                        if (depth == 32)
                            row[o + 3] = image.A == null ? (byte)255 : Q(image.A[i]);
                    }

                    if (rle)
                        WriteRleRow(output, row, width, pixelBytes);
                    else
                        output.Write(row, 0, row.Length);
                }

                // TGA 2.0 footer with no extension or developer area.
                output.Write(new byte[8], 0, 8);
                var signature = Encoding.ASCII.GetBytes("TRUEVISION-XFILE.\0");
                output.Write(signature, 0, signature.Length);
                return output.ToArray();
            }
        }

        static byte Q(float value)
        {
            return (byte)ColorMath.Quantize(value, 255);
        }

        static bool SamePixel(byte[] row, int a, int b, int pixelBytes)
        {
            int pa = a * pixelBytes;
            int pb = b * pixelBytes;
            for (int k = 0; k < pixelBytes; k++)
            {
                if (row[pa + k] != row[pb + k])
                    return false;
            }
            return true;
        }

        static void WriteRleRow(Stream output, byte[] row, int count, int pixelBytes)
        {
            int i = 0;
            while (i < count)
            {
                int run = 1;
                while (i + run < count && run < 128 && SamePixel(row, i, i + run, pixelBytes))
                    run++;
                if (run >= 2)
                {
                    output.WriteByte((byte)(0x80 | (run - 1)));
                    output.Write(row, i * pixelBytes, pixelBytes);
                    i += run;
                    continue;
                }

                int start = i;
                int raw = 0;
                while (i < count && raw < 128)
                {
                    if (i + 1 < count && SamePixel(row, i, i + 1, pixelBytes))
                        break;
                    i++;
                    raw++;
                }
                output.WriteByte((byte)(raw - 1));
                output.Write(row, start * pixelBytes, raw * pixelBytes);
            }
        }

        static int U16(byte[] d, int p)
        {
            return d[p] | (d[p + 1] << 8);
        }
    }
}
