# Docent: UX design

Status: Decisions of 2026-10-13 applied, including the five layout findings from the mock-ups (section 8). Mock-ups of every screen are in `docs/mockups/` (regenerate with `python3 scripts/mockups/screens.py docs/mockups`).
Mode: screen map and cross-screen rules for the whole app (the style guide is `docs/Style Guide.md`; per-screen specs, so far only `docs/UX Spec - Library reading.md`, follow both).
Sources of truth, in this order: `docs/Software Requirements.md` (requirements, user flows), `docs/Stories and Test Plan.md` (what was decided and built), `docs/Technical Design.md` (UI table), `CLAUDE.md`, then the built pages (`src/BookSearch.App/Pages/*.xaml`) and `Strings/en-US/Resources.resw`. Where they disagree, section 8 says so and nothing is silently picked.
Not covered: visual styling (style guide) and per-screen interaction tables (screen specs). Nothing here was seen on a real screen, because the app only builds and runs on Windows: the images in `docs/mockups/` are drawn from the XAML values (spacing, widths, wording), with a stand-in font, and are used for review in place of screenshots.

## 1. Who it is for and what it must feel like

- **Person:** one reader or learner with tens to a few hundred books on one Windows PC, who wants to revisit ideas across them and also read them (requirements overview, US-4 to US-17).
- **Main jobs, in the order the person comes to them:** get the app ready once (wizard); add books; ask a question and see the passages; read a book or a passage in context; come back to earlier questions.
- **Principles** (each comes from a requirement or a past decision):
  1. **Private and offline.** Nothing leaves the PC; the UI says so up front and never asks for an account (G1, NFR-9).
  2. **Passages first.** Search results never wait for the language model; summaries fill in afterwards (FR-27).
  3. **Never invent.** A summary says "doesn't cover your question" rather than guess; the library not covering a topic is stated, with the nearest passages (G3, FR-25).
  4. **Calm, text-first.** A reading tool: few controls, plain wording, no decoration that competes with the text.
  5. **Everything reachable by keyboard and a screen reader**, and readable at large text and in dark and high-contrast themes (NFR-22).
  6. **Nothing destructive without a confirmation**, and the original book files are never touched (NFR-11).

## 2. Screen map

```text
                               ┌─ start-up decision (Startup.Decide) ─┐
                               │                                       │
        models missing ────────┤                                       ├──── all fine
                               ▼                                       ▼
                ┌────────────────────────────┐             ┌──────────────────────────────────────┐
  (error) ────► │ Start-up error             │             │ Shell: navigation bar + content      │
                │ message + detail           │             │                                      │
                └────────────────────────────┘             │  Ask ◄─── home (every start)         │
                                                           │   │   ▲                              │
                ┌────────────────────────────┐             │   │   └── Ctrl+F from anywhere       │
                │ First-run wizard           │──finish────►│   ▼ click a result                   │
                │ 1 Privacy  2 Hardware      │             │  Reader ◄── Read / Continue ◄── Library
                │ 3 Models   4 Done          │             │   │  Back                            │
                └────────────────────────────┘             │   ▼                                  │
                       ▲                                   │  History ── Continue this thread ──► Ask
                       │ "Delete all data"                 │  Library ── Open file ──► original file in another program
                       └───────────────────────────────────┤  Settings (bottom of the bar)        │
                                                           └──────────────────────────────────────┘
        Dialogs (over the page): remove a book · edit tags · add tags to selected · search filter ·
        delete a thread · clear all history · delete all data
```

| Screen | Goal it serves | Reached from | Way back |
| --- | --- | --- | --- |
| First-run wizard | Get the models in place and explain privacy, once | App start when models are missing; after "Delete all data" | Back button between steps; **Get started** enters the shell on Ask |
| Start-up error | Say why the app can't start, with the detail to report | A failure while opening the library | Close the app |
| **Ask** | Ask a question, see passages and a short summary of each | Navigation bar; Ctrl+F; History's "Continue this thread" | Navigation bar |
| **Reader** | Read a book or a passage in context | A result on Ask; Library's Read / Continue | **Back** button, Esc, Alt+Left |
| **Library** | Add, read, tag and remove books; watch indexing | Navigation bar | Navigation bar |
| **History** | Re-read earlier conversations; continue or delete one | Navigation bar | Navigation bar |
| **Settings** | Search, models, indexing and data options; delete data | Navigation bar (bottom) | Navigation bar |

Order in the navigation bar, top to bottom: **Ask, History, Library**, then **Settings** at the bottom. The Reader has no entry of its own.

## 3. Navigation and global actions

| Action | Where it lives | Behaviour |
| --- | --- | --- |
| Go to a page | Navigation bar (icons: Find, Clock, Library, Setting; each has a tooltip and a spoken name) | Replaces the page; Ask keeps its thread; Library should keep its scroll position (built, not yet seen on Windows) |
| Search | The question box at the bottom of Ask; **Ctrl+F** anywhere opens Ask with the cursor in the box | Enter searches |
| Stop a summary | **Esc** on Ask | Stops the newest summary still waiting or being written |
| Leave the Reader | **Back** button, **Esc**, **Alt+Left** | Returns to the page that opened it |
| New conversation | **New thread** on Ask | Starts a fresh conversation; the old one stays in History |
| Add books | **Add books** on Library, or drop files on the Library page | Rows appear as Queued |
| Settings and data deletion | Settings page | Changes apply at once, except where a note says "next start" or "new books only" |
| Help / About / version | **Not present**; backlog story ST-37 "Production readiness" (issue 10) | |

## 4. Screens

Per screen: the goal, what is on it, the states that matter, and where it stands against the other documents.

### 4.1 First-run wizard

- **Goal:** the person understands that everything stays on this PC, picks a model size, and gets the models onto the PC (download or import).
- **Steps:** (1) privacy note, (2) RAM check and a choice of model size with a recommended badge and a warning below the minimum, (3) download with per-file progress, Cancel, Import from file and, when downloads are switched off, "Allow downloads", (4) "You're ready".
- **States:** below the minimum RAM (warning); download in progress; paused or failed with a retry; downloads not allowed; models imported from disk.
- **Primary action:** the accent button on the right (Next, Download, **Get started**).
- **Against the docs:** the requirements and the app now agree: first run ends on Ask (decision 1).

### 4.2 Ask

- **Goal:** ask a question in my own words and get the best passages from my books, each with a short summary I can trust, then follow up.
- **On the page:** title and **New thread**; notices (no books yet, books still indexing, only some books indexed); the conversation: each question as a bubble (with edit and refresh icons on the newest), chips for "Searched as…" and the search filter (one row) and, under them, "Also searched: …" when the question was also written in the other languages of the library (no remove button), warnings, a list of results (title, author, place, a 2-3 sentence summary, an excerpt with the rare question words underlined); the chips share one row; the **Filter…** button with what is searched and **Clear**; the question box and **Search**.
- **States:** no books; still indexing; searching; results with summaries waiting or writing; a result not covering the question (stays, says so); the library not covering the question (banner, nearest passages, no summaries); keyword-only because the embedding model is not ready (warning); summaries off (results only); a follow-up that could not be rewritten (warning); a question that could not be translated (warning "The language model could not write this question in the other languages of your books, so it was searched only as typed. Books in other languages may be missing from the results."; mock-up `ask-not-translated.png`; with automatic summaries off nothing is translated and nothing is said); stopped or failed summaries (each unfinished result says why); a thread restored from History or after a restart (text only, no results).
- **Primary action:** **Search**; in the no-books state **Go to Library**.
- **Spec status:** behaviour is in stories ST-14 to ST-15, ST-23 to ST-28, ST-30 to ST-34; there is no single screen spec yet.

### 4.3 Reader

- **Goal:** read the text of a book (or the passage a search found) in context, and move through the book.
- **On the page:** Back, title, author, chapter title, page range (PDF), **Previous / Next section** with "Section n of m", **Open file**, the text one paragraph per row (the opened passage on a soft background, the question's rare words underlined).
- **States:** loading; reading; first or last chapter; no readable text; book gone; original file moved (Open file disabled with a note).
- **Primary action:** reading itself; **Next section** is the main control.
- **Spec status:** `docs/UX Spec - Library reading.md` (built as ST-35).

### 4.4 Library

- **Goal:** read my books, and keep them in order (add, tag, remove) while they index, without the page asking me to do all of that at once.
- **On the page:** the title; a command bar whose left part is **Find a book** and whose commands are **Add books** and **Select** (in selection mode: "N selected", **Add tags to N books**, **Cancel**); notices (duplicates, unsupported files, worker stopped, running as administrator); one **indexing strip** while work remains ("Indexing 2 books", one overall bar, **Pause** / **Resume**); the list.
- **A row** is quiet: title; author · language · "Section 12 of 61"; the status only when the book is not ready (with the reason when rejected or failed); tags as small chips; one main button (**Continue**, **Read** or **Open file**) and a "⋯" menu with **Tags…** and **Remove** (also the row's context menu). Tick boxes appear only in selection mode, where a click on a row ticks it and the row's buttons are hidden.
- **States:** empty (accent **Add books**, drop hint); queued; indexing n %; ready; rejected or failed with a reason; file moved or cannot start (a note); paused (strip says so); find matched nothing; selection mode.
- **Primary action:** **Continue** (accent) on a book in progress; the accent **Add books** in the empty state.
- **Spec status:** `docs/UX Spec - Library reading.md`; the quiet layout is ST-36 (third round).

### 4.5 History

- **Goal:** find an earlier conversation and pick it up again, or throw it away.
- **On the page:** one expander per conversation (first question, number of questions, time of the last); opened, each question with its time, status and summary text; **Continue this thread**; a close icon that deletes the thread after a confirmation.
- **States:** empty; entries still waiting or being written (status line, filled in as they finish); stopped, failed or not covered (status line).
- **Primary action:** **Continue this thread**.
- **Spec status:** ST-23, ST-24. The summaries are plain text; results are not stored (decision 8: a reopened thread shows its summaries as text, like History).

### 4.6 Settings

- **Goal:** change how the app behaves, and see or delete my data.
- **On the page:** cards for **Search and summaries** (results per search, earlier questions for follow-ups, automatic summaries, search in the other languages of my books, summary context size), **Models** (model size with a note that changing it reloads the app, allow downloads) and **Indexing** (CPU cap); a folded **Advanced** section (language model memory, chunk size and overlap); **Your data** (data folder, database version, **Clear all history**, **Delete all data**).
- **States:** a setting that does not apply at once says when; Delete all data refused (an error InfoBar at the top).
- **Primary action:** none: changes apply as they are made.
- **Spec status:** ST-18, ST-26, ST-30, ST-31, ST-36.

### 4.7 Dialogs

Remove a book (confirm); edit tags of one book and add tags to several (one text box, comma separated, with tags already in use shown); search filter (tags and books, with a find box and **Apply** / **Search all books**); delete a thread; clear all history; delete all data (keep models or delete them too). Each has a primary button that names the action ("Remove", "Clear history", "Apply") and **Cancel**.

## 5. Shared patterns

| Situation | Pattern | Where it is used today | Gap |
| --- | --- | --- | --- |
| Nothing here yet | A short title and one sentence saying what to do next, plus the action when there is one | Library, Ask (with **Go to Library**), History | — |
| Working | A progress bar (indeterminate or with a percentage) plus words saying what; never a spinner alone | The Library's indexing strip, wizard, Ask search, Reader | — |
| A notice that matters but does not block | An **InfoBar** at the top of the page, with a severity and a close button | Library, Ask, wizard, Reader, Settings | — |
| A failure of one thing | The message sits where the result would have been, says what went wrong and what to do next | Result summaries, Library rows, Reader | — |
| Destructive or irreversible | A dialog whose title names the thing and whose primary button repeats the verb | Remove, delete thread, clear history, delete all | — |
| Something applies later | A note under the control saying when; a setting with no note applies at once | Chunk size ("new books only"), language model memory ("next start"), model size ("reloads the app") | — |
| Long text in a row | Trim with an ellipsis and show the full text on hover | Library titles | — |

**Vocabulary** (use these words; the right column lists what to avoid):

| Use | Avoid |
| --- | --- |
| book, library | document, file (except "original file" / "Open file") |
| passage (a piece of a book a search found), result (one entry in the list) | chunk, hit, source |
| summary | answer (the app does not answer from its own knowledge) |
| **thread** (the buttons say "New thread", "Continue this thread", "Delete this thread"; the two strings that said "conversation" now say "thread") | conversation, session, chat |
| indexing | embedding, vectorizing |
| section | chapter (a book with no headings has sections only) |

**Tone:** plain, second person, no jargon, no exclamation marks; errors say what happened and what to do; the privacy promise is stated as a fact ("Everything stays on this PC"), not as marketing.

## 6. Display conditions and accessibility

| Condition | Rule | Status |
| --- | --- | --- |
| Smallest window | 800 × 600 (NFR-22, decision 3); nothing needs horizontal scrolling; rows wrap | Not set in the app yet (issue 13) |
| Text scaling to 200 % and display scaling | Layouts have no fixed heights; text uses theme sizes | Reader fixes 16 px / 26 px line height; checked only by reading the XAML |
| Light and dark | Theme resources only, no fixed colours (a test scans the XAML) | Built; not seen on screen |
| High contrast | Same resources, so the platform swaps the colours | Required by NFR-22; not seen |
| Keyboard only | Every control reachable; Ctrl+F, Enter, Esc, Alt+Left; Enter on a Library row reads | Built; tab order is document order |
| Screen reader | A name for every interactive control (a test checks the XAML); result and Library rows read as one sentence | Built; not heard on Narrator |
| Right-to-left and CJK text | Excerpts, summaries, questions and the Reader follow the text's direction | Built (ST-19) |
| Reduced motion | The app adds no animation of its own | — |
| English only | All strings in `Resources.resw` (NFR-17) | Built |

## 7. How the pieces fit: the three main journeys

1. **First use:** wizard (privacy, model size, download) → Library (empty state: "Add your books") → add books → rows turn Ready one by one → click a book to read, or go to Ask and ask.
2. **Ask and read:** Ask → type, Enter → passages appear at once, summaries fill in → click a result → Reader at that passage → Back → ask a follow-up (it is rewritten to stand alone; a chip shows how).
3. **Come back later:** History → open a conversation → **Continue this thread** → Ask shows it (as text if it was from an earlier run) → ask on; or Library → **Continue** → the book at the chapter and paragraph where I stopped.

## 8. Decisions (2026-10-13) and open issues

### Decided and applied

| # | Issue | Decision | Where it landed |
| --- | --- | --- | --- |
| 1 | Home screen and what first run hands over to | **Ask is home on every start**, including after the wizard; the empty Ask page has a **Go to Library** button; the wizard's last button is **Get started** | ST-36; requirements UX flows |
| 2 | Navigation shows icons only | **Keep icons only**; tooltips and spoken names are enough | Nothing to change; style guide |
| 3 | No smallest window, no high-contrast requirement | **800 × 600, 200 % text, high contrast** added to NFR-22; the minimum window size still has to be set in the app (issue 13) | Requirements NFR-22 |
| 4 | "Section/chapter" and "thread/conversation" | **Section** and **thread** everywhere | ST-36 (Reader buttons, two strings) |
| 5 | Two accent buttons on Library | **Add books is the accent only while the library is empty**; with books, the rows' Read / Continue is the accent (but see issue 14) | ST-36 |
| 6 | Expert options next to everyday ones | A folded **Advanced** section (language model memory, chunk size, overlap) | ST-36 |
| 7 | When settings apply | **Default is "applies at once"**; anything that needs a restart or only affects new books says so; "applies to the next summary" notes were removed; model size says it reloads the app | ST-36; requirements FR-30 |
| 8 | Reopened thread has no results | **Fine**; the summaries show as text, **like History** | Requirements FR-42 |
| 9 | Errors in three styles | **InfoBars everywhere**: Settings now uses one, inside the Your data card above the buttons | ST-36 |
| 10 | No About or help | **Not now**; a backlog story for production readiness (About card with version and licences, shortcuts) | Stories: ST-37 (backlog) |
| 11 | Requirements out of date | **Updated**: a User experience section, the flows, FR-9, NFR-22, FR-30, FR-42 | Requirements |
| 12 | Nothing seen on a screen | **Reviewed from the XAML with drawn mock-ups** in `docs/mockups/` instead of screenshots; real screenshots still welcome later | This section |

### The quiet Library (2026-10-13, "everything looks good", then "Go, build it")

The owner found the Library chaotic: it was the entry for reading, tagging, indexing, adding and removing, and every row showed every tool. Decided and built: one main button per row, **Tags…** and **Remove** in a "⋯" menu, tick boxes only in a **Select** mode (which keeps **Find a book**), indexing shown once in a strip with Pause, a find box, tags as chips, no "Ready" line. Mock-ups: `library.png`, `library-select.png`, `library-menu.png`, `library-800x600.png`, `library-select-800x600.png`, `library-dark.png`, `library-empty.png`. Open: sort by recently read (backlog).

### Findings from the mock-ups: decided and applied (2026-10-13, "go with your recommendations")

| # | Finding | Decision | Where it landed |
| --- | --- | --- | --- |
| 13 | The window had no minimum or default size | Minimum **800 × 600**, default **1100 × 750** (scaled by the display), `WindowSizing` | ST-36; `MainWindow` |
| 14 | A long Library list was a column of accent buttons | Only **Continue** (a book in progress) is the accent; **Read** is a standard button | ST-36; Library row |
| 15 | Ask spent about 190 px before the first result; the filter chip repeated the filter line | The two chips share **one row** (the "Searched as" chip gives way with an ellipsis); the filter chip is hidden on the newest question while it equals the filter line | ST-36; Ask turn |
| 16 | The Library header could not wrap | The page commands are in a **CommandBar** that moves what does not fit behind "…"; the accent **Add books** button lives in the empty state (superseded in part by the quiet Library: Pause moved to the indexing strip, tagging to Select mode) | ST-36; Library header |
| 17 | The Reader's text column hugged the left edge | The reading column is **centred**; Settings stays one column | ST-36; Reader |

## 9. What this document does not decide

- The look: that is `docs/Style Guide.md`, which needs your reaction to its sample screen.
- Per-screen specs for Ask, History, Settings and the wizard (only Library reading exists). Each would follow the template and this document.
- Anything about the model's output quality, which only real runs can show.
