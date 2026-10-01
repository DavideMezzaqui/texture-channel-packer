using System;
using System.Text;

namespace DAVFX.TextureChannelPacker.Codecs
{
    public sealed class ProbeResult
    {
        /// <summary>True when the header was understood.</summary>
        public bool Known;
        public int Width = -1;
        public int Height = -1;
        public SamplePrecision Precision = SamplePrecision.UNorm8;
        public string Format = "Unknown";

        public bool HasSize => Width > 0 && Height > 0;
    }

    /// <summary>
    /// Reads size and sample precision from file headers without decoding pixels.
    /// Used to pick a lossless read format and to verify what Unity's importer returned.
    /// </summary>
    public static class ImageProbe
    {
        public static ProbeResult Probe(byte[] data, string extension)
        {
            var result = new ProbeResult();
            if (data == null || data.Length < 16)
                return result;
            try
            {
                if (PngCodec.IsPng(data))
                    return ProbePng(data);
                if (data[0] == 0x76 && data[1] == 0x2F && data[2] == 0x31 && data[3] == 0x01)
                    return ProbeExr(data);
                if (data[0] == '8' && data[1] == 'B' && data[2] == 'P' && data[3] == 'S')
                    return ProbePsd(data);
                if ((data[0] == 'I' && data[1] == 'I' && data[2] == 42 && data[3] == 0) ||
                    (data[0] == 'M' && data[1] == 'M' && data[2] == 0 && data[3] == 42))
                    return ProbeTiff(data);
                if (data[0] == '#' && data[1] == '?')
                    return ProbeHdr(data);
                if (data[0] == 'B' && data[1] == 'M')
                    return ProbeBmp(data);
                if (data[0] == 'G' && data[1] == 'I' && data[2] == 'F')
                    return new ProbeResult { Known = true, Format = "GIF", Width = data[6] | (data[7] << 8), Height = data[8] | (data[9] << 8) };
            }
            catch (IndexOutOfRangeException)
            {
                // Truncated or unusual header: report unknown.
            }
            catch (ArgumentException)
            {
            }
            result.Format = string.IsNullOrEmpty(extension) ? "Unknown" : extension.TrimStart('.').ToUpperInvariant();
            return result;
        }

        static ProbeResult ProbePng(byte[] d)
        {
            int depth = d[24];
            return new ProbeResult
            {
                Known = true,
                Format = "PNG",
                Width = PngCodec.ReadInt32BE(d, 16),
                Height = PngCodec.ReadInt32BE(d, 20),
                Precision = depth == 16 ? SamplePrecision.UNorm16 : SamplePrecision.UNorm8
            };
        }

        static ProbeResult ProbeExr(byte[] d)
        {
            var result = new ProbeResult { Known = true, Format = "EXR", Precision = SamplePrecision.Half };
            int pos = 8;
            bool anyFloat = false;
            bool anyChannel = false;
            while (pos < d.Length)
            {
                string name = ReadCString(d, ref pos);
                if (name.Length == 0)
                    break;
                string type = ReadCString(d, ref pos);
                int size = BitConverter.ToInt32(d, pos);
                pos += 4;
                if (size < 0 || size > d.Length - pos)
                    break;
                int valueStart = pos;
                if (type == "chlist")
                {
                    int p = valueStart;
                    while (p < valueStart + size)
                    {
                        string channel = ReadCString(d, ref p);
                        if (channel.Length == 0)
                            break;
                        int pixelType = BitConverter.ToInt32(d, p);
                        p += 16;
                        anyChannel = true;
                        if (pixelType != 1)
                            anyFloat = true; // 0 = uint, 2 = float: both need 32-bit floats to be exact.
                    }
                }
                else if (name == "dataWindow" && type == "box2i")
                {
                    int xMin = BitConverter.ToInt32(d, valueStart);
                    int yMin = BitConverter.ToInt32(d, valueStart + 4);
                    int xMax = BitConverter.ToInt32(d, valueStart + 8);
                    int yMax = BitConverter.ToInt32(d, valueStart + 12);
                    result.Width = xMax - xMin + 1;
                    result.Height = yMax - yMin + 1;
                }
                pos = valueStart + size;
            }
            if (anyFloat || !anyChannel)
                result.Precision = SamplePrecision.Float;
            return result;
        }

        static ProbeResult ProbePsd(byte[] d)
        {
            int depth = (d[22] << 8) | d[23];
            return new ProbeResult
            {
                Known = true,
                Format = "PSD",
                Height = PngCodec.ReadInt32BE(d, 14),
                Width = PngCodec.ReadInt32BE(d, 18),
                Precision = depth == 32 ? SamplePrecision.Float : depth == 16 ? SamplePrecision.UNorm16 : SamplePrecision.UNorm8
            };
        }

        static ProbeResult ProbeTiff(byte[] d)
        {
            bool little = d[0] == 'I';
            int ifd = (int)U32(d, 4, little);
            int entries = U16(d, ifd, little);
            int width = -1, height = -1, bits = 8, sampleFormat = 1;
            for (int i = 0; i < entries; i++)
            {
                int e = ifd + 2 + i * 12;
                int tag = U16(d, e, little);
                int type = U16(d, e + 2, little);
                int count = (int)U32(d, e + 4, little);
                switch (tag)
                {
                    case 256: width = TiffValue(d, e, type, little); break;
                    case 257: height = TiffValue(d, e, type, little); break;
                    case 258:
                        if (count * 2 <= 4)
                            bits = U16(d, e + 8, little);
                        else
                            bits = U16(d, (int)U32(d, e + 8, little), little);
                        break;
                    case 339:
                        if (count * 2 <= 4)
                            sampleFormat = U16(d, e + 8, little);
                        else
                            sampleFormat = U16(d, (int)U32(d, e + 8, little), little);
                        break;
                }
            }

            SamplePrecision precision;
            if (sampleFormat == 3)
                precision = bits <= 16 ? SamplePrecision.Half : SamplePrecision.Float;
            else if (bits > 16)
                precision = SamplePrecision.Float;
            else if (bits > 8)
                precision = SamplePrecision.UNorm16;
            else
                precision = SamplePrecision.UNorm8;

            return new ProbeResult { Known = true, Format = "TIFF", Width = width, Height = height, Precision = precision };
        }

        static int TiffValue(byte[] d, int entry, int type, bool little)
        {
            return type == 3 ? U16(d, entry + 8, little) : (int)U32(d, entry + 8, little);
        }

        static ProbeResult ProbeHdr(byte[] d)
        {
            var result = new ProbeResult { Known = true, Format = "HDR", Precision = SamplePrecision.Float };
            int limit = Math.Min(d.Length, 4096);
            string header = Encoding.ASCII.GetString(d, 0, limit);
            string[] lines = header.Split('\n');
            bool blank = false;
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (!blank)
                {
                    if (line.Length == 0)
                        blank = true;
                    continue;
                }
                string[] parts = line.Split(' ');
                if (parts.Length == 4)
                {
                    int a, b;
                    if (int.TryParse(parts[1], out a) && int.TryParse(parts[3], out b))
                    {
                        bool yFirst = parts[0].EndsWith("Y");
                        result.Height = yFirst ? a : b;
                        result.Width = yFirst ? b : a;
                    }
                }
                break;
            }
            return result;
        }

        static ProbeResult ProbeBmp(byte[] d)
        {
            return new ProbeResult
            {
                Known = true,
                Format = "BMP",
                Width = BitConverter.ToInt32(d, 18),
                Height = Math.Abs(BitConverter.ToInt32(d, 22))
            };
        }

        static string ReadCString(byte[] d, ref int pos)
        {
            int start = pos;
            while (pos < d.Length && d[pos] != 0)
                pos++;
            string value = Encoding.ASCII.GetString(d, start, pos - start);
            pos++;
            return value;
        }

        static int U16(byte[] d, int p, bool little)
        {
            return little ? d[p] | (d[p + 1] << 8) : (d[p] << 8) | d[p + 1];
        }

        static uint U32(byte[] d, int p, bool little)
        {
            return little
                ? (uint)(d[p] | (d[p + 1] << 8) | (d[p + 2] << 16) | (d[p + 3] << 24))
                : (uint)((d[p] << 24) | (d[p + 1] << 16) | (d[p + 2] << 8) | d[p + 3]);
        }
    }
}
