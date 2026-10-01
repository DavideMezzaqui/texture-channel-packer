# DAVFX Texture Channel Packer

Pack, swap, split, invert and remap texture channels in Unity without losing quality.

Most channel packers read the texture Unity already imported, which is compressed, often downscaled to 2048 and sometimes altered (alpha dilation, normal map swizzle). This tool reads **the original source file**, so a channel you copy arrives bit for bit identical.

## Install

Unity 2021.3 or newer. Git must be installed on your machine.

1. Open **Window > Package Manager**.
2. Click **+ > Add package from git URL...**
3. Paste:

```
https://github.com/DavideMezzaqui/Claude-Test-1.git?path=/com.davfx.texture-channel-packer
```

To lock a version, add a branch, tag or commit at the end: `...?path=/com.davfx.texture-channel-packer#v1.0.0`.

Open the tool from **Tools > DAVFX > Texture Channel Packer**, or right click textures in the Project window > **DAVFX Channel Packer**.

## Quick start

1. Pick a preset in the toolbar (for example **Unity > HDRP - URP Mask Map**).
2. Select your textures in the Project window and click **Use Selection**. They are matched to the slots by name (`_Metallic`, `_AO`, `_Roughness`...). You can also drag them into the slots.
3. Check the preview and the messages under the settings.
4. Click **Pack**. The file is saved next to the first input, named after the texture set plus the preset suffix (`T_Rock_MaskMap.png`).

## What you can do

**Per output channel**

- Source: any channel of any input (Red, Green, Blue, Alpha, Luminance, Average, Min, Max) or a constant (0-1 or 0-255).
- Combine with a second source: Multiply, Add, Subtract, Minimum, Maximum, Average, Difference, Screen.
- Color conversion: sRGB to Linear, Linear to sRGB.
- Invert.
- Levels: input range, gamma, output range.

Operations run in this order: source, combine, color conversion, invert, levels. A channel with no changes is copied without any math.

**Output**

- Layouts: RGBA, RGB, Grayscale, Grayscale + Alpha.
- Formats: PNG (8 or 16-bit), TGA (8-bit, optional RLE), EXR (half or float). All lossless.
- Bit depth **Auto** picks the lowest depth that keeps every source value (16-bit sources give 16-bit PNG, float sources give float EXR).
- Size: largest input, smallest input, same as a given input, or custom. Different input sizes are an error until you choose a resample filter (Nearest, Bilinear, Area).
- Import settings for new files: tool settings (color space, normal map type, compression), copied from an input, or Unity defaults. Max Size is always raised to fit the real resolution.
- Overwriting an existing file replaces it in place: GUID, import settings and every material reference are kept. A backup goes to `Library/DAVFX_ChannelPacker/Backups`.

**Split mode** writes every channel of a texture as its own grayscale file at the same bit depth.

**Project window actions** (right click on textures): Open in Channel Packer, Split Channels, Flip Normal Green (DirectX - OpenGL), Remove Alpha, Invert RGB. They always create new files.

## Presets

| Preset | Output |
|---|---|
| HDRP - URP Mask Map | R Metallic, G Occlusion, B Detail Mask, A Smoothness |
| HDRP - URP Mask Map (from Roughness) | same, A = 1 - Roughness |
| URP - Built-in Metallic + Smoothness | RGB Metallic, A Smoothness |
| HDRP Detail Map | R desaturated Albedo, G Normal Y, B Smoothness, A Normal X |
| Albedo + Alpha | RGB Albedo, A Opacity |
| Albedo + Smoothness | RGB Albedo, A Smoothness |
| ORM | R Occlusion, G Roughness, B Metallic |
| Flip Green (DirectX - OpenGL) | G inverted |
| 4 / 3 Grayscale Masks | one grayscale texture per channel (VFX masks) |
| Swap Red - Blue, Remove Alpha, Extract Channel, Invert RGB, Roughness - Smoothness | channel utilities |
| Solid Color, Flat Normal Map | new textures from constants |

Empty slots in the material presets fall back to Unity's defaults (Metallic 0, Occlusion 1, Detail Mask 1, Smoothness 0.5), and the tool tells you when that happens.

## How quality is preserved

| Source | How it is read |
|---|---|
| PNG (1 to 16-bit, gray, palette, RGB, RGBA, interlaced, tRNS) | built-in decoder, exact |
| TGA (8/16/24/32-bit, RLE, palette, any origin) | built-in decoder, exact |
| JPG | Unity's decoder (the file is lossy by nature, the tool says so) |
| EXR, HDR, PSD, TIFF, BMP, GIF and others | a temporary copy is imported uncompressed, full size, readable and linear, read, verified and deleted |

- The source file and its .meta are never modified while reading.
- After a temporary import the tool checks format, size and mip count. If something in the project (an AssetPostprocessor, a preset) changed them, you get an error instead of a silently degraded result.
- Values are kept as 32-bit floats in the form `k / (2^bits - 1)`, which converts back to the exact same integers for 8 and 16-bit data.
- Every step that changes data (resizing, lower bit depth, HDR clamping, sRGB conversion in 8-bit, lossy sources) is listed before you pack.

## Scripting and embedding

```csharp
using DAVFX.TextureChannelPacker;

var request = new PackRequest
{
    Recipe = PackPresets.Find("HDRP - URP Mask Map").Recipe.Clone(),
    Inputs = { metallic, occlusion, null, smoothness },   // null = preset default
    Output = { folder = "Assets/Textures", fileName = "T_Rock_MaskMap" }
};
PackResult result = ChannelPacker.Pack(request);
```

`ChannelPacker.Split`, `TextureSourceLoader.Load` and `PackEngine.Evaluate` are public too.

The whole UI is `ChannelPackerView`, an IMGUI view that any editor window can host:

```csharp
[SerializeField] PackerState m_State = new PackerState();
ChannelPackerView m_View;

void OnEnable()  => m_View = new ChannelPackerView(m_State, this, Repaint);
void OnDisable() => m_View.Dispose();
void OnGUI()     => m_View.OnGUI();
```

## Tests

**In Unity**: add the package to `testables` in `Packages/manifest.json`, then run the EditMode tests in **Window > General > Test Runner**.

```json
"testables": [ "com.davfx.texture-channel-packer" ]
```

**Outside Unity** (codecs and engine, needs the .NET 8 SDK):

```
dotnet test Tests~/CoreTests/CoreTests.csproj
python3 Tests~/Reference/reference_check.py    # pip install pillow numpy pypng OpenEXR
```

The reference check compares the decoders and encoders with pypng, Pillow and the official OpenEXR library on more than 280 files.

## Limits

- Sources read through Unity's importer are limited to 16384 pixels per side.
- PSD files are read as Unity reads them: the flattened image.
- Gray EXR outputs are written as R = G = B so every application imports them the same way.
