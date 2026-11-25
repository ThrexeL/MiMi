"""Generate MiMi's multi-resolution Windows icon using only Python's standard library."""

from pathlib import Path
import math
import struct
import zlib

ROOT = Path(__file__).resolve().parents[1]
OUTPUT = ROOT / "src" / "MiMi.App" / "Assets" / "mimi.ico"
SIZES = (16, 24, 32, 48, 64, 128, 256)
SAMPLES = 4


def chunk(kind, data):
    return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)


def rounded_distance(x, y, left, top, right, bottom, radius):
    half = (right - left) / 2
    cy = (top + bottom) / 2
    qx = abs(x - (left + right) / 2) - (half - radius)
    qy = abs(y - cy) - ((bottom - top) / 2 - radius)
    return math.hypot(max(qx, 0), max(qy, 0)) + min(max(qx, qy), 0) - radius


def pixel(x, y):
    distance = rounded_distance(x, y, 1.5, 1.5, 30.5, 30.5, 8)
    if distance > 0:
        return (0, 0, 0, 0)
    if distance > -1.35:
        return (255, 194, 102, 255)
    color = (20, 18, 16)
    for ex in (11.5, 20.5):
        if ((x - ex) / 2.65) ** 2 + ((y - 15.4) / 3.45) ** 2 <= 1:
            color = (255, 247, 232)
        if ((x - (ex - 0.6)) / 0.65) ** 2 + ((y - 14.3) / 0.8) ** 2 <= 1:
            color = (255, 255, 255)
    if rounded_distance(x, y, 6, 25, 26, 27.2, 1.1) <= 0:
        alpha = round(105 * max(0, 1 - abs(y - 26.1)))
        return (255, 180, 92, alpha)
    return (*color, 255)


def png(size):
    raw = bytearray()
    for py in range(size):
        raw.append(0)
        for px in range(size):
            totals = [0, 0, 0, 0]
            for sy in range(SAMPLES):
                for sx in range(SAMPLES):
                    rgba = pixel((px + (sx + 0.5) / SAMPLES) * 32 / size,
                                 (py + (sy + 0.5) / SAMPLES) * 32 / size)
                    for i in range(4):
                        totals[i] += rgba[i]
            raw.extend(round(value / (SAMPLES * SAMPLES)) for value in totals)
    header = struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", header) + chunk(b"IDAT", zlib.compress(bytes(raw), 9)) + chunk(b"IEND", b"")


def main():
    images = [png(size) for size in SIZES]
    offset = 6 + 16 * len(images)
    entries = bytearray()
    for size, image in zip(SIZES, images):
        entries.extend(struct.pack("<BBBBHHII", size if size < 256 else 0,
                                   size if size < 256 else 0, 0, 0, 1, 32, len(image), offset))
        offset += len(image)
    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    OUTPUT.write_bytes(struct.pack("<HHH", 0, 1, len(images)) + entries + b"".join(images))
    print(f"Wrote {OUTPUT} ({OUTPUT.stat().st_size} bytes, {len(images)} resolutions)")


if __name__ == "__main__":
    main()
