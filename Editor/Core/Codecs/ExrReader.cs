using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace DAVFX.TextureChannelPacker.Codecs
{
    /// <summary>
    /// Lossless OpenEXR reader for the files most tools write: single part scanline images with half or float
    /// channels and NONE, RLE, ZIPS, ZIP or PIZ compression. Values are returned exactly as stored.
    /// Other variants (tiled, multi-part, deep, lossy compressions, subsampled or layer-only channels) throw an
    /// unsupported <see cref="PackException"/> so the caller can fall back to Unity's importer.
    /// The RLE, ZIP and PIZ decoders are ports of the OpenEXR reference implementation
    /// (BSD-3-Clause, Copyright Contributors to the OpenEXR Project, see Third Party Notices.md).
    /// </summary>
    public static class ExrReader
    {
        const int VersionTiled = 0x200;
        const int VersionLongNames = 0x400;
        const int VersionDeep = 0x800;
        const int VersionMultipart = 0x1000;

        const int PixelUInt = 0;
        const int PixelHalf = 1;
        const int PixelFloat = 2;

        const int CompressionNone = 0;
        const int CompressionRle = 1;
        const int CompressionZips = 2;
        const int CompressionZip = 3;
        const int CompressionPiz = 4;

        /// <summary>Upper bound for one decompressed block (a 16384 wide RGBA float PIZ block is 8 MB).</summary>
        const int MaxBlockBytes = 1 << 27;

        /// <summary>Memory the worker buffers may use together; large blocks get fewer workers.</summary>
        const long ScratchBudget = 1L << 29;

        static readonly string[] s_CompressionNames =
        {
            "NONE", "RLE", "ZIPS", "ZIP", "PIZ", "PXR24", "B44", "B44A", "DWAA", "DWAB", "HTJ2K256", "HTJ2K32", "LJ2K", "ZSTD"
        };

        static readonly float[] s_HalfToFloat = BuildHalfTable();

        sealed class Channel
        {
            public string Name;
            public int Type;
            public int Size;
            public int XSampling;
            public int YSampling;
            public float[] Plane;
        }

        struct Window
        {
            public int XMin, YMin, XMax, YMax;

            public bool SameAs(Window other)
            {
                return XMin == other.XMin && YMin == other.YMin && XMax == other.XMax && YMax == other.YMax;
            }
        }

        /// <summary>Position and sizes of one block, checked before any pixel memory is allocated.</summary>
        struct Block
        {
            public int Start;
            public int Packed;
            public int FirstLine;
            public int Lines;
            public int Unpacked;
        }

        /// <summary>Per worker buffers, reused across the blocks a worker decodes.</summary>
        sealed class Scratch
        {
            public readonly byte[] Raw;
            public readonly byte[] Temp;
            public readonly Piz.Tables Piz;

            public Scratch(int blockBytes, int compression)
            {
                if (compression == CompressionNone)
                    return; // Blocks are read in place.
                Raw = new byte[blockBytes];
                if (compression == CompressionPiz)
                    Piz = new Piz.Tables(blockBytes / 2);
                else
                    Temp = new byte[blockBytes];
            }
        }

        public static bool IsExr(byte[] data)
        {
            return data != null && data.Length >= 4 && data[0] == 0x76 && data[1] == 0x2F && data[2] == 0x31 && data[3] == 0x01;
        }

        public static string CompressionName(int compression)
        {
            return compression >= 0 && compression < s_CompressionNames.Length ? s_CompressionNames[compression] : $"unknown ({compression})";
        }

        public static PixelImage Decode(byte[] data)
        {
            if (!IsExr(data))
                throw new PackException("Not an OpenEXR file.");
            try
            {
                return DecodeChecked(data);
            }
            catch (AggregateException e)
            {
                var inner = e.Flatten().InnerExceptions[0];
                if (inner is PackException pack)
                    throw pack.Wrap();
                if (inner is OutOfMemoryException)
                    throw inner;
                throw new PackException($"The EXR file is damaged ({inner.GetType().Name}).", inner);
            }
            catch (Exception e) when (!(e is PackException) && !(e is OutOfMemoryException))
            {
                throw new PackException($"The EXR file is damaged ({e.GetType().Name}).", e);
            }
        }

        // ------------------------------------------------------------------ header

        static PixelImage DecodeChecked(byte[] data)
        {
            if (data.Length < 8)
                throw Truncated();
            int version = ReadInt32(data, 4);
            if ((version & 0xFF) != 2)
                throw PackException.Unsupported($"OpenEXR file version {version & 0xFF} is not supported by the built-in reader.");
            if ((version & VersionMultipart) != 0)
                throw PackException.Unsupported("Multi-part EXR files are not supported by the built-in reader.");
            if ((version & VersionDeep) != 0)
                throw PackException.Unsupported("Deep EXR files are not supported by the built-in reader.");
            if ((version & VersionTiled) != 0)
                throw PackException.Unsupported("Tiled EXR files are not supported by the built-in reader.");
            if ((version & ~(0xFF | VersionLongNames)) != 0)
                throw PackException.Unsupported("This EXR file uses format flags the built-in reader does not know.");

            var channels = new List<Channel>();
            bool hasChannels = false;
            int compression = -1;
            int lineOrder = 0;
            Window? dataWindow = null;
            Window? displayWindow = null;

            int pos = 8;
            while (true)
            {
                string name = ReadString(data, ref pos, data.Length);
                if (name.Length == 0)
                    break;
                string type = ReadString(data, ref pos, data.Length);
                if (pos > data.Length - 4)
                    throw Truncated();
                int size = ReadInt32(data, pos);
                pos += 4;
                if (size < 0 || size > data.Length - pos)
                    throw Truncated();

                switch (name)
                {
                    case "channels":
                        RequireAttribute(name, type, "chlist", size, -1);
                        ReadChannels(data, pos, size, channels);
                        hasChannels = true;
                        break;
                    case "compression":
                        RequireAttribute(name, type, "compression", size, 1);
                        compression = data[pos];
                        break;
                    case "dataWindow":
                        RequireAttribute(name, type, "box2i", size, 16);
                        dataWindow = ReadWindow(data, pos);
                        break;
                    case "displayWindow":
                        RequireAttribute(name, type, "box2i", size, 16);
                        displayWindow = ReadWindow(data, pos);
                        break;
                    case "lineOrder":
                        RequireAttribute(name, type, "lineOrder", size, 1);
                        lineOrder = data[pos];
                        break;
                    case "type":
                        if (type == "string" && Encoding.ASCII.GetString(data, pos, size) != "scanlineimage")
                            throw PackException.Unsupported("This EXR file does not contain a scanline image, which the built-in reader requires.");
                        break;
                }
                pos += size;
            }

            if (!hasChannels || compression < 0 || dataWindow == null || displayWindow == null)
                throw new PackException("The EXR header is incomplete (channels, compression or data window missing).");
            if (compression > CompressionPiz)
                throw PackException.Unsupported($"EXR files with {CompressionName(compression)} compression are not supported by the built-in reader.");
            if (lineOrder > 1)
                throw new PackException($"Invalid EXR line order ({lineOrder}).");

            var window = dataWindow.Value;
            long widthL = (long)window.XMax - window.XMin + 1;
            long heightL = (long)window.YMax - window.YMin + 1;
            if (widthL <= 0 || heightL <= 0)
                throw new PackException($"Invalid EXR data window ({window.XMin}, {window.YMin}) - ({window.XMax}, {window.YMax}).");
            PngCodec.CheckSize("EXR", (int)Math.Min(widthL, int.MaxValue), (int)Math.Min(heightL, int.MaxValue));
            if (!window.SameAs(displayWindow.Value))
                throw PackException.Unsupported("EXR files whose data window differs from the display window are not supported by the built-in reader.");
            int width = (int)widthL;
            int height = (int)heightL;

            if (channels.Count == 0)
                throw new PackException("The EXR file has no channels.");
            for (int i = 0; i < channels.Count; i++)
            {
                var channel = channels[i];
                if (channel.XSampling != 1 || channel.YSampling != 1)
                    throw PackException.Unsupported("EXR files with subsampled channels (such as luminance/chroma images) are not supported by the built-in reader.");
                if (i > 0)
                {
                    int order = string.CompareOrdinal(channels[i - 1].Name, channel.Name);
                    if (order == 0)
                        throw new PackException($"The EXR channel '{channel.Name}' is listed twice.");
                    if (order > 0)
                        throw PackException.Unsupported("The EXR channel list is not sorted by name, which the built-in reader does not support.");
                }
            }

            var mapping = MapChannels(channels);

            long lineBytesL = 0;
            foreach (var channel in channels)
                lineBytesL += (long)channel.Size * width;
            int linesPerBlock = compression == CompressionZip ? 16 : compression == CompressionPiz ? 32 : 1;
            if (lineBytesL * Math.Min(linesPerBlock, height) > MaxBlockBytes)
                throw PackException.Unsupported("This EXR file has too many channels for the built-in reader.");
            int lineBytes = (int)lineBytesL;
            int blockBytes = lineBytes * Math.Min(linesPerBlock, height);

            // Every block is located and checked first: a damaged or truncated file fails here,
            // before the (possibly large) pixel planes are allocated.
            int blockCount = (height + linesPerBlock - 1) / linesPerBlock;
            if (pos > data.Length - 8L * blockCount)
                throw Truncated();
            long tableEnd = pos + 8L * blockCount;
            var blocks = new Block[blockCount];
            for (int i = 0; i < blockCount; i++)
                blocks[i] = Locate(data, ReadInt64(data, pos + 8 * i), tableEnd, i, linesPerBlock, window.YMin, height, lineBytes, compression);

            mapping.Allocate(width * height);
            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = (int)Math.Max(1, Math.Min(Environment.ProcessorCount, ScratchBudget / (2L * blockBytes + 1)))
            };
            Parallel.For(0, blockCount, options,
                () => new Scratch(blockBytes, compression),
                (block, state, scratch) =>
                {
                    DecodeBlock(data, blocks[block], width, lineBytes, compression, channels, scratch);
                    return scratch;
                },
                scratch => { });

            var image = mapping.CreateImage(width, height);
            string description = $"EXR {PixelImage.PrecisionName(image.Precision)} {image.LayoutName} ({CompressionName(compression)}";
            if (mapping.Ignored > 0)
                description += mapping.Ignored == 1 ? ", 1 other channel ignored" : $", {mapping.Ignored} other channels ignored";
            image.SourceDescription = description + ")";
            return image;
        }

        /// <summary>Which file channels feed the image, decided before decoding.</summary>
        sealed class Mapping
        {
            public Channel R, G, B, A, Y;
            public List<Channel> Used;
            public bool Color;
            public bool AnyFloat;
            public int Ignored;

            public void Allocate(int pixels)
            {
                foreach (var channel in Used)
                    channel.Plane = new float[pixels];
            }

            /// <summary>Builds the image once the planes are decoded.</summary>
            public PixelImage CreateImage(int width, int height)
            {
                var precision = AnyFloat ? SamplePrecision.Float : SamplePrecision.Half;
                float[] alpha = A?.Plane;
                if (!Color)
                    return PixelImage.Gray(width, height, Y?.Plane ?? new float[width * height], alpha, precision);

                // Gray images are written as R = G = B (see ExrWriter): read them back as gray. Compared bit by bit,
                // so -0 and +0 or different NaNs keep their own channels.
                if (R != null && G != null && B != null && SameBits(R.Plane, G.Plane) && SameBits(R.Plane, B.Plane))
                    return PixelImage.Gray(width, height, R.Plane, alpha, precision);

                float[] zero = null;
                float[] PlaneOf(Channel channel)
                {
                    return channel != null ? channel.Plane : zero ?? (zero = new float[width * height]);
                }
                return new PixelImage(width, height, PlaneOf(R), PlaneOf(G), PlaneOf(B), alpha, precision);
            }

            static bool SameBits(float[] a, float[] b)
            {
                for (int i = 0; i < a.Length; i++)
                {
                    if (HalfConverter.SingleToBits(a[i]) != HalfConverter.SingleToBits(b[i]))
                        return false;
                }
                return true;
            }
        }

        /// <summary>
        /// Maps the file channels to image planes. R, G, B and A are used, Y (luminance) when there is no color channel.
        /// Missing color channels read as 0 and missing alpha as opaque, like OpenEXR's RGBA interface.
        /// </summary>
        static Mapping MapChannels(List<Channel> channels)
        {
            var m = new Mapping
            {
                R = Find(channels, "R"),
                G = Find(channels, "G"),
                B = Find(channels, "B"),
                A = Find(channels, "A"),
                Y = Find(channels, "Y")
            };
            if (Find(channels, "RY") != null || Find(channels, "BY") != null)
                throw PackException.Unsupported("Luminance/chroma EXR files are not supported by the built-in reader.");
            m.Color = m.R != null || m.G != null || m.B != null;
            if (!m.Color && m.Y == null && m.A == null)
                throw PackException.Unsupported("This EXR file has no R, G, B, A or Y channel (for example only named layers), which the built-in reader does not support.");

            var used = new List<Channel>();
            if (m.Color)
            {
                if (m.R != null) used.Add(m.R);
                if (m.G != null) used.Add(m.G);
                if (m.B != null) used.Add(m.B);
                m.Y = null;
            }
            else if (m.Y != null)
            {
                used.Add(m.Y);
            }
            if (m.A != null)
                used.Add(m.A);

            foreach (var channel in used)
            {
                if (channel.Type == PixelUInt)
                    throw PackException.Unsupported($"The EXR channel '{channel.Name}' stores 32-bit integers, which the built-in reader does not support.");
                m.AnyFloat |= channel.Type == PixelFloat;
            }
            m.Used = used;
            m.Ignored = channels.Count - used.Count;
            return m;
        }

        static Channel Find(List<Channel> channels, string name)
        {
            foreach (var channel in channels)
            {
                if (channel.Name == name)
                    return channel;
            }
            return null;
        }

        static void ReadChannels(byte[] data, int start, int size, List<Channel> channels)
        {
            int end = start + size;
            int p = start;
            while (true)
            {
                string name = ReadString(data, ref p, end);
                if (name.Length == 0)
                    return;
                if (p > end - 16)
                    throw new PackException("The EXR channel list is damaged.");
                int type = ReadInt32(data, p);
                int xSampling = ReadInt32(data, p + 8);
                int ySampling = ReadInt32(data, p + 12);
                p += 16;
                if (type < PixelUInt || type > PixelFloat)
                    throw new PackException($"The EXR channel '{name}' has an unknown pixel type ({type}).");
                channels.Add(new Channel
                {
                    Name = name,
                    Type = type,
                    Size = type == PixelHalf ? 2 : 4,
                    XSampling = xSampling,
                    YSampling = ySampling
                });
            }
        }

        static void RequireAttribute(string name, string type, string expectedType, int size, int expectedSize)
        {
            if (type != expectedType || (expectedSize >= 0 && size != expectedSize))
                throw new PackException($"The EXR header attribute '{name}' is damaged.");
        }

        static Window ReadWindow(byte[] data, int pos)
        {
            return new Window
            {
                XMin = ReadInt32(data, pos),
                YMin = ReadInt32(data, pos + 4),
                XMax = ReadInt32(data, pos + 8),
                YMax = ReadInt32(data, pos + 12)
            };
        }

        // ------------------------------------------------------------------ blocks

        static Block Locate(byte[] data, long offset, long tableEnd, int index, int linesPerBlock, int yMin, int height, int lineBytes, int compression)
        {
            if (offset < tableEnd || offset > data.Length - 8)
                throw new PackException("The EXR offset table is damaged (the file may be truncated).");
            var block = new Block { Start = (int)offset + 8, FirstLine = index * linesPerBlock };
            if (ReadInt32(data, (int)offset) != (long)yMin + block.FirstLine)
                throw new PackException("The EXR offset table is damaged.");
            block.Packed = ReadInt32(data, (int)offset + 4);
            if (block.Packed <= 0 || block.Packed > data.Length - block.Start)
                throw Truncated();
            block.Lines = Math.Min(linesPerBlock, height - block.FirstLine);
            block.Unpacked = block.Lines * lineBytes;
            if (block.Packed > block.Unpacked)
                throw new PackException("An EXR data block is larger than its uncompressed size (damaged file).");
            if (compression == CompressionNone && block.Packed != block.Unpacked)
                throw new PackException("An uncompressed EXR data block has the wrong size (damaged file).");
            return block;
        }

        static void DecodeBlock(byte[] data, Block block, int width, int lineBytes, int compression, List<Channel> channels, Scratch scratch)
        {
            int start = block.Start;
            int packed = block.Packed;
            int unpacked = block.Unpacked;
            int lines = block.Lines;
            int firstLine = block.FirstLine;

            byte[] source;
            int sourceStart = 0;
            if (packed == unpacked)
            {
                // Stored as is: OpenEXR writes a block raw whenever compression would not make it smaller.
                source = data;
                sourceStart = start;
            }
            else
            {
                source = scratch.Raw;
                switch (compression)
                {
                    case CompressionRle:
                        RleDecompress(data, start, packed, scratch.Temp, unpacked);
                        Reconstruct(scratch.Temp, scratch.Raw, unpacked);
                        break;
                    case CompressionZips:
                    case CompressionZip:
                        Zlib.Decompress(data, start, packed, scratch.Temp, unpacked);
                        Reconstruct(scratch.Temp, scratch.Raw, unpacked);
                        break;
                    default:
                        Piz.Decompress(data, start, packed, scratch.Raw, unpacked, width, lines, channels, scratch.Piz);
                        break;
                }
            }

            Unpack(source, sourceStart, lines, firstLine, width, lineBytes, channels);
        }

        /// <summary>Converts line-major little-endian samples into the image planes.</summary>
        static void Unpack(byte[] source, int start, int lines, int firstLine, int width, int lineBytes, List<Channel> channels)
        {
            var half = s_HalfToFloat;
            for (int line = 0; line < lines; line++)
            {
                int p = start + line * lineBytes;
                int row = (firstLine + line) * width;
                foreach (var channel in channels)
                {
                    var plane = channel.Plane;
                    if (plane == null)
                    {
                        p += channel.Size * width;
                    }
                    else if (channel.Type == PixelHalf)
                    {
                        for (int x = 0; x < width; x++, p += 2)
                            plane[row + x] = half[source[p] | (source[p + 1] << 8)];
                    }
                    else
                    {
                        for (int x = 0; x < width; x++, p += 4)
                            plane[row + x] = HalfConverter.BitsToSingle((uint)(source[p] | (source[p + 1] << 8) | (source[p + 2] << 16) | (source[p + 3] << 24)));
                    }
                }
            }
        }

        /// <summary>OpenEXR RLE: a negative count is followed by that many literal bytes, otherwise one byte repeated count + 1 times.</summary>
        static void RleDecompress(byte[] data, int start, int count, byte[] output, int expected)
        {
            int i = start;
            int end = start + count;
            int o = 0;
            while (i < end)
            {
                int n = (sbyte)data[i++];
                if (n < 0)
                {
                    n = -n;
                    if (n > end - i || n > expected - o)
                        throw Corrupt();
                    Buffer.BlockCopy(data, i, output, o, n);
                    i += n;
                    o += n;
                }
                else
                {
                    if (i >= end)
                        throw Corrupt();
                    n++;
                    if (n > expected - o)
                        throw Corrupt();
                    byte value = data[i++];
                    for (int k = 0; k < n; k++)
                        output[o++] = value;
                }
            }
            if (o != expected)
                throw Corrupt();
        }

        /// <summary>Undoes the byte predictor and the even/odd byte split shared by RLE and ZIP.</summary>
        static void Reconstruct(byte[] split, byte[] output, int count)
        {
            for (int i = 1; i < count; i++)
                split[i] = (byte)(split[i - 1] + split[i] - 128);

            int t1 = 0;
            int t2 = (count + 1) / 2;
            int s = 0;
            while (s < count)
            {
                output[s++] = split[t1++];
                if (s < count)
                    output[s++] = split[t2++];
            }
        }

        // ------------------------------------------------------------------ PIZ (wavelet + Huffman)

        static class Piz
        {
            const int UShortRange = 1 << 16;
            const int BitmapSize = UShortRange >> 3;

            const int HufEncSize = (1 << 16) + 1;
            const int HufDecBits = 14;
            const int HufDecSize = 1 << HufDecBits;
            const int HufDecMask = HufDecSize - 1;
            const int ShortZeroCodeRun = 59;
            const int LongZeroCodeRun = 63;
            const int ShortestLongRun = 2 + LongZeroCodeRun - ShortZeroCodeRun;

            internal struct HufDec
            {
                /// <summary>Length of a short code, 0 otherwise.</summary>
                public int Len;
                /// <summary>Symbol of a short code.</summary>
                public int Lit;
                /// <summary>True when codes longer than the table start with these bits.</summary>
                public bool Long;
            }

            internal sealed class Tables
            {
                public readonly ushort[] Words;
                public readonly ushort[] Lut = new ushort[UShortRange];
                public readonly byte[] Bitmap = new byte[BitmapSize];
                public readonly ulong[] Codes = new ulong[HufEncSize];
                public readonly HufDec[] Decoder = new HufDec[HufDecSize];

                // Codes longer than HufDecBits, decoded canonically: the codes of one length are consecutive
                // numbers assigned in symbol order, so each length is a range [FirstCode, FirstCode + count).
                public readonly int[] LongLengths = new int[64];
                public int LongLengthCount;
                public readonly ulong[] FirstCode = new ulong[64];
                public readonly int[] LengthStart = new int[65];
                public readonly int[] SymbolsByLength = new int[HufEncSize];

                public Tables(int words)
                {
                    Words = new ushort[words];
                }
            }

            public static void Decompress(byte[] data, int start, int count, byte[] output, int unpacked, int width, int lines,
                List<Channel> channels, Tables tables)
            {
                int end = start + count;
                if (count < 4)
                    throw Corrupt();
                int minNonZero = data[start] | (data[start + 1] << 8);
                int maxNonZero = data[start + 2] | (data[start + 3] << 8);
                int pos = start + 4;
                if (maxNonZero >= BitmapSize)
                    throw Corrupt();

                var bitmap = tables.Bitmap;
                Array.Clear(bitmap, 0, bitmap.Length);
                if (minNonZero <= maxNonZero)
                {
                    int n = maxNonZero - minNonZero + 1;
                    if (n > end - pos)
                        throw Corrupt();
                    Buffer.BlockCopy(data, pos, bitmap, minNonZero, n);
                    pos += n;
                }
                int maxValue = ReverseLutFromBitmap(bitmap, tables.Lut);

                if (pos > end - 4)
                    throw Corrupt();
                uint hufBytes = ReadUInt32(data, pos);
                pos += 4;
                if (hufBytes > (uint)(end - pos))
                    throw Corrupt();

                int words = unpacked / 2;
                var buffer = tables.Words;
                HufDecompress(data, pos, (int)hufBytes, buffer, words, tables);

                // Wavelet decoding, one pass per 16-bit word of each channel.
                int channelStart = 0;
                foreach (var channel in channels)
                {
                    int wordsPerSample = channel.Size / 2;
                    for (int j = 0; j < wordsPerSample; j++)
                        Wav2Decode(buffer, channelStart + j, width, wordsPerSample, lines, wordsPerSample * width, maxValue);
                    channelStart += width * lines * wordsPerSample;
                }

                var lut = tables.Lut;
                for (int i = 0; i < words; i++)
                    buffer[i] = lut[buffer[i]];

                // Channel-major words back to the line-major little-endian layout of the other compressions.
                int o = 0;
                for (int y = 0; y < lines; y++)
                {
                    channelStart = 0;
                    foreach (var channel in channels)
                    {
                        int n = width * (channel.Size / 2);
                        int source = channelStart + y * n;
                        for (int k = 0; k < n; k++)
                        {
                            ushort value = buffer[source + k];
                            output[o++] = (byte)value;
                            output[o++] = (byte)(value >> 8);
                        }
                        channelStart += lines * n;
                    }
                }
            }

            static int ReverseLutFromBitmap(byte[] bitmap, ushort[] lut)
            {
                int k = 0;
                for (int i = 0; i < UShortRange; i++)
                {
                    if (i == 0 || (bitmap[i >> 3] & (1 << (i & 7))) != 0)
                        lut[k++] = (ushort)i;
                }
                int n = k - 1;
                while (k < UShortRange)
                    lut[k++] = 0;
                return n;
            }

            // -------------------------------------------------------------- wavelet

            static void Wav2Decode(ushort[] buffer, int start, int nx, int ox, int ny, int oy, int maxValue)
            {
                bool w14 = maxValue < (1 << 14);
                int n = nx > ny ? ny : nx;
                int p = 1;
                while (p <= n)
                    p <<= 1;
                p >>= 1;
                int p2 = p;
                p >>= 1;

                while (p >= 1)
                {
                    int py = start;
                    int ey = start + oy * (ny - p2);
                    int oy1 = oy * p;
                    int oy2 = oy * p2;
                    int ox1 = ox * p;
                    int ox2 = ox * p2;
                    ushort i00, i01, i10, i11;

                    for (; py <= ey; py += oy2)
                    {
                        int px = py;
                        int ex = py + ox * (nx - p2);
                        for (; px <= ex; px += ox2)
                        {
                            int p01 = px + ox1;
                            int p10 = px + oy1;
                            int p11 = p10 + ox1;
                            if (w14)
                            {
                                Wdec14(buffer[px], buffer[p10], out i00, out i10);
                                Wdec14(buffer[p01], buffer[p11], out i01, out i11);
                                Wdec14(i00, i01, out buffer[px], out buffer[p01]);
                                Wdec14(i10, i11, out buffer[p10], out buffer[p11]);
                            }
                            else
                            {
                                Wdec16(buffer[px], buffer[p10], out i00, out i10);
                                Wdec16(buffer[p01], buffer[p11], out i01, out i11);
                                Wdec16(i00, i01, out buffer[px], out buffer[p01]);
                                Wdec16(i10, i11, out buffer[p10], out buffer[p11]);
                            }
                        }

                        // Odd column.
                        if ((nx & p) != 0)
                        {
                            int p10 = px + oy1;
                            if (w14)
                                Wdec14(buffer[px], buffer[p10], out i00, out buffer[p10]);
                            else
                                Wdec16(buffer[px], buffer[p10], out i00, out buffer[p10]);
                            buffer[px] = i00;
                        }
                    }

                    // Odd line.
                    if ((ny & p) != 0)
                    {
                        int px = py;
                        int ex = py + ox * (nx - p2);
                        for (; px <= ex; px += ox2)
                        {
                            int p01 = px + ox1;
                            if (w14)
                                Wdec14(buffer[px], buffer[p01], out i00, out buffer[p01]);
                            else
                                Wdec16(buffer[px], buffer[p01], out i00, out buffer[p01]);
                            buffer[px] = i00;
                        }
                    }

                    p2 = p;
                    p >>= 1;
                }
            }

            /// <summary>Wavelet basis without modulo arithmetic (data below 14 bits).</summary>
            static void Wdec14(ushort l, ushort h, out ushort a, out ushort b)
            {
                unchecked
                {
                    int hi = (short)h;
                    int ai = (short)l + (hi & 1) + (hi >> 1);
                    a = (ushort)(short)ai;
                    b = (ushort)(short)(ai - hi);
                }
            }

            /// <summary>Wavelet basis with modulo arithmetic (full 16-bit data).</summary>
            static void Wdec16(ushort l, ushort h, out ushort a, out ushort b)
            {
                int m = l;
                int d = h;
                int bb = (m - (d >> 1)) & 0xFFFF;
                int aa = (d + bb - 0x8000) & 0xFFFF;
                b = (ushort)bb;
                a = (ushort)aa;
            }

            // -------------------------------------------------------------- Huffman

            static void HufDecompress(byte[] data, int start, int count, ushort[] output, int words, Tables tables)
            {
                if (count < 20)
                {
                    if (words != 0)
                        throw Corrupt();
                    return;
                }
                uint im = ReadUInt32(data, start);
                uint iM = ReadUInt32(data, start + 4);
                uint nBits = ReadUInt32(data, start + 12);
                if (im >= HufEncSize || iM >= HufEncSize)
                    throw Corrupt();
                if (20 + ((long)nBits + 7) / 8 > count)
                    throw Corrupt();

                int pos = start + 20;
                int left = count - 20;
                var codes = tables.Codes;
                UnpackEncTable(data, ref pos, ref left, im, iM, codes);
                if (nBits > 8L * left)
                    throw Corrupt();

                Array.Clear(tables.Decoder, 0, tables.Decoder.Length);
                BuildDecTable(codes, im, iM, tables);
                HufDecode(tables, data, pos, nBits, (int)iM, output, words);
            }

            static ulong GetBits(int count, ref ulong c, ref int lc, byte[] data, ref int pos)
            {
                while (lc < count)
                {
                    c = (c << 8) | data[pos++];
                    lc += 8;
                }
                lc -= count;
                return (c >> lc) & ((1UL << count) - 1);
            }

            /// <summary>Reads the code lengths (6 bits each, with zero runs) and rebuilds the canonical codes.</summary>
            static void UnpackEncTable(byte[] data, ref int pos, ref int left, uint im, uint iM, ulong[] codes)
            {
                Array.Clear(codes, 0, codes.Length);
                int start = pos;
                ulong c = 0;
                int lc = 0;
                for (; im <= iM; im++)
                {
                    if (lc < 6 && pos - start >= left)
                        throw Corrupt();
                    ulong l = codes[im] = GetBits(6, ref c, ref lc, data, ref pos);
                    if (l == LongZeroCodeRun)
                    {
                        if (lc < 8 && pos - start >= left)
                            throw Corrupt();
                        ulong run = GetBits(8, ref c, ref lc, data, ref pos) + ShortestLongRun;
                        if (im + run > iM + 1UL)
                            throw Corrupt();
                        while (run-- > 0)
                            codes[im++] = 0;
                        im--;
                    }
                    else if (l >= ShortZeroCodeRun)
                    {
                        ulong run = l - ShortZeroCodeRun + 2;
                        if (im + run > iM + 1UL)
                            throw Corrupt();
                        while (run-- > 0)
                            codes[im++] = 0;
                        im--;
                    }
                }
                left -= pos - start;
                CanonicalCodeTable(codes);
            }

            /// <summary>Canonical Huffman codes from code lengths; each entry becomes code &lt;&lt; 6 | length.</summary>
            static void CanonicalCodeTable(ulong[] codes)
            {
                var n = new ulong[59];
                for (int i = 0; i < HufEncSize; i++)
                    n[(int)codes[i]]++;

                ulong c = 0;
                for (int i = 58; i > 0; i--)
                {
                    ulong next = (c + n[i]) >> 1;
                    n[i] = c;
                    c = next;
                }

                for (int i = 0; i < HufEncSize; i++)
                {
                    ulong l = codes[i];
                    if (l > 0)
                        codes[i] = l | (n[(int)l]++ << 6);
                }
            }

            static void BuildDecTable(ulong[] codes, uint im, uint iM, Tables tables)
            {
                var decoder = tables.Decoder;
                var lengthStart = tables.LengthStart;
                Array.Clear(lengthStart, 0, lengthStart.Length);
                uint first = im;

                for (; im <= iM; im++)
                {
                    ulong c = codes[im] >> 6;
                    int l = (int)(codes[im] & 63);
                    if ((c >> l) != 0)
                        throw Corrupt();

                    if (l > HufDecBits)
                    {
                        // Long code: the entry its first 14 bits select only records that long codes start there.
                        ref HufDec entry = ref decoder[(int)(c >> (l - HufDecBits))];
                        if (entry.Len != 0)
                            throw Corrupt();
                        entry.Long = true;
                        lengthStart[l + 1]++;
                    }
                    else if (l != 0)
                    {
                        // Short code: fills every entry that starts with it.
                        int start = (int)(c << (HufDecBits - l));
                        int count = 1 << (HufDecBits - l);
                        for (int k = 0; k < count; k++)
                        {
                            ref HufDec entry = ref decoder[start + k];
                            if (entry.Len != 0 || entry.Long)
                                throw Corrupt();
                            entry.Len = l;
                            entry.Lit = (int)im;
                        }
                    }
                }

                // Long code symbols grouped by length (counting sort keeps symbol order inside a length).
                tables.LongLengthCount = 0;
                for (int l = HufDecBits + 1; l < 64; l++)
                {
                    if (lengthStart[l + 1] > 0)
                        tables.LongLengths[tables.LongLengthCount++] = l;
                    lengthStart[l + 1] += lengthStart[l];
                }
                if (tables.LongLengthCount == 0)
                    return;
                var next = (int[])lengthStart.Clone();
                var symbols = tables.SymbolsByLength;
                for (uint symbol = first; symbol <= iM; symbol++)
                {
                    int l = (int)(codes[symbol] & 63);
                    if (l > HufDecBits)
                    {
                        if (next[l] == lengthStart[l])
                            tables.FirstCode[l] = codes[symbol] >> 6;
                        else if ((codes[symbol] >> 6) != tables.FirstCode[l] + (ulong)(next[l] - lengthStart[l]))
                            throw Corrupt();
                        symbols[next[l]++] = (int)symbol;
                    }
                }
            }

            static void HufDecode(Tables tables, byte[] data, int start, uint nBits, int rlc, ushort[] output, int words)
            {
                var decoder = tables.Decoder;
                ulong c = 0;
                int lc = 0;
                int o = 0;
                int i = start;
                int ie = start + (int)(((long)nBits + 7) / 8);

                while (i < ie)
                {
                    c = (c << 8) | data[i++];
                    lc += 8;

                    while (lc >= HufDecBits)
                    {
                        var entry = decoder[(int)((c >> (lc - HufDecBits)) & HufDecMask)];
                        if (entry.Len != 0)
                        {
                            lc -= entry.Len;
                            GetCode(entry.Lit, rlc, ref c, ref lc, data, ref i, ie, output, ref o, words);
                        }
                        else
                        {
                            if (!entry.Long)
                                throw Corrupt();

                            // At most one range per length can hold the next bits: O(lengths) per symbol,
                            // whatever the number of long codes.
                            int symbol = -1;
                            for (int g = 0; g < tables.LongLengthCount; g++)
                            {
                                int l = tables.LongLengths[g];
                                while (lc < l && i < ie)
                                {
                                    c = (c << 8) | data[i++];
                                    lc += 8;
                                }
                                if (lc < l)
                                    break;
                                ulong rank = ((c >> (lc - l)) & ((1UL << l) - 1)) - tables.FirstCode[l];
                                if (rank < (ulong)(tables.LengthStart[l + 1] - tables.LengthStart[l]))
                                {
                                    symbol = tables.SymbolsByLength[tables.LengthStart[l] + (int)rank];
                                    lc -= l;
                                    break;
                                }
                            }
                            if (symbol < 0)
                                throw Corrupt();
                            GetCode(symbol, rlc, ref c, ref lc, data, ref i, ie, output, ref o, words);
                        }
                    }
                }

                // The last byte is padded with zero bits.
                int padding = (int)((8 - (long)nBits) & 7);
                c >>= padding;
                lc -= padding;
                while (lc > 0)
                {
                    var entry = decoder[(int)((c << (HufDecBits - lc)) & HufDecMask)];
                    if (entry.Len == 0 || entry.Len > lc)
                        throw Corrupt();
                    lc -= entry.Len;
                    GetCode(entry.Lit, rlc, ref c, ref lc, data, ref i, ie, output, ref o, words);
                }

                if (o != words)
                    throw Corrupt();
            }

            /// <summary>Writes a symbol, or repeats the previous one when the symbol is the run-length code.</summary>
            static void GetCode(int symbol, int rlc, ref ulong c, ref int lc, byte[] data, ref int i, int ie, ushort[] output, ref int o, int words)
            {
                if (symbol == rlc)
                {
                    if (lc < 8)
                    {
                        if (i >= ie)
                            throw Corrupt();
                        c = (c << 8) | data[i++];
                        lc += 8;
                    }
                    lc -= 8;
                    int run = (byte)(c >> lc);
                    if (run > words - o || o == 0)
                        throw Corrupt();
                    ushort previous = output[o - 1];
                    while (run-- > 0)
                        output[o++] = previous;
                }
                else if (o < words)
                {
                    output[o++] = (ushort)symbol;
                }
                else
                {
                    throw Corrupt();
                }
            }
        }

        // ------------------------------------------------------------------ helpers

        static float[] BuildHalfTable()
        {
            var table = new float[1 << 16];
            for (int i = 0; i < table.Length; i++)
                table[i] = HalfConverter.HalfToFloat((ushort)i);
            return table;
        }

        static string ReadString(byte[] data, ref int pos, int end)
        {
            // Names are at most 255 bytes (31 without the long names flag).
            int start = pos;
            int limit = Math.Min(end, start + 256);
            while (pos < limit && data[pos] != 0)
                pos++;
            if (pos >= limit)
                throw new PackException("The EXR header is damaged.");
            string value = Encoding.UTF8.GetString(data, start, pos - start);
            pos++;
            return value;
        }

        static int ReadInt32(byte[] d, int p)
        {
            return d[p] | (d[p + 1] << 8) | (d[p + 2] << 16) | (d[p + 3] << 24);
        }

        static uint ReadUInt32(byte[] d, int p)
        {
            return (uint)ReadInt32(d, p);
        }

        static long ReadInt64(byte[] d, int p)
        {
            return (long)ReadUInt32(d, p) | ((long)ReadInt32(d, p + 4) << 32);
        }

        static PackException Truncated()
        {
            return new PackException("The EXR file is truncated.");
        }

        static PackException Corrupt()
        {
            return new PackException("The EXR file is damaged (invalid compressed data).");
        }
    }
}
