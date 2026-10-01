#!/usr/bin/env python3
"""
Cross-checks the C# codecs against independent reference implementations.

1. Generates PNG/TGA fixtures with pypng and Pillow (every bit depth, color type,
   interlacing, palette transparency, RLE, both TGA origins) plus the exact
   expected pixel values.
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
        print(f"Generated {n} reference fixtures")

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
