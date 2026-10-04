#!/usr/bin/env python3
"""
Cross-checks the C# codecs against independent reference implementations.

1. Generates PNG/TGA fixtures with pypng and Pillow (every bit depth, color type,
   interlacing, palette transparency, RLE, both TGA origins) and OpenEXR fixtures
   with the OpenEXR library (every lossless compression, half/float/uint channels,
   NaN/Inf/subnormal values, line orders, data window offsets, plus variants the
   built-in reader must reject as unsupported), with the exact expected values.
2. Runs the C# ReferenceTests, which decode those fixtures and must match exactly,
   and which encode images that are written to an output folder.
3. Decodes the C# outputs with pypng, Pillow and OpenEXR and checks every value.

Requirements: dotnet 8, pip install pillow numpy pypng OpenEXR
Usage: python3 reference_check.py
"""
import os
import struct
import subprocess
import sys
import tempfile

import numpy as np
import png
from PIL import Image
import OpenEXR
import Imath

HERE = os.path.dirname(os.path.abspath(__file__))
PROJECT = os.path.join(HERE, "..", "CoreTests", "CoreTests.csproj")
rng = np.random.default_rng(42)


def write_expected(path, rgba, maxval):
    """rgba: (h, w, 4) uint16 array."""
    h, w, _ = rgba.shape
    with open(path, "wb") as f:
        f.write(struct.pack("<iii", w, h, maxval))
        f.write(rgba.astype("<u2").tobytes())


def read_expected(path):
    with open(path, "rb") as f:
        w, h, maxval = struct.unpack("<iii", f.read(12))
        data = np.frombuffer(f.read(), dtype="<u2").reshape(h, w, 4)
    return data, maxval


def to_rgba(values, channels, maxval):
    """values (h, w, c) -> (h, w, 4) with gray expanded and opaque alpha."""
    h, w = values.shape[:2]
    out = np.full((h, w, 4), maxval, dtype=np.uint16)
    if channels in (1, 2):
        for c in range(3):
            out[..., c] = values[..., 0]
        if channels == 2:
            out[..., 3] = values[..., 1]
    else:
        out[..., :3] = values[..., :3]
        if channels == 4:
            out[..., 3] = values[..., 3]
    return out


def gen_fixtures(folder):
    count = 0
    sizes = [(1, 1), (13, 11), (3, 9), (64, 17)]

    # pypng: every color type, depth and interlacing.
    for (w, h) in sizes:
        for interlace in (False, True):
            for depth in (1, 2, 4, 8, 16):
                maxval = (1 << depth) - 1
                v = rng.integers(0, maxval + 1, size=(h, w, 1), dtype=np.uint16)
                name = f"pypng_gray{depth}_{w}x{h}_{'i' if interlace else 'n'}"
                writer = png.Writer(w, h, greyscale=True, bitdepth=depth, interlace=interlace)
                with open(os.path.join(folder, name + ".png"), "wb") as f:
                    writer.write(f, v.reshape(h, w).tolist())
                write_expected(os.path.join(folder, name + ".expected"), to_rgba(v, 1, maxval), maxval)
                count += 1

            for depth in (8, 16):
                maxval = (1 << depth) - 1
                for channels, kwargs in ((2, dict(greyscale=True, alpha=True)),
                                         (3, dict(greyscale=False, alpha=False)),
                                         (4, dict(greyscale=False, alpha=True))):
                    v = rng.integers(0, maxval + 1, size=(h, w, channels), dtype=np.uint16)
                    name = f"pypng_c{channels}_{depth}_{w}x{h}_{'i' if interlace else 'n'}"
                    writer = png.Writer(w, h, bitdepth=depth, interlace=interlace, **kwargs)
                    with open(os.path.join(folder, name + ".png"), "wb") as f:
                        writer.write(f, v.reshape(h, w * channels).tolist())
                    write_expected(os.path.join(folder, name + ".expected"), to_rgba(v, channels, maxval), maxval)
                    count += 1

            # Palette with partial transparency (tRNS) at every palette depth.
            for depth in (1, 2, 4, 8):
                entries = 1 << depth
                palette = [tuple(int(x) for x in rng.integers(0, 256, size=4)) for _ in range(entries)]
                idx = rng.integers(0, entries, size=(h, w), dtype=np.uint16)
                name = f"pypng_pal{depth}_{w}x{h}_{'i' if interlace else 'n'}"
                writer = png.Writer(w, h, palette=palette, bitdepth=depth, interlace=interlace)
                with open(os.path.join(folder, name + ".png"), "wb") as f:
                    writer.write(f, idx.tolist())
                pal = np.array(palette, dtype=np.uint16)
                write_expected(os.path.join(folder, name + ".expected"), pal[idx], 255)
                count += 1

            # Color key transparency.
            for depth in (8, 16):
                maxval = (1 << depth) - 1
                v = rng.integers(0, 4, size=(h, w, 1), dtype=np.uint16) * (maxval // 3)
                key = int(maxval // 3)
                name = f"pypng_graykey{depth}_{w}x{h}_{'i' if interlace else 'n'}"
                writer = png.Writer(w, h, greyscale=True, bitdepth=depth, interlace=interlace, transparent=key)
                with open(os.path.join(folder, name + ".png"), "wb") as f:
                    writer.write(f, v.reshape(h, w).tolist())
                exp = to_rgba(v, 1, maxval)
                exp[..., 3] = np.where(v[..., 0] == key, 0, maxval)
                write_expected(os.path.join(folder, name + ".expected"), exp, maxval)
                count += 1

            v = rng.integers(0, 2, size=(h, w, 3), dtype=np.uint16) * 255
            key = (255, 0, 255)
            name = f"pypng_rgbkey8_{w}x{h}_{'i' if interlace else 'n'}"
            writer = png.Writer(w, h, greyscale=False, bitdepth=8, interlace=interlace, transparent=key)
            with open(os.path.join(folder, name + ".png"), "wb") as f:
                writer.write(f, v.reshape(h, w * 3).tolist())
            exp = to_rgba(v, 3, 255)
            match = (v[..., 0] == key[0]) & (v[..., 1] == key[1]) & (v[..., 2] == key[2])
            exp[..., 3] = np.where(match, 0, 255)
            write_expected(os.path.join(folder, name + ".expected"), exp, 255)
            count += 1

    # Pillow: different encoder (filters, compression), plus TGA.
    for (w, h) in sizes:
        for mode, channels in (("L", 1), ("LA", 2), ("RGB", 3), ("RGBA", 4)):
            v = rng.integers(0, 256, size=(h, w, channels), dtype=np.uint8)
            img = Image.fromarray(v[..., 0] if channels == 1 else v, mode)
            expected = to_rgba(v.astype(np.uint16), channels, 255)
            name = f"pil_{mode}_{w}x{h}"
            img.save(os.path.join(folder, name + ".png"), optimize=True)
            write_expected(os.path.join(folder, name + ".expected"), expected, 255)
            count += 1
            if mode == "LA":
                continue  # Pillow cannot write LA TGA.
            for rle in (False, True):
                for orientation in (1, -1):
                    tname = f"pil_{mode}_{w}x{h}_{'rle' if rle else 'raw'}_{'top' if orientation == 1 else 'bottom'}"
                    img.save(os.path.join(folder, tname + ".tga"), compression="tga_rle" if rle else None, orientation=orientation)
                    write_expected(os.path.join(folder, tname + ".expected"), expected, 255)
                    count += 1

        v16 = rng.integers(0, 65536, size=(h, w), dtype=np.uint16)
        Image.fromarray(v16.astype(np.int32), "I").convert("I;16").save(os.path.join(folder, f"pil_I16_{w}x{h}.png"))
        write_expected(os.path.join(folder, f"pil_I16_{w}x{h}.expected"), to_rgba(v16[..., None], 1, 65535), 65535)
        count += 1
    return count


# ---------------------------------------------------------------------------------------- OpenEXR fixtures

HALF = Imath.PixelType(Imath.PixelType.HALF)
FLOAT = Imath.PixelType(Imath.PixelType.FLOAT)
UINT = Imath.PixelType(Imath.PixelType.UINT)
EXR_LOSSLESS = ["NO", "RLE", "ZIPS", "ZIP", "PIZ"]
EXR_UNSUPPORTED = ["PXR24", "B44", "B44A", "DWAA", "DWAB", "ZSTD", "HTJ2K32"]


def half_to_float_bits(h):
    """Exact float32 bit pattern of each half (NaN payloads kept), independent of numpy's conversion."""
    h = h.astype(np.uint32)
    sign = (h & 0x8000) << 16
    exp = (h >> 10) & 0x1F
    man = h & 0x3FF
    out = np.zeros_like(h)
    normal = (exp > 0) & (exp < 31)
    out[normal] = sign[normal] | ((exp[normal] + 112) << 23) | (man[normal] << 13)
    special = exp == 31
    out[special] = sign[special] | 0x7F800000 | (man[special] << 13)
    zero = (exp == 0) & (man == 0)
    out[zero] = sign[zero]
    sub = (exp == 0) & (man != 0)
    m = man[sub]
    e = np.zeros_like(m)
    for _ in range(10):
        need = (m & 0x400) == 0
        m = np.where(need, m << 1, m)
        e = np.where(need, e + 1, e)
    out[sub] = sign[sub] | ((113 - e) << 23) | ((m & 0x3FF) << 13)
    return out


def exr_samples(kind, ptype, shape):
    """Raw sample bits (uint16 for half, uint32 for float/uint) following a pattern that exercises a decoder path."""
    n = int(np.prod(shape))
    if ptype == HALF:
        if kind == "full":      # every bit pattern: NaN, Inf, -0, subnormals; PIZ 16-bit wavelet path
            v = rng.integers(0, 65536, size=n, dtype=np.uint32)
        elif kind == "smooth":  # few distinct values; PIZ 14-bit wavelet path
            base = np.linspace(0, 1, n, dtype=np.float32) * rng.integers(1, 40)
            v = np.round(base).astype(np.float16).view(np.uint16).astype(np.uint32)
        elif kind == "runs":    # long constant runs; RLE runs and the Huffman run-length code
            v = np.repeat(rng.integers(0, 65536, size=n // 23 + 1, dtype=np.uint32), 23)[:n]
        elif kind == "ramp":    # many distinct but compressible values; PIZ 16-bit wavelet path on wide blocks
            v = (int(rng.integers(0, 65536)) + np.arange(n, dtype=np.uint32)) & 0xFFFF
        elif kind == "zero":    # empty PIZ bitmap
            v = np.zeros(n, dtype=np.uint32)
        else:                   # skewed: many rare values; long Huffman codes
            v = (rng.geometric(0.004, size=n) * 7 + 15360).astype(np.uint32) & 0xFFFF
        return v.astype(np.uint16).reshape(shape)
    if kind == "full" or ptype == UINT:
        v = rng.integers(0, 1 << 32, size=n, dtype=np.uint64).astype(np.uint32)
    elif kind == "smooth":
        v = np.linspace(-2, 3, n, dtype=np.float32).view(np.uint32)
    elif kind == "runs":
        v = np.repeat(rng.integers(0, 1 << 32, size=n // 17 + 1, dtype=np.uint64).astype(np.uint32), 17)[:n]
    elif kind == "ramp":
        v = (np.uint32(rng.integers(0, 1 << 31)) + np.arange(n, dtype=np.uint32) * np.uint32(3)).astype(np.uint32)
    elif kind == "zero":
        v = np.zeros(n, dtype=np.uint32)
    else:
        v = (rng.geometric(0.01, size=n).astype(np.float32) * 0.125).view(np.uint32)
    return v.reshape(shape)


def write_exr(path, w, h, compression, channels, line_order=None, window=None, display=None):
    """channels: {name: (pixel type, bits array (h, w))}."""
    header = OpenEXR.Header(w, h)
    header["compression"] = Imath.Compression(getattr(Imath.Compression, compression + "_COMPRESSION"))
    header["channels"] = {name: Imath.Channel(ptype) for name, (ptype, _) in channels.items()}
    if window is not None:
        header["dataWindow"] = Imath.Box2i(Imath.point(window[0], window[1]), Imath.point(window[0] + w - 1, window[1] + h - 1))
        header["displayWindow"] = header["dataWindow"]
    if display is not None:
        header["displayWindow"] = Imath.Box2i(Imath.point(display[0], display[1]), Imath.point(display[2], display[3]))
    if line_order is not None:
        header["lineOrder"] = Imath.LineOrder(line_order)
    f = OpenEXR.OutputFile(path, header)
    f.writePixels({name: bits.astype("<u2" if ptype == HALF else "<u4").tobytes() for name, (ptype, bits) in channels.items()})
    f.close()


def write_exr_expected(path, w, h, channels):
    """Expected PixelImage: R, G, B (missing = 0, or Y when there is no color), optional A, exact float bits."""
    def bits_of(name):
        ptype, bits = channels[name]
        return half_to_float_bits(bits) if ptype == HALF else bits.astype(np.uint32)

    color = any(c in channels for c in "RGB")
    zero = np.zeros((h, w), dtype=np.uint32)
    if color:
        planes = [bits_of(c) if c in channels else zero for c in "RGB"]
        used = [c for c in "RGB" if c in channels]
    elif "Y" in channels:
        planes = [bits_of("Y")] * 3
        used = ["Y"]
    else:
        planes = [zero] * 3
        used = []
    has_alpha = "A" in channels
    if has_alpha:
        planes.append(bits_of("A"))
        used.append("A")
    gray = not color
    if color and all(c in channels for c in "RGB"):
        # Identical R, G and B (how gray images are written) are read back as gray.
        gray = np.array_equal(planes[0], planes[1]) and np.array_equal(planes[0], planes[2])
    precision = 3 if any(channels[c][0] == FLOAT for c in used) else 2
    with open(path, "wb") as f:
        f.write(struct.pack("<iiii", w, h, (1 if has_alpha else 0) | (2 if gray else 0), precision))
        for plane in planes:
            f.write(plane.astype("<u4").tobytes())


def gen_exr_fixtures(folder):
    count = 0
    sizes = [(1, 1), (13, 11), (64, 17), (37, 70), (300, 40)]
    configs = {
        "rgba_half_full": {"R": (HALF, "full"), "G": (HALF, "full"), "B": (HALF, "full"), "A": (HALF, "full")},
        "rgb_float_full": {"R": (FLOAT, "full"), "G": (FLOAT, "full"), "B": (FLOAT, "full")},
        "mixed": {"R": (HALF, "smooth"), "G": (FLOAT, "smooth"), "B": (HALF, "runs"), "A": (FLOAT, "skewed")},
        "y_half_smooth": {"Y": (HALF, "smooth")},
        "ya_float": {"Y": (FLOAT, "runs"), "A": (HALF, "skewed")},
        "rgba_extra": {"R": (HALF, "skewed"), "G": (HALF, "skewed"), "B": (HALF, "skewed"), "A": (HALF, "runs"),
                       "Z": (FLOAT, "full"), "id": (UINT, "full")},
        "rg_only": {"R": (HALF, "runs"), "G": (HALF, "smooth")},
        "a_only": {"A": (HALF, "smooth")},
        "rgb_half_runs": {"R": (HALF, "runs"), "G": (HALF, "runs"), "B": (HALF, "runs")},
        "rgba_half_ramp": {"R": (HALF, "ramp"), "G": (HALF, "ramp"), "B": (HALF, "ramp"), "A": (HALF, "ramp")},
        "rgb_float_ramp": {"R": (FLOAT, "ramp"), "G": (FLOAT, "ramp"), "B": (FLOAT, "ramp")},
        "rgba_zero": {"R": (HALF, "zero"), "G": (HALF, "zero"), "B": (HALF, "zero"), "A": (FLOAT, "zero")},
    }
    for compression in EXR_LOSSLESS:
        for (w, h) in sizes:
            for config, spec in configs.items():
                channels = {name: (ptype, exr_samples(kind, ptype, (h, w))) for name, (ptype, kind) in spec.items()}
                name = f"exr_{compression}_{config}_{w}x{h}"
                write_exr(os.path.join(folder, name + ".exr"), w, h, compression, channels)
                write_exr_expected(os.path.join(folder, name + ".exrexpected"), w, h, channels)
                count += 1

        # Data window away from the origin, bottom-up line order.
        w, h = 29, 45
        channels = {c: (HALF, exr_samples("skewed", HALF, (h, w))) for c in "BGR"}
        name = f"exr_{compression}_offset_decreasing"
        write_exr(os.path.join(folder, name + ".exr"), w, h, compression, channels,
                  line_order=Imath.LineOrder.DECREASING_Y, window=(5, -7))
        write_exr_expected(os.path.join(folder, name + ".exrexpected"), w, h, channels)
        count += 1

    # Large images: long (> 14 bit) Huffman codes and 16-bit wavelets in PIZ.
    for kind, (w, h) in (("skewed", (512, 256)), ("ramp", (512, 256)), ("ramp", (601, 45))):
        channels = {c: (HALF, exr_samples(kind, HALF, (h, w))) for c in "ABGR"}
        for compression in ("PIZ", "ZIP", "RLE"):
            name = f"exr_{compression}_large_{kind}_{w}x{h}"
            write_exr(os.path.join(folder, name + ".exr"), w, h, compression, channels)
            write_exr_expected(os.path.join(folder, name + ".exrexpected"), w, h, channels)
            count += 1

    # Valid files the built-in reader must leave to Unity's importer.
    unsupported = 0

    def mark(name):
        open(os.path.join(folder, name + ".exrunsupported"), "w").close()

    rgba = {c: (HALF, exr_samples("smooth", HALF, (16, 16))) for c in "RGBA"}
    for compression in EXR_UNSUPPORTED:
        name = f"exr_unsupported_{compression}"
        try:
            if hasattr(Imath.Compression, compression + "_COMPRESSION"):
                write_exr(os.path.join(folder, name + ".exr"), 16, 16, compression, rgba)
            else:
                with OpenEXR.File({"compression": getattr(OpenEXR, compression + "_COMPRESSION")},
                                  {"RGBA": rng.random((16, 16, 4), dtype=np.float32)}) as f:
                    f.write(os.path.join(folder, name + ".exr"))
        except Exception as e:  # compression not built into this OpenEXR
            print(f"  (skipped {compression}: {e})")
            continue
        mark(name)
        unsupported += 1
    write_exr(os.path.join(folder, "exr_unsupported_window.exr"), 4, 4, "ZIP", {"R": (FLOAT, exr_samples("full", FLOAT, (4, 4)))},
              display=(0, 0, 9, 9))
    mark("exr_unsupported_window")
    write_exr(os.path.join(folder, "exr_unsupported_layers.exr"), 8, 8, "ZIP",
              {f"diffuse.{c}": (HALF, exr_samples("full", HALF, (8, 8))) for c in "RGB"})
    mark("exr_unsupported_layers")
    write_exr(os.path.join(folder, "exr_unsupported_uint.exr"), 8, 8, "ZIP", {"R": (UINT, exr_samples("full", UINT, (8, 8)))})
    mark("exr_unsupported_uint")
    header = OpenEXR.Header(8, 8)
    header["channels"] = {"Y": Imath.Channel(HALF), "RY": Imath.Channel(HALF, 2, 2), "BY": Imath.Channel(HALF, 2, 2)}
    f = OpenEXR.OutputFile(os.path.join(folder, "exr_unsupported_chroma.exr"), header)
    f.writePixels({"Y": np.zeros(64, dtype="<f2").tobytes(), "RY": np.zeros(16, dtype="<f2").tobytes(), "BY": np.zeros(16, dtype="<f2").tobytes()})
    f.close()
    mark("exr_unsupported_chroma")
    tiles = OpenEXR.TileDescription()
    tiles.xSize, tiles.ySize, tiles.mode = 16, 16, OpenEXR.ONE_LEVEL
    with OpenEXR.File({"compression": OpenEXR.ZIP_COMPRESSION, "type": OpenEXR.tiledimage, "tiles": tiles},
                      {"RGB": rng.random((20, 30, 3), dtype=np.float32)}) as f:
        f.write(os.path.join(folder, "exr_unsupported_tiled.exr"))
    mark("exr_unsupported_tiled")
    parts = [OpenEXR.Part({"compression": OpenEXR.ZIP_COMPRESSION}, {"RGB": rng.random((8, 8, 3), dtype=np.float32)}, name=n) for n in ("a", "b")]
    with OpenEXR.File(parts) as f:
        f.write(os.path.join(folder, "exr_unsupported_multipart.exr"))
    mark("exr_unsupported_multipart")
    unsupported += 6

    # The independent half conversion must agree with numpy wherever numpy is unambiguous (not NaN).
    every = np.arange(65536, dtype=np.uint32)
    finite = (every & 0x7C00) != 0x7C00
    numpy_bits = every.astype(np.uint16).view(np.float16).astype(np.float32).view(np.uint32)
    assert np.array_equal(half_to_float_bits(every)[finite], numpy_bits[finite]), "half conversion helper is wrong"
    return count + unsupported, unsupported


def check_outputs(folder):
    checked = 0
    for name in sorted(os.listdir(folder)):
        path = os.path.join(folder, name)
        base, ext = os.path.splitext(name)
        if ext not in (".png", ".tga", ".exr"):
            continue
        if ext == ".exr":
            check_exr(path, base, folder)
            checked += 1
            continue

        expected, maxval = read_expected(os.path.join(folder, base + ".expected"))
        h, w, _ = expected.shape
        if ext == ".png":
            # pypng: exact 16-bit support.
            reader = png.Reader(filename=path)
            rw, rh, rows, info = reader.asDirect()
            planes = info["planes"]
            data = np.array([list(r) for r in rows], dtype=np.uint16).reshape(rh, rw, planes)
            got = to_rgba(data, planes, maxval)
            assert info["bitdepth"] == (16 if maxval == 65535 else 8), name
            assert np.array_equal(got, expected), f"pypng mismatch in {name}"
            if maxval == 255:
                pil = np.array(Image.open(path).convert("RGBA"), dtype=np.uint16)
                assert np.array_equal(pil, expected), f"Pillow mismatch in {name}"
        else:
            pil = np.array(Image.open(path).convert("RGBA"), dtype=np.uint16)
            assert np.array_equal(pil, expected), f"Pillow mismatch in {name}"
        checked += 1
    return checked


def check_exr(path, base, folder):
    f = OpenEXR.InputFile(path)
    header = f.header()
    dw = header["dataWindow"]
    w = dw.max.x - dw.min.x + 1
    h = dw.max.y - dw.min.y + 1
    with open(os.path.join(folder, base + ".fexpected"), "rb") as e:
        ew, eh, nch = struct.unpack("<iii", e.read(12))
        exp = np.frombuffer(e.read(), dtype="<f4").reshape(nch, eh, ew)
    assert (w, h) == (ew, eh), base
    names = ["R", "G", "B", "A"][:nch]
    assert sorted(header["channels"].keys()) == sorted(names), f"{base}: channels {list(header['channels'].keys())}"
    for i, c in enumerate(names):
        ptype = header["channels"][c].type
        if ptype == Imath.PixelType(Imath.PixelType.FLOAT):
            got = np.frombuffer(f.channel(c, Imath.PixelType(Imath.PixelType.FLOAT)), dtype=np.float32).reshape(h, w)
            assert np.array_equal(got.view(np.uint32), exp[i].astype(np.float32).view(np.uint32)), f"{base}.{c} float mismatch"
        else:
            got = np.frombuffer(f.channel(c, Imath.PixelType(Imath.PixelType.HALF)), dtype=np.float16).reshape(h, w)
            want = exp[i].astype(np.float16)  # numpy rounds to nearest even, like the C# writer
            assert np.array_equal(got.view(np.uint16), want.view(np.uint16)), f"{base}.{c} half mismatch"


def main():
    with tempfile.TemporaryDirectory() as tmp:
        fixtures = os.path.join(tmp, "fixtures")
        outputs = os.path.join(tmp, "outputs")
        os.makedirs(fixtures)
        os.makedirs(outputs)
        n = gen_fixtures(fixtures)
        n_exr, n_unsupported = gen_exr_fixtures(fixtures)
        print(f"Generated {n} PNG/TGA and {n_exr} EXR reference fixtures ({n_unsupported} unsupported variants)")

        env = dict(os.environ, CP_FIXTURES=fixtures, CP_OUTPUTS=outputs, DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_NOLOGO="1")
        dotnet = os.environ.get("DOTNET", "dotnet")
        result = subprocess.run([dotnet, "test", PROJECT, "--filter", "FullyQualifiedName~ReferenceTests", "--logger", "console;verbosity=normal"],
                                env=env, capture_output=True, text=True)
        tail = "\n".join(line for line in result.stdout.splitlines() if "Passed" in line or "Failed" in line or "error" in line.lower())
        print(tail)
        if result.returncode != 0:
            print(result.stdout[-4000:])
            sys.exit("C# reference tests failed")

        checked = check_outputs(outputs)
        print(f"Verified {checked} files written by the C# encoders with pypng / Pillow / OpenEXR")
        print("ALL REFERENCE CHECKS PASSED")


if __name__ == "__main__":
    main()
