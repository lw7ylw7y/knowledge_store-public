# Docent (Local RAG) — Software Requirements

Oct 3, 2026 · @Lin

## Overview

A Windows desktop app that answers topic questions using only the books the user has added, running fully offline on their own machine. It pairs a local vector store with a small local LLM (retrieval-augmented generation) so every answer is grounded in, and cited back to, the user's own library.

**Problem.** Keyword search inside ebooks and PDFs misses passages that discuss a topic without using the exact words. Cloud AI tools can answer conceptually, but they draw on the open internet and require uploading private or purchased books.

**Vision.** "Ask my bookshelf." The user types a topic or question; the app finds the most relevant passages across all their books, shows them with book, chapter and page, and on request writes a short summary that cites those passages and nothing else.

**Scope (v1).** Single user, single Windows PC, local files only (EPUB, PDF, TXT, MD), semantic search with ranked excerpts, plus on-demand cited summaries. No accounts, no sync, no cloud calls after the one-time model download.

## Goals and non-goals

v1 succeeds if a user can add a dozen books and get relevant, cited passages for a conceptual question in seconds, with no data leaving the PC.

**Goals**

- G1. Private by design: books, index, queries and answers never leave the machine.
- G2. Semantic search that finds passages by meaning, not only by keyword.
- G3. Grounded summaries: every generated claim cites a retrieved passage; the app says so when the library has nothing relevant.
- G4. Runs on a typical consumer laptop without a dedicated GPU.
- G5. Free to run: open-weight models and open-source components only.

**Non-goals (v1)**

- General chat or answers drawn from the model's own knowledge or the internet.
- Fine-tuning or training models locally.
- Multi-user, sharing, cloud sync, or mobile/macOS clients.
- DRM removal. Protected ebooks (e.g. Kindle, Adobe DRM) are rejected with a clear message.
- OCR. Image-only or scanned books are rejected (FR-7).

## Users and user stories

The primary user is a reader or learner with a personal library of tens to a few hundred books who wants to revisit ideas across them. A secondary user is a student or researcher cross-referencing a topic.

| ID | As a user, I want to… | So that… | Priority |
| --- | --- | --- | --- |
| US-1 | install the app on Windows with a normal installer | I don't need Python, Docker or a terminal | Must |
| US-2 | add books by file picker or drag-and-drop | my library is searchable | Must |
| US-3 | see indexing progress per book | I know when a book is ready to search | Must |
| US-4 | type a topic and get the most relevant passages | I find ideas even when the wording differs | Must |
| US-5 | see book, chapter and page for each passage and open it | I can read it in context | Must |
| US-6 | ask for a short summary of the shown passages, with citations | I get an overview without reading every hit | Must |
| US-7 | be told when my books don't cover a topic | I'm not misled by an invented answer | Must |
| US-8 | name a book or author in my question and have the search limited to it automatically | I can focus on one book without setting filters by hand | Should |
| US-9 | remove a book and have it purged from the index | my library stays accurate | Should |
| US-10 | ask a follow-up question in the same thread | I can dig deeper on a topic | Could (later); delivered by DCR-2 (US-15) and DCR-6 (model rewriting) |
| US-13 | read my past questions with their summaries, search one again, and close entries I don't want (DCR-1) | I can pick up where I left off without redoing a question | Should |
| US-14 | have the summary written for me in the background while I read the results (DCR-1) | I don't have to click and wait for it | Should |
| US-15 | ask related questions one after another and see the whole conversation (DCR-2) | I can dig deeper without retyping the context | Should |
| US-16 | start a new thread when I move on, and delete whole threads I no longer need (DCR-2) | my conversations stay organised and nothing is lost by moving on | Should |
| US-17 | click a book in the Library to read it, and come back to where I stopped (DCR-10) | the Library is where I read my books, not only where I tag and index them | Should |
| US-18 | ask in my own language and still find passages in my books written in another (DCR-11) | my library has English and Chinese books, and a question in one should find the other | Should |

## Functional requirements

Requirements use "shall" for must-haves and "should" for v1 nice-to-haves; each traces to a user story.

### Installation and first run

- FR-1. The app shall install via a per-user .exe installer that needs no admin rights, distributed by direct download as an unsigned build with a published SHA-256 checksum, for Windows 10 22H2+ and Windows 11, x64. (US-1)
- FR-2. On first run, the app shall detect RAM, CPU and GPU and recommend a model tier (see Non-functional). (US-1)
- FR-3. The app shall download the selected LLM and embedding model once, show progress and checksum, and allow importing model files from disk for fully offline setup. (US-1)

### Library management

- FR-4. The user shall add books via file picker or drag-and-drop; supported formats are EPUB, PDF (text layer), TXT and MD. (US-2)
- FR-5. The app shall extract title, author and table of contents from file metadata. (US-2)
- FR-6. The app shall detect duplicates by file hash and skip re-indexing. (US-2)
- FR-7. The app shall reject DRM-protected and image-only files with a specific reason. (US-2)
- FR-8. The user shall remove a book; its chunks and vectors shall be deleted from the store. (US-9)
- FR-9. *(Amended 2026-10-13: the filter by tag is on the Ask page, FR-21; the Library only shows and edits tags.)* The user should be able to tag books, one or many at a time, on the Library page. (US-8)
- FR-10. The app could watch a chosen folder and auto-add new files. (US-12)

### Ingestion and indexing

- FR-11. The app shall extract text while preserving structure: book, chapter, section, page (PDF) or location (EPUB). (US-5)
- FR-12. The app shall split text into overlapping chunks (default about 500 tokens, 15% overlap) that respect paragraph boundaries, storing location metadata per chunk. (US-4, US-5)
- FR-13. The app shall embed each chunk with the local embedding model and store vectors plus metadata in the local vector store. (US-4)
- FR-14. The app shall also build a keyword (full-text) index over the same chunks for hybrid search. (US-4)
- FR-15. Indexing shall run in the background, show per-book status (queued, indexing, ready, failed), and resume after the app closes or the PC restarts. (US-3)
- FR-16. The user shall be able to pause indexing and cap its CPU use. (US-3)

### Search

- FR-17. The user shall enter a topic or natural-language question in a single search box. (US-4)
- FR-18. The app shall run hybrid retrieval (vector similarity + keyword), merge results (e.g. reciprocal rank fusion), and return the top N passages (default 3 after DCR-2; it was 5). (US-4)
- FR-19. *(Amended by DCR-8.)* Each result shall show the passage excerpt with the query's central terms highlighted (a word is central if it is in fewer than 2% of the library's chunks, at least 5; the others are not marked; marked words are semibold and underlined, not only coloured), book title, author, chapter and page/location, and a relevance indicator. (US-5)
- FR-20. Selecting a result shall open the passage in context in a built-in reader view, with an option to open the source file in the default app. (US-5) *(DCR-10: the reader view can also be opened on a whole book, FR-44.)*
- FR-21. (Replaced by DCR-3: automatic detection of a book or author named in the question worked badly and was removed.) The user shall be able to tag each book with free-form tags, one or many, edited on the Library page for one book or for several selected at once, and to filter searches by tags and/or individually chosen books from a Filter button next to the question box. The filter stays until the user clears it, every question is searched inside it, each question shows what it was searched in, and a filter that matches no indexed book says so instead of searching everything. (US-8)
- FR-46. *(DCR-11, ST-38, written and unit-tested, not yet built on Windows.)* When the books being searched (after the filter, FR-21) are in two or more languages, the app shall also search the question in those languages, at most three, the ones with the most books, **without guessing the question's own language**: the LLM writes the question in each of them and returns it unchanged in its own language, which is dropped, and each version is searched by meaning and by keyword with all the lists merged as in FR-18, so a passage in another language is found by a query in its own language. The question and its translations are shown above the results ("Also searched: …"). With the books in one language L, only a question clearly in another language (detected by script or common words) is translated, into L; nothing is translated, and no LLM call is made, when the books are in one language and the question is in it, when its language cannot be told, or when the setting is off. When translations were made, the coverage test (FR-25) uses the normal threshold for every query, because each language has a query of its own. Like a follow-up rewrite (FR-27) the translation waits for the LLM (at most 15 s once no summary is being written, 60 s when the LLM has to be started); if the LLM is unavailable, the time limit passes or its reply has no usable line, the question is searched as typed and a warning says that books in other languages may be missing. With automatic summaries off the LLM is not kept loaded, so nothing is translated and no warning is shown (the setting's note says so). The translations are kept with the history entry, so a summary finished after a restart searches the same queries. (US-18)
- FR-22. The app should optionally re-rank the top candidates with a small local cross-encoder for better precision. (US-4)

### Summary (RAG)

- FR-23. *(Amended by DCR-1 and DCR-9.)* When a search finishes (FR-32) and automatic summaries are on, the app shall generate with the local LLM a short summary (2 to 3 sentences) **of each passage shown**, one passage at a time, using only that passage (and a little text around it) as context. Each summary appears under its result when it is complete or as it is written. A passage that does not help answer the question is not removed: its summary says, in plain words, that it doesn't cover the question. With automatic summaries off the results are shown without summaries and there is no way to write one except asking the question again with them on (DCR-9; the Summarize button and the summary panel were removed). Esc stops the newest summary that is waiting or being written (press again for the one before it). (US-6)
- FR-24. *(Replaced by DCR-9.)* A summary was one text with inline citations that linked to its results. Each summary now belongs to exactly one result and sits under it, so there are no inline citations. (US-6)
- FR-25. If no passage clears a relevance threshold, the app shall say the library does not cover the topic and show the nearest passages without summaries. (US-7)
- FR-26. The summary shall stream token by token, and the user shall be able to stop generation. (US-6)
- FR-27. *(Amended by DCR-1, DCR-6 and DCR-11.)* Search shall not depend on the LLM: passages appear without waiting for it. **The exceptions are a follow-up question (FR-40), which waits for the LLM to rewrite it, and a question that is also searched in other languages (FR-46, DCR-11), which waits for the LLM to translate it** (a few seconds when the LLM is idle; while summaries are being written it waits for them to finish, then at most 15 s (60 s when the LLM is not running yet and has to be started); after that, or if the LLM is unavailable or automatic summaries are off, the question is searched as typed). The LLM loads when the first summary is requested (the first search, if automatic summaries are on) and stops after 10 minutes idle. (US-4, US-6)
- FR-28. *(Delivered by FR-40 as amended by DCR-6.)* The user can ask follow-up questions; the app rewrites each follow-up into a standalone query before retrieval. (US-10)
- FR-29. The user could save passages and answers to a notes list and export it as Markdown. (US-11)

### Background summary and search history (DCR-1)

- FR-32. After each search whose results are final and covered by the library, the app shall queue a summary and write it in the background, in a panel of its own beside the results; the results shall never wait for it. Summaries are written one at a time in the order the searches were made; a new search shall not stop a summary that is being written. The user can stop the summary on screen (a waiting one leaves the queue). (US-14)
- FR-33. *(Amended by DCR-2: entries are grouped into threads, FR-43.)* The app shall keep a history of searches across sessions. Each entry holds the question, the date and time, and the summary as plain text (without citation markers), with its status (waiting, writing, written, stopped, failed, not covered, or none when no summary was requested). It holds no list of results. (US-13)
- FR-34. *(Amended by DCR-2: "Search again" becomes "Continue this thread", and closing deletes a whole thread, FR-43.)* The user shall be able to read a past entry without running the search or the model again, search again with the current library (which adds a new entry), and close (delete) one entry. (US-13)
- FR-35. History shall be independent of the library: removing a book shall not change existing entries. (US-13)
- FR-36. History shall be stored only in the local library database, never in logs (NFR-16), and shall be removed by Settings' "Clear all history" and by "Delete all data" (FR-31). (US-13)
- FR-37. Settings shall let the user turn the automatic summary off (results are then shown without summaries) and clear all history. There is no history size setting. (US-13, US-14)
- FR-38. A summary that was waiting or being written when the app closed shall be completed automatically after the next start: the question is searched again and the summary written from those results, in the original order. (US-14)

### Conversation threads (DCR-2)

- FR-39. The Ask page shall show searches as a thread: each question followed by a collapsible list of its results, with new questions added below, and each result carrying its own summary (DCR-9). Each question shows 3 results. Earlier questions stay open as they were. (US-15)
- FR-40. *(Amended by DCR-6.)* A follow-up (a question that starts with a pronoun or connector, or has a pronoun such as "it", "this" or "they" anywhere in it; length is not considered) shall be rewritten by the LLM into a standalone question using the last N earlier questions of the thread (not their answers or results). Only the rewritten question is searched and summarized; nothing else from the earlier questions is used. The page shows the rewritten question in a removable indicator; removing it searches the follow-up as typed. If the LLM cannot be used or does not answer within 15 s (counted from the moment no summary is being written or waiting) the follow-up is searched as typed and the page says so; with automatic summaries off it is searched as typed without a rewrite. N is a setting (0 to 3, default 1; 0 turns it off). *(DCR-7, ST-32.)* The newest question can be asked again with a retry button: it replaces the first attempt, a rewrite that worked is kept (the model is not asked again) while follow-up handling is on, and one that failed is made again. (US-15)
- FR-41. The summary prompt shall include up to the last three questions of the thread (not their summaries). (US-15)
- FR-42. "New thread" shall start a new thread and leave the previous one in the history. On start the latest thread shall be restored, showing each past question with its summary text, the same text as the History page (no results, because results are not stored; decided 2026-10-13). (US-16)
- FR-43. The history shall list threads (first question as title, number of questions, time of the last one); opening a thread shows its questions and summaries; "Continue this thread" resumes it; closing a thread deletes all of it (with a confirmation when it has more than one question). A single question cannot be deleted. (US-16)
- FR-44. *(DCR-10, `docs/UX Spec - Library reading.md`; ST-35, written and unit-tested, not yet built on Windows.)* On the Library page, clicking a book that is ready, or its **Read** / **Continue** button, or pressing Enter on it, shall open the built-in reader on that book. Selecting books for tagging shall be done in a Select mode with tick boxes (a click on a row then ticks it and never opens it); Ctrl+click and Shift+click shall not select. For a book that is not ready (queued, indexing, rejected or failed) the same click or its **Open file** button shall open the original file in its default program instead. The reader's Back shall return to the Library at the same scroll position and say "Back to Library". *(UX design, 2026-10-13:)* the Library list is quiet: each row shows one main button and a menu for Tags… and Remove; tick boxes appear only in a Select mode; indexing progress is shown once, above the list, with Pause; a Find a book box narrows the list. (US-17)
- FR-45. *(DCR-10, ST-35, written and unit-tested, not yet built on Windows.)* The app shall remember, for each book, where the reader stopped (the chapter and the first visible paragraph in it) and open the book there the next time; a book never opened starts at the beginning; a place that no longer exists (the book was indexed again) falls back to the nearest one without an error. The place is stored in the library database, removed with the book and by "Delete all data". (US-17)

### Settings

- FR-30. *(Amended by DCR-11: the search-in-other-languages setting.)* Settings shall include: model choice, automatic summary on/off (amended by DCR-1; was Summarize on/off), search in the other languages of the library on/off (DCR-11, on by default), clear history, number of passages, chunking defaults (applied to books added afterwards; no re-index prompt), indexing CPU cap, and data folder location. *(UX design, 2026-10-13:)* a setting applies as soon as it is changed; one that does not (it needs the app or the language model to restart, or applies to new books only) says so beside it; expert options (chunk size and overlap, language model memory) are grouped under a folded **Advanced** section; errors are shown in an InfoBar like everywhere else.
- FR-31. The app shall offer "Delete all data" that removes the library index, history and (optionally) models.

## Non-functional requirements

Zero cost, privacy, background indexing and search results within 10 seconds are hard requirements; other targets are initial and should be validated in the proof of concept.

| ID | Area | Requirement |
| --- | --- | --- |
| NFR-1 | Performance | Adding books returns control immediately. Extraction, chunking and embedding run in a background worker at below-normal priority; the UI stays responsive and already-indexed books stay searchable while indexing runs. |
| NFR-2 | Performance | Search returns ranked results within 10 s (p95) for a 200-book library on the minimum-spec machine, CPU only. Target is 2 s or less typical. |
| NFR-3 | Performance | Summarize shows its first token within 15 s on minimum spec (CPU only) and within 5 s with a supported GPU; a summary of one result (2 to 3 sentences) finishes within 30 s on minimum spec (DCR-9: the summaries of all results are written one after another, so the whole list takes about that many times longer; the targets are to be re-confirmed on a real PC). Targets confirmed in M0. |
| NFR-4 | Performance | Indexing processes at least 1 average book (\~100k words) per 3 minutes on minimum spec, CPU only. |
| NFR-5 | Scale | Supports at least 1,000 books / \~1M chunks without changing architecture. |
| NFR-6 | Footprint | Installer ≤ 200 MB excluding models; idle RAM ≤ 300 MB; models load on demand and unload after idle. |
| NFR-7 | Reliability | Indexing is crash-safe and resumable; the store is never left corrupt after a crash or power loss. |
| NFR-8 | Quality | On a test set, ≥ 90% of summary sentences are supported by the passage they were written from; out-of-library questions get the not-covered response ≥ 80% of the time. |
| NFR-9 | Security / privacy | No network calls except the user-initiated download of the models and of the llama.cpp program that runs them, and the optional update check; all can be disabled. No telemetry by default. Local model processes listen on 127.0.0.1 only. |
| NFR-10 | Security / privacy | All features work with networking disabled once models are installed. |
| NFR-11 | Security / privacy | All data lives under one folder in the user's profile (default `%LOCALAPPDATA%`), readable only by that Windows user. |
| NFR-12 | Security | Book files are untrusted input: parsing runs in a separate low-privilege worker with file-size and page limits, so a malformed or malicious file fails only that book. |
| NFR-13 | Security | Downloaded models are verified by SHA-256 against a pinned manifest before loading; models are only non-executable weight formats (GGUF, ONNX, safetensors). The one program the app downloads, llama.cpp's `llama-server`, is a zip pinned in the same manifest (official llama.cpp release, SHA-256 checked before anything is unpacked); nothing else is downloaded or run. |
| NFR-14 | Security | Book text is treated as data in the summary prompt. The LLM has no tools, file or network access, so instructions hidden in a book can affect only the summary text. |
| NFR-15 | Security | Builds are unsigned and distributed by direct download with published SHA-256 checksums; updates install only with user approval. The app runs without admin rights. |
| NFR-16 | Security / privacy | Logs and crash reports contain no book text or questions by default. |
| NFR-17 | World readiness | UI is English only in v1, with all strings externalized so it can be localized later. |
| NFR-18 | World readiness | Book content and questions support full Unicode, including CJK, right-to-left scripts (Arabic, Hebrew) and diacritics, displayed correctly in excerpts and the reader. |
| NFR-19 | World readiness | Semantic search uses a multilingual embedding model, so books and questions can be in any supported language, including a question in one language finding passages in another. |
| NFR-20 | World readiness | Keyword search and chunking are language-aware: sentence splitting and a tokenizer that handle scripts without spaces (e.g. Chinese, Japanese, Thai). Each book's language is detected and stored. |
| NFR-21 | World readiness | Summaries are written in the language of the question by default, using a multilingual LLM; quality may vary by language. |
| NFR-22 | Accessibility | Full keyboard navigation, screen-reader labels, respects Windows text scaling and dark mode. *(Amended 2026-10-13, UX design:)* the smallest supported window is 800 × 600; every screen stays usable at text scaled to 200 % (nothing cut off, nothing needing horizontal scrolling); the app uses theme resources only so that high-contrast themes work; meaning is never carried by colour alone. |
| NFR-23 | Cost | Every resource is free of charge, including but not limited to the LLM, embedding model, vector store, database, inference runtime, parsing libraries, UI framework and build tools. No paid tiers, subscriptions, API keys or usage fees, and licenses permit free local use and redistribution. |

**Hardware tiers**

| Tier | RAM | GPU | Suggested LLM size (quantized) |
| --- | --- | --- | --- |
| Minimum | 8 GB | None | Qwen3.5-2B, 4-bit |
| Recommended | 16 GB | None or integrated | Qwen3.5-4B, 4-bit (default) |
| Enhanced | 16 GB+ | NVIDIA ≥ 6 GB VRAM, or NPU | Qwen3.5-9B, 4-bit |

## Proposed architecture

Two pipelines share one local data folder: ingestion turns books into searchable chunks in the background, and the query path returns ranked passages, with a small local LLM summarizing them only when asked.

&#91;embedded content: local RAG architecture · ingestion and query pipelines\]

The query path reuses the ingestion embedding model, so a question and the book chunks land in the same vector space.

| Component | Responsibility | Chosen technology |
| --- | --- | --- |
| Desktop shell | Library, Ask, Reader and Settings screens | WinUI 3 on .NET |
| Ingestion service | Extraction, chunking, embedding, job queue | .NET background worker; free open-source PDF and EPUB parsers (chosen in M0) |
| Keyword index | Full-text search over chunks | SQLite FTS5 |
| Vector store | Approximate nearest-neighbour search on embeddings | sqlite-vec, in the same SQLite file |
| Embedding model | Text to vectors for chunks and queries | Qwen3-Embedding-0.6B (GGUF) served by llama-server |
| LLM runtime | Load model, stream tokens, CPU with GPU/NPU offload | llama.cpp llama-server as a child process on 127.0.0.1 running Qwen3.5 (4B default); Vulkan or CUDA offload when present |
| Orchestrator | Search filter, hybrid merge, threshold; prompt build and citation mapping for Summarize | In-process module in the app |

**Grounding prompt.** The orchestrator sends each retrieved passage to the model on its own, instructs it to answer only from that passage in 2 to 3 sentences, and to reply "not covered" when the passage doesn't address the question (DCR-9; before it, passages were numbered and cited, and the citations validated).

**Process model.** UI, ingestion worker and LLM runtime run as separate processes, so a heavy indexing job or model crash never freezes the window.

## Data model and storage

Everything sits in one data folder: a SQLite database for records and keyword search, a vector index for embeddings, and the model files.

| Entity | Key fields | Notes |
| --- | --- | --- |
| Book | id, file\_path, file\_hash, title, author, format, tags, added\_at, status, embedding\_model\_id | One row per file; hash drives de-duplication. |
| Section | id, book\_id, title, order, parent\_id | Chapter/section tree from the TOC. |
| Chunk | id, book\_id, section\_id, text, token\_count, page\_start, page\_end, location, chunk\_index | Unit of retrieval; FTS5 index over `text`. |
| Embedding | chunk\_id, vector, model\_id | Stored in the vector index; model\_id forces re-embed when the model changes. |
| IngestJob | id, book\_id, stage, progress, error, updated\_at | Makes indexing resumable. |
| Conversation | id, created\_at, title | Search/answer threads. |
| Turn | id, conversation\_id, query, rewritten\_query, retrieved\_chunk\_ids, answer, created\_at | Keeps citations reproducible. |
| Note | id, chunk\_id or turn\_id, text, created\_at | Saved passages and answers (Could). |

The app stores extracted text and vectors but never modifies or copies the original book files; removing a book deletes its rows and vectors.

## User experience

The UX design is in `docs/UX Design.md` (screen map, screens, shared patterns, decisions) and the visual rules in `docs/Style Guide.md`; per-screen specs (so far `docs/UX Spec - Library reading.md`) follow both. In short:

- **Screens:** a first-run wizard, then a shell with a navigation bar (icons with tooltips, no labels) holding **Ask** (home, opened on every start), **History**, **Library** and **Settings**; the **Reader** opens from a result or from the Library and returns with Back, Esc or Alt+Left.
- **Feel:** calm and text-first, like a good e-reader; Windows 11 (Fluent) controls and theme resources; one accent button per view.
- **Reference apps:** not yet chosen by the owner (open).
- **Words:** book, library, passage, result, summary, thread, indexing, section.

## UX flows

The app has a navigation bar with Ask (home), History, Library and Settings (at the bottom), plus the Reader (opened from a result or a book) and a first-run wizard.

**First run**

1. Install and launch; a welcome screen explains that everything stays on this PC.
2. The app checks hardware and recommends a model tier; the user accepts or picks another.
3. Models download (or are imported from disk) with progress and size shown.
4. The user lands on the Ask screen. With no books yet, it says "Add books first" and offers a **Go to Library** button.

**Add books**

1. Drag files onto Library or click Add.
2. Each book appears immediately with status Queued, then Indexing (percent), then Ready or Failed with a reason.
3. Books become searchable as soon as each finishes; the user doesn't wait for the whole batch.
4. A ready book can be read at once (**Read** / **Continue**, FR-44); a book still being indexed, or rejected, offers **Open file**.

**Ask a question**

1. On Ask, the user types a topic or question; if a filter (tags and/or chosen books) is set, the search stays inside it.
2. Passages appear in a results column within about a second.
3. A short summary appears under each result (2 to 3 sentences, from that passage only); a passage that doesn't cover the question stays in the list and says so.
4. If coverage is weak, a banner says so and no summaries are written.
5. Clicking a passage opens Reader at that location.

## Constraints, assumptions and risks

The biggest risk is answer quality from a small model on CPU; the design treats retrieval as the core product and generation as an enhancement that can be turned off.

**Constraints**

- Windows x64 only for v1; must run without admin rights after install.
- Every component must be free: open-weight models and free, open-source libraries only; no paid APIs, services or licenses (NFR-23).

**Assumptions**

- Most user books are DRM-free EPUB or text-based PDF.
- Users accept a one-time model download of roughly 1–5 GB.
- A 1–4B instruction-tuned model is adequate for summarizing and citing provided passages, even if weak at open reasoning.

| Risk | Impact | Mitigation |
| --- | --- | --- |
| Small LLM hallucinates or ignores context | Misleading answers | Strict grounding prompt, one passage per call, relevance threshold, "not covered" path per passage, generation optional |
| Slow generation on low-end CPUs | Poor experience | Passages first, streaming, smaller model tier, GPU/NPU offload when present |
| PDF extraction is messy (columns, headers, footnotes) | Bad chunks and citations | Layout-aware extraction, header/footer stripping, per-book "re-extract" option |
| Changing the embedding model invalidates the index | Long re-index | Store model\_id per vector; background re-embed with old index kept until done |
| Model or library license changes | Can't ship | License review gate per release; keep model swappable |
| Large libraries exceed RAM | Crashes, slow search | On-disk ANN index, streamed ingestion, memory caps |
| Copyright concerns about stored text | Legal exposure | Store only for the owner's local use; no sharing or export of full text |

## Milestones, acceptance criteria and decisions

Build in four phases, each ending with a check before moving on; the proof of concept decides whether the small-model approach is good enough.

**Milestones**

1. **M0 — Proof of concept (CLI).** Ingest 10 EPUB/PDF books, hybrid search with ranked excerpts, and summaries tested separately. Exit: a 30-question test set shows useful top-5 passages for ≥ 80% of questions and meets NFR-8 grounding on the recommended tier.
2. **M1 — Core desktop app.** Library, background indexing, Ask screen with ranked excerpts and on-demand summaries, Reader view. Exit: FR-1 to FR-27 pass; NFR-1 to NFR-4 met on minimum spec.
3. **M2 — Polish.** Background summary and search history (DCR-1), automatic book scoping, settings, delete-all, accessibility, installer with published checksum. Exit: all Must and Should requirements pass; usability test with 3 users completes all flows unaided.
4. **M3 — Extras (optional).** Follow-up questions and query rewriting, notes and export, watched folders, re-ranking, GPU/NPU acceleration, OCR investigation.

**M0 exit review** (to be completed after the real-model run on target hardware)

| Check | Target | Result so far |
| --- | --- | --- |
| recall@5 on the 32 answerable questions | 80% or more | Keyword search alone: 69% (`eval-reports/baseline-keyword-only.md`). With the embedding model: not run yet |
| Out-of-library questions get the not-covered response | 80% or more | Keyword-only: 25%, as expected (every common word matches something). With the embedding model and the coverage threshold: not run yet |
| Summary sentences supported by a cited passage (NFR-8) | 90% or more | Not run yet (`bookcli grounding` produces the sheet to check by hand) |
| Search p95 (NFR-2), indexing speed (NFR-4), Summarize first token and total (NFR-3) | 10 s, 3 min per book, 15 s and 30 s | Not run yet on minimum spec (`bookcli bench`). One early Summarize run on a developer PC with Qwen 4B: 58 s to first token including model load, 102.6 s total (see Implementation notes) |
| Image-only rule | Rejects scans, accepts real books | Calibrated: scanned PDF 100% of pages under 100 characters; 7 real books at most 18% (rule: 100 characters on 80% of pages). Kept |

Decision: **pending**. Options when the numbers are in: proceed, adjust the targets (for example NFR-3), or change the model.

**Acceptance criteria (v1 release)**

- [ ] Fresh install to first search results in under 15 minutes, excluding download time.
- [ ] Works end to end with networking disabled after setup.
- [ ] A packet capture during normal use shows no outbound traffic.
- [ ] Removing a book removes all of its chunks and vectors.
- [ ] Out-of-library questions return the "not covered" response per NFR-8.
- [ ] Indexing survives a forced shutdown and resumes without duplicates.

**Decisions**

| # | Question | Decision |
| --- | --- | --- |
| 1 | App shell | WinUI 3 on .NET, Windows only. Electron is ruled out as too heavy. |
| 2 | Inference runtime | llama.cpp's `llama-server` (MIT), bundled and run as a local child process for both embeddings and Summarize. Prebuilt Windows binaries; CPU by default, Vulkan or CUDA offload when a GPU is present. |
| 3 | Default models | LLM: [Qwen3.5-4B](https://artificialanalysis.ai/articles/qwen3-5-small-models) (Apache 2.0), 4-bit GGUF; Qwen3.5-2B on 8 GB machines, Qwen3.5-9B with a GPU. Embeddings: [Qwen3-Embedding-0.6B](https://www.bentoml.com/blog/a-guide-to-open-source-embedding-models) (Apache 2.0, 100+ languages, cross-lingual). Confirm both on the M0 test set. |
| 4 | Vector store | [sqlite-vec](https://kanopylabs.com/blog/lancedb-vs-chroma-vs-sqlite-vec) (MIT / Apache 2.0) in the same SQLite file as the FTS5 keyword index: one database, one transaction. Exact search is fast enough at this library size. Fallback: LanceDB (Apache 2.0). |
| 5 | Summary scope | Library-only. Summaries never use the model's general knowledge. |
| 6 | OCR | Not supported. Image-only or scanned books are rejected (FR-7). |
| 7 | Distribution | Direct download for private use: unsigned build with a published SHA-256 checksum. |

Decided in M0: PdfPig and VersOne.Epub for parsing, and sqlite-vec pinned to v0.1.9 (see the Implementation notes (M0) section of the Stories and Test Plan). Still open: llama.cpp version and the final `llama-server` flags, to confirm with the real models.

**Sources**

- [Qwen3.5 small models: Everything you need to know](https://artificialanalysis.ai/articles/qwen3-5-small-models) (Artificial Analysis)
- [Qwen3.6-35B-A3B release](https://qwen.ai/blog?id=qwen3.6-35b-a3b) (Qwen; Qwen3.6 open weights are 27B and larger, so Qwen3.5 remains the newest small family)
- [The Best Open-Source Embedding Models in 2026](https://www.bentoml.com/blog/a-guide-to-open-source-embedding-models) (BentoML)
- [Best Ollama Embedding Models 2026](https://www.morphllm.com/ollama-embedding-models) (Morph)
- [LanceDB vs Chroma vs SQLite-vec](https://kanopylabs.com/blog/lancedb-vs-chroma-vs-sqlite-vec) (Kanopy Labs)
