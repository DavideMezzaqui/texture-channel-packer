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

        /// <summary>Decodes formats the tool reads natively. Returns null for anything else.</summary>
        public static PixelImage TryDecode(byte[] data, string extension)
        {
            if (PngCodec.IsPng(data))
                return PngCodec.Decode(data);
            if (extension != null && extension.ToLowerInvariant() == ".tga")
                return TgaCodec.Decode(data);
            return null;
        }
    }
}
