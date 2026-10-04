namespace DAVFX.TextureChannelPacker.Codecs
{
    public static class ImageEncoder
    {
        /// <summary>Encodes an image with a resolved (non Auto) bit depth.</summary>
        public static byte[] Encode(PixelImage image, OutputLayout layout, OutputFormat format, OutputBitDepth depth, bool tgaRle)
        {
            switch (format)
            {
                case OutputFormat.TGA:
                    return TgaCodec.Encode(image, layout, tgaRle);
                case OutputFormat.EXR:
                    return ExrWriter.Encode(image, layout, depth == OutputBitDepth.Float);
                default:
                    return PngCodec.Encode(image, layout, depth == OutputBitDepth.Bits16 ? 16 : 8);
            }
        }

        /// <summary>
        /// Decodes formats the tool reads natively (PNG, TGA, OpenEXR). Returns null for anything else.
        /// Throws a <see cref="PackException"/> for damaged files, flagged <see cref="PackException.IsUnsupported"/>
        /// for valid variants the built-in decoders do not implement.
        /// </summary>
        public static PixelImage TryDecode(byte[] data, string extension, bool allowExr = true)
        {
            if (PngCodec.IsPng(data))
                return PngCodec.Decode(data);
            if (allowExr && ExrReader.IsExr(data))
                return ExrReader.Decode(data);
            if (extension != null && extension.ToLowerInvariant() == ".tga")
                return TgaCodec.Decode(data);
            return null;
        }
    }
}
