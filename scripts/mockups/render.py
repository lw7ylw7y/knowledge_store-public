"""Draws approximate mock-ups of the Docent screens from the XAML layout values (paddings, spacings, widths).

An approximation only: the real app uses Segoe UI Variable and WinUI controls; this uses DejaVu Sans and plain shapes. Spacing,
widths, text wrapping and what is on each screen follow the XAML in src/BookSearch.App/Pages. Kept in the repo on purpose (CLAUDE.md: draw a page before building it). Needs Pillow and the DejaVu Sans fonts.
"""
import sys
from PIL import Image, ImageDraw, ImageFont

FONT = "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"
BOLD = "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf"
SCALE = 0.92   # DejaVu is wider than Segoe UI: shrink so line breaks are about right

LIGHT = dict(bg=(243, 243, 243), surface=(255, 255, 255), card=(251, 251, 251), stroke=(229, 229, 229), text=(26, 26, 26),
             secondary=(96, 96, 96), accent=(0, 95, 184), accent_text=(255, 255, 255), accent_fg=(0, 95, 184),
             chip=(235, 235, 235), button=(251, 251, 251), button_stroke=(200, 200, 200), highlight=(255, 244, 206),
             warn_bg=(255, 244, 206), warn_stroke=(224, 190, 90), err_bg=(253, 231, 233), err_stroke=(210, 90, 100),
             info_bg=(244, 244, 244), nav=(238, 238, 238), sel=(0, 95, 184), hover=(232, 232, 232))
DARK = dict(bg=(32, 32, 32), surface=(43, 43, 43), card=(50, 50, 50), stroke=(70, 70, 70), text=(255, 255, 255),
            secondary=(190, 190, 190), accent=(96, 205, 255), accent_text=(0, 0, 0), accent_fg=(96, 205, 255),
            chip=(58, 58, 58), button=(58, 58, 58), button_stroke=(90, 90, 90), highlight=(67, 53, 25),
            warn_bg=(67, 53, 25), warn_stroke=(150, 120, 40), err_bg=(68, 39, 38), err_stroke=(170, 70, 80),
            info_bg=(50, 50, 50), nav=(40, 40, 40), sel=(96, 205, 255), hover=(60, 60, 60))

_fonts = {}


def font(size, bold=False):
    key = (round(size * SCALE), bold)
    if key not in _fonts:
        _fonts[key] = ImageFont.truetype(BOLD if bold else FONT, max(8, round(size * SCALE)))
    return _fonts[key]


def text_width(s, size, bold=False):
    return font(size, bold).getlength(s)


def wrap(s, size, bold, maxw):
    lines = []
    for para in s.split("\n"):
        words, line = para.split(" "), ""
        for w in words:
            trial = (line + " " + w).strip()
            if text_width(trial, size, bold) <= maxw or not line:
                line = trial
            else:
                lines.append(line)
                line = w
        lines.append(line)
    return lines


class Screen:
    def __init__(self, w, h, theme, nav_active=None, title="Docent", nav=True):
        self.w, self.h, self.t = w, h, theme
        self.img = Image.new("RGB", (w, h), theme["bg"])
        self.d = ImageDraw.Draw(self.img)
        self.notes = []          # layout findings
        # title bar
        self.d.rectangle([0, 0, w, 32], fill=theme["surface"])
        self.d.text((40, 8), title, font=font(12), fill=theme["text"])
        self.d.rectangle([12, 10, 26, 24], outline=theme["accent"], width=2)
        self.x0 = 0
        if nav:
            self.x0 = 48
            self.d.rectangle([0, 32, 48, h], fill=theme["nav"])
            for i, (glyph, key) in enumerate([("⌕", "ask"), ("◷", "history"), ("▤", "library")]):
                self.nav_item(i, glyph, active=(nav_active == key))
                if key == "ask":   # DejaVu has no magnifier glyph: draw one over the missing-glyph box
                    y = 44 + i * 44
                    self.d.rectangle([12, y + 6, 32, y + 30], fill=theme["hover"] if nav_active == "ask" else theme["nav"])
                    self.d.ellipse([15, y + 9, 27, y + 21], outline=theme["text"], width=2)
                    self.d.line([26, y + 20, 32, y + 27], fill=theme["text"], width=2)
            self.nav_item_bottom("⚙", active=(nav_active == "settings"))

    def nav_item(self, i, glyph, active=False):
        y = 44 + i * 44
        if active:
            self.d.rounded_rectangle([4, y, 44, y + 38], radius=4, fill=self.t["hover"])
            self.d.rectangle([4, y + 10, 6, y + 28], fill=self.t["sel"])
        self.d.text((16, y + 8), glyph, font=font(18), fill=self.t["text"])

    def nav_item_bottom(self, glyph, active=False):
        y = self.h - 52
        if active:
            self.d.rounded_rectangle([4, y, 44, y + 38], radius=4, fill=self.t["hover"])
            self.d.rectangle([4, y + 10, 6, y + 28], fill=self.t["sel"])
        self.d.text((16, y + 8), glyph, font=font(18), fill=self.t["text"])

    # ---- primitives -----------------------------------------------------------------------------------------
    def text(self, x, y, s, size=14, bold=False, color=None, maxw=None, underline_words=()):
        color = color or self.t["text"]
        lines = wrap(s, size, bold, maxw) if maxw else [s]
        lh = round(size * 1.45)
        for i, line in enumerate(lines):
            self.d.text((x, y + i * lh), line, font=font(size, bold), fill=color)
            if underline_words:
                for uw in underline_words:
                    pos = line.find(uw)
                    if pos >= 0:
                        x1 = x + text_width(line[:pos], size, bold)
                        x2 = x1 + text_width(uw, size, bold)
                        yy = y + i * lh + round(size * 1.25)
                        self.d.line([x1, yy, x2, yy], fill=color, width=1)
        if maxw is None and x + text_width(lines[0], size, bold) > self.w:
            self.notes.append(f"text runs past the window edge: {s[:40]!r}")
        return len(lines) * lh

    def button(self, x, y, label, accent=False, minw=0, h=32, flag=True):
        w = max(minw, text_width(label, 14) + 32)
        fill = self.t["accent"] if accent else self.t["button"]
        self.d.rounded_rectangle([x, y, x + w, y + h], radius=4, fill=fill,
                                 outline=None if accent else self.t["button_stroke"])
        tw = text_width(label, 14)
        self.d.text((x + (w - tw) / 2, y + 7), label, font=font(14), fill=self.t["accent_text"] if accent else self.t["text"])   # centred
        if flag and x + w > self.w:
            self.notes.append(f"button {label!r} is cut off at the right edge (ends at {int(x + w)} > {self.w})")
        return w

    def icon_button(self, x, y, glyph):
        self.d.rounded_rectangle([x, y, x + 32, y + 32], radius=4, outline=None)
        self.d.text((x + 9, y + 6), glyph, font=font(15), fill=self.t["text"])
        return 32

    def checkbox(self, x, y, checked=False):
        self.d.rounded_rectangle([x, y, x + 20, y + 20], radius=4, outline=self.t["button_stroke"],
                                 fill=self.t["accent"] if checked else self.t["surface"], width=1)
        if checked:
            self.d.text((x + 3, y), "✓", font=font(14), fill=self.t["accent_text"])

    def card(self, x, y, w, h):
        self.d.rounded_rectangle([x, y, x + w, y + h], radius=8, fill=self.t["card"], outline=self.t["stroke"])

    def chip(self, x, y, label, close=False, maxw=None):
        w = text_width(label, 14) + 24 + (28 if close else 0)
        self.d.rounded_rectangle([x, y, x + w, y + 30], radius=15, fill=self.t["chip"])
        self.d.text((x + 12, y + 6), label, font=font(14), fill=self.t["text"])
        if close:
            self.d.text((x + w - 24, y + 6), "×", font=font(15), fill=self.t["text"])
        return w

    def infobar(self, x, y, w, msg, kind="warn", closable=True):
        bg, st = {"warn": (self.t["warn_bg"], self.t["warn_stroke"]), "err": (self.t["err_bg"], self.t["err_stroke"]),
                  "info": (self.t["info_bg"], self.t["stroke"])}[kind]
        mark = {"warn": "⚠", "err": "⛔", "info": "ℹ"}[kind]
        lines = wrap(msg, 14, False, w - 90)
        h = max(48, len(lines) * 20 + 24)
        self.d.rounded_rectangle([x, y, x + w, y + h], radius=4, fill=bg, outline=st)
        self.d.text((x + 14, y + 14), mark, font=font(15), fill=self.t["text"])
        for i, l in enumerate(lines):
            self.d.text((x + 44, y + 14 + i * 20), l, font=font(14), fill=self.t["text"])
        if closable:
            self.d.text((x + w - 30, y + 12), "×", font=font(16), fill=self.t["text"])
        return h

    def progress(self, x, y, w, pct):
        self.d.rectangle([x, y, x + w, y + 3], fill=self.t["stroke"])
        self.d.rectangle([x, y, x + w * pct, y + 3], fill=self.t["accent"])

    def hline(self, x, y, w):
        self.d.line([x, y, x + w, y], fill=self.t["stroke"])

    def save(self, path):
        self.img.save(path)
        return self.notes
