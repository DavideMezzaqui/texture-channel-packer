# Changelog

All notable changes to this package are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the package uses [Semantic Versioning](https://semver.org/).

## [1.0.0] - 2026-10-01

### Added
- Texture Channel Packer window (Tools > DAVFX > Texture Channel Packer) with Pack and Split modes.
- Per channel source (any input channel, luminance, average, min, max or a constant), combine operations, sRGB/linear conversion, invert and levels.
- Lossless reading of the original source files: native PNG (1 to 16 bit, palette, interlaced) and TGA decoders, JPEG via Unity, every other format through a verified, temporary uncompressed import.
- Lossless writers: PNG 8/16-bit, TGA 8-bit (raw or RLE), EXR half/float (ZIP).
- Validation that explains every step that could change data (resizing, lower bit depth, HDR clamping, lossy sources).
- Presets: HDRP/URP Mask Map (also from roughness), URP Metallic + Smoothness, HDRP Detail Map, Albedo + Alpha, Albedo + Smoothness, ORM, normal map green flip, grayscale mask packing, channel swap, remove alpha, extract channel, invert, roughness/smoothness, solid color and flat normal.
- Automatic slot assignment and output naming from texture names.
- In-place overwrite that keeps GUID and import settings, with automatic backups.
- Project window actions: Open in Channel Packer, Split Channels, Flip Normal Green, Remove Alpha, Invert RGB.
- Public API (`ChannelPacker`, `ChannelPackerView`) for scripting and for embedding the UI in other tools.
- EditMode tests and an external .NET harness that checks the codecs against pypng, Pillow and OpenEXR.
