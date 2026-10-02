#!/usr/bin/env python3
"""Render the app's icons in the Gridline design language (docs/gridline.md).

Every icon is a list of axis-aligned rectangles designed on a 16-unit grid. For each
output size the grid is mapped to pixels with integer rounding, and line weight is
chosen per size, so edges land on whole pixels at 16, 20, 24 and 32 px instead of
being scaled and blurred. The same rectangle list produces the SVG, the PNG and the
ICO, so all three always agree.

Output is byte-for-byte deterministic: no timestamps, no external renderer, stdlib only.
Usage: python3 tools/icons/render_icons.py [output-dir]   (default: branding/icons)
"""

import struct
import sys
import zlib
from pathlib import Path

# Gridline tokens (branding/gridline/tokens/colors.css).
BLUE = "#0000A8"
GRAY = "#C6C6C6"
GRAY_LIGHT = "#E8E8E8"
WHITE = "#FFFFFF"
INK = "#101010"
RULE = "#666666"
RULE_LIGHT = "#9A9A9A"
CYAN = "#008EA1"
AMBER = "#B87900"
RED = "#B3261E"

GRID = 16
ICON_SIZES = [16, 20, 24, 32, 48, 64, 256]
TRAY_SIZES = [16, 20, 24, 32]


class Canvas:
    """Collects rectangles in pixel space for one icon at one size."""

    def __init__(self, size):
        self.size = size
        self.rects = []  # (x, y, w, h, colour), painted in order

    def px(self, units):
        return round(units * self.size / GRID)

    @property
    def line(self):
        # One pixel at 100-150% sizes, thickening slowly above that so large renders keep
        # Gridline's thin-rule character instead of turning into heavy outlines.
        return max(1, round(self.size / 20))

    def fill(self, x0, y0, x1, y1, colour):
        """Fill the grid-unit rectangle [x0,x1) x [y0,y1)."""
        px0, py0, px1, py1 = self.px(x0), self.px(y0), self.px(x1), self.px(y1)
        if px1 > px0 and py1 > py0:
            self.rects.append((px0, py0, px1 - px0, py1 - py0, colour))

    def fill_px(self, x, y, w, h, colour):
        if w > 0 and h > 0:
            self.rects.append((x, y, w, h, colour))

    def frame(self, x0, y0, x1, y1, colour, dashed=False):
        """A square outline of the current line weight just inside the grid rectangle."""
        px0, py0, px1, py1 = self.px(x0), self.px(y0), self.px(x1), self.px(y1)
        t = self.line
        if not dashed:
            self.fill_px(px0, py0, px1 - px0, t, colour)
            self.fill_px(px0, py1 - t, px1 - px0, t, colour)
            self.fill_px(px0, py0, t, py1 - py0, colour)
            self.fill_px(px1 - t, py0, t, py1 - py0, colour)
            return
        # Dashes start and end on a corner so the outline reads as a closed square.
        dash = max(t, round(2 * self.size / GRID))
        for x in range(px0, px1, dash * 2):
            w = min(dash, px1 - x)
            self.fill_px(x, py0, w, t, colour)
            self.fill_px(x, py1 - t, w, t, colour)
        for y in range(py0, py1, dash * 2):
            h = min(dash, py1 - y)
            self.fill_px(px0, y, t, h, colour)
            self.fill_px(px1 - t, y, t, h, colour)
        self.fill_px(px1 - t, py1 - t, t, t, colour)

    def pane(
        self, x0, y0, x1, y1, frame, body, strip=None, strip_units=2, dashed=False
    ):
        """A Gridline pane: square frame, body fill and an optional title strip."""
        if body is not None:
            self.fill(x0, y0, x1, y1, body)
        if strip is not None:
            t = self.line
            px0, py0, px1 = self.px(x0), self.px(y0), self.px(x1)
            height = max(t, self.px(strip_units))
            self.fill_px(px0, py0 + t, px1 - px0, height, strip)
        self.frame(x0, y0, x1, y1, frame, dashed=dashed)


# ---------------------------------------------------------------- glyphs (16-unit grid)


def verb_copy(c):
    # Two solid panes: the original stays, a copy appears.
    c.pane(1, 1, 11, 11, RULE, GRAY_LIGHT)
    c.pane(5, 5, 15, 15, INK, WHITE, strip=BLUE)


def verb_cut(c):
    # The source pane is a dashed ghost (it will leave); the moved pane is solid.
    c.pane(1, 1, 11, 11, RULE, None, dashed=True)
    c.pane(5, 5, 15, 15, INK, WHITE, strip=BLUE)


def verb_paste(c):
    # A pane set down into an in-tray. Drawn back to front: the tray's back and rim,
    # then the pane, then the tray's front face over the pane's lower edge, so the pane
    # reads as sitting inside the tray rather than standing on it.
    t = c.line
    left, right, bottom = c.px(1), c.px(15), c.px(15)
    rim, face = c.px(8), c.px(11)
    c.fill(1, 8, 15, 15, GRAY_LIGHT)
    c.fill_px(left, rim, t, bottom - rim, INK)  # left wall
    c.fill_px(right - t, rim, t, bottom - rim, INK)  # right wall
    c.fill_px(left, rim, c.px(4) - left, t, INK)  # rim, left of the pane
    c.fill_px(c.px(12), rim, right - c.px(12), t, INK)  # rim, right of the pane
    c.pane(4, 1, 12, 14, INK, WHITE, strip=BLUE)
    c.fill(1, 11, 15, 15, GRAY)  # front face
    c.fill_px(left, face, right - left, t, INK)  # top edge of the front face
    c.fill_px(left, face, t, bottom - face, INK)
    c.fill_px(right - t, face, t, bottom - face, INK)
    c.fill_px(left, bottom - t, right - left, t, INK)  # floor


STATE_STRIP = {"idle": BLUE, "running": CYAN, "paused": AMBER, "attention": RED}


def app_tile(c, state="idle", ephemeral=False):
    """The app as a Gridline window: framed tile, state-coloured title strip, two panes.

    State lives in the title strip because that is where Gridline puts status; ephemeral
    mode inverts the tile to ink so it can never be mistaken for normal mode.
    """
    tile = INK if ephemeral else GRAY
    c.fill(0, 0, GRID, GRID, tile)
    c.frame(0, 0, GRID, GRID, INK)
    t = c.line
    strip_bottom = max(c.px(4), t + 2)
    c.fill_px(t, t, c.size - 2 * t, strip_bottom - t, STATE_STRIP[state])
    if state == "paused":
        # Two vertical bars in the strip: the familiar pause mark, drawn as rules. They
        # span the strip at small sizes (a 1 px inset would leave dots) and inset above.
        inset = 1 if strip_bottom - t >= 5 else 0
        bar_h = strip_bottom - t - 2 * inset
        bar_w = t
        right = c.size - t - max(1, c.px(2))
        c.fill_px(right - 3 * bar_w, t + inset, bar_w, bar_h, WHITE)
        c.fill_px(right - bar_w, t + inset, bar_w, bar_h, WHITE)
    if ephemeral:
        c.pane(3, 6, 10, 12, RULE_LIGHT, None)
        c.pane(6, 8, 13, 14, WHITE, INK, strip=WHITE, strip_units=1)
    else:
        c.pane(3, 6, 10, 12, RULE, GRAY_LIGHT)
        c.pane(6, 8, 13, 14, INK, WHITE, strip=BLUE, strip_units=1)


ICONS = {
    "robo-copy": (verb_copy, ICON_SIZES),
    "robo-cut": (verb_cut, ICON_SIZES),
    "robo-paste": (verb_paste, ICON_SIZES),
    "app": (lambda c: app_tile(c), ICON_SIZES),
}


def _tray(state, ephemeral):
    return lambda c: app_tile(c, state, ephemeral)


for state in STATE_STRIP:
    for eph in (False, True):
        ICONS[f"tray-{state}" + ("-ephemeral" if eph else "")] = (
            _tray(state, eph),
            TRAY_SIZES,
        )


# ---------------------------------------------------------------- encoders


def rgba(colour):
    return int(colour[1:3], 16), int(colour[3:5], 16), int(colour[5:7], 16), 255


def raster(c):
    """Paint rectangles into a top-down RGBA pixel buffer (transparent background)."""
    n = c.size
    pixels = bytearray(n * n * 4)
    for x, y, w, h, colour in c.rects:
        r, g, b, a = rgba(colour)
        for row in range(max(0, y), min(n, y + h)):
            start = (row * n + max(0, x)) * 4
            for col in range(max(0, x), min(n, x + w)):
                pixels[start : start + 4] = bytes((r, g, b, a))
                start += 4
    return pixels


def svg(c):
    parts = [
        (
            f'<svg xmlns="http://www.w3.org/2000/svg" width="{c.size}" height="{c.size}" '
            f'viewBox="0 0 {c.size} {c.size}" shape-rendering="crispEdges">'
        )
    ]
    for x, y, w, h, colour in c.rects:
        parts.append(
            f'<rect x="{x}" y="{y}" width="{w}" height="{h}" fill="{colour}"/>'
        )
    parts.append("</svg>\n")
    return "\n".join(parts)


def png(size, pixels):
    def chunk(tag, data):
        body = tag + data
        return (
            struct.pack(">I", len(data))
            + body
            + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)
        )

    raw = b"".join(
        b"\x00" + bytes(pixels[r * size * 4 : (r + 1) * size * 4]) for r in range(size)
    )
    return (
        b"\x89PNG\r\n\x1a\n"
        + chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0))
        + chunk(b"IDAT", zlib.compress(raw, 9))
        + chunk(b"IEND", b"")
    )


def dib(size, pixels):
    """Classic 32-bit DIB icon image: bottom-up BGRA plus a 1-bit AND mask."""
    header = struct.pack("<IiiHHIIiiII", 40, size, size * 2, 1, 32, 0, 0, 0, 0, 0, 0)
    xor = bytearray()
    for row in range(size - 1, -1, -1):
        for col in range(size):
            i = (row * size + col) * 4
            r, g, b, a = pixels[i : i + 4]
            xor += bytes((b, g, r, a))
    mask_stride = ((size + 31) // 32) * 4
    mask = bytearray()
    for row in range(size - 1, -1, -1):
        bits = bytearray(mask_stride)
        for col in range(size):
            if pixels[(row * size + col) * 4 + 3] == 0:
                bits[col // 8] |= 0x80 >> (col % 8)
        mask += bits
    return header + bytes(xor) + bytes(mask)


def ico(images):
    """images: list of (size, pixels). 256 px is stored as PNG, smaller sizes as DIB,
    which is what every Windows icon consumer (shell, LoadImage, System.Drawing) accepts."""
    entries, blobs = [], []
    offset = 6 + 16 * len(images)
    for size, pixels in images:
        blob = png(size, pixels) if size >= 256 else dib(size, pixels)
        dim = 0 if size >= 256 else size
        entries.append(
            struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(blob), offset)
        )
        blobs.append(blob)
        offset += len(blob)
    return struct.pack("<HHH", 0, 1, len(images)) + b"".join(entries) + b"".join(blobs)


def main():
    out = Path(sys.argv[1] if len(sys.argv) > 1 else "branding/icons")
    for sub in ("svg", "png", "ico"):
        (out / sub).mkdir(parents=True, exist_ok=True)
    for name, (draw, sizes) in ICONS.items():
        images = []
        for size in sizes:
            c = Canvas(size)
            draw(c)
            (out / "svg" / f"{name}-{size}.svg").write_text(svg(c), encoding="utf-8")
            pixels = raster(c)
            (out / "png" / f"{name}-{size}.png").write_bytes(png(size, pixels))
            images.append((size, pixels))
        (out / "ico" / f"{name}.ico").write_bytes(ico(images))
    print(f"rendered {len(ICONS)} icons into {out}")


if __name__ == "__main__":
    main()
