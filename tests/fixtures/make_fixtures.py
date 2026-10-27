"""Regenerates the self-made fixtures (everything except the Project Gutenberg EPUBs).

Needs: pip install reportlab pypdf
Run from this folder:  python make_fixtures.py
"""
import io
import os
import random
import textwrap
import zipfile

from pypdf import PdfReader, PdfWriter
from reportlab.lib.pagesizes import letter
from reportlab.lib.utils import ImageReader
from reportlab.pdfgen import canvas
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
W, H = letter

BODY = [
    "The study of variation under domestication shows how much animals and plants can change when "
    "breeders choose which individuals leave offspring. Pigeons, dogs and cabbages all trace back to "
    "a few wild forms, yet their descendants differ in ways no one would predict from the ancestors.",
    "Variability is governed by many unknown laws, of which correlated growth is probably the most "
    "important. A change in one part of an organism often brings changes in others that nobody chose.",
    "In nature the struggle for existence follows from the rapid rate at which all organic beings tend "
    "to increase. Every creature produces more offspring than can possibly survive on the food at hand.",
    "Hence the individuals that vary even slightly in a useful direction have a better chance of "
    "surviving and of passing the useful variation on to their young.",
]
KNOWN = "Natural selection acts solely by accumulating slight successive favourable variations."


def page_body(c, page_no, known=False):
    y = H - 100
    paras = list(BODY)
    if known:
        paras.insert(1, KNOWN + " " + BODY[0])
    for para in paras:
        for line in textwrap.wrap(para, 88):
            c.drawString(72, y, line)
            y -= 14
        y -= 12


def make_origin(path, pages=8):
    c = canvas.Canvas(path, pagesize=letter)
    c.setTitle("On the Origin of Species")
    c.setAuthor("Charles Darwin")
    for n in range(1, pages + 1):
        c.setFont("Helvetica-Oblique", 9)
        header = "ON THE ORIGIN OF SPECIES" if n % 2 == 0 else "CHAPTER IV. NATURAL SELECTION"
        c.drawCentredString(W / 2, H - 40, header)
        c.setFont("Helvetica", 11)
        page_body(c, n, known=(n == 5))
        c.setFont("Helvetica", 9)
        c.drawCentredString(W / 2, 30, str(n))
        c.showPage()
    c.save()


def make_twocol(path):
    c = canvas.Canvas(path, pagesize=letter)
    c.setTitle("Two Column Paper")
    left = ["Left column opens here."] + [BODY[i % 4] for i in range(5)] + ["Left column ends here."]
    right = ["Right column opens here."] + [BODY[(i + 1) % 4] for i in range(5)] + ["Right column ends here."]
    for _ in range(2):
        c.setFont("Helvetica", 10)
        for x, paras in ((60, left), (320, right)):
            y = H - 80
            for para in paras:
                for line in textwrap.wrap(para, 44):
                    c.drawString(x, y, line)
                    y -= 13
                y -= 9
        c.showPage()
    c.save()


def make_scanned(path):
    rnd = random.Random(7)
    c = canvas.Canvas(path, pagesize=letter)
    for _ in range(3):
        img = Image.new("L", (300, 400))
        img.putdata([rnd.randrange(256) for _ in range(300 * 400)])
        c.drawImage(ImageReader(img), 36, 36, W - 72, H - 72)
        c.showPage()
    c.save()


def make_locked(src, path):
    w = PdfWriter(clone_from=PdfReader(src))
    w.encrypt("secret")
    with open(path, "wb") as f:
        w.write(f)


CONTAINER = """<?xml version="1.0"?>
<container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
<rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles>
</container>"""


def make_epub(path, title, author, lang, chapters, encryption=None, extra=None):
    """chapters: list of (chapter title, [paragraphs])."""
    manifest = "".join(
        f'<item id="c{i}" href="ch{i}.xhtml" media-type="application/xhtml+xml"/>' for i in range(len(chapters)))
    manifest += '<item id="ncx" href="toc.ncx" media-type="application/x-dtbncx+xml"/>'
    for name in (extra or {}):
        manifest += f'<item id="x{abs(hash(name))}" href="{name}" media-type="application/octet-stream"/>'
    spine = "".join(f'<itemref idref="c{i}"/>' for i in range(len(chapters)))
    opf = f"""<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://www.idpf.org/2007/opf" version="2.0" unique-identifier="id">
<metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
<dc:title>{title}</dc:title><dc:creator>{author}</dc:creator><dc:language>{lang}</dc:language>
<dc:identifier id="id">urn:uuid:{abs(hash(title))}</dc:identifier></metadata>
<manifest>{manifest}</manifest><spine toc="ncx">{spine}</spine></package>"""
    nav = "".join(
        f'<navPoint id="n{i}" playOrder="{i + 1}"><navLabel><text>{t}</text></navLabel>'
        f'<content src="ch{i}.xhtml"/></navPoint>' for i, (t, _) in enumerate(chapters))
    ncx = f"""<?xml version="1.0" encoding="utf-8"?>
<ncx xmlns="http://www.daisy.org/z3986/2005/ncx/" version="2005-1"><head/>
<docTitle><text>{title}</text></docTitle><navMap>{nav}</navMap></ncx>"""
    with zipfile.ZipFile(path, "w") as z:
        z.writestr("mimetype", "application/epub+zip", compress_type=zipfile.ZIP_STORED)
        z.writestr("META-INF/container.xml", CONTAINER)
        if encryption:
            z.writestr("META-INF/encryption.xml", encryption)
        z.writestr("OEBPS/content.opf", opf)
        z.writestr("OEBPS/toc.ncx", ncx)
        for i, (t, paras) in enumerate(chapters):
            body = f"<h1>{t}</h1>" + "".join(f"<p>{p}</p>" for p in paras)
            z.writestr(f"OEBPS/ch{i}.xhtml",
                       f'<?xml version="1.0" encoding="utf-8"?><html xmlns="http://www.w3.org/1999/xhtml">'
                       f"<head><title>{t}</title></head><body>{body}</body></html>")
        for name, data in (extra or {}).items():
            z.writestr("OEBPS/" + name, data)


def enc_xml(algorithm, uri):
    return f"""<?xml version="1.0"?>
<encryption xmlns="urn:oasis:names:tc:opendocument:xmlns:container" xmlns:enc="http://www.w3.org/2001/04/xmlenc#">
<enc:EncryptedData><enc:EncryptionMethod Algorithm="{algorithm}"/>
<enc:CipherData><enc:CipherReference URI="{uri}"/></enc:CipherData></enc:EncryptedData></encryption>"""


KOKORO = [
    ("上　先生と私", [
        "私はその人を常に先生と呼んでいた。だからここでもただ先生と書くだけで本名は打ち明けない。",
        "これは世間を憚かる遠慮というよりも、その方が私にとって自然だからである。",
        "私はその人の記憶を呼び起すごとに、すぐ「先生」といいたくなる。筆を執っても心持は同じ事である。",
    ]),
    ("中　両親と私", [
        "私が先生と知り合いになったのは鎌倉である。その時私はまだ若々しい書生であった。",
        "暑中休暇を利用して海水浴に行った友達からぜひ来いという端書を受け取ったので、私は多少の金を工面して出掛けた。",
    ]),
]


def main():
    os.chdir(HERE)
    make_origin("origin.pdf")
    make_twocol("twocol.pdf")
    make_scanned("scanned.pdf")
    make_locked("origin.pdf", "locked.pdf")
    make_epub("kokoro.epub", "こころ", "夏目漱石", "ja", KOKORO)
    chapters = [("Chapter One", ["A plain paragraph of text for the encrypted book."])]
    make_epub("drm.epub", "Protected Book", "Some Author", "en", chapters,
              encryption=enc_xml("http://www.w3.org/2001/04/xmlenc#aes128-cbc", "OEBPS/ch0.xhtml"))
    make_epub("fontobf.epub", "Obfuscated Font Book", "Some Author", "en", chapters,
              encryption=enc_xml("http://www.idpf.org/2008/embedding", "OEBPS/font.otf"),
              extra={"font.otf": b"not a real font"})
    with open("notes.md", "w", encoding="utf-8") as f:
        f.write("# Reading Notes\n\nIntro paragraph before any chapter heading.\n\n"
                "## Habits\n\nSmall habits compound over time.\n\n### Morning\n\nWake early and read.\n\n"
                "## Memory\n\nSpaced repetition beats cramming.\n")
    with open("plain.txt", "w", encoding="utf-8") as f:
        f.write("A short plain text file.\n\nIt has two paragraphs, separated by a blank line.\n")
    with open("utf16.txt", "w", encoding="utf-16") as f:  # writes a BOM
        f.write("Texte en UTF-16 avec des accents: café, élève, naïve.\n\nDeuxième paragraphe.\n")
    with open("arabic.txt", "w", encoding="utf-8") as f:
        f.write("كان يا ما كان في قديم الزمان رجل يحب الكتب كثيرا. كان يقرأ كل ليلة قبل النوم، "
                "ويحلم بالمكتبات الكبيرة والبعيدة.\n\nوفي يوم من الأيام وجد كتابا قديما في السوق.\n")


if __name__ == "__main__":
    main()
