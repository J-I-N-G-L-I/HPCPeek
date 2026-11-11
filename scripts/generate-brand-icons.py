"""Regenerate HPCPeek's original vector-derived icons using only Python's standard library.

The four joined compute-node blocks form an H. The mark is intentionally unrelated to
University of Leeds artwork; only the compact-monogram scale of its documentation
favicon informed this design. Run from any directory: python scripts/generate-brand-icons.py
"""
from __future__ import annotations

import hashlib
import json
from pathlib import Path
import struct
import zlib

ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "src/AirePeek.Desktop/Assets"
PREVIEW = ROOT / "docs/redesign/icons"
SAGE = (89, 114, 93)
IVORY = (245, 243, 238)
CHARCOAL = (56, 53, 47)
PALE = (240, 237, 229)
NODES = ((14, 12, 12, 18, 2.5), (14, 34, 12, 18, 2.5),
         (38, 12, 12, 18, 2.5), (38, 34, 12, 18, 2.5),
         (22, 28, 20, 8, 1.5))


def rounded(x: float, y: float, rect: tuple[float, ...]) -> bool:
    left, top, width, height, radius = rect
    if not left <= x <= left + width or not top <= y <= top + height:
        return False
    dx = max(left + radius - x, 0, x - (left + width - radius))
    dy = max(top + radius - y, 0, y - (top + height - radius))
    return dx * dx + dy * dy <= radius * radius


def point(x: float, y: float, variant: str) -> tuple[int, int, int, int]:
    if variant == "mac":
        x, y = (x - 32) / .875 + 32, (y - 32) / .875 + 32
    elif variant.startswith("tray"):
        x, y = (x - 32) / 1.35 + 32, (y - 32) / 1.35 + 32
    foreground = PALE if variant == "tray-light" else CHARCOAL if variant == "tray-dark" else IVORY
    if any(rounded(x, y, node) for node in NODES):
        return (*foreground, 255)
    if variant in ("app", "mac") and rounded(x, y, (2, 2, 60, 60, 14)):
        return (*SAGE, 255)
    return (0, 0, 0, 0)


def pixels(size: int, variant: str) -> bytes:
    # Small tray icons need accurate subpixel coverage; larger exports need fewer samples.
    samples = 4 if size <= 128 else 2 if size <= 512 else 1
    denominator = samples * samples
    data = bytearray(size * size * 4)
    offset = 0
    for y in range(size):
        for x in range(size):
            alpha = red = green = blue = 0
            for sy in range(samples):
                py = (y + (sy + .5) / samples) * 64 / size
                for sx in range(samples):
                    px = (x + (sx + .5) / samples) * 64 / size
                    r, g, b, a = point(px, py, variant)
                    alpha += a
                    red += r * a
                    green += g * a
                    blue += b * a
            if alpha:
                data[offset:offset + 4] = bytes((round(red / alpha), round(green / alpha), round(blue / alpha), round(alpha / denominator)))
            offset += 4
    return bytes(data)


def chunk(name: bytes, data: bytes) -> bytes:
    return struct.pack(">I", len(data)) + name + data + struct.pack(">I", zlib.crc32(name + data) & 0xffffffff)


def png(width: int, height: int, rgba: bytes) -> bytes:
    rows = b"".join(b"\0" + rgba[y * width * 4:(y + 1) * width * 4] for y in range(height))
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0)) + chunk(b"IDAT", zlib.compress(rows, 9)) + chunk(b"IEND", b"")


def image(size: int, variant: str) -> bytes:
    return png(size, size, pixels(size, variant))


def ico(variant: str) -> bytes:
    sizes = (16, 20, 24, 32, 40, 48, 64, 128, 256)
    images = [image(size, variant) for size in sizes]
    header = struct.pack("<HHH", 0, 1, len(images))
    offset = len(header) + 16 * len(images)
    directory = bytearray()
    for size, encoded in zip(sizes, images):
        directory += struct.pack("<BBBBHHII", size % 256, size % 256, 0, 0, 1, 32, len(encoded), offset)
        offset += len(encoded)
    return header + directory + b"".join(images)


def icns() -> bytes:
    sizes = {16: "icp4", 32: "icp5", 64: "icp6", 128: "ic07", 256: "ic08", 512: "ic09", 1024: "ic10"}
    encoded = {size: image(size, "mac") for size in sizes}
    payload = bytearray()
    for size, code in sizes.items():
        data = encoded[size]
        payload += code.encode("ascii") + struct.pack(">I", len(data) + 8) + data
    for code, size in (("ic11", 32), ("ic12", 64), ("ic13", 256), ("ic14", 512)):
        data = encoded[size]
        payload += code.encode("ascii") + struct.pack(">I", len(data) + 8) + data
    return b"icns" + struct.pack(">I", len(payload) + 8) + payload


def svg(variant: str) -> str:
    foreground = "#F0EDE5" if variant == "tray-light" else "#38352F" if variant == "tray-dark" else "#F5F3EE"
    background = '<rect x="2" y="2" width="60" height="60" rx="14" fill="#59725D"/>' if variant == "app" else ""
    transform = ' transform="translate(-11.2 -11.2) scale(1.35)"' if variant.startswith("tray") else ""
    nodes = "".join(f'<rect x="{x}" y="{y}" width="{w}" height="{h}" rx="{r}"/>' for x, y, w, h, r in NODES)
    return f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64" role="img" aria-label="HPCPeek connected compute nodes">{background}<g fill="{foreground}"{transform}>{nodes}</g></svg>\n'


def blend(target: bytearray, width: int, source: bytes, size: int, left: int, top: int) -> None:
    for y in range(size):
        for x in range(size):
            s = (y * size + x) * 4
            d = ((top + y) * width + left + x) * 4
            alpha = source[s + 3] / 255
            for channel in range(3):
                target[d + channel] = round(source[s + channel] * alpha + target[d + channel] * (1 - alpha))


def preview() -> None:
    width, height = 640, 300
    data = bytearray((*IVORY, 255)) * (width * height)
    for y in range(200, height):
        data[y * width * 4:(y + 1) * width * 4] = bytes((*CHARCOAL, 255)) * width
    for row, variant in enumerate(("app", "tray-dark", "tray-light")):
        for x, size in zip((24, 144, 240, 330, 420, 510), (80, 64, 48, 32, 20, 16)):
            blend(data, width, pixels(size, variant), size, x, row * 100 + (100 - size) // 2)
    (PREVIEW / "hpcpeek-icon-preview.png").write_bytes(png(width, height, bytes(data)))
    cards = []
    for variant in ("app", "tray-dark", "tray-light"):
        entries = []
        for size in (16, 20, 24, 32, 48, 64):
            name = f"{variant}-{size}.png"
            (PREVIEW / name).write_bytes(image(size, variant))
            entries.append(f'<figure><img src="{name}" width="{size}" height="{size}" alt="{variant} at {size}px"><figcaption>{size}px</figcaption></figure>')
        cards.append(f'<section class="{variant}"><h2>{variant}</h2><div>{"".join(entries)}</div></section>')
    (PREVIEW / "index.html").write_text('<!doctype html><html lang="en"><meta charset="utf-8"><title>HPCPeek icon sizes</title><style>body{font:14px system-ui;background:#eae7df;color:#38352f;padding:32px}h1{font-weight:500}h2{font-size:14px;font-weight:500}section{background:#f5f3ee;padding:18px;margin:16px 0;border-radius:8px}section.tray-light{background:#38352f;color:#f0ede5}section div{display:flex;align-items:center;gap:40px}figure{margin:0;min-width:64px;text-align:center}figcaption{font-size:11px;margin-top:12px}img{display:inline-block;vertical-align:middle}</style><h1>HPCPeek — four connected compute nodes</h1><p>Original vector mark forming H. App tile and transparent monochrome tray variants, shown at native pixel sizes. This is not University of Leeds artwork.</p>' + ''.join(cards) + '</html>', encoding="utf-8")


def main() -> None:
    ASSETS.mkdir(parents=True, exist_ok=True)
    PREVIEW.mkdir(parents=True, exist_ok=True)
    for variant in ("app", "tray-dark", "tray-light"):
        name = f"hpcpeek-{variant}"
        (ASSETS / f"{name}.svg").write_text(svg(variant), encoding="utf-8")
        (ASSETS / f"{name}.png").write_bytes(image(256, variant))
        (ASSETS / f"{name}.ico").write_bytes(ico(variant))
    (ASSETS / "app.icns").write_bytes(icns())
    # Compatibility aliases keep any previously retained internal asset reference on the new mark.
    for suffix, variant in (("", "tray-dark"), ("-light", "tray-light")):
        for extension in ("png", "ico"):
            (ASSETS / f"aire-peek{suffix}.{extension}").write_bytes((ASSETS / f"hpcpeek-{variant}.{extension}").read_bytes())
    preview()
    manifests = []
    for path in sorted(ASSETS.glob("hpcpeek-*")):
        manifests.append({"file": path.name, "bytes": path.stat().st_size, "sha256": hashlib.sha256(path.read_bytes()).hexdigest()})
    path = ASSETS / "app.icns"
    manifests.append({"file": path.name, "bytes": path.stat().st_size, "sha256": hashlib.sha256(path.read_bytes()).hexdigest()})
    (PREVIEW / "asset-manifest.json").write_text(json.dumps({"design": "original four-node H mark", "generator": "scripts/generate-brand-icons.py (Python standard library)", "icoSizes": [16, 20, 24, 32, 40, 48, 64, 128, 256], "assets": manifests}, indent=2), encoding="utf-8")
    print(f"Generated {len(manifests)} branded assets and native-size preview in {PREVIEW}")


if __name__ == "__main__":
    main()
