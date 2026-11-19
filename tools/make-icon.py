"""Makes src/EstreamAssistant.App/app.ico (blue rounded square, white barcode). No dependencies."""
import struct, zlib, os

def render(size):
    ss = 4  # supersampling
    n = size * ss
    r = n * 0.22
    # barcode bars: (x0, x1) in 0..1 units, and the vertical span
    bars = [(0.20, 0.25), (0.29, 0.31), (0.35, 0.42), (0.46, 0.48), (0.52, 0.55),
            (0.59, 0.66), (0.70, 0.72), (0.76, 0.80)]
    y0, y1 = 0.27, 0.73
    rows = []
    for py in range(size):
        row = bytearray()
        for px in range(size):
            acc = [0, 0, 0, 0]
            for sy in range(ss):
                for sx in range(ss):
                    x = px * ss + sx + 0.5; y = py * ss + sy + 0.5
                    # rounded-rect mask
                    cx = min(max(x, r), n - r); cy = min(max(y, r), n - r)
                    if (x - cx) ** 2 + (y - cy) ** 2 > r * r:
                        continue
                    u, v = x / n, y / n
                    white = y0 <= v <= y1 and any(a <= u <= b for a, b in bars)
                    c = (255, 255, 255) if white else (0x3D - int(18 * v), 0x7F - int(30 * v), 0xD6 - int(30 * v))
                    acc[0] += c[0]; acc[1] += c[1]; acc[2] += c[2]; acc[3] += 255
            k = ss * ss
            a = acc[3] // k
            if a:
                row += bytes([acc[0] * 255 // acc[3], acc[1] * 255 // acc[3], acc[2] * 255 // acc[3], a])
            else:
                row += b"\0\0\0\0"
        rows.append(b"\0" + bytes(row))
    def chunk(t, d):
        return struct.pack(">I", len(d)) + t + d + struct.pack(">I", zlib.crc32(t + d) & 0xffffffff)
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(b"".join(rows), 9)) + chunk(b"IEND", b""))

sizes = [16, 24, 32, 48, 64, 256]
pngs = [render(s) for s in sizes]
out = struct.pack("<HHH", 0, 1, len(sizes))
offset = 6 + 16 * len(sizes)
for s, p in zip(sizes, pngs):
    out += struct.pack("<BBBBHHII", s % 256, s % 256, 0, 0, 1, 32, len(p), offset)
    offset += len(p)
out += b"".join(pngs)
path = os.path.join(os.path.dirname(__file__), "..", "src", "EstreamAssistant.App", "app.ico")
open(path, "wb").write(out)
open(os.path.join(os.path.dirname(__file__), "..", "docs", "icon-256.png"), "wb").write(pngs[-1])
print("wrote", os.path.normpath(path))
