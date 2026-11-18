#!/usr/bin/env python3
"""Draws src/app.ico: a clipboard with an arrow landing on it.

Pure standard library on purpose - the repo has no build dependencies, and the icon is drawn from
shapes rather than scaled from one bitmap so that the 16 px tray size stays crisp.

    python3 tools/make-icon.py [preview.png]
"""
import struct
import sys
import zlib
from pathlib import Path

SIZES = [16, 20, 24, 32, 40, 48, 64, 256]
SAMPLES = 4  # per axis, for anti-aliasing

BOARD = (0x2F, 0x6F, 0xDE)
CLIP = (0x16, 0x2F, 0x66)
ARROW = (0xFF, 0xFF, 0xFF)


def rounded_rect(left, top, right, bottom, radius):
    def inside(x, y):
        if not (left <= x <= right and top <= y <= bottom):
            return False
        nearest_x = min(max(x, left + radius), right - radius)
        nearest_y = min(max(y, top + radius), bottom - radius)
        return (x - nearest_x) ** 2 + (y - nearest_y) ** 2 <= radius ** 2
    return inside


def triangle(a, b, c):
    def side(p, q, x, y):
        return (q[0] - p[0]) * (y - p[1]) - (q[1] - p[1]) * (x - p[0])

    def inside(x, y):
        signs = [side(a, b, x, y), side(b, c, x, y), side(c, a, x, y)]
        return all(s >= 0 for s in signs) or all(s <= 0 for s in signs)
    return inside


# back to front, in a 0..1 square
LAYERS = [
    (rounded_rect(0.13, 0.11, 0.87, 0.97, 0.11), BOARD),
    (rounded_rect(0.32, 0.02, 0.68, 0.23, 0.07), CLIP),
    (rounded_rect(0.42, 0.36, 0.58, 0.62, 0.0), ARROW),
    (triangle((0.26, 0.58), (0.74, 0.58), (0.50, 0.87)), ARROW),
]


def render(size):
    """Rows of (r, g, b, a), top to bottom."""
    rows = []
    for py in range(size):
        row = []
        for px in range(size):
            red = green = blue = covered = 0
            for sy in range(SAMPLES):
                for sx in range(SAMPLES):
                    x = (px + (sx + 0.5) / SAMPLES) / size
                    y = (py + (sy + 0.5) / SAMPLES) / size
                    color = None
                    for inside, layer_color in LAYERS:
                        if inside(x, y):
                            color = layer_color
                    if color:
                        red += color[0]
                        green += color[1]
                        blue += color[2]
                        covered += 1
            if covered:
                row.append((red // covered, green // covered, blue // covered,
                            round(255 * covered / SAMPLES ** 2)))
            else:
                row.append((0, 0, 0, 0))
        rows.append(row)
    return rows


def png(rows):
    def chunk(kind, data):
        body = kind + data
        return struct.pack('>I', len(data)) + body + struct.pack('>I', zlib.crc32(body))

    size = len(rows)
    raw = b''.join(b'\0' + bytes(channel for pixel in row for channel in pixel) for row in rows)
    return (b'\x89PNG\r\n\x1a\n'
            + chunk(b'IHDR', struct.pack('>IIBBBBB', size, size, 8, 6, 0, 0, 0))
            + chunk(b'IDAT', zlib.compress(raw, 9))
            + chunk(b'IEND', b''))


def dib(rows):
    """The classic icon format; the small sizes use it because not every reader takes png there."""
    size = len(rows)
    header = struct.pack('<IiiHHIIiiII', 40, size, size * 2, 1, 32, 0, 0, 0, 0, 0, 0)
    pixels = b''.join(bytes((b, g, r, a)) for row in reversed(rows) for (r, g, b, a) in row)
    mask_row_bytes = ((size + 31) // 32) * 4
    mask = b''
    for row in reversed(rows):
        bits = bytearray(mask_row_bytes)
        for x, pixel in enumerate(row):
            if pixel[3] == 0:
                bits[x // 8] |= 0x80 >> (x % 8)
        mask += bytes(bits)
    return header + pixels + mask


def ico(images):
    directory = b''
    data = b''
    offset = 6 + 16 * len(images)
    for size, image in images:
        directory += struct.pack('<BBBBHHII', size % 256, size % 256, 0, 0, 1, 32, len(image), offset)
        data += image
        offset += len(image)
    return struct.pack('<HHH', 0, 1, len(images)) + directory + data


def main():
    rendered = {size: render(size) for size in SIZES}
    images = [(size, png(rows) if size == 256 else dib(rows)) for size, rows in rendered.items()]
    target = Path(__file__).resolve().parent.parent / 'src' / 'app.ico'
    target.write_bytes(ico(images))
    print('wrote', target, target.stat().st_size, 'bytes')
    if len(sys.argv) > 1:
        Path(sys.argv[1]).write_bytes(png(rendered[256]))


main()
