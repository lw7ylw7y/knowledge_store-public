"""Builds the Windows icon from the two logo drawings: docs/logo/docent-small.svg for the small sizes (16 to 40 pixels, fewer and
bolder details) and docs/logo/docent.svg for the larger ones. Writes src/BookSearch.App/Assets/Docent.ico (PNG-compressed
entries, the form Windows has read since Vista) and docs/logo/docent-256.png (for the README).

    python scripts/make-icon.py        needs:  pip install cairosvg
"""
import io, struct, pathlib
import cairosvg

root = pathlib.Path(__file__).resolve().parent.parent
small, large = root / "docs/logo/docent-small.svg", root / "docs/logo/docent.svg"
sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]

def png(svg: pathlib.Path, size: int) -> bytes:
    return cairosvg.svg2png(url=str(svg), output_width=size, output_height=size)

images = [(s, png(small if s <= 40 else large, s)) for s in sizes]
header = struct.pack("<HHH", 0, 1, len(images))
offset = 6 + 16 * len(images)
entries, blobs = b"", b""
for size, data in images:
    w = 0 if size >= 256 else size
    entries += struct.pack("<BBBBHHII", w, w, 0, 0, 1, 32, len(data), offset + len(blobs))
    blobs += data
(root / "src/BookSearch.App/Assets/Docent.ico").write_bytes(header + entries + blobs)
(root / "docs/logo/docent-256.png").write_bytes(png(large, 256))
print(f"wrote Docent.ico with {len(images)} sizes: {sizes}")
