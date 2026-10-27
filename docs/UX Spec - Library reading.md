# UX spec: Reading a book from the Library

Status: Built as ST-35 and made quiet by ST-36 (written and unit-tested; not yet built or reviewed on Windows). Your four decisions are recorded in section 11. **The rows, header and tagging described below were simplified on 2026-10-13: see "The quiet Library" below.**
Related: ST-12/ST-13 (Library page), ST-16 (Reader), ST-25 (tags), NFR-18 (right-to-left text), NFR-22 (accessibility).
Follows: no screen map and no style guide exist yet (see section 12, question 7). This spec uses the app's current look: the Fluent theme resources and the type styles already used on the Library, Ask and Reader pages.

> Mode: screen spec. Covers: what the Library does for a person who wants to read, how a book is opened and resumed, how selecting books for tagging fits beside it, books that are not ready, and the Reader's Back. Does not cover: reading features (bookmarks, notes, font size, page rendering of PDFs), a new Reader layout, the Ask page, or a style guide for the whole app.

## The quiet Library (ST-36, third round; supersedes the row layout and header below)

- **Header:** the title; a command bar with **Find a book** at its left and **Add books** and **Select**. Folded into "…" when narrow.
- **Row:** title; "author · language · Section n of m"; the status only when the book is not ready (with the reason when rejected or failed); tags as chips; **one** main button (**Continue** accent, **Read**, **Open file**; same width, text centred) and a "⋯" menu with **Tags…** and **Remove**, also the right-click menu.
- **Selecting books to tag:** **Select** turns on tick boxes (and hides the row buttons); the header shows "N selected", **Find a book**, **Add tags to N books** and **Cancel**. A click on a row ticks it; ticks stay on books the find box hides; tagging or Cancel leaves the mode.
- **Indexing:** one strip above the list ("Indexing 2 books", one overall bar, **Pause** / **Resume**) while work remains or indexing is paused; rows show only "Indexing 42%" as text.
- **Keyboard:** Tab into the find box and the command bar; arrow keys move between rows; **Enter** on a row reads (or opens the file), and in Select mode ticks or unticks it and is announced as "Press Enter to select / to clear the selection"; the row's menu opens from the "⋯" button, or Shift+F10 / the Menu key on a focused row (to be confirmed on Windows). The row's menu is not offered in Select mode.
- **Overall progress** counts the books that finished since the strip appeared as 100 %, so the bar never moves backwards.
- **Not built, backlog:** sort by recently read; "Open original file" in the menu of a ready book.
- Mock-ups: `docs/mockups/library*.png`.

## 1. Goal and context

- **Person and situation:** the owner of the library, on their own PC, who has added books and wants to read them.
- **Goal:** "I want to read the book I chose, from where I stopped." The Library's main job is reading; tagging and indexing are secondary jobs.
- **What they do today instead:** they ask a question on the Ask page and open a result, or open the original file from the Reader's **Open file** button. There is no direct way to start reading from the list of books.
- **Success looks like:** one click on a book (or on its **Read** / **Continue** button) shows its text in the Reader at the place they left; Back returns to the same place in the Library; tagging several books at once still works without opening anything by accident.

## 2. Scope

- **In:** the click on a book row and its **Read** / **Continue** / **Open file** button; remembering where the person stopped (chapter and place inside it); selecting books for tagging; books that are not ready; the Reader's Back label and destination; how the row's visual weight reflects that reading is the main job.
- **Out / backlog:** bookmarks and highlights of the reader's own; reading statistics; rendering PDF pages; text size or theme controls in the Reader; a right-click menu; a screen map and a style guide.
- **Settled earlier and kept:**
  - The Reader shows extracted text, not the original layout (a plan decision: "no PDF page rendering").
  - Tagging works on ticked books through **Add tags to selected…** and per book through **Tags…**.
  - Removing a book asks for confirmation.
  - The Reader's chapters are the book's sections that have text, in reading order. A book with no headings (many TXT and PDF files) is one section, so resuming needs a place *inside* the chapter, not only the chapter.

## 3. Place in the app

- **Reached from:** Library (navigation bar), new. The Reader is today reached only from a result on the Ask page.
- **Leads to:** the Reader; for a book that is not ready, the original file in its default program.
- **Way back:** **Back to Library** button, Esc and Alt+Left in the Reader return to the Library with the same scroll position and the book still in view. Today Back always says "Back to results" and returns to the page that opened the Reader.
- **Navigation bar:** stays on "Library" while the Reader is open from here, so the person knows where they are.

## 4. Task flow

Reading:

1. Library → the person sees their books, each with a status and, for a Ready book, a **Read** (never opened) or **Continue** (opened before) button.
2. They press the button, click the row, or arrow to the row and press Enter.
3. The Reader opens on the book at the saved place (the start the first time): title, author, chapter, "Section n of m", the text.
4. They read, moving between chapters with **Previous section** / **Next section**; the place is saved as they go. **Back to Library** returns.

Not ready (queued, indexing, rejected, failed):

1. The row's button says **Open file**. Pressing it, or clicking the row, opens the original file in the program Windows uses for it. The Reader is not used.

Tagging (unchanged in purpose, different in gesture):

1. Library → tick the box at the left of two or more books.
2. Press **Add tags to selected…**, choose tags, confirm. The ticks are cleared.

Common wrong turn this design must survive: the person clicks a row meaning to tick it (the old habit) and the book opens. Back returns to exactly where they were and nothing is lost; the visible **Read** button says what the row now does.

## 5. Screens and states

Library row, by book status:

| State | When it happens | What the person sees | What they can do |
| --- | --- | --- | --- |
| Ready, never opened | Indexing finished | Title, author, "Ready", tags, a standard **Read** button, **Tags…**, **Remove**, a tick box; hover highlight | Read (button, click, Enter). Tick for tagging. |
| Ready, opened before | As above, and a place is saved | Same, the button reads **Continue** and is the accent, and a line "Section 12 of 61" | Continue where they stopped |
| Queued | Waiting for the indexer | "Queued", **Open file** button | Open file (button or click); tick, **Tags…**, **Remove** |
| Reading the file / Indexing n% | Being processed | Status, progress bar, **Open file** button | Same as Queued |
| Rejected | The file could not be used (for example a scanned PDF) | "Rejected", the reason, **Open file** button | Open file; tick, **Tags…**, **Remove** |
| Failed | Indexing failed | "Failed", the reason, **Open file** button | Same as Rejected |
| File moved or deleted | Open file is pressed and the path no longer exists | A note in the row: "The original file was moved or deleted." | Remove, or add the book again |
| File cannot be started | Windows has no program for it, or it failed to start | A note: "The file could not be opened: {reason}" (the launcher gives one message for every failure) | Remove, or add the book again |
| Library empty | No books | Existing empty state | Add books |
| Very many books | Hundreds of rows | The list scrolls; rows keep their size | Scroll; the position survives Back |
| Long title or tags | A title or tag list wider than the row | Title is cut with an ellipsis; tags wrap | Hover shows the full title |

Reader, opened from the Library:

| State | When it happens | What the person sees | What they can do |
| --- | --- | --- | --- |
| Loading | A chapter is being fetched | Progress bar under the header | Wait; Back |
| Reading | Normal | Book, author, chapter title, "Section n of m", text | Previous / Next section, Open file, Back |
| First chapter | Position 1 | **Previous chapter** disabled | Next |
| Last chapter | Position m | **Next chapter** disabled | Previous |
| No text | The book has no chapter with text | Error bar: "This book has no readable text." | Open file, Back |
| Book gone | Removed or re-indexed since the list was drawn | Error bar: "This book could not be opened. It may have been removed." | Back |
| Saved place no longer exists | The book was re-indexed and has fewer chapters or paragraphs | Opens at the nearest place (the last chapter, or the start of the chapter) with no error | Read on |
| Original file moved | The file at its old path is missing | Existing message: "File not found. The original file was moved or deleted." (Open file disabled) | Keep reading |
| Very long chapter | Hundreds of paragraphs | Text scrolls; one paragraph per row | Scroll |

## 6. Wireframes

Library with books. The page commands are a command bar (icon and label); inside the list only **Continue** (a book in progress) is the accent button; **Read**, **Open file**, **Tags…** and **Remove** are plain. The accent **Add books** button appears only in the empty state.

```text
Library                                ⏸ Pause indexing   + Add books   ⌗ Add tags to selected…
────────────────────────────────────────────────────────────────────────────────────────
 ☐  Pride and Prejudice                                      [Continue*] [Tags…] [Remove]
    Jane Austen
    Ready · Section 12 of 61
    Tags: novels, 1800s
 ☑  Origin of Species                                        [Read]      [Tags…] [Remove]
    Charles Darwin
    Ready
 ☐  Moby Dick                                                [Open file] [Tags…] [Remove]
    Herman Melville
    Indexing 42% · you can open the original meanwhile
    ▓▓▓▓▓▓▓░░░░░░░░░
 ☐  Scanned letters.pdf                                      [Open file] [Tags…] [Remove]
    Rejected: no text could be extracted from this file.
```

(* the accent button; the page commands are a command bar that folds into "…" when narrow.) On hover a row gets the standard list hover background; the tooltip of a Ready row reads "Read this book" ("Continue reading this book" after the first time), of the others "Open the original file".

Reader opened from the Library (unchanged layout; the Back label changes):

```text
[← Back to Library]
Pride and Prejudice
Jane Austen
Chapter XII
[Previous section]  Section 12 of 61  [Next section]                         [Open file]
────────────────────────────────────────────────────────────────────────────────────────
…the paragraph where the person stopped is the first one shown.
```

## 7. Visual notes

- **Primary action, and how the screen makes it stand out:** reading is the Library's main job; **Continue** (a book in progress) is the accent button of its row, and **Read** is a standard button so a long list is not all accent. **Add books** is the accent button in the empty state only; otherwise it is a command-bar item. **Pause indexing**, **Add tags to selected…**, **Tags…** and **Remove** are plain: tagging and indexing are visibly secondary.
- **Grouping and hierarchy:** read first the title (strong), then author (secondary), status and place, then tags. The row's buttons are right-aligned in one group: primary first, then the secondary pair.
- **Density for this view:** list. Rows stay compact; the tick box adds about 32 px at the left; **Read**, **Continue** and **Open file** share one width (minimum 100 px) so the row buttons line up from row to row.
- **Departures from the style guide, and why:** none; the style guide's accent rule 2 comes from this screen.

## 8. Interaction spec

| Element | Action | Result | Keyboard | Announced as (screen reader) |
| --- | --- | --- | --- | --- |
| Book row (Ready) | Click | Opens the Reader on this book at the saved place | Arrow keys move between rows; Enter opens | "Pride and Prejudice, Jane Austen, Ready, Section 12 of 61. Press Enter to continue reading." |
| Read / Continue button | Click | Same as clicking the row | Tab to it, Enter or Space | "Continue reading Pride and Prejudice" |
| Book row (not ready, rejected, failed) | Click | Opens the original file in its default program; the Reader is not used | Enter does the same | "Moby Dick, Herman Melville, Indexing 42 percent. Press Enter to open the original file." |
| Open file button | Click | Same as clicking the row | Tab to it, Enter or Space | "Open the original file of Moby Dick" |
| Tick box | Click | Ticks or unticks this book; the row does not open | Tab to the box, Space toggles | "Select Pride and Prejudice" / "Selected" |
| Add tags to selected… | Click | Opens the tags dialog for all ticked books (as today); ticks clear afterwards | Tab to it, Enter | "Add tags to selected books" (disabled when none are ticked) |
| Tags… (per row) | Click | Opens the tag editor for this book only; the row does not open | Tab to it, Enter | "Edit this book's tags" |
| Remove (per row) | Click | Confirmation first; the row does not open | Tab to it, Enter | "Remove Pride and Prejudice" |
| Reader: Back | Click | Returns to the Library, at the same scroll position, the book still in view; the place is saved first | Esc or Alt+Left | "Back to Library" |
| Reader: Previous / Next section | Click | Loads that chapter from its top and saves it as the place | Tab, Enter | existing |
| Reader: scrolling | Scroll | The first visible paragraph is checked about once a second and saved when it changed, and when leaving | — | — |
| Reader: Open file | Click | Opens the original file in the default app (as today) | Tab, Enter | existing |

- **Focus:** after opening a book from the Library, focus goes to the chapter text; after Back, focus returns to the row that was opened (**neither is built in ST-35**: the page is kept alive so the scroll position survives, but focus is not moved; putting focus on the list risked undoing the scroll to the saved paragraph, and needs trying on Windows). Esc in the Reader does what Back does.
- **Undo and cancel:** opening a book changes nothing, so it needs no undo; Back is the way out. Removing a book is the one destructive action and keeps its confirmation dialog (it cannot be undone, and the original file is untouched). Ticks can be cleared by unticking; the tags dialog can be cancelled. The saved place is only ever moved forward by reading; there is no "reset place" control in this change.
- **Shortcuts:** Enter opens the focused row; Esc or Alt+Left leaves the Reader. No new global shortcut. Ctrl+click and Shift+click do not select (decided).

## 9. Content

| Where | Text |
| --- | --- |
| Row button, Ready, never opened | Read |
| Row button, Ready, opened before | Continue |
| Row button, not ready, rejected, failed | Open file |
| Row place line | Section {n} of {m} |
| Row status while indexing | Indexing {n}% · you can open the original meanwhile |
| Row tooltip (Ready) | Read this book / Continue reading this book |
| Row tooltip (other) | Open the original file |
| Row note, file missing | The original file was moved or deleted. |
| Row note, no program or any other failure | The file could not be opened: {reason}. |
| Reader, Back button (from the Library) | Back to Library |
| Reader, Back button (from a search result, existing) | Back to results |
| Reader error, no text | This book has no readable text. Try Open file to see the original. |
| Reader error, book gone | This book could not be opened. It may have been removed. Go back to the Library and check the list. |
| Row accessible name (Ready) | {title}, {author}, Ready. Press Enter to read. |

## 10. Accessibility and platform notes

- **Keyboard-only path:** Tab into the list, arrows to a book, Enter to read, Esc to go back; Tab to a tick box and press Space to select; Tab to **Add tags to selected…**.
- **Meaning not by colour:** the state of a row is its status text and its button label (Read, Continue, Open file), not colour; the tick box state is announced as "Selected"; the accent button is also labelled.
- **Smallest supported window:** not stated in the requirements (open question 7). Assumed 800 × 600: the three buttons stay at the right, title and status wrap, nothing needs horizontal scrolling. At that width the three buttons together take about 260 px.
- **Text scaling and display scaling:** the tick box and the buttons keep a 44 px target at 200 % text; rows grow in height, no fixed heights.
- **Light, dark and high-contrast themes:** only theme resources are used (no fixed colours); the accent button, hover and selected states use the theme's resources, which have high-contrast variants. Not checked on Windows.
- **Reduced motion:** the change adds no animation.
- **Departures from platform conventions, and why:** opening an item with a single click is what Windows 11 apps with list rows do (Settings, Store, Photos); a tick box at the left is the Fluent pattern for selecting several (as in Mail and File Explorer's "Item check boxes"). A visible Read button on every row is not usual for Windows lists but costs one column and answers "what happens if I click?".

## 11. Decisions

| Decision | Recommendation | Runner-up, and why rejected |
| --- | --- | --- |
| What a click does | **Decided by you:** click or Enter on a row reads, **and** each Ready row has a visible **Read** / **Continue** button; tagging moves to the tick box. | Click = select plus a Read button. Rejected by you: the Library's main function is reading; tagging and indexing are secondary. |
| Which chapter opens | **Decided by you:** resume is important. Built as: chapter *and* the first visible paragraph, saved as you read; the start the first time. | Chapter only. Rejected: books with no headings are one chapter, so it would resume at the start of the whole book. |
| Books not ready | **Decided by you:** the row's action is **Open file** (the original in its default program); no partial reading in the Reader. Recommended extension: the same for Rejected and Failed books, since the file is still there to open. | Open the chapters indexed so far. Rejected by you. |
| Ctrl+click / Shift+click | **Decided by you:** no; one meaning per gesture. | Select as in File Explorer. Rejected: a click would then mean read or select depending on a key. |
| Back | Returns to the Library at the same scroll position; the label says where it goes. | Always "Back to results". Rejected: false when the Reader was opened from the Library. |
| What the not-ready row does on click | Clicks the row's primary action: opens the file. | Only the button opens the file; a click does nothing. Rejected: a row that looks clickable and does nothing is worse; the button says "Open file" so it is not a surprise. |
| Telling people the row is clickable | The visible Read button, a hover highlight and a tooltip. No caption line. | A one-line caption under the title. Rejected now: the button makes it plain, and a caption would be permanent clutter. |
| Right-click menu | Not in this change. | A context menu with Read, Edit tags, Remove. Rejected for now: the buttons already exist; add it later with the keyboard menu key. |

## 12. Risks and open questions

- **Habit:** people used to click a row to select will open the Reader by accident. Back with the position kept makes this cheap, but not free.
- **Open question 1 (small, recommendation given):** is "chapter plus first visible paragraph" the right granularity for the saved place? Recommendation: yes; save it a moment after scrolling stops and when leaving, as one row per book in the database (not a settings blob), so two writes never overwrite each other's books.
- **Open question 2 (small):** a re-indexed or edited book has new chapter ids. Recommendation: store the chapter's position number and paragraph number, and clamp to the nearest place when the book has changed, with no error shown.
- **Open question 3 (needs trying on Windows):** the Library page is rebuilt when you go Back, which scrolls the list to the top and clears ticks today. Keeping the scroll position needs the page to be kept alive or its position saved.
- **Open question 4 (needs trying on Windows):** whether a button inside a clickable row reliably swallows its own click, and whether Tab reaches the buttons inside each row, in the list control used.
- **Open question 5:** launching an external program from a single click is a surprise for a row that is "not ready". Mitigated by the button label and tooltip; try it and say if a click on a not-ready row should rather do nothing.
- **Open question 6:** a book that is Ready but whose original file moved still reads fine (the text is stored); the **Open file** in the Reader says it is gone. Is that enough, or should the Library row say it too?
- **Open question 7 (project level):** the requirements have no "User experience" section (reference apps, visual style), no smallest window size and no high-contrast requirement, and there is no screen map or style guide. Without them every screen spec re-decides the look. Recommendation: write a one-page screen map first (five screens plus the wizard) and a style guide next, before the next screen is specified; both need your input on the feel you want.
- **Not known:** how long a very long book's first chapter takes to appear (a chapter is built from its chunks when opened), and how quickly the paragraph position can be restored in a very long chapter.

## 13. How to check it

1. Add three books and wait until they are Ready. Each shows a **Read** button. Click the first row → the Reader shows that book, "Section 1 of n". Scroll a little, press **Back to Library** → the same list, the first book still in view.
2. The first row now shows **Continue** and "Section 1 of n"; press it → the Reader opens at the paragraph where you left, not at the top.
3. Read on to chapter 3, go Back, close and reopen the app, press **Continue** → chapter 3 at the saved place.
4. Tick two books by their boxes → neither opens; **Add tags to selected…** is enabled. Add a tag → both show it and the ticks clear.
5. With the keyboard only: Tab into the list, arrow to a book, Enter → the Reader; Esc → back to the same book in the list.
6. Add a book and, while it shows "Indexing", press its **Open file** → the original opens in its program and the Library stays as it was.
7. Add a scanned PDF (Rejected) → its row shows **Open file** and the reason; it opens in the PDF viewer.
8. Move a book's original file and press **Open file** → the row says "The original file was moved or deleted."
9. Click **Tags…** on a Ready book → the tag editor opens and the Reader does not. Click **Remove** → the confirmation appears and the Reader does not.
10. With Narrator on, move over a Ready row → it says the title, author, "Ready", the section and "Press Enter to continue reading".
11. Set text size to 200 % and the window to its smallest size → rows, tick boxes and buttons are all reachable and nothing is cut off.
12. Switch Windows to dark mode and to a high-contrast theme → rows, buttons and the hover state are readable.

Screenshots for review:
- [ ] Normal (Library with Ready (Read and Continue), Indexing and Rejected books)
- [ ] Empty
- [ ] Loading (Reader while a chapter loads)
- [ ] Error (Reader: book gone)
- [ ] Smallest window
- [ ] Dark theme
- [ ] Large text
- [ ] A row after Open file failed (the note)
- [ ] Reader opened from the Library (Back label)

## 14. Review

Not built yet. Filled in after the screen is built, from the screenshots above.

| Severity | Finding | Screenshot | Spec line or heuristic |
| --- | --- | --- | --- |

- Looks good:
- Reviewed:
- Not checked, and why:
