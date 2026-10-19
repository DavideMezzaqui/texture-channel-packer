# Changelog

All notable changes to this package are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the package uses [Semantic Versioning](https://semver.org/).

## [Unreleased]

## [1.0.1] - 2026-10-19

### Fixed
- Overwriting an existing texture now imports a newly added alpha channel and raises Max Size to the new resolution, instead of silently dropping alpha or downscaling.
- New textures are never downscaled on import: Max Size is raised for "Unity defaults" too, and for the platform overrides copied by "Copy from input". "Tool settings" also disable power-of-two rescaling.
- "Copy from input" (also used by the Project window actions) no longer copies Flip Green Channel, swizzles or Create from Grayscale, which transformed the already processed pixels a second time.
- Split mode in the window asks before replacing existing files.
- Files under version control are checked out before being overwritten; a read-only file gives a clear error instead of an unexpected one.
- Project window actions on many large textures free each decoded image right away instead of keeping them all in memory.
- A source that fails to load with an unexpected error is no longer retried on every repaint (which re-imported it in a loop).
- 16-bit PNG outputs over 2 GB of pixel data are reported before packing; EXR files over 2 GB give a clear error.
- Output folders ending with a dot are refused (Windows would create a different folder).
- Two-word map names ("Base_Color", "Ambient_Occlusion") are fully removed from the automatic output name.
- TGA palette indices below the first color map entry decode as opaque black, not transparent.
- Quantization rounds values just below a half step down, as it should.

### Changed
- Output folder has an "Auto" toggle, like the name, to go back to the folder of the first input.
- The window records an undo snapshot only on events that can change it, not on every mouse move.
- `ChannelPacker.SplitTargets` lists the files a split would write. Split with `neverOverwrite` creates a missing output folder.
- Package manifest links to the repository, documentation, changelog and license.

## [1.0.0] - 2026-10-05

### Added
- Built-in OpenEXR reader: single part scanline files with half or float channels and NONE, RLE, ZIPS, ZIP or PIZ compression are read bit for bit, without a temporary import (faster, no AssetDatabase activity). Other variants still go through Unity's importer, and when both fail the error says why.
- Preview: RGBA (or LA) view over a checkerboard, so transparency is visible, and a live readout of the output values (0-1 and 0-255) under the mouse.
- Reference check against the OpenEXR library on more than 300 EXR files, embedded OpenEXR samples for the Unity tests, EXR fuzzing.
- Gray EXR files (R = G = B, as the tool writes them) are read back as gray.
- Third party notice for the OpenEXR decoding algorithms (BSD-3-Clause).
- Texture Channel Packer window (Tools > DAVFX > Texture Channel Packer) with Pack and Split modes.
- Per channel source (any input channel, luminance, average, min, max or a constant), combine operations, sRGB/linear conversion, invert and levels.
- Lossless reading of the original source files: native PNG (1 to 16 bit, palette, interlaced) and TGA decoders, JPEG via Unity, every other format through a verified, temporary uncompressed import.
- Lossless writers: PNG 8/16-bit, TGA 8-bit (raw or RLE), EXR half/float (ZIP).
- Validation that explains every step that could change data (resizing, lower bit depth, HDR clamping, lossy sources).
- Presets: HDRP Mask Map and Terrain Layer Mask Map (both also from roughness), URP Metallic + Smoothness, HDRP Detail Map, Albedo + Alpha, Albedo + Smoothness, ORM, normal map green flip, grayscale mask packing, channel swap, remove alpha, extract channel, invert, roughness/smoothness, solid color and flat normal. Channel layouts verified against the HDRP and URP shader sources.
- Automatic slot assignment and output naming from texture names.
- In-place overwrite that keeps GUID and import settings, with automatic backups.
- Project window actions: Open in Channel Packer, Split Channels, Flip Normal Green, Remove Alpha, Invert RGB.
- Public API (`ChannelPacker`, `ChannelPackerView`) for scripting and for embedding the UI in other tools.
- EditMode tests and an external .NET harness that checks the codecs against pypng, Pillow and OpenEXR.
- Compatibility with Unity 2021.3, 2022.3 and Unity 6, verified by compiling against the real editor assemblies of 2021.3.45f2, 2022.3.62f3, 6000.0.84f1 and 6000.6.4f1 (`compile_matrix.sh`).
- On Unity 2022.1+ the temporary lossless import also forces the identity channel swizzle and disables green flipping, so project presets cannot alter the data.
- Fuzz tests (corrupted and truncated files) and a differential test of the engine against a naive implementation on thousands of random recipes.
