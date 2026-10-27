"""Draws the Docent screens as PNG mock-ups, from the layout values and wording of the XAML pages.

Usage:  python3 scripts/mockups/screens.py docs/mockups

Hand-maintained: when a page's XAML changes, change the matching function here and re-run it, so the picture is looked at
BEFORE the page is built (see CLAUDE.md). It is an approximation: DejaVu Sans stands in for Segoe UI and controls are plain
shapes, but spacing, widths, wrapping and every label follow the XAML. Needs Pillow.
"""
import sys, os
sys.path.insert(0, os.path.dirname(__file__))
from render import Screen, LIGHT, DARK, text_width

OUT = sys.argv[1] if len(sys.argv) > 1 else "."
findings = {}


def done(s, name):
    notes = s.save(os.path.join(OUT, name))
    findings[name] = notes


def page_header(s, title, right=None):
    x = s.x0 + 32
    s.text(x, 44, title, 28, bold=True)
    return 44 + 40


# ------------------------------------------------------------------ Ask ------------------------------------------------
def ask(theme, w, h, name, empty=False, translated=True):
    s = Screen(w, h, theme, nav_active="ask")
    x, right = s.x0 + 32, w - 32
    s.text(x, 44, "Ask", 28, bold=True)
    tw = text_width("New thread", 14) + 32
    s.button(right - tw, 46, "New thread")
    y = 100
    if empty:
        s.text(x, y, "Add books first", 20, bold=True)
        y += 34
        y += s.text(x, y, "Your library has no books yet. Add some in the Library, then ask questions about them.", 14,
                    maxw=min(640, right - x))
        s.button(x, y + 8, "Go to Library", accent=True)
    else:
        # question bubble
        q = "Why did Elizabeth refuse the first proposal?"
        bw = min(right - x, text_width(q, 14, True) + 24 + 80)
        s.d.rounded_rectangle([x, y, x + bw, y + 54], radius=8, fill=s.t["chip"])
        s.text(x + 12, y + 8, q, 14, bold=True)
        s.text(x + 12, y + 30, "13 Oct 2026, 09:12", 12, color=s.t["secondary"])
        s.icon_button(x + bw - 76, y + 8, "✎")
        s.icon_button(x + bw - 40, y + 8, "⟳")
        y += 66
        cw = s.chip(x, y, "Searched as: Why did Elizabeth Bennet refuse Mr Collins's proposal?", close=True)
        # the filter chip is hidden while it repeats the filter line at the bottom (UX design issue 15)
        y += 38
        if translated:
            # DCR-11: the question was also searched in the other language of the library (a French book here: the mock-up font
            # has no Chinese glyphs); the chip sits on its own row under the first and has no remove button
            s.chip(x, y, "Also searched: Pourquoi Elizabeth Bennet a-t-elle refusé la demande de M. Collins ?")
            y += 38
        else:
            y += 6
            y += s.infobar(x, y, min(900, right - x),
                           "The language model could not write this question in the other languages of your books, so it was searched "
                           "only as typed. Books in other languages may be missing from the results.", "warn") + 10
        s.text(x + 28, y, "Results", 14)
        s.d.text((x + 6, y), "▾", font=__import__("render").font(14), fill=s.t["text"])
        y += 30
        results = [
            ("Pride and Prejudice", "Jane Austen · Chapter XIX · pp. 112–115",
             "Elizabeth refuses him because she does not respect him; she says she could not make him happy, "
             "and he could not make her so.",
             "… he was not the man to be kept back by a refusal. \"Do not consider me now as an elegant female…\"", ["refusal"]),
            ("Pride and Prejudice", "Jane Austen · Chapter XXXIV · p. 190",
             "This passage doesn't cover your question.", "\"You are mistaken, Mr. Darcy, if you suppose that the mode of your declaration…\"", []),
            ("Emma", "Jane Austen · Chapter VII",
             "Emma advises Harriet to decline Mr Martin's offer, which Harriet does after a long conversation.",
             "… the refusal of a proposal she had meant to accept was a disappointment to Mr. Martin …", ["refusal", "proposal"]),
        ]
        for title, where, summary, excerpt, under in results:
            if y > h - 190:
                break
            s.text(x, y, title, 14, bold=True)
            y += 20
            s.text(x, y, where, 14, color=s.t["secondary"])
            y += 22
            hgt = s.text(x, y, summary, 14, maxw=min(900, right - x),
                         color=s.t["secondary"] if summary.startswith("This passage") else None)
            y += hgt + 4
            hgt = s.text(x, y, excerpt, 14, color=s.t["secondary"], maxw=min(900, right - x), underline_words=under)
            y += hgt + 14
    # bottom bar
    by = h - 100
    s.button(x, by, "Filter…")
    fx = x + text_width("Filter…", 14) + 32 + 8
    ftxt = "Searching in: novels" if not empty else "Searching all books"
    s.text(fx, by + 6, ftxt, 14, color=s.t["secondary"])
    if not empty:
        s.button(fx + text_width(ftxt, 14) + 8, by, "Clear")
    by += 44
    s.d.rounded_rectangle([x, by, right - 100, by + 34], radius=4, fill=s.t["surface"], outline=s.t["button_stroke"])
    s.text(x + 10, by + 7, "Ask a question about your books", 14, color=s.t["secondary"])
    s.button(right - 90, by, "Search", accent=not empty)
    done(s, name)


# ------------------------------------------------------------------ Library (quiet rows) -----------------------------
def library(theme, w, h, name, mode="normal"):
    """The Library: one main button per row, a menu for the rest, tick boxes only in selection mode, one indexing strip
    (no bar on the rows), a find box. mode: normal | select | menu | empty"""
    from render import font
    s = Screen(w, h, theme, nav_active="library")
    x, right = s.x0 + 32, w - 32
    s.text(x, 44, "Library", 28, bold=True)
    if mode == "empty":
        y = 100
        s.text(x, y, "Your library is empty", 20, bold=True)
        y += 34
        y += s.text(x, y, "Add books to search them. Everything stays on this PC.", 14, maxw=right - x)
        s.text(x, y + 4, "You can also drag books onto this page.", 14, color=s.t["secondary"])
        s.button(x, y + 40, "Add books", accent=True)
        done(s, name)
        return
    # header: find box + commands (selection mode swaps the commands)
    cx = right
    if mode == "select":
        for glyph, label in reversed([("⌗", "Add tags to 2 books"), ("×", "Cancel")]):
            wd = text_width(label, 14) + 44
            cx -= wd
            s.d.text((cx + 8, 48), glyph, font=font(15), fill=s.t["text"])
            s.d.text((cx + 30, 51), label, font=font(14), fill=s.t["text"])
        fw = 200 if w < 1000 else 240
        fx = cx - fw - 16
        s.d.rounded_rectangle([fx, 46, fx + fw, 78], radius=4, fill=s.t["surface"], outline=s.t["button_stroke"])
        s.d.text((fx + 10, 53), "Find a book", font=font(14), fill=s.t["secondary"])   # still there: find the books to tick
        s.text(x + text_width("Library", 28, True) + 24, 52, "2 selected", 14, color=s.t["secondary"])
        if fx < x + text_width("Library", 28, True) + 24 + text_width("2 selected", 14) + 8:
            s.notes.append("selection header is too wide: the find box collides with the title or the count")
    else:
        for glyph, label in reversed([("+", "Add books"), ("☑", "Select")]):
            wd = text_width(label, 14) + 44
            cx -= wd
            s.d.text((cx + 8, 48), glyph, font=font(15), fill=s.t["text"])
            s.d.text((cx + 30, 51), label, font=font(14), fill=s.t["text"])
        fw = 200 if w < 1000 else 240
        fx = cx - fw - 16
        s.d.rounded_rectangle([fx, 46, fx + fw, 78], radius=4, fill=s.t["surface"], outline=s.t["button_stroke"])
        s.d.text((fx + 10, 53), "Find a book", font=font(14), fill=s.t["secondary"])
    menu_at = None
    y = 100
    # indexing strip (only while work is running)
    s.d.rounded_rectangle([x, y, right, y + 44], radius=4, fill=s.t["card"], outline=s.t["stroke"])
    s.text(x + 14, y + 11, "Indexing 2 books", 14)
    s.progress(x + 230, y + 20, 200, 0.42)
    bw = text_width("Pause", 14) + 32
    s.button(right - bw - 8, y + 6, "Pause")
    y += 60
    books = [
        ("Pride and Prejudice", "Jane Austen · EN · Section 12 of 61", ["novels", "1800s"], "Continue", ""),
        ("On the Origin of Species", "Charles Darwin · EN", [], "Read", ""),
        ("Moby Dick", "Herman Melville · EN", [], "Open file", "Indexing 42%"),
        ("scanned-letters.pdf", "", [], "Open file", "Rejected: no text could be extracted from this file."),
    ]
    for i, (title, meta, tags, primary, status) in enumerate(books):
        top = y
        if mode == "select":
            s.checkbox(x, y + 18, checked=(i < 2))
        tx = x + (32 if mode == "select" else 0)
        btn_w = 100 + 8 + 32
        content_w = right - tx - btn_w - 16
        ty = y + 10
        ty += s.text(tx, ty, title, 14, bold=True, maxw=content_w)
        if meta:
            ty += s.text(tx, ty, meta, 14, color=s.t["secondary"], maxw=content_w)
        if status:
            ty += s.text(tx, ty, status, 14, color=s.t["secondary"], maxw=content_w)
        if tags:
            tcx = tx
            for t in tags:
                tcx += s.chip(tcx, ty + 2, t) + 6
            ty += 36
        row_h = max(ty - y + 10, 60)
        if mode != "select":
            by = y + (row_h - 32) // 2
            s.button(right - btn_w, by, primary, accent=(primary == "Continue"), minw=100)
            s.d.text((right - 24, by + 4), "⋯", font=font(18), fill=s.t["text"])
        y += row_h
        s.hline(x, y, right - x)
        if mode == "menu" and i == 0:
            menu_at = (right - 150, top + row_h // 2 + 22)
    if menu_at:                                    # drawn last, so it sits on top of the rows below it
        mx, my = menu_at
        s.d.rounded_rectangle([mx + 3, my + 3, mx + 143, my + 83], radius=6, fill=s.t["stroke"])      # a soft shadow
        s.d.rounded_rectangle([mx, my, mx + 140, my + 80], radius=6, fill=s.t["surface"], outline=s.t["button_stroke"])
        s.text(mx + 16, my + 10, "Tags…", 14)
        s.text(mx + 16, my + 46, "Remove", 14)
        s.d.line([mx + 8, my + 40, mx + 132, my + 40], fill=s.t["stroke"])
    done(s, name)


# ------------------------------------------------------------------ Reader ---------------------------------------------
def reader(theme, w, h, name):
    s = Screen(w, h, theme, nav_active="library")
    x = s.x0 + 32
    s.button(x, 40, "← Back to Library")
    y = 90
    s.text(x, y, "Pride and Prejudice", 28, bold=True)
    y += 40
    s.text(x, y, "Jane Austen", 14, color=s.t["secondary"])
    y += 24
    s.text(x, y, "Chapter XIX", 20, bold=True)
    y += 34
    b1 = s.button(x, y, "Previous section")
    s.text(x + b1 + 16, y + 6, "Section 12 of 61", 14, color=s.t["secondary"])
    nx = x + b1 + 16 + text_width("Section 12 of 61", 14) + 16
    s.button(nx, y, "Next section")
    s.button(w - 32 - text_width("Open file", 14) - 32, y, "Open file")
    y += 52
    colw = min(820, w - x - 32)
    cx0 = x + (w - x - 32 - colw) // 2          # the reading column is centred
    paras = [
        "The next morning opened a new scene at Longbourn. Mr. Collins made his declaration in form. Having resolved to do it without loss of time, as his leave of absence extended only to the following Saturday, he proceeded to do it with great composure.",
        "\"Believe me, my dear Miss Elizabeth, that your modesty, so far from doing you any disservice, rather adds to your other perfections. You would have been less amiable in my eyes had there not been this little unwillingness.\"",
        "\"I am not now to learn,\" replied Mr. Collins, with a formal wave of the hand, \"that it is usual with young ladies to reject the addresses of the man whom they secretly mean to accept, when he first applies for their favour.\"",
    ]
    for i, p in enumerate(paras):
        lines_h = len(__import__("render").wrap(p, 16, False, colw)) * 26
        if i == 1:
            s.d.rectangle([cx0 - 4, y - 2, cx0 + colw + 4, y + lines_h + 2], fill=s.t["highlight"])
        s.text(cx0, y, p, 16, maxw=colw, underline_words=["refuse"] if i == 2 else [])
        y += lines_h + 12
        if y > h - 40:
            break
    done(s, name)


# ------------------------------------------------------------------ History --------------------------------------------
def history(theme, w, h, name):
    s = Screen(w, h, theme, nav_active="history")
    x, right = s.x0 + 32, w - 32
    s.text(x, 44, "History", 28, bold=True)
    y = 100
    threads = [
        ("Why did Elizabeth refuse the first proposal?", "3 questions · last 13 Oct 2026, 09:40", True),
        ("What is natural selection?", "1 question · 12 Oct 2026, 18:02", False),
        ("Who is Mr Darcy?", "2 questions · 10 Oct 2026, 21:15", False),
    ]
    for title, details, expanded in threads:
        hh = 62
        s.d.rounded_rectangle([x, y, right - 40, y + hh], radius=4, fill=s.t["card"], outline=s.t["stroke"])
        s.text(x + 16, y + 10, title, 14, bold=True)
        s.text(x + 16, y + 32, details, 14, color=s.t["secondary"])
        s.d.text((right - 70, y + 20), "▾" if expanded else "▸", font=__import__("render").font(14), fill=s.t["text"])
        s.icon_button(right - 32, y + 10, "×")
        y += hh
        if expanded:
            qs = [("Why did Elizabeth refuse the first proposal?", "13 Oct 2026, 09:12",
                   "Pride and Prejudice, Chapter XIX: Elizabeth refuses him because she does not respect him.\n\nEmma, Chapter VII: Emma advises Harriet to decline the offer."),
                  ("And Mr Darcy's first proposal?", "13 Oct 2026, 09:40", "Pride and Prejudice, Chapter XXXIV: She rejects it, citing his treatment of Wickham.")]
            y += 8
            for q, when, summ in qs:
                s.text(x + 16, y, q, 14, bold=True)
                y += 20
                s.text(x + 16, y, when, 14, color=s.t["secondary"])
                y += 22
                y += s.text(x + 16, y, summ, 14, maxw=min(900, right - x - 40)) + 12
            s.button(x + 16, y, "Continue this thread")
            y += 48
        y += 8
    done(s, name)


# ------------------------------------------------------------------ Settings -------------------------------------------
def settings(theme, w, h, name, error=False):
    s = Screen(w, h, theme, nav_active="settings")
    x = s.x0 + 32
    cw = min(640, w - x - 32)
    s.text(x, 44, "Settings", 28, bold=True)
    y = 92
    msg = "Some files could not be deleted (The process cannot access the file). Close other programs using them and try again."
    def card(title, rows_h):
        nonlocal y
        s.card(x, y, cw, rows_h)
        s.text(x + 16, y + 14, title, 20, bold=True)
        return y + 52
    cy = card("Search and summaries", 410)
    s.text(x + 16, cy, "Passages per search (1 to 10)", 14); s.d.rectangle([x + 16, cy + 22, x + 256, cy + 54], outline=s.t["button_stroke"]); s.text(x + 24, cy + 28, "3", 14); cy += 66
    s.text(x + 16, cy, "Earlier questions used to understand a follow-up (0 to 3)", 14, maxw=cw - 32); s.d.rectangle([x + 16, cy + 22, x + 256, cy + 54], outline=s.t["button_stroke"]); s.text(x + 24, cy + 28, "1", 14); cy += 66
    cy += s.text(x + 16, cy, "Write a summary automatically after each search:  On", 14, maxw=cw - 32) + 10
    cy += s.text(x + 16, cy, "Also search in the other languages of your books:  On", 14, maxw=cw - 32) + 4
    cy += s.text(x + 16, cy, "The language model writes your question in the other languages in your library, so books in those languages are "
                 "found too. The question and its translations are shown above the results. This needs the automatic summary above to be on.", 14, maxw=cw - 32, color=s.t["secondary"]) + 10
    s.text(x + 16, cy, "Text around each result in a summary", 14); s.d.rectangle([x + 16, cy + 22, x + 256, cy + 54], outline=s.t["button_stroke"]); s.text(x + 24, cy + 28, "Medium", 14)
    y += 422
    cy = card("Models", 190)
    s.text(x + 16, cy, "Model size", 14); s.d.rectangle([x + 16, cy + 22, x + 256, cy + 54], outline=s.t["button_stroke"]); s.text(x + 24, cy + 28, "4 GB model", 14); cy += 60
    s.text(x + 16, cy, "Recommended for this PC: 4 GB model", 14, color=s.t["secondary"]); cy += 22
    s.text(x + 16, cy, "Changing the model size reloads the app. If the other model is not on this PC yet, you are asked to download it.", 14, maxw=cw - 32, color=s.t["secondary"])
    y += 202
    cy = card("Indexing", 110)
    s.text(x + 16, cy, "Share of the CPU that indexing may use (%)", 14); s.d.line([x + 16, cy + 36, x + 316, cy + 36], fill=s.t["accent"], width=3)
    y += 122
    s.d.rounded_rectangle([x, y, x + cw, y + 48], radius=4, fill=s.t["card"], outline=s.t["stroke"])
    s.text(x + 16, y + 13, "Advanced", 14, bold=True); s.d.text((x + cw - 30, y + 13), "▸", font=__import__("render").font(14), fill=s.t["text"])
    y += 60
    cy = card("Your data", 250 if error else 180)
    s.text(x + 16, cy, "Data folder", 14, bold=True); s.text(x + 16, cy + 20, "C:\\Users\\you\\AppData\\Local\\BookSearch", 14); cy += 50
    if error:
        cy += s.infobar(x + 16, cy, cw - 32, msg, "err") + 12
    s.button(x + 16, cy, "Clear all history", flag=False); cy += 44
    s.button(x + 16, cy, "Delete all data…", flag=False)
    if y + 180 > h:
        s.notes.append("Your data card is below the fold at this height (the page scrolls)")
    done(s, name)


# ------------------------------------------------------------------ Wizard ---------------------------------------------
def wizard(theme, w, h, name, step="models"):
    s = Screen(w, h, theme, nav=False)
    x = 48
    cw = min(640, w - 96)
    if step == "models":
        s.text(x, 52, "Get the models", 28, bold=True)
        y = 100
        y += s.text(x, y, "Docent needs two models: one to find passages by meaning, one to write short summaries. They are downloaded once and then work offline.", 14, maxw=cw) + 12
        for n, size, pct, status in [("Qwen3-Embedding-0.6B", "0.6 GB", 1.0, "Done"), ("Qwen3.5-4B (Q4_K_M)", "2.7 GB", 0.45, "Downloading… 45%")]:
            s.text(x, y, n, 14, bold=True); s.text(x + cw - text_width(size, 14), y, size, 14)
            s.progress(x, y + 26, cw, pct)
            s.text(x, y + 36, status, 14)
            y += 72
        bx = x
        bx += s.button(bx, y, "Download", accent=True) + 8
        bx += s.button(bx, y, "Cancel") + 8
        s.button(bx, y, "Import from file…")
    s.button(w - 48 - 80, h - 64, "Back")
    s.button(w - 48 - 80 - 8 - 96, h - 64, "Next", accent=True)
    done(s, name)


if __name__ == "__main__":
    os.makedirs(OUT, exist_ok=True)
    ask(LIGHT, 1280, 800, "ask.png")
    ask(LIGHT, 1280, 800, "ask-empty.png", empty=True)
    ask(DARK, 1280, 800, "ask-dark.png")
    ask(LIGHT, 1280, 800, "ask-not-translated.png", translated=False)
    ask(LIGHT, 800, 600, "ask-800x600.png")
    library(LIGHT, 1280, 800, "library.png")
    library(LIGHT, 1280, 800, "library-empty.png", mode="empty")
    library(DARK, 1280, 800, "library-dark.png")
    library(LIGHT, 800, 600, "library-800x600.png")
    library(LIGHT, 1280, 800, "library-select.png", mode="select")
    library(LIGHT, 800, 600, "library-select-800x600.png", mode="select")
    library(LIGHT, 1280, 800, "library-menu.png", mode="menu")
    reader(LIGHT, 1280, 800, "reader.png")
    reader(LIGHT, 800, 600, "reader-800x600.png")
    history(LIGHT, 1280, 800, "history.png")
    settings(LIGHT, 1280, 800, "settings.png")
    settings(LIGHT, 1280, 1000, "settings-error.png", error=True)
    wizard(LIGHT, 1280, 800, "wizard-models.png")
    for k, v in findings.items():
        for n in v:
            print(f"{k}: {n}")
