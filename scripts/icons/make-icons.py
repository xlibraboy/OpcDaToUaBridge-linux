#!/usr/bin/env python3
"""Generate the OpcBridge application icons for the three Windows apps.

The .ico files under src/*/Assets/ are generated, not hand-drawn: this script is
the source of truth, so a colour or shape change is an edit plus a re-run instead
of a binary blob nobody can review. Requires Pillow only (no ImageMagick, no
Inkscape, no browser).

    python3 scripts/icons/make-icons.py                 # write the three .ico files
    python3 scripts/icons/make-icons.py --preview DIR   # also render a PNG contact sheet

Every size is drawn on its own canvas (supersampled 4x, then Lanczos-downsampled),
so a stroke keeps its proportion at 16px instead of smearing the way one big
artwork scaled down does; sizes below 32px drop detail that cannot survive them.

The three icons share one dark tile and one accent palette so they read as a
family in the Start Menu folder, and differ by silhouette so they stay apart at
16px: the server is stacked rack bars, the runtime is a screen with a live trace,
the designer is a pen over a layout grid.

    opcbridge-server.ico     OpcBridge.App           (bridge service + dashboard)
    opcbridge-hmi.ico        OpcBridge.Hmi           (HMI runtime)
    opcbridge-designer.ico   OpcBridge.Hmi.Designer  (display authoring)
"""
import argparse
import struct
import sys
from pathlib import Path

try:
    from PIL import Image, ImageDraw
except ImportError:  # pragma: no cover - environment guard
    sys.exit("Pillow is required: python3 -m pip install --user pillow")

REPO_ROOT = Path(__file__).resolve().parents[2]

# One 4x canvas per size: everything is drawn in fractions of the icon edge and
# only rounded at the end, which is what keeps the small sizes legible.
SUPERSAMPLE = 4

# Tile and palette are the HMI theme's (src/OpcBridge.Hmi/Themes/SharedResources.axaml:
# CanvasColor / CardBorderColor / AccentMarker / AccentValue / QualityGood), so the
# icons sit in the same colour world as the app they launch.
TILE_TOP = (0x1B, 0x27, 0x33)
TILE_BOTTOM = (0x07, 0x0D, 0x14)
TILE_BORDER = (0x33, 0x42, 0x4F)
ACCENT_TOP = (0x63, 0xB4, 0xE4)
ACCENT_BOTTOM = (0x2A, 0x6F, 0x9C)
ACCENT_DIM = (0x2F, 0x6D, 0x93)
INK = (0x08, 0x12, 0x1A)
OK_GREEN = (0x61, 0xCC, 0x69)
AMBER_TOP = (0xF0, 0xC4, 0x72)
AMBER_BOTTOM = (0xCE, 0x8B, 0x2C)
WOOD = (0xF2, 0xE7, 0xD2)
GRAPHITE = (0x25, 0x2D, 0x36)

SIZES = (16, 20, 24, 32, 40, 48, 64, 128, 256)
# Windows expects the large entry as PNG and the rest as 32bpp bitmaps; keeping the
# small entries as BMP is what the shell, the MSI icon table and the shortcut
# plumbing all read without complaint.
PNG_FROM = 256


def lerp(a: tuple[int, int, int], b: tuple[int, int, int], t: float):
    """Blend two RGB colours, t=0 giving a and t=1 giving b."""
    return tuple(round(a[i] + (b[i] - a[i]) * t) for i in range(3))


def grad_tile(w: int, h: int, radius: int, top, bottom) -> Image.Image:
    """Rounded rectangle filled with a vertical gradient (RGBA, transparent outside)."""
    strip = Image.new("RGB", (1, h))
    strip.putdata([lerp(top, bottom, y / max(1, h - 1)) for y in range(h)])
    body = strip.resize((w, h), Image.Resampling.NEAREST)
    mask = Image.new("L", (w, h), 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, w - 1, h - 1), radius=radius, fill=255)
    out = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    out.paste(body, (0, 0), mask)
    return out


class Canvas:
    """A supersampled icon canvas addressed in fractions of the icon edge."""

    def __init__(self, size: int):
        self.size = size
        self.ss = size * SUPERSAMPLE
        self.img = Image.new("RGBA", (self.ss, self.ss), (0, 0, 0, 0))
        self.draw = ImageDraw.Draw(self.img)

    def px(self, fraction: float) -> int:
        return round(fraction * self.ss)

    def box(self, x0: float, y0: float, x1: float, y1: float) -> tuple[int, int, int, int]:
        return (self.px(x0), self.px(y0), self.px(x1), self.px(y1))

    def paste_tile(self, x0: float, y0: float, x1: float, y1: float, radius: float, top, bottom) -> None:
        x0p, y0p, x1p, y1p = self.box(x0, y0, x1, y1)
        self.img.alpha_composite(grad_tile(x1p - x0p, y1p - y0p, self.px(radius), top, bottom), (x0p, y0p))

    def rounded(self, coords, radius: float, fill=None, outline=None, width: float = 0.0) -> None:
        self.draw.rounded_rectangle(
            coords,
            radius=self.px(radius),
            fill=fill,
            outline=outline,
            width=max(1, self.px(width)) if outline else 0,
        )

    def circle(self, cx: float, cy: float, r: float, fill) -> None:
        self.draw.ellipse(self.box(cx - r, cy - r, cx + r, cy + r), fill=fill)

    def line(self, points, fill, width: float) -> None:
        self.draw.line(
            [(self.px(x), self.px(y)) for x, y in points],
            fill=fill,
            width=max(1, self.px(width)),
            joint="curve",
        )

    def rotated(self, layer: Image.Image, angle: float, cx: float, cy: float) -> None:
        turned = layer.rotate(angle, resample=Image.Resampling.BICUBIC, expand=True)
        self.img.alpha_composite(turned, (self.px(cx) - turned.width // 2, self.px(cy) - turned.height // 2))

    def down(self) -> Image.Image:
        return self.img.resize((self.size, self.size), Image.Resampling.LANCZOS)


def base_tile(c: Canvas) -> None:
    c.paste_tile(0.0, 0.0, 1.0, 1.0, 0.185, TILE_TOP, TILE_BOTTOM)
    c.rounded(c.box(0.015, 0.015, 0.985, 0.985), radius=0.175, outline=TILE_BORDER, width=0.014)


def draw_server(c: Canvas, detail: bool) -> None:
    """Bridge/server: a stack of rack units with status LEDs."""
    base_tile(c)

    width, height, gap = 0.54, 0.118, 0.072
    total = 3 * height + 2 * gap
    top = (1.0 - total) / 2
    x0 = (1.0 - width) / 2
    for i in range(3):
        y0 = top + i * (height + gap)
        c.paste_tile(x0, y0, x0 + width, y0 + height, height * 0.45, ACCENT_TOP, ACCENT_BOTTOM)
        if detail:
            c.circle(x0 + width - 0.062, y0 + height / 2, 0.026, OK_GREEN)


def draw_hmi(c: Canvas, detail: bool) -> None:
    """Runtime: a screen showing a live trace."""
    base_tile(c)

    c.paste_tile(0.185, 0.205, 0.815, 0.655, 0.075, ACCENT_TOP, ACCENT_BOTTOM)

    trace = (
        [(0.26, 0.47), (0.355, 0.335), (0.445, 0.505), (0.535, 0.30), (0.625, 0.425), (0.745, 0.375)]
        if detail
        else [(0.27, 0.47), (0.42, 0.34), (0.57, 0.50), (0.74, 0.37)]
    )
    c.line(trace, INK, 0.075)

    c.rounded(c.box(0.435, 0.655, 0.565, 0.755), radius=0.03, fill=ACCENT_DIM)
    c.rounded(c.box(0.315, 0.755, 0.685, 0.825), radius=0.035, fill=ACCENT_DIM)
    if detail:
        c.circle(0.795, 0.235, 0.072, OK_GREEN)


def draw_designer(c: Canvas, detail: bool) -> None:
    """Designer: a pen over the widget grid it authors."""
    base_tile(c)

    c.rounded(c.box(0.195, 0.195, 0.805, 0.805), radius=0.09, outline=ACCENT_DIM, width=0.05)
    for i in (0.4, 0.6):
        c.line([(0.195, i), (0.805, i)], ACCENT_DIM, 0.035)
        c.line([(i, 0.195), (i, 0.805)], ACCENT_DIM, 0.035)
    pen(c)


def pen(c: Canvas) -> None:
    """Amber pen at 45 degrees: barrel, sharpened wood, graphite point."""
    length, width = 0.72, 0.165
    lw, lh = c.px(width), c.px(length)
    wood, tip = c.px(0.085), c.px(0.075)
    barrel = lh - wood - tip
    graphite = lw * 0.34

    layer = Image.new("RGBA", (lw, lh), (0, 0, 0, 0))
    layer.alpha_composite(grad_tile(lw, barrel, c.px(0.035), AMBER_TOP, AMBER_BOTTOM))
    pen_draw = ImageDraw.Draw(layer)
    pen_draw.polygon(
        [(0, barrel), (lw, barrel), ((lw + graphite) / 2, barrel + wood), ((lw - graphite) / 2, barrel + wood)],
        fill=WOOD,
    )
    pen_draw.polygon(
        [((lw - graphite) / 2, barrel + wood), ((lw + graphite) / 2, barrel + wood), (lw / 2, lh)],
        fill=GRAPHITE,
    )
    c.rotated(layer, -45, 0.5, 0.5)


def write_ico(path: Path, images: dict[int, Image.Image]) -> None:
    """Write a multi-size .ico: PNG for the 256px entry, 32bpp bitmaps for the rest."""
    entries = []
    for size in sorted(images):
        img = images[size].convert("RGBA")
        if size >= PNG_FROM:
            payload = _png_bytes(img)
        else:
            payload = _bitmap_bytes(img)
        entries.append((img.width, img.height, payload))

    header = struct.pack("<HHH", 0, 1, len(entries))
    offset = len(header) + 16 * len(entries)
    directory, blobs = b"", b""
    for width, height, payload in entries:
        directory += struct.pack(
            "<BBBBHHII",
            width if width < 256 else 0,
            height if height < 256 else 0,
            0,  # palette entries
            0,  # reserved
            1,  # colour planes
            32,  # bits per pixel
            len(payload),
            offset,
        )
        blobs += payload
        offset += len(payload)

    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(header + directory + blobs)


def _png_bytes(img: Image.Image) -> bytes:
    from io import BytesIO

    buf = BytesIO()
    img.save(buf, "PNG", optimize=True)
    return buf.getvalue()


def _bitmap_bytes(img: Image.Image) -> bytes:
    """One ICO bitmap entry: BITMAPINFOHEADER + bottom-up BGRA rows + a 1bpp AND mask."""
    from io import BytesIO

    w, h = img.size
    rows = [img.tobytes("raw", "BGRA")[y * w * 4 : (y + 1) * w * 4] for y in range(h)]
    pixels = b"".join(reversed(rows))  # ICO bitmaps are bottom-up
    mask_stride = ((w + 31) // 32) * 4  # 1bpp rows padded to 4 bytes
    mask = b"\x00" * (mask_stride * h)
    header = struct.pack("<IiiHHIIiiII", 40, w, h * 2, 1, 32, 0, len(pixels), 0, 0, 0, 0)
    return header + pixels + mask


ICONS = {
    "opcbridge-server.ico": draw_server,
    "opcbridge-hmi.ico": draw_hmi,
    "opcbridge-designer.ico": draw_designer,
}

OUTPUT_DIRS = {
    "opcbridge-server.ico": REPO_ROOT / "src/OpcBridge.App/Assets",
    "opcbridge-hmi.ico": REPO_ROOT / "src/OpcBridge.Hmi/Assets",
    "opcbridge-designer.ico": REPO_ROOT / "src/OpcBridge.Hmi.Designer/Assets",
}


def render(draw, size: int) -> Image.Image:
    canvas = Canvas(size)
    draw(canvas, detail=size >= 32)
    return canvas.down()


def preview(directory: Path, zoom: int = 4) -> None:
    """One contact sheet per icon: every size at its true size over a light and a dark
    strip, with the sizes that need it also shown magnified."""
    directory.mkdir(parents=True, exist_ok=True)
    margin, gap, label = 16, 14, 18
    width = margin * 2 + sum(SIZES) + gap * (len(SIZES) - 1)
    strip_h = 170 + 256 + label + gap

    for name, draw in ICONS.items():
        sheet = Image.new("RGB", (width, 34 + strip_h * 2), (0xEE, 0xF1, 0xF4))
        pen = ImageDraw.Draw(sheet)
        pen.text((margin, 12), name, fill=(0x20, 0x28, 0x30))

        for strip, bg in enumerate(((0xF3, 0xF5, 0xF7), (0x10, 0x16, 0x1C))):
            top = 34 + strip * strip_h
            pen.rectangle((0, top, width, top + strip_h - gap), fill=bg)

            x = margin
            for size in SIZES:
                if size > 32:
                    continue
                shown = render(draw, size).resize((size * zoom, size * zoom), Image.Resampling.NEAREST)
                sheet.paste(shown, (x, top + 150 - shown.height), shown)
                pen.text((x, top + 152), f"{size}px x{zoom}", fill=(0x60, 0x68, 0x70))
                x += shown.width + gap

            x = margin
            for size in SIZES:
                shown = render(draw, size)
                sheet.paste(shown, (x, top + 180), shown)
                pen.text((x, top + 180 + size + 3), str(size), fill=(0x60, 0x68, 0x70))
                x += size + gap

        sheet.save(directory / f"{name.removesuffix('.ico')}.png")


def check_ico(path: Path) -> str:
    """Re-read what was written: the shell, the compiler and the MSI icon table all
    parse the container, so a malformed directory entry must fail here, not on Windows."""
    from io import BytesIO

    blob = path.read_bytes()
    reserved, kind, count = struct.unpack("<HHH", blob[:6])
    if (reserved, kind) != (0, 1):
        raise SystemExit(f"{path}: not an icon container")
    sizes = []
    for i in range(count):
        width, height, _, _, planes, bpp, length, offset = struct.unpack_from("<BBBBHHII", blob, 6 + 16 * i)
        if offset + length > len(blob):
            raise SystemExit(f"{path}: entry {i} runs past the end of the file")
        sizes.append((width or 256, height or 256))
        assert planes == 1 and bpp == 32, f"{path}: entry {i} is not 32bpp"
    with Image.open(BytesIO(blob)) as img:
        loaded = sorted(img.ico.sizes()) if hasattr(img, "ico") else [img.size]
        if loaded != sorted(sizes):
            raise SystemExit(f"{path}: directory says {sorted(sizes)}, reader sees {loaded}")
    large = blob[struct.unpack_from("<I", blob, 6 + 16 * (count - 1) + 12)[0] :]
    kind_of_256 = "png" if large[:8] == b"\x89PNG\r\n\x1a\n" else "bitmap"
    return f"{count} entries {sizes[0][0]}..{sizes[-1][0]}px, 256px as {kind_of_256}"


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--preview", metavar="DIR", help="also write a PNG contact sheet into DIR")
    args = parser.parse_args()

    for name, draw in ICONS.items():
        images = {size: render(draw, size) for size in SIZES}
        path = OUTPUT_DIRS[name] / name
        write_ico(path, images)
        print(f"{path.relative_to(REPO_ROOT)}  {path.stat().st_size:>6} bytes  {check_ico(path)}")

    if args.preview:
        preview(Path(args.preview))
        print(f"preview -> {args.preview}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
