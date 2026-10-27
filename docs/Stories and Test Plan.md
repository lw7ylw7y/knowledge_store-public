# Docent — Stories and Test Plan

Oct 4, 2026 · @Lin

Built from Doc and Doc.

## Feasibility review

The technical design is doable for one experienced developer with free tools: every component exists, is free, and has been combined this way before. The real uncertainty is CPU speed for indexing and Summarize, which M0 measures before any UI work.

| Area | Verdict | Notes |
| --- | --- | --- |
| WinUI 3 unpackaged + Inno Setup | Doable | Well-trodden path. Self-contained runtime plus llama.cpp binaries may approach the 200 MB installer limit (NFR-6); measure in ST-21. |
| `llama-server` sidecar | Doable | Qwen3.5 small and Qwen3-Embedding are published as GGUF files that llama.cpp runs; language-neutral HTTP API. |
| sqlite-vec in Microsoft.Data.Sqlite | Doable, verify first | Loads as an extension DLL. Pre-1.0, so pin the version; partition keys need a recent release. ST-01 proves it on day one. |
| FTS5 + trigram tokenizer | Doable | Trigram needs SQLite 3.34+, which the bundled SQLite provides. |
| PDF / EPUB extraction | Doable, quality risk | Headers, footers and multi-column PDFs need cleanup; sampled in ST-02. |
| ICU sentence splitting | Doable, fiddly | Calls Windows' `icu.dll` directly; a punctuation-based splitter is the fallback. |
| Search within 10 s | Comfortable | Expected about 1 s on 60k chunks. |
| Indexing 3 min per book on CPU | Likely | Needs \~1.5 chunks/s from the 0.6B embedding model; measured in ST-08. |
| Summarize 15 s first word on CPU | Borderline | Depends on Qwen3.5-2B prompt speed on an 8 GB laptop; measured in ST-08. Summarize is optional, so a miss doesn't block v1. |
| Worker process + named pipe | Doable, more moving parts than needed | Simplify (below). |
| Reader jumping to a passage | Hardest UI piece | PDF page rendering with highlights is costly; simplify (below). |
| Zero cost | Met | Every library, model and tool is free. |

**Recommended simplifications** (reflected in the stories)

1. **Progress through the database, not a named pipe.** The worker writes progress to `ingest_job`; the app polls once a second. Pause and CPU cap go in a small `worker_control` table. One less IPC channel to build and debug.
2. **Text-based Reader for both formats.** Show the extracted chapter text with the passage highlighted, plus "Open file" for the original. No PDF page rendering.
3. **CPU only in v1.** GPU offload (Vulkan build, enhanced tier) moves to M3; the installer ships only the CPU build, which also helps the size limit.

**Effort.** About 14–23 weeks of focused solo work for M0–M2, summed from the story sizes: roughly 5–9 weeks for M0 (most of the core pipeline), 5–7 for M1, 4–7 for M2 (2–3 before the two change requests: DCR-1, background summary and history, added about a week or two, and DCR-2, conversation threads, about another one to two). Sizes: S ≈ 1–2 days, M ≈ 3–5 days, L ≈ 1–2 weeks.

## Progress

The unfinished stories are listed in build order (ST-23 is built before ST-17; IDs are not renumbered).

M0 is being built story by story. Each story below ends with a **Result** note listing what was done and where it differs from the plan. Library choices, measured results and open items are collected in [Implementation notes (M0)](#implementation-notes-m0) at the end of this document.

| Story | Status | Notes |
| --- | --- | --- |
| ST-01 Solution skeleton and database | Done | Linux verified; Windows `vec0.dll` not yet run |
| ST-02 Text extraction and reject rules | Done | Also handles two-column PDFs |
| ST-03 Chunker | Done | ICU tests need libicu on Linux |
| ST-04 Model server client and embeddings | Done, real model not yet run | Verified with a stand-in server; integration tests skip without `BOOKSEARCH_M0_CONFIG` |
| ST-05 Ingest pipeline (console) | Done, real model not yet run | Verified with a stand-in server |
| ST-06 Hybrid search (console) | Done, real model not yet run | Verified with a stand-in server |
| ST-07 Summarize (console) | Done, real model not yet run | Verified with a stand-in server |
| ST-08 Evaluation runner and M0 exit | Tooling done; real-model run and go/no-go pending | Keyword-only baseline recorded |
| ST-09 Model manifest, tier detection and download | Done | Verified against a local file server; the first real download is still to do |
| ST-10 Model server supervisor | Code and unit tests done; Windows job object and manual steps not run | Needs the app (ST-12) for the E2E steps |
| ST-11 Background worker and job queue | Code and unit tests done; Windows manual steps not run | Real run of the worker against a stand-in server passes |
| ST-12 App shell and first-run wizard | Built and run on Windows; wizard panels confirmed; manual steps 2-4 not yet reported | See the README's "Desktop app" section to build it |
| ST-13 Library page | Built and run on Windows by the user; bugs found were fixed (file dialog, drag and drop under administrator) | See the Result note; needs `llama-server.exe` in the data folder's `bin` to index |
| ST-14 Ask page: search results | Built and run on Windows by the user; cross-language ranking changed after use | Meaning-based results need `llama-server.exe` and the embedding model |
| ST-15 Summarize in the app | Built and run on Windows by the user; several bugs found and fixed (button layout, repeat runs, UI thread, `RichTextBlock`) | Speed and the Spanish-language step still unreported; superseded in part by ST-23 |
| ST-16 Reader page | Built and run on Windows; the user reported it looks good | Chapter text is rebuilt from chunks, so it works without the original file |
| ST-23 Background summary panel and search history (DCR-1) | Code and unit tests done; not yet built or run on Windows | Summary panel on the right of the results, queued in the background after each search (one at a time, none cancelled); History page; unfinished summaries completed after a restart |
| ST-17 Automatic book scoping | Code and unit tests done; not yet built or run on Windows | Scope chip above the results; stricter matching rule than the plan (see the Result note) |
| ST-24 Conversation threads in the Ask page (DCR-2) | Code and unit tests done; not yet built or run on Windows | Ask page as a conversation, threads in History, 3 results, light follow-up context; the old history is wiped by the migration |
| ST-25 Tags and the search filter (DCR-3) | Code and unit tests done; not yet built or run on Windows | Tags per book (Library), a Filter button on Ask, replaces implicit scoping by the typed title; migration 5 |
| ST-26 Summaries from contexts, one paragraph each (DCR-4; replaced by ST-34) | Code and unit tests done; not yet built or run on Windows | Results of one place are merged with text around them; the model writes a paragraph per context; Settings: small/medium/large. Quality and speed are for the user to judge |
| ST-28 Edit the newest question and search again | Code and unit tests done; not yet built or run on Windows | A pencil button on the newest question puts it back in the question box; searching replaces it |
| ST-29 The name Docent and the app icon | Code and unit tests done; not yet built or run on Windows | New name wherever the user sees one; icon on the program, the window, the installer and the Start menu entry |
| ST-30 One context per book, longer paragraph (DCR-5; replaced by ST-34) | Code and unit tests done; not yet built or run on Windows | Every result of one book is one context and one paragraph of about 120 words; reverses part of ST-26 on the user's request |
| ST-31 Follow-ups rewritten to stand on their own (DCR-6) | Code and unit tests done; not yet built or run on Windows | The chat model rewrites a follow-up from the earlier questions; only the rewritten question is searched and summarized; amends FR-27, FR-40; migration 6 |
| ST-32 Retry the newest question (DCR-7) | Code and unit tests done; not yet built or run on Windows | A retry icon right of the pencil asks the newest question again in place (a rewrite that worked is kept; a failed one is tried again), with a new search and summary |
| ST-33 Highlight only rare question words (DCR-8) | Code and unit tests done; not yet built or run on Windows | A question word is highlighted in the results and the Reader only if it is in under 2% of the library's chunks (at least 5); marked semibold and underlined instead of in the accent colour |
| ST-34 One summary per result (DCR-9) | Code and unit tests done; not yet built or run on Windows | Each result gets its own 2 to 3 sentence summary under it, written one after another; a passage that doesn't cover the question stays and says so; replaces the one combined summary and its citations (reverses ST-26/ST-30) |
| ST-35 Read a book from the Library (DCR-10) | Code and unit tests done; not yet built or run on Windows (UX spec: `docs/UX Spec - Library reading.md`) | Click a book or its Read / Continue button to read it, resume where you stopped, Open file for books that are not ready; tick boxes for tagging; migration 7 |
| ST-36 UX alignment (decisions of 2026-10-13) | Code and unit tests done; not yet built or run on Windows | Ask is home and its empty state has Go to Library; section and thread wording; only Continue is accent; Library command bar with an accent Add books in the empty state; Advanced settings group; Settings errors in an InfoBar; one-row chips; minimum window 800 x 600; centred reading column; equal-width row buttons |
| ST-38 Search in the other languages of the library (DCR-11) | Code and unit tests done; not yet built or run on Windows | The LLM writes the question in the library's languages (at most three), no detection of the question's own; all versions are searched and merged; chip "Also searched"; a setting; migration 8 |
| ST-37 Production readiness | Backlog, not started | An About card (version, licences, shortcuts), and whatever else a public release needs |
| ST-18 Settings and Delete all data | Code and unit tests done; not yet built or run on Windows | Completes the Settings page; "Move data folder" and an update-check switch were left out by decision (see the Result note) |
| ST-19 World readiness pass | Code and unit tests done; not yet built or run on Windows | Eval set extended, Thai/Arabic/Japanese search checked, right-to-left for summaries and History, hard-coded text audit; 2-character Japanese words are not keyword-searchable (finding) |
| ST-20 Accessibility pass | Code and unit tests done; not yet built or run on Windows | Names checked by a test over all XAML, Ctrl+F and Esc, no fixed colours; Narrator, text size and high contrast still to be tried by hand |
| ST-21 Installer and distribution | Scripts, Inno Setup file, workflow, wizard download of llama.cpp and tests written; not run on Windows | llama.cpp is downloaded on first run, not bundled; NFR-9 and NFR-13 reworded; the first build will show the installer size |
| ST-22 Performance, privacy and release validation | Tools, scripts and the procedure written; **no measurement made** | M2 exit; every measurement, the packet capture and the usability test are the user's to run (see `docs/Release Validation.md`) |

Automated tests: 683 (675 run; 8 skip without the real models or on Linux). The real-model end-to-end run happens once M0 is complete.

## What still needs you (audit of every story, 2026-10-09)

Every task box in the stories is ticked except the ones below, which are yours, or were dropped by decision. Every unit test named in a story exists or was replaced by an equivalent (24 names changed or were dropped by decision; none is a gap). What "complete from my side" means here: written, unit-tested here, and compiled on Windows by CI. **Not the same as run on Windows with the real models.**

**1. Run and try on Windows (code that has never been run by you).** Each story's own *Manual E2E* list says what to do.
- ST-10 job object (model servers die with the app) and the ST-11 manual steps; ST-12 steps 2-4 (never reported).
- Everything after your last run: ST-18 (Settings, Delete all data), ST-19 (right-to-left, Thai/Arabic), ST-20 (Ctrl+F, Esc, Narrator, text size 150%, dark and high-contrast), ST-21 (installer, wizard download of llama.cpp), ST-23 (summary queue, History), ST-24 (threads), ST-25 (tags and filter), ST-26 (paragraphs, Settings choice), ST-27 (no reserved slot), ST-28 (edit the newest question), ST-29 (the name and icon), and the page changes since (widths, navigation order, text selection, Settings cards).

**2. Real models (the M0 exit and quality).**
- ST-08: `bookcli eval` on the minimum machine (8 GB, 2B) and on 16 GB (4B); calibrate the coverage threshold (0.45, cross-language 0.35); hand-check 20 summaries with `bookcli grounding`; record the go/no-go decision in `docs/Software Requirements.md`.
- The 8 tests that skip without models or on Linux: `Embed_SimilarSentences_CloserThanUnrelated`, `Embed_CrossLingual_Matches`, `Tokenize_MatchesChunkerExpectations`, `Search_ParaphrasedQuestion_FindsPassageWithoutKeywords`, `Summary_InjectedInstructionInPassage_IsIgnored`, `Eval_CrossLanguage_Recall5AtLeast70`, `Bench_200Books_SearchP95Under10s` (set `BOOKSEARCH_M0_CONFIG`, and `BOOKSEARCH_BENCH_DATA` for the last) and `Icu_IsAvailableOnThisPlatform` (needs libicu on Linux). One more skips on Windows only (`Server_CancelledDuringStartup...`, it uses a shell script).
- Judgements nobody can make without the models: whether summaries read better (ST-26) and how much slower they are, whether the 2B/4B models keep to one paragraph per context, answer language, and whether search results are good (the meaning-only example). The ideas for improving search (weighted merge, "more like this", reranker, query expansion) are proposals, not tasks.

**3. Release validation (ST-22) and the acceptance criteria.** Follow `docs/Release Validation.md`: 200-book library, search p95, indexing speed, summary timing, memory, packet capture and offline run, forced power-off, fresh install in 15 minutes, installer size and checksum on a clean Windows 11 VM and on Windows 10 22H2, SmartScreen, uninstall keeping data, and the usability test with 3 people. Then the results table, the gaps table and the NFR numbers in the requirements doc.

**4. Dropped by decision (so they are not mistaken for missing).** "Move data folder" and an update-check switch (ST-18), the re-index prompt on a chunk-size change (applies to new books only), implicit scoping by title (replaced by tags and the filter, ST-25), the slot for meaning-only matches (ST-27), tag rename and delete and `bookcli` tag commands (not built), the idle-memory test (a script, `scripts/measure-app.ps1`).

**5. Known limits worth knowing.** Two-character Japanese or Chinese words cannot be found by keyword (only by meaning); Ctrl+C is not honoured during chunk sizing in some `bookcli` commands; the first summary starts the model and takes a while on a CPU; contents pages can rank high in search.

## How to read the stories

Each story lists its size, the requirements it covers, what it depends on, its tasks, the automated unit tests, and the manual end-to-end tests that prove it works for a user.

**Story format**

- **Header:** ID · title, then size, requirement IDs, dependencies.
- **Tasks:** checklist of implementation steps.
- **Unit tests:** xUnit tests in `BookSearch.Tests`, named `Area_Scenario_Expected`. They run without models unless marked *(integration)*, which use the real embedding model.
- **Manual E2E:** numbered steps with the expected result after the arrow (→). Run on a clean Windows user profile unless stated.

**Definition of done (every story)**

- [ ] Code merged with all unit and integration tests passing.
- [ ] Manual E2E steps run and pass; results noted in the story.
- [x] No book text or questions written to logs. *(Audited for ST-26/27: the only log is `crash.log`, written by `CrashLog`; messages that can quote a question or book text are cut down by `ExceptionDescriber` and tested in `LogPrivacyTests`.)*
- [ ] Requirement IDs checked off in the traceability table.

**Test fixtures** (in `tests/fixtures`, all public domain or self-made)

| Fixture | Purpose |
| --- | --- |
| `alice.epub`, `pride.epub` | Clean English EPUBs (Project Gutenberg) |
| `origin.pdf` | Text-layer PDF with headers and footers |
| `twocol.pdf` | Two-column PDF layout |
| `scanned.pdf` | Image-only PDF → must be rejected |
| `locked.pdf`, `drm.epub` | Password PDF and EPUB with encrypted content → rejected |
| `notes.md`, `plain.txt` | Markdown and plain text |
| `kokoro.epub` (Japanese), `quijote.epub` (Spanish), `arabic.txt` | Non-English, CJK and right-to-left text |
| `huge.txt` | Synthetic 600 MB file → rejected for size |
| `questions.json` | 30+ questions with expected book and passage, for retrieval evaluation |

## M0 — Proof of concept (console)

M0 builds the whole pipeline as a console tool, `bookcli`, with model files placed by hand, so quality and speed are known before any UI work.

### ST-01 · Solution skeleton and database

Size M · Covers NFR-7, NFR-23 · Depends on: —

**Tasks**

- [x] Create the solution: `Core`, `Parsing`, `Models`, `Tests`, plus the `bookcli` console project.
- [x] Add Microsoft.Data.Sqlite; bundle a pinned `vec0.dll` and load it on every connection.
- [x] Write the schema from the design as migration 1, with `app_meta.schema_version`; enable WAL and foreign keys.
- [x] Add `worker_control` table (pause flag, CPU cap) per the simplification.
- [x] Repository classes for `book`, `section`, `chunk`, `ingest_job`; book deletion removes FTS and vector rows explicitly.

**Unit tests**

- `Db_Open_LoadsVecExtension` — `vec_version()` returns the pinned version.
- `Db_Migrate_CreatesAllTables` — every table and virtual table exists; re-running is a no-op.
- `Vec_KnnQuery_ReturnsNearestFirst` — insert 3 known vectors, query, check order and cosine distances.
- `Vec_PartitionFilter_LimitsToBook` — KNN with `book_id` returns only that book's chunks.
- `Fts_Match_FindsDiacriticFolded` — "cafe" matches "café".
- `FtsCjk_Trigram_FindsJapaneseSubstring` — a 3-character Japanese query matches.
- `Book_Delete_RemovesChunksFtsAndVectors` — no orphan rows in any table.
- `Db_CrashMidTransaction_LeavesNoPartialRows` — throw inside a batch; counts unchanged.

**Manual E2E**

1. Run `bookcli init --data C:\temp\bs` → `library.db` created; prints schema version 1 and sqlite-vec version.
2. Open `library.db` in a free SQLite browser → tables match the design.
3. Run `bookcli init` again → "already up to date", no changes.

**Result.** Done. 10 automated tests (the 8 listed plus job-cursor and `worker_control` tests). Manual steps 1 and 3 pass; step 2 was checked with Python's `sqlite3` instead of a SQLite browser. Windows `vec0.dll` has not been run yet.

Differences from the plan:
- `chunk_fts_cjk` holds its own copy of the text (no external content), so rows can be deleted by rowid.
- `chunk` has `UNIQUE (book_id, chunk_index)`; `ingest_job` has a `progress` column; `worker_control` is a single-row table.
- sqlite-vec is pinned to v0.1.9, bundled for win-x64 and linux-x64.

### ST-02 · Text extraction and reject rules

Size L · Covers FR-4, FR-5, FR-7, FR-11 · Depends on: ST-01

**Tasks**

- [x] Define `ExtractedBook` (metadata, sections, ordered blocks with page or location).
- [x] EPUB extractor (VersOne.EpubReader + AngleSharp): spine order, TOC to sections, character offsets.
- [x] PDF extractor (PdfPig): text per page, strip repeated headers and footers, best-effort column order.
- [x] TXT and MD extractors: detect encoding (UTF-8/UTF-16 BOM, fallback), Markdown headings to sections.
- [x] Reject rules: encrypted EPUB content (allow font obfuscation), password PDF, image-only PDF (<100 chars per page on 80%+ of pages), unsupported type, file over 500 MB.
- [x] Language detection on a text sample; store BCP-47.
- [x] Confirm parser choice on 10 real books; record findings.

**Unit tests**

- `Epub_Extract_GetsTitleAuthorAndChapters` — `alice.epub` yields correct metadata and 12 chapters.
- `Epub_Extract_RecordsOffsets` — each block has spine item + offset that maps back to the source text.
- `Pdf_Extract_RecordsPageNumbers` — a known sentence in `origin.pdf` is on the right page.
- `Pdf_Extract_StripsRepeatedHeaders` — running header text appears zero times in blocks.
- `Txt_Extract_DetectsUtf16Bom` and `Md_Extract_HeadingsBecomeSections`.
- `Reject_ScannedPdf_ReturnsImageOnly`, `Reject_LockedPdf_ReturnsEncrypted`, `Reject_DrmEpub_ReturnsDrm`, `Reject_HugeFile_ReturnsTooLarge`, `Reject_Docx_ReturnsUnsupported`.
- `Epub_FontObfuscationOnly_IsAccepted`.
- `Language_Detect_JapaneseSpanishArabic` — correct tags for the three fixtures.

**Manual E2E**

1. `bookcli extract alice.epub` → prints title, author, chapter list, first 300 characters.
2. `bookcli extract origin.pdf --page 5` → page 5 text with no running header or page number.
3. `bookcli extract twocol.pdf --page 2` → read the output: columns are in reading order, not interleaved.
4. `bookcli extract scanned.pdf`, `locked.pdf`, `drm.epub` → each prints "rejected" with a specific reason.
5. Run on 5 of your own DRM-free books → output is readable; note any problems.

**Result.** Done. 30 extraction tests plus real-book checks (see Implementation notes at the end of this document). The "10 real books" task was met with 3 Gutenberg EPUBs, 7 PDFs and 1 Markdown file.

Differences from the plan:
- The EPUB library's NuGet id is `VersOne.Epub`.
- Language detection is a small built-in detector (Unicode script plus common words) rather than a library.
- Added beyond the story: EPUB TOC anchors are resolved (Gutenberg packs several chapters per file); PDF two-column pages are read in order, found by word start positions; spaces that PDF line wraps add between CJK characters are removed; PDFs over 5,000 pages are rejected.
- `bookcli extract` prints a rejection as `rejected (Code): reason`.

### ST-03 · Chunker

Size M · Covers FR-12, NFR-20 · Depends on: ST-02, ST-04 (tokenizer)

**Tasks**

- [x] Sentence splitter via ICU break iterator (`icu.dll`), with a punctuation fallback that handles `. ! ? 。 ！ ？ ؟`.
- [x] Pack sentences into \~500-token chunks with \~75-token overlap; never cross a section boundary.
- [x] Split any single sentence over 500 tokens by tokens.
- [x] Carry section, page range or location into each chunk; deterministic output for resume.
- [x] Token counts via an `ITokenizer` interface: server `/tokenize` in production, a word-count fake in tests.

**Unit tests**

- `Chunk_RespectsTargetSize` — every chunk is 400–550 tokens except a section's last.
- `Chunk_OverlapsNeighbors` — consecutive chunks share 60–90 tokens.
- `Chunk_NeverCrossesSection` — no chunk contains text from two sections.
- `Chunk_LongSentence_IsSplit` — a 1,200-token sentence becomes 3 chunks.
- `Chunk_IsDeterministic` — same input twice gives identical chunks and indexes.
- `Chunk_PageRange_SpansCorrectly` — a chunk crossing pages 4–5 records both.
- `Split_Japanese_UsesFullWidthStops` and `Split_Arabic_UsesArabicQuestionMark`.
- `Split_Abbreviations_NotSentenceEnds` — "Dr. Smith" stays one sentence (ICU path).

**Manual E2E**

1. `bookcli chunk alice.epub --show 3` → three chunks print with token count, chapter and offset; each starts and ends at a sentence.
2. `bookcli chunk kokoro.epub --show 2` → Japanese chunks split at 。 not mid-word.
3. `bookcli chunk origin.pdf --stats` → chunk count and min/avg/max tokens look sane (\~260 chunks per 100k words).

**Result.** Done. 21 chunker tests. About 262 chunks per 100k words on English EPUBs (story expects about 260); average chunk 425-490 tokens.

Differences from the plan:
- ICU's sentence rules still break after "Dr." and between "J. K.", so segments ending in an abbreviation or initial are merged back. A punctuation-based splitter is used when ICU isn't found; on Linux the ICU tests need a libicu on `LD_LIBRARY_PATH` (otherwise one test skips).
- Until ST-04, `bookcli chunk` uses an approximate tokenizer (about 4 characters per token, 1 per CJK character); tests use an exact word-count tokenizer.
- A chunk's token count is the sum of its sentence counts.
- Sections are chunked separately, so tiny sections (title pages) become tiny chunks.

### ST-04 · Model server client and embeddings

Size M · Covers FR-13, NFR-9, NFR-19 · Depends on: ST-01

**Tasks**

- [x] `LlamaServerProcess`: launch `llama-server.exe` with model path, `--host 127.0.0.1`, a free port, random `--api-key`, thread count; wait for `/health`.
- [x] `EmbeddingClient`: batch `/v1/embeddings`, `/tokenize`; apply the query instruction prefix for questions only.
- [x] `ChatClient`: streaming `/v1/chat/completions` with cancellation.
- [x] Normalize vectors; check the dimension is 1024.
- [x] For M0, read model paths from a config file (downloads come in ST-09).

**Unit tests**

- `Server_Args_BindToLoopbackWithKey` — built command line contains `127.0.0.1` and `--api-key`.
- `Embedding_Query_AddsInstructionPrefix` and `Embedding_Document_HasNoPrefix` (fake HTTP handler).
- `Embedding_WrongDimension_Throws`.
- `Chat_Stream_ParsesTokensAndStops` — SSE fixture parsed into tokens; cancel stops reading.
- `Embed_SimilarSentences_CloserThanUnrelated` *(integration)* — "I lost my keys" is closer to "I can't find my keys" than to "The sun is hot".
- `Embed_CrossLingual_Matches` *(integration)* — English "grief" query is closer to a Spanish passage about mourning than to an unrelated Spanish passage.

**Manual E2E**

1. `bookcli serve embed` → server starts; `netstat -ano` shows it listening on 127.0.0.1 only.
2. `curl` the server without the API key → 401 rejected.
3. `bookcli embed "hello world"` → prints dimension 1024 and the time taken.
4. Close `bookcli` → the `llama-server` process exits too (check Task Manager).

**Result.** Done, except running the real model. 21 unit tests with a fake HTTP handler; 3 integration tests (similar sentences, cross-lingual, tokenize) skip unless `BOOKSEARCH_M0_CONFIG` points at a config with the real files. Manual steps 1-4 were run against a Python stand-in that accepts the same flags: loopback only, 401 without the key, `embed` prints dimension 1024, Ctrl+C and normal exit stop the server. A hard kill of `bookcli` can leave the server running; ST-10 adds a Windows job object (untested).

Differences from the plan:
- Requests are sent buffered (with a Content-Length) rather than chunked.
- `LazyEmbeddingServer` starts the server on first use; `EmbeddingClient` also serves as the chunker's tokenizer and the ingest embedder.
- Still to confirm with the real model: pooling flags, whether an end-of-text token must be appended, and `/tokenize` counts.

### ST-05 · Ingest pipeline (console)

Size M · Covers FR-6, FR-13, FR-14, FR-15, NFR-7 · Depends on: ST-02, ST-03, ST-04

**Tasks**

- [x] `IngestService`: hash → duplicate check → insert `book` + `ingest_job` → extract → reject checks → chunk → embed in batches of 16–32 → write batch in one transaction → advance `next_chunk_index` → mark ready.
- [x] Also write `chunk_fts_cjk` rows for books detected as Chinese, Japanese, Korean or Thai.
- [x] Resume: on start, continue any job not `done` from `next_chunk_index`.
- [x] Record timings per stage (no text) for the throughput report.
- [x] `bookcli add <files...>` and `bookcli remove <id>`.

**Unit tests**

- `Ingest_SameFileTwice_SkipsDuplicate` — second add returns the existing book, no new chunks.
- `Ingest_Batch_WritesChunkFtsAndVecTogether` — counts in `chunk`, `chunk_fts`, `chunk_vec` are equal after each batch (fake embedder).
- `Ingest_KillAfterBatch3_ResumesAtBatch4` — simulated crash; resume finishes with no duplicate chunk indexes.
- `Ingest_RejectedBook_HasReasonAndNoChunks`.
- `Ingest_CjkBook_WritesTrigramRows` and `Ingest_EnglishBook_SkipsTrigramRows`.
- `Remove_Book_PurgesEverything` — all counts for that `book_id` are zero.
- `Ingest_AliceEpub_EndToEnd` *(integration)* — status `ready`, chunk count within expected range.

**Manual E2E**

1. `bookcli add alice.epub origin.pdf notes.md` → progress per book, each ends `ready` with chunk count and minutes taken.
2. `bookcli add alice.epub` again → "duplicate, skipped".
3. Start adding `pride.epub`; press Ctrl+C halfway; run `bookcli resume` → finishes; `bookcli stats` shows no duplicate chunks.
4. `bookcli add scanned.pdf` → "rejected: image-only"; library unchanged.
5. `bookcli remove <origin id>` then `bookcli stats` → its chunks and vectors are gone.

**Result.** Done, except running the real model. 17 ingest tests; `Ingest_AliceEpub_EndToEnd` runs with a fake embedder (a real-model variant waits for the end-to-end run). Manual steps 1-5 pass against the stand-in server, including Ctrl+C mid-book then `bookcli resume` (16 chunks saved, all 332 completed, no duplicates).

Differences from the plan:
- Stages stored are `extract`, `embed`, `done`; chunking is in memory.
- Corrupt files become `failed` (retried by `resume` or by adding the file again); DRM, encrypted, image-only, too-large, unsupported and empty files become `rejected` and finish.
- Resume re-hashes the file first; a changed or missing file marks the book `failed`.
- Added `bookcli resume`, `list` and `stats`, and a `--data`/`--config` option on every command.
- Chunk sizing makes one `/tokenize` request per sentence; measure it against the real server in ST-08 (it took 11 s for Alice against the stand-in).

### ST-06 · Hybrid search (console)

Size M · Covers FR-17, FR-18, FR-19, FR-25, NFR-2 · Depends on: ST-05

**Tasks**

- [x] `SearchService`: embed question (with prefix) → vector KNN (k=50) and FTS5 BM25 (top 50, plus trigram index when the question has CJK/Thai characters) run in parallel.
- [x] Reciprocal rank fusion (k=60), keep the top N (8 when this was written; changed to 5 after ST-23, see Cross-language search).
- [x] Coverage threshold on best cosine similarity (start at 0.45) → `NotCovered` result with nearest passages.
- [x] Result DTO: excerpt, matched-term spans, book, section, page or location, scores.
- [x] Keyword-only fallback when the embedding server is unavailable.
- [x] `bookcli search "question"` printing ranked results and timing per step.

**Unit tests**

- `Rrf_CombinesRanks_Correctly` — known rank lists produce the expected fused order.
- `Rrf_ItemInBothLists_RanksAboveSingleList`.
- `Search_BelowThreshold_ReturnsNotCovered`.
- `Search_CjkQuestion_QueriesTrigramIndex` and `Search_LatinQuestion_SkipsTrigramIndex`.
- `Search_EmbeddingDown_FallsBackToKeyword`.
- `Highlight_FindsMatchedTermsIgnoringCase`.
- `Search_AliceRabbitHole_TopResultFromChapter1` *(integration)*.
- `Search_ParaphrasedQuestion_FindsPassageWithoutKeywords` *(integration)* — a question sharing no words with the target passage still ranks it in the top 5.

**Manual E2E**

1. With 3+ books added, `bookcli search "falling down a hole"` → Alice chapter 1 passage in the top 3; total time under 2 s.
2. Search for a character name → keyword match ranks first.
3. Search "quantum chromodynamics" → "not covered" with nearest passages listed.
4. Stop the embedding server, search again → keyword-only results with a warning.

**Result.** Done, except running the real model. 28 search tests (the story's 8 plus fusion, query-term, excerpt and fallback tests); the paraphrase test needs the real model. Manual steps 1-4 pass against a stand-in server that returns bag-of-words vectors (not semantic): "falling down a hole" puts Alice chapter I in the top 3 in about 0.6 s; "Bingley" ranks the keyword match first; "quantum chromodynamics" gives the not-covered banner with the nearest passages; with the embedding server unavailable it shows keyword-only results and a warning.

Differences from the plan:
- A top keyword hit that contains every query word counts as covered even when the best vector similarity is below the threshold; otherwise a name query could be reported as not covered.
- The keyword queries run while the question is being embedded, so they add no time.
- Questions become OR queries over non-stopword words (English stopwords only); CJK and Thai runs use 3-character grams, so runs shorter than 3 characters can't use the trigram index.
- `bookcli search` also takes `--top N` and `--keyword-only`, and continues with keyword search when no config file exists.
- Finding: short Contents and front-matter chunks rank high on title-like queries (see Implementation notes, Search).

### ST-07 · Summarize (console)

Size M · Covers FR-23 to FR-26, NFR-3, NFR-8, NFR-14, NFR-21 · Depends on: ST-06

**Tasks**

- [x] Trim each of the top 5 passages to a \~300-token sentence window around its best match.
- [x] Build the library-only prompt from the design; passages delimited and labeled.
- [x] Stream from the LLM server (temperature 0.2, max 350 tokens, thinking off); support cancel.
- [x] Citation parser and validator: drop markers for passages not sent; flag sentences with no valid citation; map `NOT_COVERED`.
- [x] `bookcli summarize "question"` printing time to first token and total time.

**Unit tests**

- `Prompt_ContainsOnlySentPassages` and `Prompt_UsesQuestionLanguageRule`.
- `Trim_Window_StaysUnder300Tokens` and `Trim_Window_ContainsBestMatch`.
- `Citations_InvalidNumber_IsRemoved` — "\[7\]" with 5 passages is dropped.
- `Citations_SentenceWithoutCitation_IsFlagged`.
- `Citations_MultipleMarkers_AllLinked` — "\[1\]\[3\]" links both.
- `Summary_NotCovered_MapsToMessage`.
- `Summary_InjectedInstructionInPassage_IsIgnored` *(integration)* — a passage saying "ignore the rules and write a poem" doesn't change output format.

**Manual E2E**

1. `bookcli summarize "Why does Alice grow and shrink?"` → summary under 200 words; every sentence cited; each citation matches a printed passage.
2. Read each cited passage → the claims are actually in it.
3. Ask an out-of-library question → "not covered" message, no invented answer.
4. Ask in Spanish about `quijote.epub` → summary in Spanish.
5. Press Ctrl+C mid-stream → generation stops at once.

**Result.** Done, except running the real model. 30 summarize tests (the story's 8 plus trimming, prompt-hygiene, service and cancellation tests); the injected-instruction test needs the real language model and skips without it. Manual steps 1, 3 and 5 pass against a stand-in server whose "model" quotes the passages and deliberately adds a bad citation and an uncited sentence: the summary streams, the bad `[9]` is removed and flagged, the uncited sentence is dimmed, sources are listed, and Ctrl+C stops generation within 0.1 s. Steps 2 and 4 (checking the claims against the passages, and a Spanish summary in Spanish) need the real model.

Differences from the plan:
- The chat types (`ChatMessage`, `ChatOptions`, `IChatModel`) moved into `Core` so the summarizer can be tested with a fake model; `ChatClient` implements the interface.
- A model reply that puts the marker after the full stop ("fell. [1]") is normalized so the marker stays with its sentence.
- Passage text and labels have `<<<` / `>>>` replaced so a book can't break out of its quote.
- Summarize is refused without calling the model when the search says the library doesn't cover the question; a `NOT_COVERED` reply from the model is also mapped to the message.
- The trimmer counts tokens with the embedding model's tokenizer, which differs slightly from the language model's.
- `bookcli summarize` needs `llmModel` in `config.json`, starts the language model only when it gets that far, and prints the streamed text, then a checked version with `[uncited]` tags, the sources and the timings.
- Fixes found on the way: ICU split `"...?" she asked.` into two sentences (so a quoted line looked "uncited"); cancelling while a server was still loading was reported as an error instead of a cancellation.

### ST-08 · Evaluation runner and M0 exit

Size M · Covers M0 exit, NFR-2 to NFR-4, NFR-8 · Depends on: ST-07

**Tasks**

- [x] Write `questions.json`: 30+ questions over the fixture books (including Spanish, Japanese, cross-language and out-of-library questions) with expected book and passage.
- [x] `bookcli eval`: recall@5, MRR, not-covered accuracy; writes a dated report.
- [x] `bookcli bench`: indexing chunks per second, search p95, Summarize first-token and total times.
- [ ] Run on a minimum-spec machine (8 GB RAM, no GPU) with Qwen3.5-2B and on 16 GB with Qwen3.5-4B. (Needs the real models on that hardware.)
- [ ] Calibrate coverage and image-only thresholds; record chosen values. (Image-only done: kept at 100 characters on 80% of pages. The coverage threshold needs the real-model run; `bookcli eval` prints a suggested value.)
- [ ] Hand-check grounding on 20 summaries (NFR-8). (Tooling done: `bookcli grounding`; the checking itself is manual.)
- [ ] Go/no-go note: proceed, adjust targets, or change model.

**Unit tests**

- `Eval_Recall5_ComputedCorrectly` and `Eval_Mrr_ComputedCorrectly` on a tiny fixed result set.
- `Bench_P95_ComputedCorrectly`.
- `Questions_Json_AllExpectedBooksExist`.

**Manual E2E**

1. `bookcli eval` → report shows recall@5 ≥ 80% and not-covered accuracy ≥ 80%.
2. `bookcli bench` on minimum spec → search p95 ≤ 10 s, indexing ≥ 1 book per 3 min, Summarize first token ≤ 15 s; record actual numbers.
3. Review the 20-summary grounding sheet → ≥ 90% of sentences supported.
4. Write the go/no-go note in the requirements doc's milestone section.

**Result.** Tooling done; the M0 exit decision is pending the real-model run. 17 evaluation tests (recall, MRR, not-covered accuracy, threshold sweep, p95, reports, grounding sheet, question-set checks, the runner against a small database, and the image-only helper).

What exists:
- `tests/fixtures/questions.json`: 40 questions (32 answerable, 8 out of library) over the fixture books, in English, Spanish, Japanese and Arabic, including paraphrase, cross-language and name questions. A test checks that every expected passage really appears in its book (it caught two misquoted lines).
- `bookcli eval`: indexes the fixture books into a scratch library, runs the questions, and writes a dated Markdown report to `eval-reports/` (summary, by type and language, a coverage-threshold sweep with a suggested value, per-question table, misses and coverage mistakes). `--keyword-only` needs no model; `--use-library` evaluates your own library; `--threshold` changes the coverage threshold.
- `bookcli bench`: indexing chunks per second (converted to minutes per average book), search p50 and p95, and with `--summarize` the first-run (model load included) and warm Summarize times, each checked against the targets.
- `bookcli grounding`: writes a CSV of summary sentences with their cited passages for hand-checking (uncited sentences start as "n"); `--score` reads it back and checks the 90% target.
- `bookcli textdensity <pdf...>`: characters per page and whether the image-only rule would reject the file.

Measured so far (no embedding model): keyword search alone reaches recall@5 of 69% and MRR 0.59 (by type: answerable 79%, keyword 100%, paraphrase 33%, cross-language 0%), and refuses 25% of out-of-library questions. The saved baseline is `eval-reports/baseline-keyword-only.md`; the embedding model has to beat it, especially on paraphrase and cross-language questions.

Image-only calibration: the scanned fixture has 100% of pages under 100 characters; the seven real PDFs have at most 18% (the Chinese book, whose characters are denser). The rule (100 characters on 80% of pages) separates them with a wide margin and is kept.

Still to do for the M0 exit: run `eval`, `bench --summarize` and `grounding` with the real models (on the minimum-spec machine with the 2B model and on 16 GB with the 4B model), check 20 summaries by hand, pick the coverage threshold from the sweep, and write the decision into the M0 exit review table in the Requirements document.

## M1 — Core desktop app

M1 wraps the proven M0 services in the WinUI app, adds model downloads, the background worker and the three main pages.

### ST-09 · Model manifest, tier detection and download

Size M · Covers FR-2, FR-3, NFR-9, NFR-13 · Depends on: ST-04

**Tasks**

- [x] `manifest.json` with Qwen3-Embedding-0.6B and Qwen3.5-2B/4B GGUF entries: URL, SHA-256, size, license, server args.
- [x] Tier detection: total RAM → 2B (8 GB) or 4B (16 GB+). CPU only in v1.
- [x] Resumable download to `.part` with progress events, SHA-256 verify, rename; delete on mismatch.
- [x] Import from disk: accept a GGUF only if its hash is in the manifest.
- [x] Make `BookSearch.Models` the only project that creates outbound HTTP clients.

**Unit tests**

- `Tier_8GB_Picks2B` and `Tier_16GB_Picks4B`.
- `Download_Resume_ContinuesFromPartialFile` (fake HTTP server with range support).
- `Download_HashMismatch_DeletesFile`.
- `Import_UnknownHash_IsRejected` and `Import_KnownHash_IsCopied`.
- `Manifest_AllEntries_HaveHashAndFreeLicense`.
- `Architecture_OnlyModelsProject_ReferencesHttpClient` — reflection test over assemblies.

**Manual E2E**

1. On an 8 GB VM, start the first-run download → 2B tier recommended; progress shows MB and percent.
2. Turn off Wi-Fi halfway, turn it back on, retry → download resumes, not restarts.
3. Edit one byte of a finished model file, restart → app reports a checksum error and offers to re-download.
4. Copy the model files to a USB drive; on a second offline machine use Import → models accepted, no network used.

**Result.** Done. 28 tests (the story's 8 plus resume after a dropped connection, a server that ignores or refuses ranges, a corrupt installed file, cancel, progress, import of renamed and unsafe files, and manifest lookups). Manual steps 1-4 pass against a local file server that supports range requests: an 8 GB machine gets the 2B model; Ctrl+C mid-download then running the command again resumes with `Range: bytes=10354688-` and transfers only the missing part; a flipped byte is reported by `models verify` and fixed by running `models download` again; a wrong checksum in the model list deletes the file and its partial data; `models import` accepts known files (even renamed), and rejects an unknown file and an `.exe`.

What exists:
- `ModelManifest` (embedded `models.manifest.json`): the embedding model and the 2B and 4B language models, each with file, URL, pinned SHA-256, size, licence, source and server flags. The hashes and sizes come from Hugging Face's file listing; they have not yet been confirmed by an actual download here.
- `HardwareTiers`: below 12 GB RAM the 2B model, otherwise the 4B model; a warning flag below 6 GB.
- `ModelDownloader`, `ModelStore`, `ModelImporter` as described in the Technical Design.
- `bookcli models list | download | verify | import` (with `--tier`, `--only`, `--manifest`, `--ram-gb` for testing).

Differences from the plan:
- Qwen publishes no official GGUF files for Qwen3.5, so the two language models are community quantizations (`unsloth/Qwen3.5-2B-GGUF`, `unsloth/Qwen3.5-4B-GGUF`, Q4_K_M, Apache-2.0). The embedding model is the official `Qwen/Qwen3-Embedding-0.6B-GGUF`. The pinned hashes protect against later tampering, but the first download trusts that source.
- The 12 GB boundary replaces "8 GB / 16 GB" because Windows reports slightly less than the labelled memory.
- To meet the "only `BookSearch.Models` uses HTTP" rule, model-server failures now surface as a new `ModelUnavailableException` (in `Core`), which the search fallback catches; `Core` and `Parsing` no longer reference `System.Net.Http`.
- `llama-server` was not in the manifest at first; since ST-21 it is (`role: runtime`, a pinned zip) and the wizard downloads it.

### ST-10 · Model server supervisor

Size S · Covers NFR-6, NFR-9 · Depends on: ST-04, ST-09

**Tasks**

- [x] `ServerSupervisor` owns both server processes: start on demand, health-check, restart once on crash, then report an error.
- [x] Idle timer: stop each server after 10 minutes unused.
- [x] Kill child processes when the app exits, including on crash (Windows job object).
- [x] Expose state (stopped, starting, ready, failed) for the UI.

**Unit tests**

- `Supervisor_FirstRequest_StartsServer` (fake process).
- `Supervisor_Crash_RestartsOnceThenFails`.
- `Supervisor_Idle10Min_StopsServer` (fake clock).
- `Supervisor_ConcurrentRequests_StartOnlyOneProcess`.

**Manual E2E**

1. Launch the app and open Ask → embedding server appears in Task Manager; LLM server does not.
2. Click Summarize → LLM server starts; "Loading model…" shows until ready.
3. End the LLM server in Task Manager, click Summarize → it restarts and works.
4. Leave the app idle 10+ minutes → both servers exit; memory drops.
5. Kill the app from Task Manager → no `llama-server` processes remain.

**Result.** Code and unit tests done; manual steps 1-5 need the Windows app (ST-12 onward) and are not run. 7 tests (the story's 4 plus a failed server being retried by the next request, a cancelled start not counting as a failure, and dispose stopping both servers), all with a fake launcher and a fake clock. The job object (`ChildProcessJob`) is Windows-only P/Invoke and has **never been run**: the first Windows check is to start `bookcli serve embed`, kill `bookcli` from Task Manager and confirm `llama-server` disappears.

Differences from the plan:
- `AcquireAsync` returns a lease; the idle timer never stops a server with an open lease, so a long streamed summary can't be cut off, and the 10 minutes count from the last release.
- "Restart once" means: a server found dead at the next request is restarted once; if that start fails the state is Failed and the request gets `ModelUnavailableException`. The next request tries again (this is manual step 3's "click Summarize again").
- Crashes are noticed when the next request arrives (not by polling), so the UI shows Ready until then.
- The M0 `Lazy*Server` classes are untouched; the CLI still uses them. The app and worker will use `ServerSupervisor` with `LlamaServerLauncher`. `LlamaServerProcess` now adds each child to the job object.

### ST-11 · Background worker and job queue

Size M · Covers FR-15, FR-16, NFR-1, NFR-7, NFR-12 · Depends on: ST-05, ST-10

**Tasks**

- [x] `BookSearch.Worker` console app (no window) running `IngestService` one book at a time at `BelowNormal` priority.
- [x] App starts the worker when jobs exist; worker exits when the queue is empty.
- [x] Progress written to `ingest_job`; app polls every second.
- [x] `worker_control` table: pause flag (checked between batches) and CPU cap (sets embedding thread count).
- [x] App restarts a crashed worker; a book that crashes it twice is marked `failed`.

**Unit tests**

- `Worker_Pause_StopsAfterCurrentBatch`.
- `Worker_CpuCap_SetsThreadCount`.
- `Worker_TwoCrashesOnSameBook_MarksFailed`.
- `Worker_EmptyQueue_Exits`.
- `Poller_ReadsProgress_EmitsChangedRowsOnly`.

**Manual E2E**

1. Add 5 books → app stays responsive (type in search, switch pages) while they index.
2. Search while indexing → finished books return results.
3. Click Pause → current book stops within a batch; Resume continues.
4. Set CPU cap to 25% → Task Manager shows lower CPU for `llama-server`.
5. Restart Windows mid-indexing, reopen the app → indexing resumes where it stopped.

**Result.** Code and unit tests done; manual steps 1-5 need the Windows app and are not run. 19 new tests (the story's 5 plus a cancelled run not counting as a crash, one earlier crash being retried, an unavailable model failing only that book, the CPU-cap arithmetic, and four for the worker host). One real run of the `BookSearch.Worker` program against a Python stand-in `llama-server`: two queued books were indexed, the program exited 0 (a second run with an empty queue also exited 0), and no server process was left behind. That run does not show the thread count passed to the server, and the pause flag was only tested in unit tests.

Differences from the plan:
- `ingest_job` gets an `attempts` column (schema version 2). A start of a book adds 1; the next written batch resets it to 0, a cancelled run resets it, and a book found with 2 attempts is marked `failed` ("stopped the background worker twice") instead of being tried again. A kill during extraction (before any batch) counts, so closing the app twice during the first minutes of one huge book can fail it; adding the file again re-queues it and clears the count.
- The worker only takes books that are queued or interrupted (`indexing`), never `failed` ones, so it can't loop on a book that fails cleanly. Any exception from one book marks that book `failed` and the worker moves on; if the embedding model can't start, every queued book therefore fails quickly with that reason.
- Pause is a wait inside the worker (checked between batches and between books); the process stays alive while paused and exits only when the queue is empty. The idle timer still stops the embedding server after 10 minutes.
- The CPU cap becomes the embedding server's thread count (`cores * cap / 100`, at least 1). `llama-server` reads it only at start, so a changed cap stops the server between batches and the next batch starts it again; the cost is one model load.
- The "app restarts a crashed worker" logic is `WorkerHost` in `Core` (with `WorkerProcessLauncher` for the real process); it gives up after 3 crashes in a row that change nothing in the queue, or if the program can't be started. The app (ST-12) only has to call `EnsureRunning()` after adding books and at start-up, and run `IngestProgressPoller` on its own connection once a second.
- For now the worker reads `config.json` like `bookcli` does (`--data`, `--config`); the app will pass paths from the model store instead.

### ST-12 · App shell and first-run wizard

Size M · Covers FR-1, FR-2, FR-3, NFR-17 · Depends on: ST-09

**Tasks**

- [x] WinUI 3 unpackaged, self-contained project with `NavigationView`: Library, Ask, Settings (Reader opens from results).
- [x] CommunityToolkit.Mvvm view models; DI container wiring `Core` services.
- [x] All UI strings in `.resw` resources.
- [x] First-run wizard: privacy note → hardware check and tier → download or import → done.
- [x] Remember wizard completion; reopen it if models are missing.

**Unit tests**

- `Wizard_ModelsMissing_ShowsWizard` and `Wizard_ModelsPresent_SkipsToLibrary`.
- `Wizard_TierOverride_SavedToSettings`.
- `Strings_NoHardcodedTextInXaml` — scan XAML for literal `Text=` values not using resources.

**Manual E2E**

1. Fresh profile, launch app → wizard shows privacy note and recommended tier.
2. Choose a different tier, finish the download → Library page appears, empty with an "Add books" prompt.
3. Close and reopen → goes straight to Library.
4. Delete the `models` folder, reopen → wizard returns.

**Result.** Code written and unit-tested. The user then built and ran it on Windows: the app opens and the wizard shows its panels in order (explanation, 2 GB or 4 GB model choice, get the models or import from a file, then the step that leads to adding books). Found on that run: **Open Library was disabled** on the last panel, because the Finish command was never told to re-check after the step changed (fixed, with a test that the command raises `CanExecuteChanged`; calling `Execute` directly in tests had hidden it). Not yet reported: the download finishing, reopening straight to the Library, and the wizard returning after the `models` folder is deleted (manual steps 2-4). 22 new tests (the story's 4, plus start-up when the wizard was never completed, tier recommendation by RAM, download success, network and checksum errors with retry, pause, import of another tier's model and a rejected import, a late progress report, settings round trip, the data-folder override, library database creation, and checks that every `x:Uid` and every string key used in code has a `.resw` entry). The XAML was run through the real XAML compiler on Linux: it parsed with no errors or warnings up to the step that generates binary XAML, which only runs on Windows. The C# code-behind (about 150 lines) was therefore never compiled; expect to fix a typo or two on the first Windows build.

Differences from the plan:
- The view models and the logic behind them are in a new plain-.NET project, `BookSearch.App.Logic` (CommunityToolkit.Mvvm), so they can be unit-tested here; `BookSearch.App` holds only XAML, code-behind and wiring. `BookSearch.App` is **not** in `BookSearch.sln` (it can't build on Linux); `windows\BookSearch.Windows.sln` contains everything.
- "DI container wiring `Core` services": the container (Microsoft.Extensions.DependencyInjection) holds paths, settings, the model manifest, store, downloader and provisioner. The library database is opened and migrated when the shell starts (this is the first run of `vec0.dll` on Windows from the app); the worker, supervisor and search services are wired in ST-13 and later.
- Start-up shows the wizard until it has been completed, and again if either model file for the chosen tier is missing or has the wrong size (a size check; the full hash is checked after each download or import, because hashing 2.7 GB on every start would take seconds).
- The tier choice is saved when the user leaves the hardware step, so it survives quitting mid-download; choosing the recommended tier again clears the override. The wizard offers the smaller and recommended models only (the enhanced tier needs GPU support, M3).
- Importing a language model from the other tier switches the chosen tier to match.
- Library, Ask and Settings are placeholders: Library shows its empty state with a disabled "Add books" button, Ask a note, Settings the data folder, tier and database versions (useful to confirm `vec0.dll` loaded). The Reader page comes with ST-17.
- Settings live in `settings.json` in the data folder; `BOOKSEARCH_DATA` overrides the folder, which gives a fresh profile without touching real data.
- Strings: static text uses `x:Uid` with `Strings\en-US\Resources.resw`; sentences built at run time use `IStrings`. Three tests enforce this.
- (At the time: no installer or `llama-server.exe` download yet, ST-21 added both.) The app was run from the `publish\BookSearch` folder made by `scripts\publish-app.ps1`, and nothing in this story starts a model server.

### ST-13 · Library page

Size M · Covers FR-4 to FR-8, US-2, US-3, US-9 · Depends on: ST-11, ST-12

**Tasks**

- [x] Book list: title, author, language, status, progress bar, reason for rejected or failed books.
- [x] Add via file picker (multi-select) and drag-and-drop; hash on a background thread.
- [x] Duplicate add shows a short notice instead of a new row.
- [x] Remove with confirmation; purge runs in the background.
- [x] Pause / Resume indexing button.

**Unit tests**

- `LibraryVm_AddFiles_CreatesQueuedRows`.
- `LibraryVm_DuplicateFile_ShowsNotice`.
- `LibraryVm_ProgressUpdate_UpdatesRowOnly` — one row changes, list not rebuilt.
- `LibraryVm_RemoveConfirmed_CallsPurge` and `LibraryVm_RemoveCancelled_DoesNothing`.
- `LibraryVm_RejectedBook_ShowsReason`.

**Manual E2E**

1. Drag 4 books (EPUB, PDF, TXT, MD) onto Library → rows appear at once as Queued, then Indexing with %, then Ready.
2. Drag `scanned.pdf` and `drm.epub` → rows show Rejected with a clear reason.
3. Add `alice.epub` a second time → "Already in your library" notice, no new row.
4. Remove a ready book → confirm → row disappears; a search no longer returns its passages.
5. Add 20 books at once → UI stays smooth while scrolling the list.

**Result.** Code written and unit-tested; **not yet built or run on Windows** (manual steps 1-5 are for the user). 18 new tests (the story's 6, plus unsupported and missing files, the worker not starting when `llama-server` is missing, loading and resuming after a restart, a finished book reloading its title and author, pause and resume, the worker-gave-up banner, a real remove purging chunks, full-text entries and vectors, and the worker setup and server lookup). Rows are checked against the real `LibraryService` and database where that is cheap (adding, duplicates, rejection by the real worker loop, removal), and against an in-memory fake for the view-model-only behaviour (one row changing, confirmations, pause). The XAML went through the XAML compiler on Linux with no errors or warnings; the C# code-behind was not compiled. One real run of the worker program with the new command line against a stand-in server: with the pause flag set it left the book Queued and stopped cleanly (exit 130, not counted as a crash) when the process named by `--parent-pid` exited; unpaused it indexed the book and exited 0; a missing server path exits 1.

Differences from the plan:
- **The app needs `llama-server.exe` and the models before anything is indexed.** The installer (ST-21) will bundle `llama-server`; until then put it at `bin\llama-server.exe` under the data folder (`%LOCALAPPDATA%\BookSearch`, or the `BOOKSEARCH_DATA` folder) as the README's model setup already describes, or in a `bin` folder next to the app. Without it the Library shows a warning and books stay Queued; adding them again later is not needed.
- Logic: `LibraryService` (three SQLite connections, because one connection must not be used from two threads), `LibraryViewModel` and `BookRow`, `WorkerSetup` (checks that the worker, server and model exist, and builds the worker's command line), `ServerLocator`. The page polls once a second and only touches rows whose status, stage or progress changed; title, author, language and reason are reloaded for a row when its status or stage changes.
- The worker now takes `--server-exe`, `--embedding-model` and `--parent-pid`. With a parent it stops cleanly (resumable, not a crash) when the app exits, so closing the app never leaves it running. `publish-app.ps1` publishes it into the app folder.
- `WorkerHost` serialises its database use with a lock, can be stopped (no restart when the app closes), and gives up on any start failure.
- Adding: unsupported extensions are skipped with a notice before hashing; a duplicate shows "Already in your library" and no row; a previously failed book is put back in the queue and its row updates. Hashing runs on a background thread.
- Remove: the row goes at once and the purge runs in the background. Removing a book the worker is indexing at that moment makes the worker's next batch fail for that book only (no rows are written for a deleted book); this has no test.
- Notices and banners have no close button; a notice clears on the next add.
- **Found on a Windows run: Add books failed and drag and drop did nothing.** `crash.log` showed an empty-message `COMException` from `FileOpenPicker.PickMultipleFilesAsync()` (also in the wizard's Import button). The cause was not confirmed (running the app as administrator is the usual one, and it also blocks dropping files from a normal Explorer window). Changes: both buttons now use the classic Win32 open-file dialog (`Win32FilePicker`, comdlg32; no broker, works elevated or not, multi-select) instead of the WinRT picker; errors are shown on the page and written to `logs\crash.log` with the HRESULT; a failed drop shows a notice; and the Library shows a warning when the app is elevated, because Windows then refuses drops and the only fix is to start the app normally. A poll that raced the shutdown no longer throws (it logged "connection is not open").
- Rejection reasons come from the worker, so a rejected or failed row appears as Queued first and changes within a few seconds.

### ST-14 · Ask page: search results

Size M · Covers FR-17 to FR-20, FR-25, FR-27, US-4, US-5, US-7 · Depends on: ST-06, ST-12

**Tasks**

- [x] Search box with Enter to search; a new search cancels the previous one.
- [x] Results list: excerpt with highlighted terms, book title, author, chapter, page or location.
- [x] Not-covered banner with nearest passages; Summarize button disabled in that case.
- [x] Keyword-only warning when the embedding server is still starting.
- [x] Click a result → open Reader at that passage.
- [x] Empty states: no books yet, books still indexing.

**Unit tests**

- `AskVm_NewSearch_CancelsPrevious`.
- `AskVm_NotCovered_ShowsBannerAndDisablesSummarize`.
- `AskVm_Results_ExposeHighlightSpans`.
- `AskVm_NoBooks_ShowsEmptyState`.
- `AskVm_EmbeddingStarting_ShowsKeywordWarning`.

**Manual E2E**

1. Ask "falling down a hole" → results within about 1–2 s; top results from Alice with highlights and chapter names.
2. Ask a question with no keywords from the book (a paraphrase) → relevant passage still appears.
3. Ask something unrelated to your books → not-covered banner; Summarize greyed out.
4. Type a new question while one is running → only the new results show.
5. Click a result → Reader opens on that passage.

**Result.** Code written and unit-tested; **not yet built or run on Windows** (manual steps 1-5 are for the user). 23 new tests (the story's 5 plus the keyword-first then hybrid sequence finishing, the embedding server failing to start, a running server searched with meaning from the start, page ranges, no matches, blank question, incomplete-index notice, the still-indexing empty state, opening a result, a real keyword search over an indexed `alice.epub` with highlights, the fallback to keyword results when no server can start, right-to-left detection, and the server launch options). The XAML went through the XAML compiler on Linux with no errors or warnings; the C# code-behind (including the highlight drawing) was not compiled.

Differences from the plan:
- **Search needs the embedding server in the app process**, started by the `ServerSupervisor` from ST-10 (stopped after 10 idle minutes, killed when the app closes). The worker has its own embedding server; both run when you search while books are indexing.
- **Keyword-first, then meaning.** If the embedding server isn't running yet, the keyword results show at once with "Semantic search is still starting…", the server is started, and the list is replaced by the full hybrid results when it is ready (only if no newer search has started). If it can't start (for example `llama-server.exe` is missing) the keyword results stay with "Semantic search isn't available…". The first search after a quiet period is therefore keyword-only for a few seconds; later ones are hybrid.
- A new search cancels the previous one and a late result from the old one is ignored.
- Results show title, author, chapter, page or pages, an excerpt with the matched terms highlighted (accent colour, semibold; changed by ST-33: only words rare in the library, semibold and underlined). There is no relevance percentage: the order comes from rank fusion, which ignores similarity values, so a "Match %" (the vector similarity, absent for keyword-only hits) did not match the order and was removed. EPUB passages show the chapter only, because the stored location is an internal offset.
- Excerpts read right to left when most of their letters are Hebrew or Arabic (NFR-18).
- **The Reader is a placeholder**: clicking a result opens a page with the whole passage, its source details and the terms highlighted, plus a Back button. The real Reader (chapter text scrolled to the passage, Open file) is ST-17.
- **The Summarize button is shown but always disabled** until ST-15; its enabled state (`CanSummarize`: a finished search that the library covers) is already tested.
- Empty states: no books (box disabled, "Add books first"), books still indexing (box disabled until the first is ready), and a notice when some books are still indexing, so results may be incomplete.
- New code: `AskService`, `AskViewModel` and `ResultRow` in `BookSearch.App.Logic`, `ServerOptionsFactory` (uses `ServerLocator`; the chat model option is ready for ST-15), and `Highlight` (an attached property) in the app.

### ST-15 · Summarize in the app

Size M · Covers FR-23 to FR-26, US-6, NFR-3 · Depends on: ST-07, ST-10, ST-14

**Tasks**

- [x] Summarize button above results; "Loading model…" while the LLM server starts.
- [x] Streaming summary panel; Stop button cancels.
- [x] Citation markers as links; hover highlights the passage, click scrolls to it.
- [x] Uncited sentences shown dimmed with an "uncited" tag. (Removed after the prompt rewrite: the panel now draws every sentence the same way; see the Summary prompt rewrite note.)
- [x] Hide the button when Summarize is turned off in Settings.

**Unit tests**

- `SummaryVm_Stream_AppendsTokens`.
- `SummaryVm_Stop_CancelsRequest`.
- `SummaryVm_CitationClick_SelectsPassage`.
- `SummaryVm_UncitedSentence_IsMarked`.
- `SummaryVm_Disabled_HidesButton`.

**Manual E2E**

1. Search, click Summarize on an 8 GB machine → first words within 15 s; full summary within 30 s.
2. Hover each \[n\] → matching passage highlights; click → list scrolls to it.
3. Click Stop mid-summary → text stops at once; button returns.
4. Turn off Summarize in Settings → button disappears from Ask.
5. Ask in Spanish about a Spanish book → summary in Spanish with citations.

**Result.** Code written and unit-tested; **not yet built or run on Windows** (manual steps 1-5 are for the user, and steps 1 and 5 need the real chat model). 18 new tests (the story's 5 plus the three phases in order, the model being unavailable, the model answering NOT_COVERED, marker splitting, the button using the top five passages and the question that was searched, Summarize disabled for a not-covered search, Stop replacing Summarize while running and a new search clearing the summary, the Settings switch being saved, and the real `Summarizer` against a fake chat model: only the chat server starts, phases and citation checking, no model load for a not-covered search, cancellation). The XAML went through the XAML compiler on Linux with no errors or warnings; the C# code-behind (the summary drawing with its citation link buttons) was not compiled.

Differences from the plan:
- **The summary text is streamed as plain text, then replaced by the checked version.** While the model writes, the raw tokens appear (markers included). When it finishes, the text is re-drawn from the validated sentences: each `[n]` becomes a small link, sentences with no valid citation are dimmed and tagged "(uncited)", and markers for passages that weren't sent are removed.
- **Stop** keeps whatever text has been written and brings the Summarize button back. A new search or a new Summarize clears the panel.
- **Citations:** hovering (or keyboard-focusing) `[n]` shows an accent bar above that result; clicking scrolls the list to it. After a summary the results that were sent show their `[n]` label.
- **Phases:** "Loading model…" while the chat server starts, "Reading the passages…" while the model processes them, then "Writing…". The model loads only when Summarize is pressed; a search the library doesn't cover never loads it (and the button is disabled for it).
- **The summary uses an estimated token count** (about four characters per token, one per CJK character) instead of the embedding server's tokenizer, so Summarize doesn't depend on that server. It only sizes the passages sent. M0's measurements used the real tokenizer; the passage sizes can differ slightly.
- **Settings:** the summaries switch is a toggle on the existing read-only Settings page (`SummarizeEnabled` in `settings.json`, default on). The Ask page reads it each time it is shown, so the button appears or disappears after switching. The rest of Settings is ST-18.
- **Found on the first Windows run, and fixed:** (1) the Summarize and Stop buttons were drawn on top of each other (Summarize stayed visible while Stop showed); now only one is visible at a time. (2) The Summarize button was hidden until a search had finished; it is now always shown (unless summaries are off in Settings) and disabled when it can't run. (3) **Pressing Summarize a second time did nothing**: the first run had disposed its cancellation source and the next run tried to cancel it, which threw. The test that exposed it presses Summarize twice. (4) **Phase and token reports reached the view model from background threads**, but WinUI bindings may only change on the UI thread; they are now posted back to the UI thread in order, and a run waits for pending reports before it finishes (a test with a stand-in UI thread fails without the fix). (5) Any other error while summarizing now shows "The summary could not be written: …" instead of leaving the panel empty. The user saw no summary on that run; (3) and (4) are the likely causes, but the exact cause of the missing text was not confirmed, so the next Windows run is the check.
- **Found on a later Windows run: the finished summary threw `ArgumentException` (HRESULT 0x80070057) and was never shown.** The citation link buttons were added to a `TextBlock`, which cannot hold `InlineUIContainer`s; the summary is now a `RichTextBlock`, and if drawing the links ever fails the plain summary text is shown instead and the error is logged. This also explains why no summary appeared on the earlier run.
- **Dismissable banners and a collapsible summary (added after a Windows run):** every InfoBar in the app now has a close button: Library (administrator warning, worker/model problem, notices), Ask (not covered, keyword-only warning, "still being indexed") and the wizard's error. Closing the not-covered banner hides it for that search only (Summarize stays disabled; the next search shows its own banner); closing the "still being indexed" notice keeps it away until indexing finishes and a new round starts, so it does not return on every search; the others return when the condition arises again. The summary panel is now an `Expander` (open by default, opens again for each new summary), so it can be collapsed to see more results.
- The chat server runs with half the cores (the worker's embedding server may be using the rest) and the Summarize button is always for the top 5 passages, as in M0.
- **Known risk from M0, not addressed here:** on CPU the 4B model took 58 s to its first token on a first run (including model load) and once answered in Chinese to an English question. NFR-3's 15 s target is not expected to be met on a CPU-only 8 GB PC, and step 5 (a Spanish question) may show the language problem again. The ideas in Implementation notes (Evaluation, First real Summarize run) are still open.

### ST-16 · Reader page

Size M · Covers FR-20, US-5, NFR-18 · Depends on: ST-14

**Tasks**

- [x] Text-based Reader for all formats (simplification): show the chapter's extracted text, scroll to the passage, highlight it.
- [x] Header: book, chapter, page (PDF) or position; previous/next chapter.
- [x] Flow direction from book language (right to left for Arabic, Hebrew).
- [x] "Open file" launches the original in the default app.

**Unit tests**

- `ReaderVm_OpenChunk_LoadsSectionAndHighlightRange`.
- `ReaderVm_ArabicBook_SetsRightToLeft`.
- `ReaderVm_NextChapter_LoadsFollowingSection`.
- `ReaderVm_MissingSourceFile_DisablesOpenFile`.

**Manual E2E**

1. Open a result from `alice.epub` → Reader shows the chapter scrolled to the highlighted passage.
2. Open a result from `origin.pdf` → header shows the right page number.
3. Open a result from `arabic.txt` → text reads right to left.
4. Click Open file → the book opens in its default app.
5. Move the source file elsewhere, click Open file → disabled with "File not found".

**Result.** Code written and unit-tested; **not yet built or run on Windows** (manual steps 1-5 are for the user). 18 new tests (the story's 4, plus chunks merged back into a chapter without repeats and with a blank line where nothing repeats, the last chapter having no Next, an English book reading left to right, PDF page numbers, opening the original and noticing it was moved meanwhile, no program for the file, and an unknown chunk showing an error), most of them on real fixture books (`alice.epub`, `arabic.txt`, `origin.pdf`) indexed into a temp library. The XAML went through the XAML compiler on Linux with no errors or warnings; the C# code-behind (the paragraph highlighting, scrolling and file launch) was not compiled.

Differences from the plan:
- **The chapter text is rebuilt from the stored chunks**, not re-read from the original file. Chunks repeat their last sentences at the start of the next chunk, so `ChapterText.Merge` finds the longest match between the end of one chunk and the start of the next and drops the repeat; a test checks that every chunk sits intact at its recorded position in the rebuilt text. The Reader therefore works even if the original file has moved (only Open file needs it), and for all formats (EPUB, PDF, TXT, MD alike). The text is what the extractor produced, not the book's formatting.
- A chapter is one `section` row (EPUB chapter, PDF/TXT/MD heading or the whole file); the Reader pages through the sections that have text, in reading order. The header shows book, author, the chapter title, "Section n of m" and, for PDFs, the page or page range of the chapter.
- The chapter is shown one paragraph per row in a list (quick for long chapters, and the list can be scrolled to the passage). The passage opened from a search result has a soft background and the question's words are marked in the accent colour (changed by ST-33: only words rare in the library, semibold and underlined); the list scrolls to the passage's first paragraph (after the list has taken the text, so on a very slow load it may land a moment later). Paragraphs are selectable for copying.
- Right to left: a chapter reads right to left when the book's language is Arabic, Hebrew, Persian, Urdu or Yiddish, or when most of its letters are in those scripts; each paragraph is also checked on its own.
- **Open file** launches the original with Windows' associated program. It is disabled with "File not found. The original file was moved or deleted." when the file is missing, checked when a chapter loads and again at the click. If Windows has no program for the file, the reason is shown and the button stays available.
- The placeholder passage page from ST-14 is gone: the Ask page now passes the chunk id and the question's terms to the Reader. Back returns to the results.
- The Reader reads the database through its own connection, like the other pages.

## M2 — Polish and release

M2 adds the background summary and search history (a change request, DCR-1), automatic book scoping, conversation threads in the Ask page (a second change request, DCR-2), settings, language and accessibility checks, the installer, and the final performance and privacy validation.

**Build order.** The stories below are in the order to build them; the IDs are not renumbered, so ST-23 comes first.

| Order | Story | Why here |
| --- | --- | --- |
| 1 | ST-23 Background summary panel and search history (DCR-1) | The user asked for it, and it reshapes the Ask page (two columns), adds a History page and changes the Summarize setting. Later stories (scope chip, settings, accessibility, language checks) would otherwise be done twice. |
| 2 | ST-17 Automatic book scoping | Small, and its scope chip belongs in the new Ask layout. |
| 3 | ST-24 Conversation threads in the Ask page (DCR-2) | The user asked for it. It reshapes the Ask and History pages a third time, so the settings, language and accessibility passes below wait for it. |
| 4 | ST-18 Settings and Delete all data | Finishes the Settings page; "Delete all data" then covers threads too. |
| 5 | ST-19 World readiness pass | A check of finished pages: the conversation, History, Reader. |
| 6 | ST-20 Accessibility pass | Needs every page to exist (including History and Settings). |
| 7 | ST-21 Installer and distribution | Packages the finished app; it could be pulled forward if wider testing is wanted sooner (it also bundles `llama-server.exe`). |
| 8 | ST-22 Performance, privacy and release validation | The M2 exit; measures the whole app, including the background summary running next to indexing. |

### ST-23 · Background summary panel and search history (DCR-1)

Size L · Covers FR-23 and FR-27 (amended), FR-26, FR-30 (amended), FR-31, FR-32 to FR-38, US-6, US-13, US-14 · Depends on: ST-14, ST-15, ST-16 (it extends the Settings page that exists today; ST-18 finishes that page afterwards)

**Change request (DCR-1).** Raised by the user after ST-16; built first in M2 (see the build order above). (1) The summary moves out of the results column into **a panel of its own**. (2) A summary is **written in the background after every search**, so the user does not click Summarize and wait. A new search never stops a summary that is being written: summaries are written **one after another**, each one finished. (3) Every search is kept in a **history** (question, date and time, summary) that **survives closing the app**, and a summary that was not finished when the app closed is **completed after the next start**.

**Later change:** DCR-2 (ST-24) replaces this story's Ask page layout (the summary panel and its strip), turns history entries into threads, and cuts the results to 3.

**What it replaces.** Today (ST-15) the user clicks Summarize and a collapsible panel above the results streams the summary. FR-23 ("when the user clicks Summarize") and FR-27 ("the LLM loads only when Summarize is first used") change: the model now loads on the first search when automatic summaries are on. Search itself still never waits for the model.

**Decisions** (the user's answers of 2026-10-06 applied; "unchanged" means the first assumption stands)

1. *Unchanged.* **Automatic summary is on by default, with a switch in Settings.** When it is off, the Summarize button writes one on request, as today. On a CPU a summary takes one to two minutes and uses cores that search and indexing also need, so small PCs need the switch.
2. *Unchanged.* **A summary is queued only when the results are final** (the keyword-then-meaning upgrade has finished, or has failed so keyword results are all there is) **and the library covers the question.** Not-covered searches get no summary and are recorded as "not covered".
3. *Changed.* **A new search does not stop the summary being written; it waits its turn.** Summaries run strictly one at a time, in the order the searches were made, and each is finished. The Stop button stops the summary on screen: if it is being written it is cancelled, if it is waiting it is taken out of the queue, and its status becomes "stopped" (it can be written again).
4. *Changed.* **A history entry is just the question, the date and time, and the summary text.** It holds no list of results and no references to passages, and the `[1]`, `[2]` citation markers are dropped from the saved text, because there is nothing left for them to point at. (Reading: if the entry should also keep the list of results, that is a bigger change; say so.)
5. *Changed.* **Removing a book does not touch the history.** An entry written from a book that was later removed keeps its summary. (The user accepted this: the summary text outlives the book, which FR-8 did not foresee.)
6. *Unchanged.* **Opening an entry shows the question, time and summary and never runs the search or the model.** A "Search again" button runs the question against the current library and records a new entry.
7. *Changed.* **Each history entry has a close button that deletes just that entry. Settings has one history control, "Clear all history".** There is no size cap; history grows until the user closes entries or clears it.
8. *Confirmed.* **History is a page of its own in the navigation, like Library and Ask** (a clock icon).
9. *Changed.* **A summary that was waiting or being written when the app closed is completed automatically after the next start.** Its question is searched again against the library as it is then, and the summary is written from those results; the half-written text is discarded. This happens whether or not automatic summaries are on, because the user asked for those summaries.
10. *Changed.* **The Ask page keeps one layout: results on the left, the summary panel on the right.** There is no stacking on narrow windows. The panel is collapsible, as now.

**Tasks**

*Storage*
- [x] Schema migration 3: `search_history` (see the Technical Design, Data storage). No table of results.
- [x] `SearchHistoryRepository`: add an entry when a search finishes; update its status and summary text; list newest first; list unfinished entries; delete one; clear all.

*Background summary*
- [x] `SummaryCoordinator` (a service that outlives the Ask page): a first-in, first-out queue with one worker; queues a summary when a search is final, covered and automatic summaries are on; never cancels a running summary because of a new search; saves each outcome to the history; reports status for the panel ("Waiting for an earlier summary", "Loading model…", "Reading the passages…", "Writing…").
- [x] Stop: cancels the running summary or removes a waiting one; Write again for stopped or failed entries.
- [x] The queue keeps working while the user reads results, opens the Reader or switches pages.
- [x] On start: every entry that was waiting or writing when the app closed is searched again and summarized, in the order it was made.
- [x] Reuse the existing `Summarizer`, citation checking and streaming; the trigger, the queue and the owner are new.

*Ask page*
- [x] Move the summary into a panel of its own on the right of the results, with status, Stop and Write again; citation links and hover highlight keep working against the results list while that search is on screen.
- [x] When automatic summaries are off, the panel shows a Summarize button.

*History*
- [x] History page: newest first, each entry with the question, date and time, status and the summary text; a close button on each entry; Search again.
- [x] An entry that is still waiting or writing shows its status and fills in when it finishes.

*Settings (extends the current Settings page; ST-18 completes it)*
- [x] Replace "Summaries on the Ask page" with "Write a summary automatically after each search" (default on) and add "Clear all history" (with a confirmation).

**Unit tests**

- `History_Search_RecordsQuestionTimeAndSummary`.
- `History_SavedSummary_HasNoCitationMarkers`.
- `History_SurvivesRestart` (a new connection to the same database sees the entries).
- `History_CloseEntry_DeletesOnlyThatEntry`.
- `History_ClearAll_RemovesEverything`.
- `History_RemoveBook_LeavesEntriesUntouched`.
- `History_OpenEntry_DoesNotRunSearchOrModel`.
- `History_DeleteAllData_RemovesHistory`.
- `Coordinator_FinalCoveredSearch_QueuesSummaryInBackground`.
- `Coordinator_ResultsStillUpgrading_WaitForFinalResults`.
- `Coordinator_NotCovered_NoSummaryAndStatusSaysSo`.
- `Coordinator_NewSearch_DoesNotStopRunningSummary_QueuesSecond`.
- `Coordinator_Queue_RunsOneAtATimeInSearchOrder`.
- `Coordinator_Stop_CancelsRunningSummary_AndRemovesWaitingOne`.
- `Coordinator_AutoOff_DoesNotQueueButSummarizeButtonDoes`.
- `Coordinator_ResultsNeverWaitForTheModel` (a slow summarizer does not delay the results).
- `Coordinator_Restart_CompletesUnfinishedSummaries` (the question is searched again, then summarized, in order).
- `AskVm_SummaryPanel_ShowsStatusIncludingWaiting` and `AskVm_SummaryPanel_CitationClickScrollsToResult`.
- `Settings_AutoSummaryOff_PersistsAndAppliesToNextSearch` and `Settings_ClearHistory_RequiresConfirmation`.

**Manual E2E**

1. Search with automatic summaries on → results appear at once; the summary panel on the right shows "Loading model…", then streams; the results stay usable meanwhile.
2. While it writes, open a result in the Reader and come back → the summary kept going (or finished).
3. Start a second search while the first summary is writing → the first summary finishes; the second search's panel says it is waiting, then writes; both end up in History.
4. Press Stop on a waiting summary → it leaves the queue and its entry says "stopped"; Write again brings it back.
5. Close the app while a summary is being written (or waiting), then reopen → the summary is completed without any click; History shows it filling in and then finished.
6. Search three questions, close and reopen → History lists all three, newest first, with date, time and summary.
7. Open an old entry → question, time and summary shown; no model is started; Search again records a new entry.
8. Remove a book that an entry was written from → History is unchanged.
9. Click the close button on one entry → only that entry goes.
10. Turn automatic summaries off in Settings → a search shows results and a Summarize button, and nothing runs in the background.
11. Settings → Clear all history, confirm → History is empty. Delete all data also empties it.

**Result.** Code written and unit-tested; **not yet built or run on Windows** (manual steps 1-11 are for the user). About 72 tests are new or rewritten (`HistoryTests`, `HistoryUiTests`, and the Ask, Summary and Library tests updated to the new design): history stored, closed and cleared, surviving a restart and untouched by removing a book; the queue (one at a time, in order, nothing cancelled by a new search, Stop on a running or waiting summary, write again, failures, shutdown leaving entries unfinished, restart completing them, a new search during the restart going behind the old ones, closing or clearing dropping waiting summaries); updates reaching the UI thread in order (a test with a stand-in UI thread); the summary panel, the History page and the settings. The XAML went through the XAML compiler on Linux with no errors or warnings; the C# code-behind (the summary panel layout, the History page handlers, the shell wiring) was not compiled.

Differences from the plan:
- **Status `none`:** when automatic summaries are off, the search is still recorded, with the status `none` ("no summary was written"); the Summarize button then writes one and the entry fills in. (The plan listed no status for this case.)
- **The setting is `AutoSummary`** (default on) in `settings.json`; it replaces ST-15's `SummarizeEnabled`, which hid the button. **The Summarize button is now always in the summary panel** (greyed out when there is nothing to summarize, or a summary is already waiting, being written or finished) and Stop takes its place while a summary is waiting or being written.
- **Layout:** the Ask page has two columns: results on the left, the summary panel on the right (420 px wide when open, 48 px when folded; the code-behind switches the column width). No stacking on narrow windows. (A first version used a `SplitView` in compact mode, and the folded strip appeared blank on Windows, probably because the strip was laid out at the open width and clipped; the plain column fixes that.)
- **Folding the panel (changed after first use: the "Summary" button in the search bar was confusing):** the control now belongs to the panel. An open panel has a chevron button at the top left of its header ("Hide summary"); a folded panel becomes a thin strip on the right edge with a chevron, a status indicator and a vertical "Summary" label, and clicking the strip opens it. The indicator is a spinner while the summary is waiting or being written, a check when it is ready and a warning if it failed, so a summary written in the background can be noticed without opening the panel; the strip's tooltip (and screen-reader name) says the same in words. The open or folded choice is **remembered**: a new search no longer reopens a folded panel, and the choice is saved in `settings.json` (`SummaryPanelOpen`, default open) so it survives restarts.
- **What the panel shows:** the summary of the search on screen. Searching again switches the panel to the new search while the earlier summary keeps being written; its result appears in History. The `[n]` links and hover highlight work for the search on screen only; the saved history text has no markers.
- **A search is recorded when its results are final.** If the meaning-based search was still starting, the keyword results are shown first and nothing is recorded until the full results arrive (or the meaning search turns out to be unavailable, in which case the keyword results are final). A search replaced by a newer one before that is not recorded.
- **Restart:** `ResumeUnfinishedAsync` runs when the shell opens. For each entry that was waiting or writing it starts the embedding server (if it can), searches the question again and queues the summary, oldest first; a question that is no longer covered becomes `not_covered`, a search that fails becomes `failed`. A new search made while this is happening waits until the old entries are queued, so the order holds.
- **Shutdown:** closing the app cancels the summary being written without changing its status, so it stays `writing` in the history and is completed next time; waiting entries stay `waiting`.
- **History page:** a clock icon in the navigation (between Ask and the Settings icon). Each entry shows the question, the date and time, a status line when there is no finished summary (waiting, writing, stopped, failed, not covered, none) and the summary text, with **Search again** and a close button (no confirmation to close one entry). Entries fill in live while summaries are written. **Search again** opens the Ask page and runs the question, which adds a new entry.
- **Settings page:** the switch is now "Write a summary automatically after each search", and there is a **Clear all history** button with a confirmation. Delete all data (ST-18) will remove the history with the database.
- Migration 3 adds `search_history` (`schema_version` 3). History is not touched by removing a book (decision 5).

**Risks and open points**

- **CPU and memory:** the embedding server, the chat server and the indexing worker can all run at once. On the 8 GB minimum tier this needs measuring (ST-22); automatic summaries may need to pause while indexing runs.
- **A long queue:** because no summary is ever cancelled, a burst of searches queues minutes of work at one to two minutes each. The panel and History show the waiting state and Stop removes one, but a cap or a "skip to the newest" option may be wanted after real use.
- **Finishing after a restart costs CPU at start-up** (the model loads and the queued summaries run, whether or not the user asked for anything that session). Decide after measuring whether to delay it until the app has been idle for a moment.
- **The restart re-run uses today's library,** so a completed summary can differ from what the first run would have said if books were added or removed in between.
- **Privacy:** history is user data in the local database and must stay out of logs (NFR-16). Because removing a book leaves its summaries behind (decision 5), "Clear all history" and "Delete all data" are the only ways to remove text derived from a removed book.
- **Size:** if this is too large for one story it splits cleanly into (a) the background summary queue and panel and (b) search history.

### ST-17 · Automatic book scoping

> **Removed by ST-25 (DCR-3).** The user found that scoping by a title typed in the question did not work well and chose explicit tags and a filter instead. The detector, the scope chip and `SearchOptions.AutoScope` are gone; the text below is kept as a record.

Size S · Covers FR-21, US-8 · Depends on: ST-14, ST-23 (the scope chip goes in the new Ask layout)

**Tasks**

- [x] Store `title_norm` and `author_norm` (case-folded, diacritics stripped) at ingest; backfill existing books.
- [x] Match quoted text first, then word n-grams of the question, with token-set similarity ≥ 0.85.
- [x] Apply the scope as a `book_id` filter on vector and keyword search.
- [x] Show a removable scope chip; removing it reruns the search unscoped.

**Unit tests**

- `Scope_ExactTitle_Matches` — "what does Alice in Wonderland say about time" → Alice.
- `Scope_AuthorName_MatchesAllTheirBooks`.
- `Scope_QuotedTitle_TakesPriority`.
- `Scope_CommonWord_DoesNotMatch` — "what is pride?" doesn't scope to *Pride and Prejudice* unless the full title is present.
- `Scope_Diacritics_Folded` — "Quijote" matches "Don Quijote".
- `Search_Scoped_ReturnsOnlyThoseBooks`.

**Manual E2E**

1. Ask "In Pride and Prejudice, what does Darcy think of Elizabeth?" → chip shows *Pride and Prejudice*; every result is from that book.
2. Remove the chip → results from all books.
3. Ask by author name ("what does Austen say about marriage") → scoped to her books.
4. Ask "what is pride?" → no chip; results from all books.

**Result.** Code written and unit-tested; **not yet built or run on Windows** (manual steps 1-4 are for the user). 24 new tests (the story's 6 and more): exact, partial and full titles, a common word never scoping, one-word titles needing a capital or quotes, accents folded (without ICU too), one typo in a long word, quoted text first, two titles named in full, Chinese/Japanese titles, authors (surname with a capital, full name in any case, several authors, "Last, First"), title before author, names stored at extraction and backfilled, the search staying inside the scoped books (vector, word and trigram search, one book or several), books still indexing never scoped to, the Classic options never scoping, and the chip and its removal. The XAML went through the XAML compiler on Linux with no errors or warnings; the C# code-behind was not compiled.

Differences from the plan:
- **The matching rule is stricter and simpler than "token-set similarity at least 0.85".** That measure scores "pride" against "Pride and Prejudice" as a perfect match, so it cannot tell a title from a common word, and it scores "Alice in Wonderland" against "Alice's Adventures in Wonderland" at only 0.75. The rule now is: ignore small words ("the", "of", "don", ...); a title matches when the question has **all** its significant words close together, or at least **two** of them making up **60%** or more of the title (so "Alice in Wonderland" finds "Alice's Adventures in Wonderland"); a **one-word title** (Emma, Dune, こころ excepted) needs a capital letter in the question or quotes; one lone word of a longer title never matches. A word may differ by one letter when it has six letters or more ("Quixote"). Chinese, Japanese and Thai titles match as whole phrases.
- **Authors:** the full name matches in any case; the **surname alone** (four letters or more) matches only when capitalised or quoted ("what does Austen say" scopes, "what do wells do" does not), and any word of a compound surname counts ("Cervantes" for "Cervantes Saavedra"). "Last, First" and several authors in one field are understood.
- **Order:** quoted text first; titles before authors; two different titles named in full ("compare Emma and Persuasion") scope to both; a tie between partial matches means no scope.
- Only fully indexed books are considered. The scope covers every book of a matching author; the vector search then runs once per book and the results are merged.
- **Where it runs:** inside `SearchService` (`SearchOptions.AutoScope`, on by default; `SearchOptions.Classic` and `--classic-search` turn it off), so `bookcli search` and `bookcli eval` scope too. Re-run an evaluation with `--classic-search` to compare. `SearchResult.Scope` says what was applied.
- **Stored names:** `title_norm` and `author_norm` are written when a book is extracted and filled in for older books each time the app starts. They hold the folded text (`NameFolding.Fold`): lower case, accents and punctuation removed. The accent table is built in, because .NET's normalization does nothing on a machine without ICU.
- **The chip** ("Searching only in: Pride and Prejudice" or "Searching only books by Jane Austen") sits above the results with a remove button. Removing it runs the same question again over every book; that counts as a new search, so it adds a new History entry and a new summary. A "Search again" from History re-detects the scope.
- **Known limits (a wrong scope hides the right answer, so these matter):** a capitalised one-word title or surname that is also an ordinary word or a character's name still scopes (for example "what does Emma think" scopes to the book *Emma* even if the question is about another book's Emma); the chip makes it visible and removable. Titles and authors that are very long or in unusual formats may not match. Lower-case surnames never match.


### ST-24 · Conversation threads in the Ask page (DCR-2)

Size L · Covers FR-39 to FR-43, FR-18 / FR-33 / FR-34 (amended), FR-28 (partly), US-10 (partly), US-15, US-16 · Depends on: ST-14, ST-16, ST-17, ST-23

**Change request (DCR-2).** Raised by the user after ST-17. The Ask page becomes a conversation: the user asks a question, reads the results and the summary, and asks a related question, with the whole **thread** on screen. **New thread** starts a fresh one; nothing is lost, because every question and summary is already saved in the history. In History the close button deletes a **thread** instead of one question. Only **3 results** are shown and summarized per question (was 5).

**What it replaces.** The Ask page of ST-14 and ST-23 (results on the left, the foldable summary panel on the right) is replaced by one conversation column; the folding strip and its `SummaryPanelOpen` setting go. ST-23's decisions on the History page change: an entry is now a thread, the close button deletes a thread, and "Search again" becomes "Continue this thread". Everything else from ST-23 stays: the one-at-a-time summary queue, unfinished summaries completed after a restart, the `AutoSummary` switch, Clear all history, and no references stored in the history.

**Decisions** (the user's answers of 2026-10-07; points where I read an answer a particular way are marked *Reading*)

*Without a decision*
- **The existing history is wiped.** The migration deletes every current history entry (they are not converted into one-question threads). It cannot be undone, so it is worth saying in the release notes.
- **Summaries still queue**, one per question, one at a time, exactly as in ST-23.
- **"Clear all history" and Delete all data are unchanged**; they remove every thread.

*Decisions*
1. **Layout A: one conversation column.** Each question is followed by a collapsible **Sources** list with that question's results and then its summary (the summary was first above the sources; moved below them after use). The newest question is at the bottom, the question box and **New thread** sit at the bottom, and the page scrolls to the newest answer. Older questions' sources are collapsed. The right-hand summary panel and its folding strip are removed. The summary's `[n]` links and hover highlight work within their own question.
2. **Light context for follow-ups (no language model; replaced by ST-31, which rewrites the follow-up with the model).** A follow-up that is short, or that starts with a pronoun or a connector ("and his sister?", "why?", "what about…"), is searched together with the previous question, and the screen shows a removable chip, for example "Also using your previous question". Removing the chip searches the follow-up alone. A long, self-contained question is searched on its own. Rewriting the question with the model (FR-28 as written) stays a later phase because it would make search wait for the model (FR-27).
3. **The summary prompt gets up to 3 past questions** of the thread (their text only, not their summaries), so it can read "his sister" correctly. **Results and summarized passages drop from 5 to 3**: `SearchOptions.TopN` becomes 3, `SummaryOptions.MaxPassages` becomes 3 (200 tokens each, as now). This more than pays for the extra prompt text, so summaries get faster.
4. **Scope keeps its current behaviour**, applied to the text that is searched: when a follow-up is searched together with earlier questions and one of those names a book or author, the scope is set automatically (and the scope chip can be removed as before). *Reading:* scope is looked for in the follow-up plus **up to 3 previous questions** when light context applies, and in the follow-up alone otherwise; there is no separate "sticky" scope.
5. **The button is "New thread"** (tooltip: this one stays in History).
6. **On start the latest thread is restored**, shown the way a reopened thread is (decision 8). Pressing New thread makes the next start blank until a question is asked.
7. **History lists threads.** Each shows its first question as the title, the number of questions and the time of the last one; opening it shows every question with its summary. The close button deletes the whole thread, with a confirmation only when it holds more than one question. "Search again" becomes **Continue this thread**, which opens it in the Ask page. A single question can no longer be deleted.
8. **A reopened thread shows each past question with its summary text only**, with no sources and no clickable citations, because results are not stored (ST-23 decision). Questions asked after reopening are live, with sources and links as normal.

*Readings and open points*
- **Retrieval context:** I assumed the search text is the follow-up plus the **last** previous question only (adding three questions' words would blur the search), while scope detection and the prompt use up to three. Say if retrieval should use all three.
- **Follow-up detection is a heuristic:** short lists of pronouns and connectors (English, Spanish, French, German). *(Changed by ST-31: a pronoun counts anywhere in the question and length is not considered.)*
- **Three results lower recall.** The M0 targets (recall@5) were written for five. `bookcli eval` should keep measuring with five results (an option) and also report recall@3; the reserved slot for other-language matches drops from 2 to 1 (of 3).
- **Restored summaries still being written** (an unfinished one completed after a restart) must update in the restored thread when they finish, not only in History.
- **No cap on thread length.** Long threads are cheap to show (older sources are collapsed); revisit if a thread feels slow.

**Tasks**

*Storage*
- [x] Migration 4: delete all `search_history` rows; create `search_thread` (id, created_at, updated_at); add `thread_id` to `search_history` (a question is a row in order of id) with `ON DELETE CASCADE`, so deleting a thread deletes its questions.
- [x] Repositories: create a thread, add a question to a thread, list threads (title, question count, last activity, newest first), list a thread's questions, delete a thread, clear all; remember the current thread id in the settings.

*Search and summary*
- [x] `SearchOptions.TopN` 3, `ReservedVectorSlots` 1; `SummaryOptions.MaxPassages` 3 *(changed afterwards: null, so the summary uses every result, whatever the "passages per search" setting is)*; `eval` measures with five and reports recall@3 and recall@5.
- [x] Follow-up detection and the contextual search text (follow-up plus the last previous question); scope detection over the follow-up plus up to 3 previous questions when context applies; `SearchResult` says whether context was used so the chip can show.
- [x] The summary prompt includes up to 3 past questions of the thread (a short "Earlier questions in this conversation" line in the user message, in the question's language rules as before).
- [x] The coordinator records each question in the current thread and keeps the queue and restart completion as they are.

*Ask page*
- [x] Conversation column: a turn card per question (question, status, summary with `[n]` links and streaming, collapsible Sources with the 3 results, chips for context and scope); auto-scroll to the newest turn; question box and **New thread** at the bottom.
- [x] Restore the latest thread on start (past turns as text only) and keep restored turns up to date while unfinished summaries complete.
- [x] Remove the right-hand summary panel, its strip and `SummaryPanelOpen`.

*History*
- [x] Thread list and thread detail; **Continue this thread**; close button deletes a thread (confirmation when it has more than one question).

**Unit tests**

- `Thread_NewQuestion_JoinsTheCurrentThread` and `Thread_NewThread_StartsAFreshOneAndKeepsTheOld`.
- `Thread_Restore_ShowsPastQuestionsAsTextOnly`.
- `Thread_Close_DeletesAllItsQuestions` and `Thread_ClearAllHistory_RemovesEveryThread`.
- `Migration4_WipesExistingHistory`.
- `FollowUp_ShortOrPronounLed_UsesPreviousQuestion` and `FollowUp_LongSelfContained_SearchesAlone`.
- `FollowUp_ContextChipRemoved_SearchesTheFollowUpAlone`.
- `Scope_FollowUpWithEarlierQuestionNamingABook_IsScoped`.
- `Prompt_IncludesUpToThreePastQuestions_AndNoSummaries`.
- `Search_ReturnsThreeResults` and `Summary_UsesThreeResults`.
- `Coordinator_QuestionsInAThread_QueueOneAtATime` and `Coordinator_RestartCompletion_UpdatesTheRestoredTurn`.
- `History_ListsThreadsWithTitleCountAndLastActivity` and `History_ContinueThread_OpensItInAsk`.
- `Eval_ReportsRecallAt3AndAt5`.

**Manual E2E**

1. Ask a question, then "and his sister?" → the second answer appears below the first with a chip showing the previous question was used; both stay on screen.
2. Remove the chip → the follow-up is searched alone.
3. Name a book in the first question, then ask a short follow-up → the follow-up is scoped to that book.
4. Press **New thread** → the page is empty; History shows the old thread.
5. Close the app mid-summary and reopen → the latest thread is back with its past answers as text, and the unfinished one fills in.
6. History → a thread with three questions shows its title, count and time; **Continue this thread** opens it; the close button asks for confirmation and deletes the whole thread.
7. Each question shows exactly three results and the summary cites `[1]` to `[3]`.

**Risks**
- **Recall:** three results lower recall; measure with `eval` before and after and compare with the ST-24-free baseline (`--classic-search` is not enough; keep the five-result run for comparison).
- **A wrong context** (a long unrelated follow-up treated as short) can pull in an old topic; the chip makes it visible, and removing it is one click.
- **The wipe is irreversible**; the user chose it.
- This is the third reshaping of the Ask page; ST-19 and ST-20 should wait until it is done.

**Result.** Code written and unit-tested; **not yet built or run on Windows** (manual steps 1-7 are for the user). About 28 tests are new or rewritten (`ThreadTests`, `HistoryUiTests`, and the Ask, Scope, Search, Summarize and Eval tests updated to the new design): the migration wiping the old history, threads listed with title, count and last activity, deleting a thread cascading to its questions, follow-up detection (English, Spanish, German, Chinese; long questions left alone), scope found through earlier questions, the prompt carrying only the last three past questions and no summaries, three results with one reserved slot, a question joining the current thread, New thread keeping the old one, restoring a thread as text only (nothing searched again), a restored unfinished summary filling in after the restart, the context chip removal replacing the question in place, the queue running a thread's questions one at a time, closing a thread (confirmation only above one question), Continue this thread, and recall@3 in the report. The XAML went through the XAML compiler on Linux with no errors or warnings; the C# code-behind (the conversation page, scrolling, the History handlers, the new dialog) was not compiled here, so CI is the first compile.

Where the build differs from, or settles, the plan:
- **Retrieval context** was the follow-up plus the last previous question; scope detection and the prompt used up to three (as assumed). *(Replaced by ST-31: only the rewritten question is searched and summarized.)*
- **The chips' remove buttons work on the newest question only** (they are greyed out on older ones). Removing one deletes that question from the history and asks it again in the same thread, so it is replaced, not duplicated. A question deleted this way leaves its (possibly empty) thread behind; empty threads are never listed.
- **A citation click opens that question's Sources** and highlights the passage but does **not scroll to it**: the sources sit in a nested list that the page cannot scroll into view reliably. Check this on Windows and say if it matters.
- **The question box is emptied** when the question is sent (it moves into the conversation).
- **`eval` still searches with five results** (two reserved) so the M0 numbers stay comparable, and reports recall@3 as well as recall@5. The `recall@8` row (which could never exceed five) was replaced by recall@3. The committed keyword-only baseline report is untouched.
- Settings: `SummaryPanelOpen` is gone, `CurrentThreadId` is new.

### ST-18 · Settings and Delete all data

Size S · Covers FR-30, FR-31, NFR-11 · Depends on: ST-12, ST-23, ST-24 (the automatic-summary switch and "Clear all history" already exist; this story completes the page)

**Tasks**

- [x] Settings page: model tier, automatic summary on/off and Clear all history (both added by ST-23), results count, chunk size and overlap, CPU cap, data folder (shown), downloads allowed on/off. *(Chunk changes apply to new books only, no re-index prompt; no update-check switch: decisions below.)*
- [ ] ~~Move data folder~~ *left out by decision; it needs a pointer file outside the data folder and can be its own story.*
- [x] Delete all data: confirm, stop processes, delete database (including the search history) and logs, optionally models; return to the wizard.

**Unit tests**

- `Settings_ChangeChunkSize_FlagsReindex`.
- `Settings_MoveDataFolder_MovesDbAndModels`.
- `Settings_DeleteAll_KeepModels_RemovesOnlyDbAndLogs`.
- `Settings_DownloadsOff_BlocksHttpClient`.

**Manual E2E**

1. Change passages per search to 5 → the next question in Ask shows 5 results.
2. Change chunk size to 800 and overlap to 100, then add a new book → its Reader/results show the longer chunks; books added earlier are unchanged.
3. Lower the CPU slider to 30 while a book is indexing → indexing slows (watch Task Manager); raise it back.
4. Turn "Allow model downloads" off, then Delete all data (keep models) → the wizard opens with models already installed. Repeat with "including models": the wizard's Download shows "turned off in Settings" and an **Allow downloads** button; pressing it lets the download run.
5. Change model size to the other one → the wizard opens to download it if it isn't installed; otherwise the app reloads with it.
6. Delete all data (keep models) → library empty, history gone, models folder still there, no download needed.

**Result.** Code written and unit-tested; **not yet built or run on Windows** (manual steps 1-6 are for the user). 12 new tests (`SettingsTests`, one in `WizardTests`): result count saved and clamped, overlap capped at a third of the chunk, chunk arguments reaching the worker's command line, CPU cap read on opening and written on change (never written back on load), model-size change saving the override and raising the event, downloads off blocking the download but not the import, Delete all data (keeps models / removes them, never touches other files, stops everything first then erases then returns to the wizard, cancel does nothing, a locked file shows a message and stays on Settings), and the wizard's Allow downloads button. The XAML went through the XAML compiler on Linux with only the known `GenXbf.dll` error; the C# code-behind (the dialog, the shell reload) was not compiled here, so CI is the first compile.

Where the build differs from, or settles, the plan:
- **No re-index prompt.** The user chose that a chunk-size change applies to books added afterwards only. The values go to the worker as `--chunk-tokens` / `--chunk-overlap` when it starts, so a worker already running keeps the old values until it next starts. To re-chunk a book, remove it and add it again. Ranges: 200-1000 tokens, overlap 0-200 and never above a third of the chunk.
- **No "Move data folder"** (user's decision): it would need a pointer file outside the data folder. The page shows the folder; `BOOKSEARCH_DATA` still overrides it.
- **No update-check switch** (user's decision): the app has no update check.
- **Passages per search** (1-10, default 3) is read on every search; the reserved vector slot stays 1. `eval` is unchanged.
- **CPU cap** is the existing `worker_control.cpu_cap_percent`, which the worker already reads before every batch; the slider (10-100) writes it through `LibraryService`.
- **Model size** offers the two tiers the wizard offers. Changing it reloads the shell with the new model, or opens the wizard (at its first step) when that tier's files aren't installed. A summary being written is stopped by the reload and completed later like after a restart.
- **Allow model downloads** is checked in `GatedModelProvisioner` around the wizard's download; importing from disk always works. If it is off and a model is missing, the wizard says so and has an **Allow downloads** button, so the user is never stuck.
- **Delete all data** removes `library.db` (with `-wal`/`-shm`), `settings.json` and `logs\`, and `models\` if chosen; nothing else in the data folder (for example `bin\llama-server.exe` or `config.json`). Because `settings.json` goes, the wizard runs again.
- **Layout (after use):** the page is four cards with headings: Search and summaries, Models, Indexing, Your data, plus (ST-36) a folded Advanced section holding language model memory, chunk size and overlap (data folder, database version, Clear all history and, last, Delete all data).
- **Earlier questions as context (added afterwards):** a Settings number, 0 to 3 (default 1), sets how many earlier questions of the conversation a follow-up uses. *(Changed by ST-31: the earlier questions now only go to the rewriter, and the search and the summary prompt get just the rewritten question.)* The search (extra search text and book scoping) and the summary prompt got exactly the same questions, and only when the question was detected as a follow-up (before, the prompt always got up to three). 0 turns follow-up handling off: every question is searched and summarized alone and no chip appears. The chip said "Also using your previous N questions" for N above 1 (now "Searched as: …"). After a restart an unfinished summary is rebuilt with the same rule. Which value works better is for the user to judge by use; `eval` cannot show it because its questions stand alone.
- `MainWindow.Shutdown` now clears its fields after disposing, so closing the window after a Delete all data or a reload never disposes anything twice.

### ST-19 · World readiness pass

Size S · Covers NFR-17 to NFR-21 · Depends on: ST-14, ST-15, ST-16, ST-23, ST-24

**Tasks**

- [x] Add the Japanese, Spanish and Arabic fixtures to the eval set with same-language and cross-language questions. *(The fixtures and a first set of questions already existed; six questions were added, so the set is 46.)*
- [x] Check FTS trigram search for Japanese and Thai keywords.
- [x] Check right-to-left display in results, the summary panel, History and Reader. *(Results and Reader already did it; the question, summary and History text now do too.)*
- [x] Confirm no UI string is hard-coded.

**Unit tests**

- `Eval_CrossLanguage_Recall5AtLeast70` *(integration)* — English questions find Spanish and Japanese passages.
- `Fts_ThaiWord_FoundByTrigram`.
- `Excerpt_ArabicText_PreservesCharacters` — no replacement characters after extract and store.

**Manual E2E**

1. Ask in Japanese about `kokoro.epub` → relevant Japanese passages; highlights correct.
2. Ask in English about a theme in `quijote.epub` → Spanish passages appear.
3. Search an Arabic keyword → Arabic results display right to left without broken characters.
4. Summarize in Japanese → summary in Japanese.

**Result.** Code written and unit-tested; **not yet built or run on Windows** (manual steps 1-4 are for the user; step 4 also needs the real chat model). 4 new tests (`WorldReadinessTests`: Thai word found by the trigram index through the real extract/chunk/store path, a Japanese keyword in `kokoro.epub`, Arabic text kept intact from file to excerpt, and the skipped-without-models `Eval_CrossLanguage_Recall5AtLeast70`), plus the wizard import test now checks the translated message. The XAML went through the XAML compiler on Linux with only the known `GenXbf.dll` error; the C# code-behind (`Rtl.cs`, `SummaryText.cs`) was not compiled here, so CI is the first compile.

What was found and changed:
- **Two-character Japanese words cannot be found by keyword.** The trigram index needs three characters (`QueryTerms` already says so), and most Japanese and Chinese words are two (鎌倉, 先生). They are found only by the meaning search, which is why the first draft of the Japanese test failed. This was **not changed**; a fallback for short CJK queries (a `LIKE` scan or a bigram index) is possible, at the cost of a slower query or a bigger index. Say if you want it as a story.
- **Right to left:** results (highlighted excerpts) and the Reader already followed the text's direction. Now the question, the summary (streaming and finished), the restored summary and the History titles, questions and summaries do as well, through `Rtl.For(text)` (the same `TextDirection` rule), so an Arabic question inside an English-language window reads correctly. Citation links in a right-to-left summary were not checked.
- **Hard-coded text audit:** no literal text in any XAML file or in the pages' code-behind. Four English sentences still reached the screen from code and were moved to `Resources.resw`: the two wizard import rejections (`ModelRejectedException` now carries a reason) and the summary "did not write a summary" failure (now the translated "could not be written"). **Still English by design:** exception text from Windows or .NET shown after a translated lead-in ("A file could not be written: {0}"), the summary failure detail when the model server itself throws, and the start-up error page. Only `en-US` resources exist; a second language has never been loaded, so NFR-17 is checked only as far as "no literals".
- **Cross-language eval:** `Eval_CrossLanguage_Recall5AtLeast70` indexes only the three small books (Spanish, Japanese, Arabic), runs all cross-language questions about them (7) and needs recall@5 of 70% or more. It runs on the user's PC with `BOOKSEARCH_M0_CONFIG`; not run here.

### ST-20 · Accessibility pass

Size S · Covers NFR-22 · Depends on: ST-13 to ST-18, ST-23, ST-24

**Tasks**

- [x] Automation names on every control; logical tab order. *(Names: yes. Tab order is the document order, left unchanged; see the Result note.)*
- [x] Keyboard shortcuts: Ctrl+F focuses search, Enter searches, Esc stops Summarize.
- [x] Theme resources for all colors and text; check light, dark and high-contrast. *(No fixed colours in XAML; the light, dark and high-contrast look is for the user to check.)*

**Unit tests**

- `Xaml_InteractiveControls_HaveAutomationName` — scan XAML for buttons and inputs without names.
- `Keyboard_EscWhileSummarizing_Stops`.

**Manual E2E**

1. Use the whole app with keyboard only: add a book, search, open a result, read the summary and its citations, open History, close an entry → all reachable.
2. Turn on Narrator → each control is announced with a meaningful name.
3. Set Windows text size to 150% → nothing clipped.
4. Switch dark and high-contrast themes → all text readable.

**Result.** Code written and unit-tested; **not yet built or run on Windows** (manual steps 1-4 are for the user). 6 new tests (`AccessibilityTests`): every interactive control in every XAML file has a name (an `AutomationProperties.Name`, a `Content`/`Header`, or a resource for its `x:Uid`), every `x:Uid` has resources, no fixed colours in XAML, the result and History rows read as one sentence, and Esc stopping a summary (and doing nothing when none runs). The XAML went through the XAML compiler on Linux with only the known `GenXbf.dll` error; the C# code-behind (the two key accelerators) was not compiled here, so CI is the first compile.

**Fix after the first run (bug).** Resting the pointer over text in Ask showed "Esc" and "Ctrl+F" tooltips: the accelerators are declared on the page, so the whole page was their owner. Both pages now set `KeyboardAcceleratorPlacementMode="Hidden"`; the shortcuts work as before. `AccessibilityTests.Xaml_PageWideAccelerators_ShowNoTooltip` checks every page with accelerators sets it. The cause is from the XAML and WinUI behaviour, not reproduced here; the user confirms on Windows that the tooltips are gone.

- **Names added:** the question box, the three lists (library, chapter text, model size), each result button (citation number, title, author and place) and each History expander (first question, count and time). Everything else already had a name through its `x:Uid`.
- **Ctrl+F** (anywhere in the shell) opens Ask and puts the cursor in the question box. **Enter** searched already. **Esc** on the Ask page stops the newest summary that is being written or waiting; pressing it again stops the next. This is `AskViewModel.StopSummaryCommand`; the page binds the key to it.
- **Tab order** is the order of the controls in the XAML. On the Ask page the question box comes last, after every Sources button, which is why Ctrl+F exists; I did not reorder it.
- **Text size:** no fixed heights or widths in XAML apart from a 3 px accent line over a cited result, so 150% text should not clip; to be tried.
- **Cited results** are marked by the accent line and also by the `[n]` label, so colour is not the only signal.
- **Not done:** the Reader's chapter text is one selectable block per paragraph; a screen reader reads the paragraphs in order but there is no "jump to chapter" shortcut.

### ST-21 · Installer and distribution

Size S · Covers FR-1, NFR-6, NFR-15 · Depends on: ST-12

**Tasks**

- [x] `dotnet publish` self-contained win-x64 with Windows App SDK self-contained. *(Already `scripts/publish-app.ps1`; the release script calls it.)*
- [x] Copy the pinned llama.cpp CPU build into `bin`. *(Changed by the user: the wizard downloads it on first run instead; see the Result note.)*
- [x] Inno Setup script: per-user install, no admin, Start menu shortcut, uninstaller that offers to keep data. *(`scripts/installer/BookSearch.iss`.)*
- [x] Release script writes the installer's SHA-256 next to it. *(`scripts/build-release.ps1`.)*
- [x] Check installer size ≤ 200 MB. *(The script fails above it; the number is not known until the first build.)*

**Unit tests**

- `Release_ChecksumFile_MatchesInstaller` — script output verified in CI.
- `Bin_ContainsPinnedLlamaServerVersion`.

**Manual E2E**

1. On a clean Windows 11 VM with a standard (non-admin) user, run the installer → installs without a UAC prompt.
2. Run `certutil -hashfile` on the installer → matches the published checksum.
3. Windows SmartScreen warns (unsigned) → "More info, Run anyway" works.
4. Uninstall → choose to keep data; reinstall → library still there.
5. Repeat on Windows 10 22H2.

**Result.** Scripts, installer definition, workflow, wizard download and tests written; **nothing has been run on Windows**: the installer can only be built there with Inno Setup (not available here), and the wizard has not been compiled or tried. Tests: 15 new or changed (`RuntimeInstallerTests` 8, `ReleaseTests` 5, manifest and import tests, and the wizard tests updated for the third file). They cover: the Inno Setup file (per-user, no way to ask for admin, Windows 10 22H2 and newer, a Start menu shortcut, an uninstall prompt that defaults to keeping data and always keeps it when silent, the same data folder as the app), the build script (checksum line `<hash>  <file name>`, the 200 MB limit, no llama.cpp inside the installer), the workflow comparing the checksum file with the installer, the manifest's runtime entry (official llama.cpp release URL, id and file name agree, plausible size), unpacking the zip into `bin`, a newer pin replacing an older download, a hand-installed or app-folder `llama-server.exe` counting as installed and never replaced, a zip without the server failing and leaving a working install alone, download then unpack, import of the zip from disk, and a wrong zip rejected with nothing unpacked.

Where the build differs from, or settles, the plan:
- **llama.cpp is downloaded by the app, not bundled** (the user's decision, after I proposed bundling). It is one more manifest entry (`role: runtime`, **b11430**, the build the user has been running: `0.6.0-dev (build 11430, commit 8345f3339)`; 19.4 MB zip; SHA-256 computed here and equal to the digest GitHub lists). The wizard shows it as the first row ("Program that runs the models"), downloads it with the same resumable, hash-checked downloader (so "Allow model downloads" gates it, and "Import from disk" accepts the zip when its hash is in the manifest), and `RuntimeInstaller` unpacks it into `bin` in the data folder and deletes the zip. The unpack goes to `bin.new` first, so a bad zip never damages a working install. The installer is therefore about 20 MB smaller and the app needs the network (or an import) once more on first run.
- **Two requirements were reworded** to allow this: NFR-13 (models are still only weight formats; the one program the app downloads is a pinned official zip, hash-checked before unpacking) and NFR-9 (the user-initiated download now covers the models and llama.cpp). The privacy note in the wizard says so.
- **Start-up check:** the wizard also runs when `llama-server.exe` is missing. A downloaded copy carries a marker with the zip's hash; if a later app version pins another build, the marker no longer matches and the wizard offers the new one. A copy put in `bin` by hand has no marker and is left alone, as is one in `bin` next to the app.
- **Moving the pin** is `pin-llama-server.ps1 -Build bNNNN`, which prints the manifest entry to paste; the shape test then checks it. A new build should be tried with the real models before it ships.
- **CPU only.** The design said CPU and Vulkan builds; v1 uses the CPU build only. The zip also holds about 50 other files (such as `llama-cli.exe` and `llama-bench.exe`); they are unpacked too (49.5 MB on disk).
- **The uninstaller** asks about `%LOCALAPPDATA%\BookSearch` only (which now also holds `bin`); a data folder chosen with `BOOKSEARCH_DATA` is never touched.
- **Not done:** no icon, no code signing (by design: NFR-15), no auto-update; the clean-VM and Windows 10 22H2 steps (manual 1-5, plus a first run that downloads the program) are for the user. The first real build will show the installer size against the limit.

### ST-22 · Performance, privacy and release validation

Size M · Covers NFR-1 to NFR-10, M2 exit · Depends on: ST-13 to ST-21, ST-23, ST-24

**Tasks**

- [x] Build a 200-book test library (public domain) and run `bookcli bench` against the installed app's data. *(Tools written: `bookcli corpus` and `bench --use-library`. The run is the user's.)*
- [ ] Measure idle RAM, installer size, search p95, indexing speed, Summarize timing on minimum spec, and memory and responsiveness with indexing, search and a background summary all running (8 GB tier). *(`scripts/measure-app.ps1` and the procedure in `docs/Release Validation.md` are ready; **the measuring is for the user**.)*
- [ ] Privacy run: networking off after setup; packet capture during a normal session. *(`scripts/check-network.ps1` and the procedure are ready; the run is the user's.)*
- [ ] Usability test with 3 people completing the flows unaided. *(Script and sheet in `docs/Release Validation.md`.)*
- [ ] Fix or log every gap; update the requirements doc with measured numbers. *(The "Gaps" table is empty until the measurements exist.)*

**Unit tests**

- `Bench_200Books_SearchP95Under10s` *(integration, nightly)*.
- `Bench_IdleMemory_Under300MB` *(integration, nightly)*. *(Not written as a test: it needs the installed app running, so `scripts/measure-app.ps1` does it by hand.)*

**Manual E2E**

1. Fresh install to first search results in under 15 minutes, excluding download time.
2. Disable networking; add a book, search, summarize → everything works.
3. Wireshark capture during 10 minutes of normal use → no outbound traffic.
4. Force power-off during indexing; restart → indexing resumes, no duplicates.
5. Three users each add books, ask a question, open a passage and read the summary without help.
6. On an 8 GB PC, index a book while searching with automatic summaries on → the app stays responsive and nothing runs out of memory.

**Result (tools only).** Written and unit-tested here; **none of the measurements has been made**: they need the user's PC, the real models, an installed app and, for the usability test, three people. 9 new tests (`BenchTests`): the catalogue listing (public domain only, one language, EPUB present, pages followed, duplicates counted once, stops at the count), the download (skips files already there, no half-written file after an error, a `corpus.json` list), readable unique file names, the benchmark report for an existing library (indexing speed "not measured", p50 and p95), and the skipped-without-models integration test `Bench_200Books_SearchP95Under10s` (set `BOOKSEARCH_BENCH_DATA` and `BOOKSEARCH_M0_CONFIG`).

What was built:
- **`bookcli corpus`** lists the most downloaded non-copyrighted English EPUBs through gutendex.com and downloads them from Project Gutenberg one every 2 seconds, resuming an interrupted run. It uses the text-only EPUB (about 0.5 MB; the catalogue's default link is the one with every picture, 25 MB for Pride and Prejudice), checked here with three real downloads. Project Gutenberg asks bulk users to be gentle; 200 books once is fine, and the folder is git-ignored (`bench-library/`).
- **`bookcli bench --use-library`** times the searches on an existing library (for example the app's data folder, with the app closed) and reports the library size, p50, p95 and the target; the indexing line says "not measured".
- **`scripts/measure-app.ps1`** samples the working set and CPU of `BookSearch`, `BookSearch.Worker` and `llama-server` and writes a CSV; **`scripts/check-network.ps1`** lists any TCP connection or UDP endpoint of those processes to or on an address other than this PC (not a packet capture: the Wireshark run is still needed).
- **`docs/Release Validation.md`** is the procedure for each measurement, a usability test script with a sheet, a results table with the targets and an empty gaps table.

Where this differs from the plan:
- The idle-memory test is a script, not an xUnit test (see above).
- The memory target is read as the app with the models **unloaded**: the model servers stop after 10 idle minutes (`ServerSupervisor`), and the loaded models add roughly 1 GB (embedding) and 3 GB (the 4B model), which is why the 8 GB tier uses the 2B model. The guide has you measure after 12 idle minutes to confirm they are gone.
- Running the 200-book index takes hours on the minimum machine (NFR-4 allows 3 minutes a book, so up to 10 hours); start it in the evening.


### ST-25 · Tags and the search filter (DCR-3)

Size M · Covers FR-21 (replaced), FR-40 (amended), US-8 · Depends on: ST-13, ST-14, ST-24

**Change request (DCR-3).** Raised by the user after using ST-17: naming a book in the question did not narrow the search reliably. The user chose these (in answer to three questions):

- **Free-form tags**, any number per book (not folders).
- **A Filter button next to the question box** (not `#tag` typing).
- **Remove the implicit scoping** (not keep it as a fallback).

**What it does.** Each book can carry tags: on the Library page, **Tags…** on a book opens a box for a comma-separated list, and ticking several books enables **Add tags to selected…**. On the Ask page, **Filter…** lists the tags (with how many books each has) and the indexed books (with a find box); applying it limits every search to the books with any chosen tag plus the books chosen by name. The filter stays until **Clear** is pressed, also across restarts. Each question shows what it was searched in, and a filter that matches no indexed book says so instead of searching everything.

**Tasks**

- [x] Migration 5: `book_tag` (tag, lower-cased key, cascade on removing a book) and `search_history.filter`.
- [x] `Tags` rules (trimmed, one space between words, no leading `#`, 40 characters, case-insensitive; one spelling per tag), `TagRepository` (list with counts, set, add to many, resolve a filter to indexed books).
- [x] `SearchFilter` (tags plus books with titles, JSON), `SearchService.SearchAsync(bookIds)`, `FilterMatchedNoBooks`, `IAskService.GetFilterOptionsAsync` and a `filter` argument.
- [x] Remove implicit scoping: `BookScopeDetector`, `BookScope`, `AutoScope`, the scope chip, `SearchContext.ScopeText`, the stored folded names and their start-up backfill.
- [x] Library: tags on rows, **Tags…**, multi-select and **Add tags to selected…**.
- [x] Ask: Filter button, summary line, Clear, per-question chip, filter kept in `settings.json`, stale entries dropped when the page is shown; the filter is kept with each history entry and used again when a summary is finished after a restart.
- [x] Dialogs for the filter and for editing tags; strings; accessibility names.

**Unit tests (`FilterTests` and additions to `LibraryTests`)**

Tag text rules; set, replace and case-insensitive duplicates; one spelling per tag; add to several books; tags removed with the book; a filter resolved to indexed books only (tag, chosen book, both, none, an unknown book); filter JSON; search inside book ids by keywords and by meaning; a question that names a book no longer narrows the search; an empty id list finds nothing and says so; the real `AskService` resolving tags and books; the Ask page choosing, remembering, clearing and pruning a filter, showing it on the question, and reporting a filter with no books; a summary resumed after a restart using its filter; the migration; the Library page editing tags, cancelling, and tagging selected books.

**Manual E2E**

1. Library: open **Tags…** on a book, type `novels, Austen`, save → the book shows `Tags: Austen, novels`.
2. Tick three books → **Add tags to selected…** → type `to read` → all three show it.
3. Ask: **Filter…** → tick the tag `to read` → Apply → the line under the box says it is searching only in `to read`; ask a question → every result comes from those three books, and the question shows `Searched only in: to read`.
4. Tick one book in the dialog as well → results can also come from that book.
5. **Clear** → the next question searches every book. Close and reopen the app with a filter set → it is still set.
6. Remove a book that was in the filter, then open the Ask page → the book is dropped from the filter.
7. Set a filter to a tag whose only book is still indexing → the question says no indexed book matches.
8. Type a book's title in a question with no filter → the search is not narrowed (implicit scoping is gone).

**Result.** Code written and unit-tested; **not yet built or run on Windows** (manual steps 1-8 are for the user). 24 tests are new or changed. The XAML went through the XAML compiler on Linux with only the known `GenXbf.dll` error; the C# code-behind (the Library selection handler, the dialogs, the Ask page binding) was not compiled here, so CI is the first compile.

Where the build differs from, or settles, the plan:
- **ST-17 is gone.** The removal also deleted `Scope_*`/`Search_Scoped_*` tests and the folded names (`title_norm`, `author_norm`); the two columns stay in the table, unused, because dropping them would need a table rebuild.
- **The filter is global, not per thread:** it stays when you press New thread, so it is easy to forget it is on; the line under the box always says what the search is limited to. Each question records the filter it used, so History and a restart show the truth.
- **A filter entry that no longer exists is dropped silently** when the Ask page is shown: a tag nobody carries, a removed book, or a book being re-indexed (only fully indexed books can be searched).
- **Tags are set on the Library page only.** Tagging is by hand, with a text box and a list of tags in use; there is no rename-a-tag or delete-a-tag command (clear it from each book), and no `bookcli` tag command.
- **Book selection:** the Library list now has tick boxes (it was not selectable). **Add tags to selected…** sits after **Add books** in the header, and the ticks are cleared once the tags are added (and when the page is opened again).
- **A follow-up no longer carries a scope:** `SearchContext` lost `ScopeText`; the filter is the only way to narrow a search.

### ST-26 · Summaries from contexts, one paragraph each (DCR-4)

Size M · Covers FR-22 to FR-24 (amended), FR-33, NFR-14 · Depends on: ST-15, ST-23, ST-24

**Change request (DCR-4).** The user found the summaries confusing because the passages were disconnected: each was a 200-token cut of a chunk with the sentences before and after it removed, and the model tried to join passages from different places into one story. (The "same book, different areas are separate contexts" part was reversed by ST-30.) The user chose three of the ideas offered, and added: *the answer is in paragraphs, each from a unique context; a unique context is a different book, or the same book where two results come from completely different areas.*

**What it does.**
1. **More text around each result:** the result's own chunk is kept up to 350 tokens (was 200), and up to 100 tokens of whole sentences from just before and after it are added from the neighbouring chunks of the same section.
2. **Contexts in reading order:** results of one section that lie within 2 chunks of each other are merged into one continuous context (the chunks between are filled in, the chunker's overlap is removed). Everything else is a context of its own. Contexts are ordered by their best result.
3. **One paragraph per context:** the prompt tells the model the contexts are separate and unrelated, to write a short paragraph (about 50 words) for each context that helps, naming its source first, never linking contexts, ending each with the citation numbers of that context, and not guessing pronouns. The paragraphs are shown as paragraphs (and kept in the History).
4. **A Settings choice** for the amount of surrounding text: small (the earlier 200 + 0), medium (350 + 100, the default), large (500 + 200).

**Tasks**

- [x] `ChunkRepository.GetRun`, `ChunkIndex` and `SectionId` on search hits.
- [x] `ContextBuilder`, `IChunkSource` (the Ask service reads it under its lock; the console tool reads its connection), `SummaryContext`, `SummaryOptions` (350 / 100 / merge distance 2, `ForLevel`).
- [x] New system prompt and a prompt that lists contexts with their citation numbers; reply cap 300 tokens.
- [x] `CitationValidator` keeps paragraph breaks (`StartsParagraph`); the Ask page draws one paragraph per context; the History text keeps blank lines.
- [x] Settings: "Text around each result in a summary"; strings; tests.

**Unit tests (`ContextTests` and updates to the summary tests)**

Nearby results merged in reading order with the gap filled and the overlap removed; far-apart results, other books and other chapters separate; contexts ordered by best result; page ranges in the label; text from before and after within the budget and marked; none at the start of a section, none with 0 budget or no source; a cut core gets no neighbour on the cut sides; the level budgets; the prompt citing a merged context by all its results; paragraphs kept by the validator (one paragraph stays one, a dropped first sentence still starts the next); the saved text; the paragraph flag reaching the Ask page; the setting; the range query.

**Manual E2E**

1. Ask a question whose answer is in one scene of a book → the summary is one paragraph naming that book, citing the results it came from; the Sources list still has the same results.
2. Ask a question that finds passages in two books → two paragraphs, one per book, each ending with its own citation numbers, and neither mentions the other.
3. Same for one book with results from far-apart chapters → separate paragraphs.
4. Compare small, medium and large on the same questions in Settings: is a paragraph clearer with more text, and how much longer does the summary take (the first-word time on the minimum machine against NFR-3)?
5. Ask a question the passages cannot answer → "not covered" as before.
6. Open the History → the summary keeps its paragraphs.

**Result.** Code written and unit-tested; **not yet built or run on Windows, and never tried with the real model**: whether the paragraphs read better and whether the 2B and 4B models obey "one paragraph per context, no linking" is for the user to judge. 20 tests are new or changed. The XAML went through the XAML compiler on Linux with only the known `GenXbf.dll` error; the C# code-behind (`SummaryText`, the wiring in `MainWindow`) was not compiled here, so CI is the first compile.

Where the build differs from, or settles, the plan:
- **Citation numbers are unchanged:** still the numbers of the results on screen, so the links and highlights keep working; a merged context is cited by all its results (for example `[1][3]`).
- **Speed:** the medium default sends about 550 tokens for each context where the old prompt sent 200, so a summary of three separate results reads about twice as much text. Small restores the old cost. The 100-second CPU summary seen in the first real run will not improve; it may get slower. Measure it (ST-22).
- **The grounding sheet** (`bookcli grounding`) now shows the whole context text for each citation, since that is what the model read.
- **Not done (the user may want them):** the "show more context" button in the Sources list, a reranker, and an automatic note when the results are too unrelated to summarize. A quality score column in the grounding sheet was offered and not built.
- **The reply cap** is 300 tokens (was 250) so that three paragraphs fit.

### ST-28 · Edit the newest question and search again

Size S · Covers FR-18 (amended) · Depends on: ST-24

**Request.** The user asked to be able to edit the last question and run the search again.

**What it does.** The newest question in the conversation has a pencil button. Pressing it puts the question back in the question box (text selected, ready to type), shows a note "Editing your last question. Searching replaces it." with a **Cancel** button, and leaves the question on screen until the search. Searching then **replaces** the old question: the old question, its results and its summary are removed from the conversation and from the History (a summary still being written is stopped), and the new one is added in the same place. If the text is unchanged it is a plain "search again", which is useful after changing the filter or a setting. The new search uses the current filter and, for a follow-up, the earlier questions of the conversation (not the one being replaced). Only the newest question can be edited, because later questions would no longer follow from an edited one; a question restored from an earlier run can be edited too. An emptied box does nothing and keeps the old question; New thread ends the edit.

**Tasks**

- [x] `TurnViewModel.EditCommand` (newest only), `AskViewModel` editing state, **Cancel**, replace-in-place (the same mechanism as removing the context chip), focus and select the question box.
- [x] Ask page: pencil button, note and Cancel; strings; accessibility names.

**Unit tests (`EditQuestionTests`, 10)**

Edit puts the question in the box and the search replaces it (the History keeps one entry, not two); only the newest can be edited; searching an unchanged question runs the search again with a new answer; a changed filter applies; Cancel changes nothing; an emptied box deletes nothing; editing stops a summary that is being written; New thread ends the edit; a follow-up uses the earlier questions but not the replaced one; a restored question can be edited.

**Manual E2E**

1. Ask a question, press the pencil on it → the box holds the question, selected, with the note above it.
2. Change a word and press Enter → the old question and answer are replaced by the new ones; open the History → one entry for that question.
3. Press the pencil, then Cancel → nothing changes.
4. Ask two questions → only the second has a pencil.
5. Set a filter, press the pencil and Search without changing the text → the same question is answered inside the filter.
6. Press the pencil while a summary is being written → that summary stops and the new search starts.

**Result.** Code written and unit-tested; **not yet built or run on Windows**. The XAML went through the XAML compiler on Linux with only the known `GenXbf.dll` error; the C# code-behind (focus and select, the event wiring) was not compiled here, so CI is the first compile. Not done: editing an older question (it would have to drop the questions after it), and an Esc key to cancel the edit (Esc already stops a summary).

### ST-29 · The name Docent and the app icon

Size S · Covers NFR-15 (installer) · Depends on: ST-12, ST-21

**Request.** The user wanted a name that means "search my memory palace" and a logo, and chose **Docent** (a guide who knows a museum or palace and answers visitors' questions) and a drawing of a library hall with bookshelves along the walls and a magnifying glass looking in (smaller than the doorway).

**What changed**
- **The logo:** two drawings in `docs/logo/`: `docent.svg` (full detail) and `docent-small.svg` (fewer, bolder shelves for small sizes), and `docent-256.png` for the README.
- **The icon file:** `scripts/make-icon.py` (needs `pip install cairosvg`) builds `src/BookSearch.App/Assets/Docent.ico` with 16, 20, 24, 32, 40 (from the small drawing) and 48, 64, 96, 128, 256 (from the detailed one). The program (`ApplicationIcon`), the window (`AppWindow.SetIcon`, file copied next to the program) and the installer (`SetupIconFile`) use it.
- **The name:** window title and the privacy text in the wizard (`Resources.resw`), the installer (application name, install folder `Docent`, Start menu entry, file name `Docent-Setup-<version>.exe`, "Start Docent" at the end), the release script and workflow, the README (with the logo), and the titles of the three documents.
- **A new installer id:** an install made under the old name stays a separate entry in Apps.
- **Task Manager** shows the program's description, now "Docent".

**Kept on purpose**
- **The program file is still `BookSearch.exe`** (and `BookSearch.Worker.exe`), and the projects and namespaces keep their names. Renaming the executable touches resource loading in the WinUI app, the worker path, the scripts and the installer, which cannot be tried here; do it after testing on Windows if it matters.
- **The data folder is still `%LOCALAPPDATA%\BookSearch`** (and `BOOKSEARCH_DATA`), so existing libraries, settings and models are found. Renaming it needs a step that moves the library.

**Unit tests (`BrandingTests`, 5)**: the icon file is a valid ICO with all ten sizes, each a PNG; the program, window and installer use it; the user-visible text says Docent; the data folder name is unchanged; both drawings are in the repository.

**Manual E2E**
1. Uninstall the old "Local Book Search" from Apps (answer **No** to deleting the data), build `scripts\build-release.ps1`, run `Docent-Setup-<version>.exe` → the installer shows the icon; the Start menu has "Docent" with the icon; the install folder is `...\Programs\Docent`.
2. Start Docent → the title bar and taskbar button show the icon and the title says "Docent"; Task Manager shows "Docent".
3. Your library, History and settings are still there (same data folder).
4. Look at the icon at 16, 32 and 48 pixels (taskbar, Alt+Tab, Explorer in large icons) and tell me if the small drawing needs work.

**Result.** Code and files done; **not built or run on Windows**. CI compiles the app with the icon, which proves only that the file is accepted. A quick web search for "Docent" as an app name found no clear conflict, but it was not conclusive: check GitHub, winget, an app store and a trademark search yourself before you publish.

### ST-30 · One context per book, a longer paragraph (DCR-5)

Size S · Covers FR-9 (amended) · Depends on: ST-26

**Change request (DCR-5).** The user found the result quality still not good: *if the results are from the same book, treat it as a single source, and allow the LLM to give a longer summary than a single sentence.* This reverses the part of ST-26 (DCR-4) that made "the same book where two results come from completely different areas" a separate context.

**What changed**
1. **One context per book.** `ContextBuilder` still joins nearby results of one section into one excerpt (as in ST-26); the other excerpts of the same book now follow it in reading order in the same context, each headed with its place in parentheses, e.g. `(Chapter 3, p. 12)`, and separated by a blank line. The label of such a context is the book's title alone. Nothing is filled in between excerpts.
2. **One paragraph per book, about 120 words** (was 50). The prompt now says a context may hold several excerpts of one book, that the model may combine them, and that it must not assume they follow each other; different books are still never linked.
3. **Reply cap 450 tokens** (was 300).
4. **Citations are unchanged:** the paragraph ends with the numbers of all results in the book, e.g. `[1][3]`.

**Fix after the first run (bug).** The user saw the markers of both paragraphs together at the end of the whole summary: the model wrote them once at the end instead of after each paragraph. `CitationPlacer` now puts each book's numbers at the end of the paragraph that names its title (longer titles first, so "Cooking Basics" is not also "Cooking"), replaces the model's markers in that paragraph, and drops marker-only lines. A paragraph that names no title is left as written. Not run with the real model: the cause (the model, not the page) is inferred from the code and the pasted example, since the raw reply was not available. 5 tests in `ContextTests`.

**Not changed:** the search, the budgets per result (`TokensPerPassage`, `NeighborTokens`), the Settings choice, and the number of results. The prompt is no larger than before for the same results; the reply can be up to 150 tokens longer, which on CPU at 6-7 tokens/s is up to about 20 s more per book.

**Tasks**
- [x] `ContextBuilder`: group excerpts by book, header each with `SummaryPrompt.Place`; `SummaryPrompt.Title` and `Place` split out of `Label`.
- [x] Prompt rules 3 and 4; `MaxTokens` 450.
- [x] Docs: Technical Design (Summarize), CLAUDE.md, README table, ST-26 notes.

**Unit tests (changed in `ContextTests`, `SummaryTests`, `SummarizeTests`)**: the same book far apart is one context cited by both results, in reading order, each excerpt headed with its place, nothing between them; other chapters of the same book are one context, other books are separate; contexts are ordered by their best result; the prompt rules and the 450-token cap.

**Manual E2E**
1. Ask a question that has results from two parts of one book → one paragraph naming the book, citing all those results; the Sources list is unchanged.
2. Ask one whose results are from two books → two paragraphs, one per book, not linked.
3. Compare the length and the time to finish with ST-26: report how much slower it is on your CPU.

**Result.** Code written and unit-tested; **not built or run on Windows, and not tried with the real model**: whether one longer paragraph reads better, and whether the 2B and 4B models stay near 120 words, are for the user to judge. Other ideas offered with this change and not done: a reranker, dropping Contents and front-matter chunks, spreading results across books, naming the answer language in the prompt.

### ST-31 · Follow-ups rewritten to stand on their own (DCR-6)

Size M · Covers FR-40 (amended), FR-28 (delivered), FR-27 (amended) · Depends on: ST-24

**Change request (DCR-6).** The user found that results and summaries for a follow-up carried content from earlier questions, which was confusing. Their rule: *the conversation may be used only to resolve context: after "What does despair do to a person?" the question "What's its shadow strength?" should be searched as "What is despair's shadow strength?", not by sending both questions.*

**What changed**
1. **Rewrite instead of concatenation.** `FollowUp.Detect` is still the cheap gate (starts with a pronoun or connector, or has a pronoun anywhere; the word-count and character-count length rules were removed after the first run, see 7). For a follow-up, `QuestionRewriter` gives the chat model the last N earlier questions (Settings `ContextQuestions`, 0 to 3, default 1) and the new question and gets back one standalone question (temperature 0, at most 80 tokens, same language).
2. **Only that question is used.** It is what is searched and what the summary is written for. The earlier questions are no longer added to the search text, and the summary prompt no longer has an "Earlier questions" line (`SearchContext`, `SearchResult.UsedContext` and the `previousQuestions` parameters are gone).
3. **The chip** says "Searched as: <rewritten question>"; removing it searches the question as typed (no second model call).
4. **Failures fall back to the typed question.** If the model cannot be used, or does not answer within 15 s (60 s when the call had to start the chat server, changed with ST-38: `ChatSideCall.LimitFor`; counted from the moment no summary is being written or waiting), the question is searched as typed and the turn shows a warning ("could not make this follow-up stand on its own"); if the meaning-based search is also unavailable both warnings are shown. An unusable or unchanged reply is treated as "stands on its own", with no warning.
4a. **Chains and restarts.** The rewriter is given each earlier question as it was searched (the rewritten one when there is one), so "how old is she?" after "and her sister?" still knows whose sister. A restored turn shows its "Searched as" text from `searched_as`.
4b. **Automatic summaries off: no rewrite.** The chat model is then not pre-warmed or kept loaded, and a rewrite would load it cold; follow-ups are searched as typed, with no chip and no warning. (Decided with the user after review.)
5. **History:** `search_history.searched_as` (migration 6) keeps the rewritten question; a summary completed after a restart searches and summarizes that, with no new model call.
6. **FR-27 amended (needs the user's decision):** a follow-up search now waits for the chat model, up to 15 s (a few seconds with the model loaded and idle; while a previous summary is still being written the rewrite shares the server with it, so the limit can be reached and the question is searched as typed). A question that is not a follow-up is unchanged. The way to remove the wait altogether would be to search the typed question at once and swap in the rewritten results (like the keyword to full-search upgrade); the user chose the time limit instead.
7. **Gate changed after the first real run (2026-10-11).** "Why does it break down?" after "Why do people crave connection?" was searched as typed: it has 5 words and starts with "Why", so the old gate said "stands on its own". On the user's decision the length rules (4 words; 10 characters for CJK and Thai) were removed and the gate is now: starts with a listed pronoun or connector, or has a listed pronoun anywhere (English, Spanish, French, German). Connectors and words that are also articles or conjunctions ("and", "el", "y", "es", "sie") are matched only at the start, because anywhere they would flag almost every question and make every search wait for the model. **Consequences, chosen by the user:** terse follow-ups with no pronoun ("why?", "how so?") and all Chinese, Japanese, Korean and Thai follow-ups (no word list exists) are no longer rewritten; a standalone question that contains "it" or "that" costs one model call and comes back unchanged. Tests: `ThreadTests` (pronoun anywhere, article not a pronoun, short standalone not rewritten).

**Tasks**
- [x] `FollowUp.Detect` returns the earlier questions for the rewriter; `QuestionRewriter`, `IFollowUpRewriter`/`FollowUpRewriter` (App.Logic), wired in `MainWindow` over `SupervisedChat`.
- [x] `AskViewModel`: rewrite before searching, `TurnViewModel` chip and warning; strings (`Ask_SearchedAs`, `Ask_WarningNoRewrite`, Settings text).
- [x] `search_history.searched_as` (migration 6), `SearchHistoryEntry.SearchedAs`, `SummaryJob.SearchedAs`; restart uses it.
- [x] Removed: `SearchContext`, extra search text, `UsedContext`, summary prompt "Earlier questions", `previousQuestions` through `ISummarizer`/`SummaryService`/`SummaryCoordinator`, the coordinator's `contextQuestions` function.

**Unit tests (new or rewritten, `ThreadTests`, `HistoryUiTests`, `EditQuestionTests`)**: the gate and the last-N rule; the rewriter prompt (earlier questions and the last one only), reply cleaning (quotes, label, extra lines, too long, empty), the unchanged reply, cold request, model failure and the 15 s limit becoming "unavailable" while the caller's cancellation stays cancellation; a chain of follow-ups gives the rewriter the searched (rewritten) earlier question; automatic summaries off makes no rewrite; a restored turn shows what was searched; both warnings when the rewrite and the meaning-based search fail; a follow-up is rewritten and only the rewritten question reaches the search, the summary and the history; a standalone question makes no model call; "stands on its own" answer; chip removal searches as typed without asking again; an unavailable model searches as typed with a warning; N earlier questions reach the rewriter (1, 2, 3; 0 = none); a restart uses the saved rewritten question; an edited follow-up is rewritten from the earlier questions, not the one replaced; the time limit waits while a summary is being written and a rewrite that finishes first is used; a cancellation while waiting; a failure is logged with its reason only and a log that throws still falls back to the typed question; the chat context choice (settings, recommendation by RAM, `-c` argument).

**Manual E2E (the real model; never run)**
1. Ask "What does despair do to a person?", then "What's its shadow strength?" → the chip reads "Searched as: What is despair's shadow strength?" (or close), and the results and summary are about despair's shadow strength only.
2. Ask a long, self-contained question after it → no chip, no delay.
3. Remove the chip → the follow-up is searched as typed.
4. Stop the chat model (rename its file) and ask a follow-up → it is searched as typed, with the warning.
5. Ask a follow-up, close the app while its summary is waiting, start it → the summary is written for the rewritten question.
6. Time the follow-up: how long between pressing Search and the results appearing (cold and warm model), and again while the previous question's summary is still being written (expect up to 15 s, then the typed question and the warning).
7. Turn automatic summaries off in Settings and ask a follow-up → searched as typed at once, no chip, no warning.

**Result.** Code written and unit-tested (review findings fixed: a compile error in `MainWindow`, the time limit, chained follow-ups, the restored chip, automatic summaries off); **not built or run on Windows, and not tried with the real model**: whether a 2B or 4B model rewrites well enough (it can add a wrong subject), and how long the wait is, are for the user to judge. **Follow-up after the first run (rewrite timed out while a summary was streaming):** the first diagnosis ("one slot, the rewrite queues") was wrong: the pinned build's default is `--parallel` auto with a unified pool (`--help`; the slot count is not stated there), so a rewrite already had a slot. What changed: (1) the token pool is a Settings choice ("Language model memory": 4096 standard, 8192 large; recommended 8192 from 12 GB RAM, the same line as the 4B model; `AppSettings.ChatContextTokens`, null follows the recommendation; applies when the chat server next starts); (2) the 15 s limit now starts only when no summary is being written: while one is, the rewrite runs beside it with no limit, and the limit counts from the moment the summary queue is empty (`FollowUpRewriter(afterSummary: coordinator.WhenIdleAsync)`); the search waits as long as the whole summary queue takes (including restart work), then up to 15 s; the user accepted this (2026-10-11); (3) a failed rewrite logs one `note:` line to `crash.log` with the reason (exception type or "time limit reached"), the time taken, whether a summary was running and whether the chat server was ready, never the question; a log that throws can't break the fallback. Not yet confirmed on Windows: that the rewrite runs beside a summary, the RAM of the 8192 pool, and the effect on summary speed. The cold model load still counts inside the 15 s when no summary is running (the page pre-warms the model). Known limits: it is not known whether a rewrite shares the CPU with the summary badly enough to hit 15 s even with a free slot (the log line will show it); summaries left waiting before migration 6 that were follow-ups are searched as typed once after the upgrade; `QuestionRewriter` ignores combining marks when comparing, so a Thai rewrite that differs only in tone marks counts as unchanged. Languages other than English depend on the model keeping the question's language. Evaluation pairs for follow-ups were not added to `questions.json`.

### ST-32 · Retry the newest question (DCR-7)

Size S · Covers FR-40 (amended: retry keeps a rewrite that worked) · Depends on: ST-28, ST-31

**Request (DCR-7).** The user asked for a retry icon to the right of the Edit icon, to ask the question again; and that a rewrite that succeeded is not made again on a retry.

**What it does.** The newest question has a refresh icon beside the pencil. Pressing it asks the same question again, as typed, **in place of the first attempt** (the same replace mechanism as the pencil and the chip's remove button): the old question, results and summary are removed from the conversation and the History at the moment the new attempt starts (a summary still being written is stopped), and a new one takes its place; if the search can't start (no book is ready) a finished question stays as it is. A follow-up whose rewrite worked keeps it: the model is not asked again and the "Searched as" chip stays, as long as follow-up handling is still on (automatic summaries on and "earlier questions" above 0; otherwise it is searched as typed, as FR-40 says); one that was searched as typed after a failed rewrite is rewritten again; one whose chip the user removed stays as typed. The search uses the current filter and settings. What is being typed in the question box is left alone. Retrying a question while its edit is open drops the edit and asks the original text. Only the newest question has it, as with the pencil. After a retry the keyboard focus goes to the question box. To get a *new* rewrite when the first one was poor, use the pencil and search the same text.

**Tasks**

- [x] `TurnViewModel.RetryCommand` (newest only) and `AskViewModel.Retry`.
- [x] `RunSearchAsync(replacing, rewritten, clearBox)`: the replaced question stays until the new one is added in one step and its entry is closed then (the pencil and the chip use the same path); a search that was replaced while still recording its entry closes it (`TurnViewModel.Replaced`).
- [x] `SummaryCoordinator`: discarded jobs are not queued or run, and the restart pass skips entries closed or cleared meanwhile; Summarize on a question left on screen by Clear all history still works.
- [x] Ask page: refresh button to the right of the pencil; tooltip and accessibility name ("Ask this question again"); focus to the question box.

**Unit tests (`EditQuestionTests`, 24 methods, 25 cases)**

Retry replaces the first attempt (one turn, one History entry, a new summary); only the newest question has it, and running it anyway does nothing; a half-typed question is left alone; a follow-up whose rewrite failed is rewritten on the retry and loses its warning; one that was rewritten keeps its rewrite (model not asked again, `searched_as` saved with the new entry, the summary written for it), also after a restart; with automatic summaries off or the context setting at 0 it is searched as typed; after the chip was removed it stays as typed; no book ready keeps the question and its answer; a summary being written is stopped; the current filter applies; two presses ask once; a retry while the first attempt is still being recorded leaves no second entry, also when the retry's library check is slow; retrying a question that is still being searched and then asking a new one leaves no stuck spinner; a failure while closing the old entry still asks the question; retry during an edit drops the edit and asks the original text; an entry closed while the restart pass is searching for it, or before it reaches it, is not summarized; Clear all history while the pass holds its list stops it; Summarize still works on a question left on screen after Clear all history, and an entry closed while Summarize is being set up writes nothing; retrying a question still being searched with no book ready drops it.

**Manual E2E**

1. Ask a question and press the refresh icon → the same question is answered again in place; the History still has one entry for it; the keyboard focus is in the question box.
2. Stop the chat model, ask a follow-up (it is searched as typed, with a warning), start the model, press retry → the follow-up is rewritten and the warning is gone. Ask another follow-up that is rewritten, press retry → the same "Searched as" text, with no wait for the model.
3. Type half a question in the box, press retry → the box keeps what you typed.
4. Ask two questions → only the second has the refresh icon.
5. Turn automatic summaries off in Settings, press retry on a rewritten follow-up → it is searched as typed and the chip goes.
6. Remove the chip, press retry → it is still searched as typed.

**Result.** Code written and unit-tested (several review rounds; the edge cases they found around removing the old question and the restart pass are fixed and have tests); **not yet built or run on Windows** (the XAML and code-behind compile there first, in CI). The retry button, focus and the swap of the old question for the new one are the parts to look at. Not done: retrying an older question (as with the pencil, later questions would no longer follow from it); a retry that keeps the first answer on screen until the new one has results; after a restart a question whose chip was removed is rewritten again by a retry (that choice is not saved). A failure while closing the old entry is swallowed (the old entry may then stay in the History) and the question is asked anyway. Retrying a question that is still being searched drops it at once, so if no book is ready at that moment the question is lost (a finished question is kept).

### ST-33 · Highlight only rare question words (DCR-8)

Size S · Covers FR-19 (amended) · Depends on: ST-14, ST-16

**Request (DCR-8).** The user did not like highlights on everyday words of the question such as "people" or "person" in the result excerpts and the Reader, and asked that only central words are marked, decided by the small share of the library's chunks a word is in, with no stop list.

**What it does.** After a search, each word of the question (and each 3-character gram for Chinese, Japanese, Korean and Thai) is counted in the index: it is *central* if it is in fewer than `max(5, 2% of the library's chunks)` chunks (`SearchOptions.CentralTermShare`, `CentralTermMinChunks`; the count stops at that limit, so a common word is cheap). Only central words are highlighted, in the result excerpts and in the Reader. **The search, the "covered" check and the summary still use every word** (`SearchResult.Terms`); the excerpt is still cut around the densest cluster of all of them. A question whose words are all common marks nothing. Highlighted words are now **semibold and underlined**, no longer coloured, so colour is not the only signal (the Reader's passage background stays). The share is of the whole library, not of the books in the filter.

**Why the search stop list stays.** The user asked to remove the stop list; it is no longer used for highlighting (every word is judged by its share, and "the" or "what" are in nearly every chunk), but it still decides which words go into the search query and the "every word found" coverage check, where "what" or "is" would otherwise have to be in the passage. Removing it there would make "not covered" fire on most questions.

**Tasks**

- [x] `SearchOptions.CentralTermShare` (0.02), `CentralTermMinChunks` (5); `ChunkRepository.CountFtsMatches` (capped count).
- [x] `SearchService` works out `SearchResult.CentralTerms`; `ExcerptBuilder.Build(..., highlightTerms)`; results and the Reader get `HighlightTerms`.
- [x] `Highlight` and `ReaderText` draw semibold + underline.

**Unit tests (7 in `SearchTests`, 1 in `AskTests`, 1 in `ContextTests`)**

Only a rare word is marked while the search keeps both; all common words mark nothing and still find results; the share and the floor decide (3 of 60 chunks is central at the floor of 5 and not with a floor of 1); a very small library marks every word; CJK grams follow the same rule; the excerpt is cut around all words but marks only central ones; `HighlightTerms` falls back to all terms; the Ask rows and the Reader request carry only the central words; the summary is still trimmed around every question word. Two older tests that expected `rabbit` highlighted in one book were adjusted (see below).

**Manual E2E**

1. Ask "what do people say about the whale" in a library with several books → "whale" (or the book's own rare words) is underlined in the results and the Reader, "people" is not.
2. Ask a question of only everyday words → results, nothing underlined.
3. Open a result: the yellow passage is still marked; the question's rare words inside and outside it are semibold and underlined. In a paragraph where a rare word is underlined *before* the passage starts, check that the yellow begins exactly at the passage's first character (the passage background is a `TextHighlighter` on plain-text offsets; if it is shifted, draw it on the runs instead).

**Result.** Code written and unit-tested; **not yet built or run on Windows** (the `Underline` inlines, and that the passage background still lines up with them in the Reader, are the parts to look at). **Known limit, measured on the `alice.epub` fixture (80 chunks):** in a library of one book, the book's own topic words are common: "rabbit" is in 23 chunks (29%), "hole" in 5 (6%), "falling" in 2 and "down" in 49, so "rabbit hole" marks nothing with the defaults, and "people" (14, 17%) cannot be told apart from "rabbit" by frequency alone. With several books the same words are rare across the library and are marked. If it feels wrong on a small library, the options are a larger floor (`CentralTermMinChunks`) or always marking the question's rarest word; neither was done. Japanese: a kept trigram can overlap a common word (`輩は猫` is marked while `吾輩は` is not, so part of 吾輩 is underlined), and trigrams across word boundaries tend to be rare. **Cost not measured:** each search now counts the chunks once and runs one capped count per word or gram (up to 2% of the library's chunks each, so about 20,000 index entries per everyday word at 1M chunks; skipped when nothing was found). It is inside the "fusion and result loading" budget; time it with `bookcli bench --use-library` on a large library before relying on it.

### ST-34 · One summary per result (DCR-9)

Size L · Covers FR-23 (amended), FR-24 (replaced), NFR-3 and NFR-8 (reworded) · Depends on: ST-23, ST-24, ST-26, ST-30, ST-33

**Request (DCR-9).** The user found that one summary joining several different results "reads very awkwardly". They asked for the combined summary to go and for **one summary of the passage found under each result**. If the summary is on, a result is shown with its summary in progress; if the model says a passage does not cover the topic, the result should **not disappear** (that would be strange): its summary says so in a friendly way instead. With automatic summaries off, results are shown as before. Summaries start at **2 to 3 sentences**.

**Decisions.** (1) Results appear at once with their excerpt (the user's answer), each with a "Waiting…"/"Summarizing…" line that is replaced by its summary. (2) The model is called **once per result**, one after another in list order, not once for all (a single call writing labelled sections was rejected: a small model blends the passages again and breaks the format). (3) The queue, Stop and restart behaviour of ST-23 are kept unchanged: one job per question; the job writes its results' summaries in turn. (4) The history keeps **plain text**, one paragraph per result headed with its book and place; no migration. (5) The passage is the result's own chunk plus the neighbouring sentences (the Settings "context" choice still sets the size); results of one book are no longer merged. (6) A search the library does not cover still does not call the model, as before (FR-25). **Changed after the first use:** the summaries block (title, status line, **Summarize** and **Stop** buttons, messages) is gone from the results; with automatic summaries off there is no manual way to write one (the user chose this over a small button per result), Esc still stops the one being written, and why a result has no summary ("Loading model…", "Waiting for an earlier summary…", "Stopped…", "could not be written…") is shown in the result itself. A restored question keeps its saved-text block, since its results are not stored. History > **Continue this thread** on the conversation already on screen no longer rebuilds it from the saved text, so its live results are kept (a thread reopened after a restart is still text only; saving the results themselves was not asked for and is not done).

**What changed.** Removed: the combined prompt and its contexts per book (`ContextBuilder` grouping, `MergeDistance`, `MaxPassages`), citation placement and validation (`CitationPlacer`, `CitationValidator`, `Summary`), the summary panel's streaming text and citation links (`SummaryText` in the app, `ICitationTarget`, the `[n]` labels on results). Added: `PassageSummary`, `SummaryRun.Passages`, `PassageState` (waiting, writing, written, not covered, unfinished), `ResultRow.SummaryText`.

**Tasks**

- [x] `SummaryService`: one call per result, NOT_COVERED per result (`PassageSummary.IsNotCovered`; `MayBeNotCovered` holds back a partial marker while streaming); prompt for one passage, 2 to 3 sentences; 150 output tokens.
- [x] `ContextBuilder.BuildAsync(hit)`: one result, trimmed, with neighbouring sentences.
- [x] `Summarizer`/`ISummarizer`: per-result callbacks (started, token with index, done); phases per result.
- [x] `SummaryJob.Passages` (`PassageState`), the coordinator reports each result's state, unfinished ones end as "unfinished" on stop or failure; history text via `SummaryRun.Text`.
- [x] Ask page: summary under each result (results are no longer called "Sources"); the summaries block is gone (see the change after first use); restored turns still show saved text.
- [x] `bookcli summarize`, `grounding`, `bench` use the per-result service (grounding rows: each sentence against the passage it came from).
- [x] Strings: `Ask_PassageWaiting`, `Ask_PassageWriting`, `Ask_PassageNotCovered`.

**Unit tests**

`SummarizeTests`: one call per result, each sees only its own passage, a result the model says is not covered keeps its place with no text, all not covered is not covered as a whole, the budget, the settings, no model call when the search doesn't cover it, stopping keeps the finished results, history text; the prompt (one passage, 2 to 3 sentences, NOT_COVERED, delimiters neutralized); NOT_COVERED recognition and the held-back partial marker. `SummaryTests` (`Summarizer`): phases per result, the callbacks. `HistoryUiTests`: each row fills in as its summary is written, others wait, a not-covered result stays and says so (also for a screen reader), no "waiting" line while summaries are off, stopping leaves no half-written summary looking finished, and keeps the finished ones on screen and in the history (also when the model fails on the second result), an empty reply for one result says so, a partial `NOT_COVERED` is never shown, earlier questions stay open. `HistoryTests`: the saved text is one paragraph per result. `ContextTests`: the text around a result, the budget, trimming around every question word.

**Manual E2E**

1. Ask a question → three results appear at once, each with "Waiting to be summarized…" or "Summarizing…"; each line becomes 2 to 3 sentences one after another.
2. Ask something one of the results has nothing on → that result stays, its line says "This passage doesn't cover your question."
3. Turn automatic summaries off → results without any summary line, and no button to write one.
4. Press **Esc** while the second is written → the first stays; the second says "Stopped." and the third "Not summarized.". Ask the question again (refresh icon on the newest question) to write them.
5. Ask a question, open History, press **Continue this thread** on that same conversation → the Ask page shows the same results and summaries as before, not saved text.
6. Open History → the question shows one paragraph per result, headed with its book.
7. Time it: first summary, and all three (cold model and warm), against NFR-3.

**Result.** Code written and unit-tested; **not yet built or run on Windows, and not tried with the real model**. What is unknown and for the user to judge: how good 2 to 3 sentence summaries from a 2B/4B model are; whether it decides "not covered" well (a good passage wrongly called not covered keeps its place, so nothing is lost, but it says something false); the total time for three results on the user's PC. NFR-3's total target was written for one summary and is to be re-set. **Review fixes:** earlier questions are no longer folded when a new one is asked (their summaries are inside their results; the Results expander can still be closed by hand); a stopped or failed run keeps the summaries already written, on screen and in the history (saved with the Stopped/Failed status); a result the model wrote nothing for says so ("The model did not write a summary. Try again.") and gets no paragraph in the history; the screen-reader name says "Summarizing…" while a summary is written instead of the growing text; a reply starting with punctuation before `NOT_COVERED` ("**NOT_COVERED**") is held back too; the Settings note and FR-39 and other leftover sentences about citations were reworded. Known limits: after a stop, the results not finished stay without a summary (asking the question again writes all of them anew); Esc stops only the newest summary that is waiting or running, so a second press is needed for an earlier one; the first result without a summary gives the reason and the others say "Not summarized."; the history paragraph for a not-covered result is saved in English whatever the UI language; a restored question shows saved text only; the "bench" summary numbers are now for the whole list of results of a question; the grounding sheet from before this change is not comparable.

### ST-35 · Read a book from the Library (DCR-10)

Size M · Covers FR-44, FR-45 (new), FR-20 (amended) · Depends on: ST-12/13 (Library), ST-16 (Reader), ST-25 (tags) · **Code and unit tests done; not yet built or run on Windows.**

**Request (DCR-10).** The user asked for the Library to be an access point to reading: "when I click on a book I read". Decisions (2026-10-13, after the UX spec): click on a row **and** a visible Read / Continue button; selecting for tagging moves to a tick box and Ctrl/Shift+click do not select; **resume** where the reader stopped is important; a book that is not ready opens its original file ("Just Open File") instead of the Reader. The full interaction spec, states, wording and check script are in `docs/UX Spec - Library reading.md`; this story is its build plan and must not contradict it.

**Out of scope (backlogged):** bookmarks and highlights of the reader's own, reading statistics, font and theme controls in the Reader, a right-click menu, a screen map and style guide for the app (see the spec's open question 7).

**Tasks**

- [x] Migration 7: `reading_position` (see the Technical Design) and a repository with an upsert and a get; deleted with the book (cascade) and with "Delete all data" (the database is deleted).
- [x] `IReaderService.LoadBookAsync(bookId)` (saved position, or the first chapter with text; the position's chapter number clamped) and `SavePositionAsync(bookId, chapterPos, paragraph)`.
- [x] `ReaderViewModel`: open a book (no passage, no highlighted words), save the position a moment after scrolling stops, on chapter change and on leaving; Back label "Back to Library" or "Back to results" by where it was opened from; the page restores the scroll to the saved paragraph.
- [x] Library: tick box per row (selection by the box only), accent **Read** / **Continue** button on Ready rows and **Open file** on the others, row click and Enter do the same, "Section n of m" line, tooltips, accessible names; the other row buttons and header buttons plain.
- [x] Open file from the Library through the existing file launcher, with a row note when the file is missing or cannot be started (the launcher reports one "could not be opened" message, so "no program" is not a separate note).
- [x] Back to the Library keeps the scroll position (the page is kept alive: `NavigationCacheMode.Required`; **unverified on Windows**); ticks are cleared on every visit. *(Focus returning to the row that was opened, and focus going to the chapter text on opening, are not done.)*
- [x] Strings in `Resources.resw` (all wording of the spec's section 9).
- [x] Docs: this story's Result note, the Technical Design and README text for the new behaviour, FR-9/FR-20 notes as needed.

**Unit tests (written; 19 new)**

`ReaderTests`: one position row per book and a second save replaces the first, a save for a book that is gone does nothing, removed with the book, chapter counts match the chapters the Reader pages through, `LoadBookAsync` starts at chapter 0 then at the saved place and clamps a place that no longer exists, a book opened from the Library marks no passage and highlights no word and Back says "Back to Library", the place is saved when it changes and on leaving and a new chapter starts at its top, a book opened from a search result never moves the saved place and Back says "Back to results", a book with no text and a book that is gone give their messages. `LibraryTests`: a ready book offers Read then Continue with "Section n of m", a book that is queued, indexing, rejected or failed offers Open file and launches the original not the Reader, opening a ready book asks the shell to read it and launches nothing, a missing file and a file that cannot start give the row notes, ticking never opens anything and a note clears on the next open, a visit starts with nothing ticked, the places are re-read after the Reader moved one, and `LibraryService` lists places with chapter counts and the file path.

**Manual E2E**

The 12 steps in the spec's section 13, plus the screenshots listed there for the review.

**Result.** Code written and unit-tested; **not yet built or run on Windows** (the XAML went through the XAML compiler here with only the known Windows-only error; the code-behind compiles first in CI). What was built, against the spec:

- **Reading position:** migration 7, `ReadingPositionRepository` (one upsert per book; a save for a book that no longer exists is ignored), `IReaderService.LoadBookAsync` / `SavePositionAsync`. The Reader page asks the list for its first visible paragraph once a second and the view model saves it when it changed; leaving the page saves it at once. A place is saved **only for books opened from the Library**: looking up a passage in another chapter must not move "Continue".
- **Library:** `ListView` selection is off; each row has a tick box (`BookRow.IsTicked`), an accent **Read** / **Continue** button for a ready book (changed by ST-36: only **Continue** is accent) or a plain **Open file** button otherwise, a "Section n of m" line, a tooltip, and an accessible name that says what Enter does. A click on the row or Enter does the same as that button (`ItemClick`).
- **Open file:** `LibraryViewModel` is given the same `IFileLauncher` as the Reader; the row says "The original file was moved or deleted." or "The file could not be opened: …".
- **Reader:** Back says "Back to Library" or "Back to results"; Esc and Alt+Left leave; a book with no text says so and still offers Open file.

Known limits and things to try on Windows: whether a button or tick box inside a clickable row swallows its own click (if both fired, the shell ignores a second request while the Reader is already open) and whether Tab reaches them; that the Library keeps its scroll position through Back; focus does **not** return to the row that was opened and does not go to the chapter text on opening; whether `FirstVisibleIndex` is reliable in a very long chapter; the position is not saved while a book is opened from a search result, by design; a book removed and added again starts from the beginning; the Library counts chapters only for books that have a saved place (one query for the list, one lookup per refreshed row), not measured on hundreds of books. **Review fixes:** the load of a book is awaited after its place is saved (a fire-and-forget save made a test flaky); nothing of the book shown before is kept while another loads, so a slow or failed load can never save the old book's place; each paragraph carries its index so two identical paragraphs ("* * *") scroll correctly; row and button names are bound ("Continue reading {title}") and the list item's name comes from `BookRow.ToString()`; a book with no chapters has no place.

### ST-36 · UX alignment (decisions of 2026-10-13)

Size S · Covers FR-9 (amended), FR-30 (amended), NFR-22 (amended) · Depends on: ST-13, ST-14, ST-16, ST-18 · **Code and unit tests done; not yet built or run on Windows.**

**Request.** After the whole-app UX design (`docs/UX Design.md`, `docs/Style Guide.md`) the owner decided issues 1 to 12; this story applies the ones that change the app. Mock-ups drawn from the XAML are in `docs/mockups/`.

**What changed**

- **Ask is home**, also after the wizard; the wizard's last button says **Get started** (it said "Open Library"). The Ask page's "Add books first" and "still being indexed" states have a **Go to Library** button (`AskViewModel.GoToLibraryCommand`, `LibraryRequested`; the shell selects the Library item).
- **Words:** the Reader's buttons say **Previous section / Next section**; the History empty state and the New thread tooltip say "thread".
- **Accent:** *(first round, superseded by the second round below: only **Continue** is accent, **Add books** is a command-bar item and the accent button is in the empty state.)*
- **Settings:** language model memory, chunk size and overlap moved into a folded **Advanced** expander; the model size has a note ("Changing the model size reloads the app…"); the "applies to the next summary" sentence is gone (a setting that does not say otherwise applies at once); a failed **Delete all data** is shown in an error InfoBar with a close button, placed in the Your data card above the buttons where the person is looking (`SettingsViewModel.HasMessage`, `DismissMessage`), instead of red text. On Ask, **Search** is a standard button in the no-books state, so **Go to Library** is the one accent button there.
- **Docs:** requirements (new User experience section and flows; FR-9, FR-30, FR-42 and NFR-22 reworded), UX design (decisions and five new layout findings), style guide.

**Second round (the mock-up findings, accepted 2026-10-13):** a minimum window of 800 x 600 and a first size of 1100 x 750 (`WindowSizing`, `OverlappedPresenter.PreferredMinimumWidth`, scaled by the display DPI); only **Continue** is the accent button in a Library row and **Read** is standard (`BookRow.ShowContinue` / `ShowRead`); the Library's page commands are a `CommandBar` that overflows to "…", with the accent **Add books** in the empty state (the earlier `HasBooks` swap is gone); the Ask question's two chips share one row and the filter chip is hidden on the newest question while it equals the filter line (`TurnViewModel.HasFilter`); the Reader's text column is centred.

**Unit tests (6 new in the second round, 7 more in the third: find, find following a late title, selection mode, tagging leaves it, Select needs books, the strip, the quiet row)**: the Go to Library request; the Settings message shows in the InfoBar and closes; the window sizes match NFR-22; Continue is the accent and Read is not (and swaps when the Reader saves a place); the filter chip hides and shows with the filter line. (The earlier `HasBooks` test was replaced.)

**Third round, the quiet Library (the owner found the page chaotic; mock-ups approved 2026-10-13):** each row has one main button and a "⋯" menu (Tags…, Remove; also the context menu); the status line shows only for books that are not ready and the per-row progress bar is gone; tags are chips; tick boxes appear only in **Select** mode, which keeps the find box and shows "N selected", **Add tags to N books** and **Cancel** (`IsSelecting`, `SelectCommand`, `CancelSelectionCommand`; a click ticks); one **indexing strip** above the list with the overall progress and Pause/Resume (`ShowIndexingStrip`, `IndexingText`, `IndexingPercent`; the header lost Pause); **Find a book** filters the list by title or author (`FindText`, `Shown`; ticks stay on hidden books). Removed: `ShowPause`, `PauseGlyph`, `ShowProgress`, `TagsText`, `TagsName`, `RemoveName`. **Review fixes:** the shown list is updated by single inserts and removes (so a Remove keeps focus and loading hundreds of books is one refresh); a row says what Enter does in Select mode and whether it is selected; the row menu is not offered in Select mode and Remove updates the count texts; the overall bar counts finished books so it never moves backwards; tag chips wrap (`VariableSizedWrapGrid`); the find box matches title and author, not the language code; "Indexing is paused" when nothing waits.

**Not verified:** the minimum window size, the command bar's overflow, the one-row chips, the "⋯" menu (its items get their book when it opens, because flyout items do not inherit the row's DataContext) and the context menu are only in the XAML and code-behind; they need a look on Windows (the mock-ups are drawn from the XAML, not rendered by WinUI).

**Manual E2E**

1. Finish the wizard → the Ask page opens; with no books it offers **Go to Library**, which opens the Library.
2. Library empty → the accent **Add books** button is in the empty state; add a book and wait until it is Ready → **Add books** is now an item in the command bar, and the row's **Read** is a standard button (it becomes the accent **Continue** once you have read some).
2a. Ask a question with a filter set → no "Searching in" chip on the newest question (the line at the bottom says it); change the filter → the chip appears on that question; ask another question → the first one keeps its chip. A long "Searched as" chip is cut with an ellipsis and shows its full text in a tooltip.
2b. Resize the window to 800 x 600 and set text to 200 % → the Library's commands fold behind "…"; the buttons' texts are centred.
3. Reader → buttons read "Previous section" / "Next section"; the text column is centred.
3a. Shrink the window → it stops at 800 x 600; the Library's commands move behind "…" when they do not fit.
3c. Focus a row and press Shift+F10 (or the Menu key) → its menu opens with Tags… and Remove; in Select mode no menu opens and Enter ticks the row. If Shift+F10 does nothing, the context menu has to move to the list item's container.
3b. Library → rows show one button and a "⋯" menu (Tags…, Remove); press **Select** → tick boxes appear, the row buttons hide, "Add tags to N books" and Cancel show, the find box stays; tag two books → the mode ends. While books index, one strip at the top shows overall progress and Pause. Type in **Find a book** → the list narrows.
4. Settings → **Advanced** is folded; open it for language model memory, chunk size and overlap. Make Delete all data fail (lock a file) → an error bar appears at the top and closes.

### ST-38 · Search in the other languages of the library (DCR-11)

Size M · Covers FR-46 (new), FR-27 and FR-30 (amended), US-18 · Depends on: ST-14, ST-24, ST-25, ST-31 · **Code and unit tests done; not yet built or run on Windows.**

**Request (DCR-11).** The user found that an English question finds the English chunks and ranks them above the Chinese ones, so the vector search is not language neutral. Cause, from the code: the keyword lists (word and trigram) can only match text in the question's own language, so RRF gives same-language chunks a second list; and multilingual embedding models score same-language text higher. The user's decision: *when I type English, also search in Chinese, and the other way round; the LLM that already shapes the question can do it.* Agreed with the owner: it runs only when the books searched include another language, not for every search; the languages (first agreed as the other languages, at most two) became all the library's languages, at most three, after the language detector failed on short questions (the owner's decision, 2026-10-26).

**Decisions to confirm by the owner:** (1) a follow-up in a two-language library makes two small chat calls (rewrite, then translation) instead of the one call first agreed; (2) with automatic summaries off nothing is translated and no warning is shown. **Out of scope (backlogged):** a per-language quota or score normalisation in the merge (rejected as the main fix: it only reorders what the English-biased lists already found); translating the passages or the summaries; Traditional against Simplified Chinese (the model is asked for "Chinese"); a calibrated threshold for cross-language matches (still the owner's `bookcli eval` to run).

**Tasks**

- [x] `SearchService.SearchAsync(…, alsoSearch)`: each query is searched by meaning and by keyword (word and trigram), all the lists are fused (the question's lists first); a blank or repeated query is dropped; terms of all queries are used for highlights and the summary; covered if any query clears the coverage threshold (the normal one for every query when there are translations, the language-detecting rule without them); the ranks shown are the best over the queries; if embedding fails every query falls back to keywords.
- [x] `TranslationTargets.Choose`: with two or more languages in the books in scope, all of them (most books first, at most three, only languages the translator knows by name), **without detecting the question's language**; with one language L, only a question clearly in another language is translated, into L (the detector is used only there). *(Changed after first use: the detector returns "und" for a question with none of its common words, such as "Why does Darcy love Elizabeth?", and that question was silently not translated; the question's own language now comes back unchanged and is dropped. A reply that is only copies of the question, such as a name, gives no chip and no warning, while a reply with no usable line is a failure: it is logged and shown as the warning.)*
- [x] `QueryTranslator` (Core, prompt and reply parser) and `QuestionTranslator` / `IQuestionTranslator` (App.Logic, over the chat server). The time limit, the wait for a running summary and the log line (no question text) are shared with the follow-up rewrite in `ChatSideCall`.
- [x] `IAskService.GetBookLanguagesAsync(filter)` and the `alsoSearch` argument of `SearchAsync`.
- [x] `AskViewModel`: after the follow-up rewrite and before the search, translate the searched question when the setting and automatic summaries are on; keep the translations on retry and when the "Searched as" chip is removed; `TranslationUnavailableException` searches the question alone with a warning.
- [x] History: migration 8 `search_history.also_searched` (JSON array); `SummaryJob.AlsoSearched`; a restart searches the saved translations without a model call.
- [x] Settings: `SearchOtherLanguages` (default on) and its toggle with a note; the "Also searched: …" chip on its own row under the first chip row; the warning; strings. Mock-ups: `ask.png`, `ask-800x600.png`, `ask-not-translated.png`, `settings.png` (approved 2026-10-26).
- [x] Docs: requirements (FR-46, US-18, FR-27, FR-30), this story, Technical Design (Search, Data storage, traceability), UX Design, README, CLAUDE.md.

**Unit tests (45 new, `TranslatedSearchTests`)**

Search: without the translation the other language's passage is not in the results; with it the passage is found and ranks first in the test library while the English ones stay; highlights from both queries; every query held to 0.45 when there are translations, so a 0.40 cross-language match is covered alone and not with a translation, and a blank or repeated query is no translation; blank or repeated queries are embedded once; an embedding failure keeps the keyword matches of every query; no translation is exactly the old search. Languages: every language of a multi-language library, most books first, at most three, whatever the question (English, Chinese, no common words, a number); one language: only a question clearly in another language, into it; none for no books or unnamed languages; a question with no common words is still translated; a copy of the question (a name) is no failure, a reply with no usable line is; retry after a failed translation translates again and clears the warning; lower case and once. Translator: the prompt names the languages and the reply is returned in order; no languages, no model call; the common reply shapes (label, bold, dash, full-width colon, a lone unlabelled line); a missing or repeated or too long reply is skipped. `QuestionTranslator`: a model failure becomes `TranslationUnavailableException` and the log has no question text; the caller cancelling is not a failure. History: saved with the entry; a restart searches the saved translations without a model call. Ask page: a book in another language gives a translation, a search in both and the chip; a follow-up is translated from its rewritten form; one language gives no call and no chip; the setting off, or automatic summaries off, gives no call; an unavailable model gives the warning and a search of the question alone; removing the "Searched as" chip translates the typed text again; a reply with a preamble is not taken as the translation; a trailing space does not make a question and its identical translation search twice; retry keeps translations that worked and redoes a failed one; a restored turn shows its chip; the setting's default and its saving.

**Manual E2E** (needs Windows, the real models and a library with English and Chinese books; the user runs it)

1. Ask an English question that one Chinese book answers → the chip "Also searched: …" shows the Chinese question; a passage from the Chinese book is among the results.
2. Ask a Chinese question the English books answer → the chip shows the English question; English passages appear.
3. Ask a question in a library of only one language → no chip and no chat-model call (the crash log and the model server show none); the search time is within noise of the same question with the setting off.
4. Turn the setting off in Settings → no chip, as before.
5. Stop the chat model (or turn automatic summaries off) and ask → the warning "…could not write this question in the other languages…" (with summaries off: no warning, no translation) and the results of the question as typed.
6. Press the refresh icon on the newest question → it is searched again with the same translation, with no wait for the model.
7. Close the app while a summary is waiting, start it again → the summary is written for results found with the same translations.
8. The paired-question check the owner runs: 10 to 20 questions answered in both an English and a Chinese book, asked in English, recording the rank of each book's passage with the setting on and off (`bookcli eval` has no paired set yet).

**Result.** Code written and unit-tested; **not yet built or run on Windows, and not tried with the real models**. Known limits and unknowns: whether the 2B/4B chat model translates short questions well enough (names, terms) and how long it takes on the owner's CPU against the limits (15 s with the model running, 60 s when the call has to start it, set because a cold load took 58 s to a first token; a first search can therefore wait up to 60 s before any result appears, and may still fall back to the question alone); whether the lists now rank the other language fairly, which only the paired-question check can say; with one language in the library a short Latin question in another language is still not translated (the detector says "und"), as before DCR-11; with four or more languages the fourth is only searched across languages and keeps the lower threshold only when no translation was made; a translation that is the question unchanged is dropped, but a rephrase of the question's own language survives and adds a near-duplicate query; a reply that leaves a language out leaves it without a query of its own while the normal threshold already applies; a library of one named language plus unnamed ones (for example English and Korean) counts as one language, so a Korean question is neither translated nor held to a lower threshold; the three-language request writes up to three lines, about 20 s at 6 to 7 tokens/s, against a 15 s limit: **to time on the owner's PC**, and a reply cut off by the 250 token cap is not detected (the last line is taken as it is). The translation is a separate chat call from the follow-up rewrite (agreed design said one call; a second short call kept the rewriter, its interface and its tests unchanged and translates the already standalone question), so a follow-up in a library with two languages makes two small calls. NFR-2 (search time) now has the model call in front of the search when it translates; to be timed.

## ST-37 · Production readiness (backlog)

Not started; promoted when a public release is planned. Candidates collected so far: an **About** card in Settings (version, data folder in plain words, open-source licences of the bundled components, the keyboard shortcuts: Ctrl+F, Enter, Esc, Alt+Left); the licence notices the installer should carry; an in-app link to the privacy statement; first-run telemetry stays off (there is none).

## Implementation notes (M0)

What was learned while building M0 (ST-01 to ST-05): library choices, measured results, known limits and open items. The plan above is unchanged; this records where reality differed from it.

### Database (ST-01)

- sqlite-vec is pinned to v0.1.9; the Windows (`vec0.dll`) and Linux (`vec0.so`) loadable extensions are bundled under `src/BookSearch.Core/native/` and copied next to the executable. Only the Linux build has been run.
- Schema changes from the design (applied to the Technical Design): `chunk_fts_cjk` keeps its own copy of the text so it can be deleted by rowid; `chunk` has `UNIQUE (book_id, chunk_index)`; `ingest_job` has a `progress` column; `worker_control` is a single-row table.

### Parsers (ST-02)

| Area | Choice | Notes |
| --- | --- | --- |
| EPUB | `VersOne.Epub` 3.3.6 (the NuGet id is `VersOne.Epub`, not `VersOne.EpubReader`) and AngleSharp | Alice, Pride and Prejudice and Don Quijote parse in about a second. |
| PDF | PdfPig 0.1.16 | `RecursiveXYCut` for blocks, `UnsupervisedReadingOrderDetector` for order, plus the layout rules below. |
| Language | Own detector: Unicode script plus common words | ja, zh, ko, th, ar, he, ru by script; en, es, fr, de, it, pt by stop-words. Short or mixed text is unreliable; other Latin languages return `und`. |

Rules and thresholds (calibrate in ST-08):
- **Reject:** image-only when fewer than 100 non-space characters on 80% or more of pages; encrypted EPUB content (font obfuscation allowed); password PDF; more than 500 MB; more than 5,000 PDF pages; unsupported extension.
- **PDF headers and footers:** a line in the top or bottom 12% of the page whose text, ignoring digits, repeats on 40% or more of pages (books of 3+ pages) is removed.
- **EPUB:** Gutenberg packs several chapters into one file and points the TOC at anchors inside it, so each TOC entry is resolved to the block its anchor points at. Gutenberg's own TOC labels are sometimes odd (for example "I hope Mr. Bingley will like it. CHAPTER II.") and are kept as-is. The license text is extracted like any other chapter; consider stripping it before indexing.
- **CJK PDFs:** line wraps add spaces between Chinese characters, which are removed.
- **Two-column PDFs:** a gutter is a run of at least 10 pt near the page middle where no body word starts, followed by a column edge (a word starting just after the gap on 40% or more of text rows). Word start positions are used because some PDFs report word boxes about 20 pt too wide (one of the two-column PDFs does). Rows with a word starting inside the gutter are full-width; the rest are read left column then right. Limits: tables are read column by column, and unusual layouts may be misdetected.

#### Real books tried (`Books/`, git-ignored)

| Book | Pages | Time | Result |
| --- | --- | --- | --- |
| Large technical book (PDF) | 613 | 11 s | Good: 211 outline sections, headers stripped, 2 index pages read as columns. Bullets become separate blocks. |
| Large two-column reference (PDF) | 1751 | 19 s | Two-column verses, now read in order. No outline, so one section. |
| Short two-column book (PDF) | 240 | 6 s | Good; glossary pages are two-column and read correctly. |
| Mid-size technical book (PDF) | 269 | 4 s | Good: 20 outline sections, no column false positives. |
| Book with a custom non-Latin font (PDF) | 447 | 7 s | English readable, but each page is one block, and Sanskrit lines are garbled glyphs from a custom font (not fixable without OCR). |
| Textbook (PDF) | 540 | 13 s | Good: 176 sections. About 40 pages flagged as columns: an index and tables (read column by column) and at least one single-column page that still read in order. |
| Chinese book (PDF) | 166 | 5 s | Good; language detected as zh. |
| Markdown notes | | 0.2 s | Good: 84 sections from headings. |

A plain-text version of the two-column book was worse than its PDF: it already has the two columns mixed up paragraph by paragraph, has no pages or chapters, and splits into 34,590 tiny blocks.

### Chunker (ST-03)

- Sentence splitting uses ICU's break iterator: the Windows system `icu.dll`, or on Linux a versioned `libicuuc.so.N` found on the library path. A punctuation-based splitter is the fallback when ICU isn't found.
- ICU alone still breaks after "Dr." and between "J. K.", so segments ending in a known abbreviation or single-capital initial are merged back.
- Chunks are filled to about 500 tokens (up to 550), with about 75 tokens (60 to 90) repeated from the previous chunk. Sentences longer than a chunk are cut at word boundaries, or by character for text without spaces.
- `ApproximateTokenizer` (about 4 characters per token, 1 per CJK character) is used by `bookcli chunk`. Measured: about 262 chunks per 100k words for English EPUBs (the plan expects about 260), average 425 to 490 tokens.
- A chunk's token count is the sum of its sentence counts, which can differ by a token or two from the model's count of the joined text.
- Each section is chunked on its own, so tiny sections (title pages, one-line headings) become tiny chunks. Consider merging them before indexing.
- To run the ICU tests on Linux, unpack a `libicu` package (`dpkg -x`) into a folder and set `LD_LIBRARY_PATH` to it; otherwise one test skips and the ICU variants of the splitter tests don't run.

### Model clients (ST-04)

- `LlamaServerProcess` starts `llama-server` on 127.0.0.1 on a free port with a random API key, waits for `/health`, and kills the process tree on dispose.
- `EmbeddingClient` adds the retrieval instruction to questions only, sends passages in batches of 16, normalizes vectors and requires 1024 dimensions. It also implements the chunker's tokenizer through `/tokenize`. `ChatClient` streams `/v1/chat/completions` with thinking off.
- Requests are sent buffered (with a Content-Length), not chunked.
- The console tools read `<data folder>/config.json` (or `--config`): `serverExe`, `embeddingModel`, optional `llmModel`, `threads`. A sample is printed when the file is missing.
- Ctrl+C and normal exit stop the server. A hard kill of `bookcli` can leave it running until ST-10 adds a Windows job object.
- **Not run against the real `llama-server` and Qwen3-Embedding-0.6B yet.** Unit tests use a fake HTTP handler, and the console tools were run against a Python stand-in that accepts the same flags. Three integration tests run when `BOOKSEARCH_M0_CONFIG` points at a config with the real files. Still to confirm: the `--pooling last` flags, whether Qwen3-Embedding needs an explicit end-of-text token, and that `/tokenize` counts match what `/v1/embeddings` consumed.

### Ingest (ST-05)

- Job stages stored: `extract`, `embed`, `done`. Chunking is in memory (it is deterministic), so there is no stored `chunk` stage. Sections and book metadata are saved in one transaction when extraction finishes; each batch of 16 chunks is saved together with the job cursor in one transaction.
- Resume re-hashes the file (a changed or missing file marks the book `failed`), re-extracts and re-chunks it, and continues at `next_chunk_index`.
- Unreadable or corrupt files become `failed`: they stay in the queue and are retried by `bookcli resume` or by adding the file again. DRM, encrypted, image-only, too-large, unsupported and empty files become `rejected` and are finished; adding the same file again reports a duplicate.
- `UNIQUE (book_id, chunk_index)` stops two processes ingesting the same book from writing duplicates (one fails). Run one ingest at a time; the ST-11 worker will be the only writer.
- **Speed concern for ST-08:** chunk sizing calls `/tokenize` once per sentence (8 in parallel). Against the Python stand-in, chunking took 11 s for Alice and 39 s for Pride and Prejudice. The real server should be much faster, but measure it; if it is a bottleneck, size chunks with `ApproximateTokenizer` and use the model only for embedding.

### Search (ST-06)

- The keyword side turns a question into an OR query over its words, dropping a small English stopword list, so BM25 favours the rarer words. Both retrievers return 50 candidates; reciprocal rank fusion (k = 60) merges the vector list, the word list and the trigram list, and the top 8 are shown.
- A result shows an excerpt of about 320 characters, not the whole chunk. A chunk is about 500 tokens (several paragraphs), so the full text behind each hit gives real context; the excerpt is cut around the densest cluster of matched words and the matches are highlighted.
- The coverage threshold (0.45) applies to the best vector similarity, with one exception: if a top keyword hit contains every query word the library counts as covering the question. Both rules are to be calibrated in ST-08 with the real model.
- If the embedding server is down or not configured the search still returns keyword results with a warning; "not covered" then means nothing matched.
- **Finding: Contents pages rank high.** On "falling down the rabbit hole", Alice's short Contents chunk (a list of the chapter titles, including "Down the Rabbit-Hole") ranked first with stand-in vectors and second on keywords, ahead of two real chapter I passages. Short chunks gain from BM25's length normalization, and PDFs and EPUBs with long tables of contents will show the same. Options: skip chunks from tables of contents when indexing (EPUB nav and landmarks, outline titles such as "Contents"), or filter very short or list-like chunks. Decide after the real-model evaluation (ST-08).
- Limits: stopwords are English only (a Spanish question keeps "de" and highlights it); trigram search needs 3 or more characters, so a two-character Chinese word is found by the vector side only.
- Timings with 1,400 chunks: keyword 7-14 ms, vector 21-42 ms, fuse and load under 50 ms. Embedding time in these runs is the stand-in server's.

### Summarize (ST-07)

- The prompt carries the top 5 results, each trimmed to whole sentences around the sentence with the most query terms (up to about 300 tokens). On the four-book test library the prompt was about 1,600 tokens, inside the 2,000-token budget.
- A model reply is checked after it finishes: markers for passages that weren't sent are removed, sentences with no valid citation are flagged, and `NOT_COVERED` becomes a plain message. If the search already found nothing the library covers, the model is not called at all.
- Sentence boundaries decide which sentences count as "uncited", so splitting matters. ICU split a quoted question from its attribution; this is now merged back, as the punctuation splitter already did.
- The language model is started on demand and stopped with the tool; a cancel during loading or generation stops the server. The idle timeout and crash restart come with ST-10.
- **Not tested yet with the real model:** whether a 2B to 4B model follows the citation rules, whether it resists the injected instruction, and time to first token on CPU. The summary is only as good as the retrieved passages and the model; the evaluation (ST-08) measures how many sentences are supported by a cited passage.

### Summary prompt rewrite (by the user, after ST-17)

The system prompt in `SummaryPrompt.SystemText` was rewritten by the user: a "precise research assistant" with six numbered rules (grounding strictly in the passages, `NOT_COVERED` as the only output when they lack the answer, citations `[1]` or `[1][3]` "at the end of relevant thoughts or paragraphs", synthesis in a neutral tone, passage text treated as untrusted, the question's language and under 200 words). The Technical Design shows the new text and the prompt tests check its key phrases.

Things to watch, none of them tested with the real model yet:
- **Fewer citations, but no "uncited" marks in the panel (user's decision).** The old prompt asked for a citation after every sentence and the panel dimmed and tagged any sentence without one. The new rule asks for citations at the end of a thought or paragraph, so the panel no longer dims or tags such sentences: every sentence is drawn the same way and only the `[n]` markers are links. The checker still removes markers that point at passages that were not sent and still records which sentences carry no marker of their own (`Uncited`), for `bookcli grounding` and `bookcli summarize`; the grounding check (NFR-8) therefore still starts those sentences as "unsupported" until judged by hand.
- **"Synthesize rather than repeat verbatim"** asks a small model for more than quoting; check grounding with `bookcli grounding` against the previous prompt.
- `NOT_COVERED.` (with a full stop) is accepted by the checker, as is `NOT_COVERED` followed by text.

### Continuous integration (added after ST-17)

`.github/workflows/ci.yml` runs on every push and pull request (and by hand) on GitHub Actions:
- **Linux job:** `dotnet build BookSearch.sln` and all unit tests, with ICU available so the sentence-splitting tests run instead of skipping.
- **Windows job:** the same, then `scripts/publish-app.ps1`, which compiles the WinUI 3 app (XAML and code-behind) and the worker as a self-contained publish, and a check that `BookSearch.exe`, `BookSearch.Worker.exe` and `native/win-x64/vec0.dll` are in the output. This is what removes the "the C# code-behind was not compiled" caveat from the Result notes of ST-12 to ST-17, ST-23.
- Both jobs write a test summary to the run page (`scripts/ci_summary.py`) and upload the `.trx` files for 14 days; a new push cancels the older run of the same branch. Only official GitHub actions are used.
- **First run (commit `07802a1`) was green.** Linux: 400 passed, 5 skipped (the real-model tests), in 1 min 9 s. Windows: 399 passed, 6 skipped (the same 5, plus one test that uses a shell script as a stand-in model server and is Linux-only), including the WinUI compile and the publish check, in 3 min 55 s. So the code-behind of ST-12 to ST-17 and ST-23 compiles on Windows, and the existing tests pass there too.
- **Not included, by decision:** anything that needs a model. The user ruled out evaluation runs in CI (they would take hours), so `bookcli eval`, `bench` and `grounding` remain manual steps on the user's PC. Ideas for cheap automatic summary checks (answer language, length, marker validity) would also need the models and are therefore not scheduled.

### Summarize speed (after ST-17; written for the single combined summary, superseded by ST-34)

On a CPU a summary costs model load + reading the passages + writing the answer (the early 4B run: 58 s to the first word including the load, 102 s in all, at 6-7 tokens/s). Three changes, none measured with the real model yet:
- **Smaller prompt:** `SummaryOptions` now sends **4 passages of up to 200 tokens** (about 1,100 tokens including the instructions) instead of 5 of 300 (about 2,000). Reading time falls roughly in proportion. The fifth result is still shown but is not summarized, so the summary cites `[1]` to `[4]`. `bookcli summarize --passages` follows the new default.
- **Shorter answer:** the prompt asks for **under 120 words** (was 200) and the reply is capped at **250 tokens** (was 350); writing time is proportional to length, so this is roughly 40% less writing. NFR-3's "full summary" is now about 120 words.
- **Model loaded ahead of time:** `SummaryCoordinator.PrewarmAsync` starts the chat server in the background when the app opens and each time the Ask page is shown, but only if automatic summaries are on and the library has at least one ready book. It never summarizes anything, ignores a missing model or server, and while the model is running it postpones the 10-minute idle shutdown. The first summary then finds the model loaded (the panel still says "Loading model…" if it has to wait for it). Cost: the 2-3 GB model stays in memory while the app is in use, which matters on the 8 GB tier; the Settings switch for automatic summaries turns it off.

- **Later change:** the summary is no longer limited to a fixed number of passages. It uses every result the search returns (`SummaryOptions.MaxPassages` is null), so with the "passages per search" setting at 3 nothing changes, and a higher setting makes the prompt longer and the summary slower (about 200 tokens a passage). `bookcli summarize --passages <n>` still limits it.

To judge them: compare `bookcli bench --summarize` and `bookcli grounding` before and after (fewer and shorter passages may lower grounding), and watch the "prompt eval time" and "eval time" lines in the `llama-server` log. The remaining ideas (a calibrated token estimate for indexing, physical-core thread counts, pausing indexing during a summary, GPU) are not done.

### Cross-language search (added after ST-14)

Found when using the app: a Chinese question returned Chinese books and an English question returned English books. Cause: only the vector search is language-neutral; the word and CJK-trigram keyword searches only match text in the question's own language, and fusion adds the scores of the lists a passage appears in, so same-language passages score about double. Multilingual embedding models also rate same-language passages higher than equivalent ones in another language, and the "not covered" threshold (0.45) was set on same-language questions.

Two changes, both easy to revert (`git revert` the commit, or `bookcli search --classic-search` / `bookcli eval --classic-search` to compare against the old rules without reverting):
- **Reserved slots** (`SearchOptions.ReservedVectorSlots`, default 2 since the result count was cut to 5; it was 3 of 8): of the 5 results, the top 3 by fusion are kept and the other 2 are the best vector matches fusion left out (`ResultSelector`), provided their similarity is at least 0.35. Results now also carry the book's language (`SearchHit.Language`).
- **Cross-language coverage threshold** (`CrossLanguageCoverageThreshold`, default 0.35): the question is covered if any of the 10 best vector matches clears the threshold for its language: 0.35 when the passage's language differs from the question's (both detected, neither "und"), 0.45 otherwise. A question the detector can't classify, or a book with no recorded language, keeps 0.45.

**Not yet measured with the real model.** 0.35 and 3 slots are guesses, not calibrated. To compare: run `bookcli eval --classic-search` and `bookcli eval` (look at the cross-language and out-of-library rows), and `bookcli search "<question>"` with and without `--classic-search` on your own books. Risks: the reserved slots can show weakly related passages from another language; a short question can be mis-detected (for example Spanish as English), which makes same-language hits get the lower threshold and lets some unrelated questions look covered. Only 3 of the 40 evaluation questions are cross-language, so a run mostly checks that nothing got worse.

### Reserved slot removed (ST-27, after ST-26)

The user saw that the 4th result (a passage that matched by meaning, with none of the question's words) was a better answer than the 1st (a weaker match that shared words with the question), and asked to remove the reserved slot described above, since English questions already showed Spanish and Japanese results every time on the user's library. `ResultSelector`, `SearchOptions.ReservedVectorSlots` and the slot-specific tests are gone; the results are now the first `TopN` of the fused list. `eval` keeps its five results. The lower cross-language coverage threshold (0.35) stays.

What the slot actually did, found while removing it: it did not take keyword-only results (the opposite of what was remembered); it took the best *meaning* match that the fusion had left out and added it **last**. So with 4 or more results the best meaning match was shown after weaker ones, which is what the user saw. The cost of removing it: such a match now appears only if the fusion already ranks it in the top `TopN`. The unit test `Search_AMeaningOnlyMatchOutsideTheMergedTop_IsNotShown...` records that: a passage best by meaning that shares no words with the question can fall out of the results. The proposed remedy is a weighted or score-based merge, not a slot at the end (not built; other ideas discussed: a reranker, "more like this" and background query expansion).

To check the effect on the user's library: `bookcli search "<question>" --top 8` shows each result's `vector #` and `keyword #` ranks; before this change the same command listed the reserved result last.

### Evaluation (ST-08)

- Keyword search alone is a reasonable baseline: recall@5 69%, MRR 0.59 on the 32 answerable questions, with 100% on name questions and 79% on questions that share words with the passage, but 33% on paraphrases and 0% on cross-language questions. Those last two are what the embedding model must fix.
- Out-of-library questions are the hard case for keyword search: only 25% got "nothing matched", because ordinary words ("capital", "language", "plants") appear in any book. The coverage threshold on vector similarity (and its exact-keyword override) is what has to do this job; `bookcli eval` records each question's best similarity and prints a suggested threshold.
- Metrics treat a hit as correct only when it comes from the expected book and contains an expected phrase, so a related passage from the right book can still count as a miss.
- The benchmark measures Summarize with the model already loaded (warm) and separately for the first run, because the desktop app keeps the model loaded and a command-line run does not.

#### First real Summarize run (developer PC, Qwen 4B, CPU)

Question in English, two Chinese passages, `--passages 2`:
- 58.0 s to first token, 102.6 s in total, for about 776 prompt tokens and an 11-sentence Chinese reply.
- Time to first token **includes loading the model**, because the language-model server is started inside the first request. So the 58 s is model load plus prompt processing and cannot be split from the output alone. The remaining 44.6 s is generation, roughly 6 to 7 tokens per second, which is typical for a 4B model on a CPU.
- A command-line run is always a cold start. In the desktop app the model stays loaded, so a second question would skip the load.
- Every sentence cited a valid passage and none was flagged uncited, a good first sign for the citation rules. Whether the claims are in the passages is for the grounding sheet.
- **The model answered in Chinese to an English question**, because the passages were Chinese and the prompt only says "same language as the question". Stating the language outright ("Answer in English") is the likely fix.
- Ideas not yet applied: report model-load, prompt-processing and generation times separately; name the answer language in the prompt; ask for a shorter answer (generation time grows with its length: 200 words needs roughly 40 s at this speed, so NFR-3's 30 s for 200 words will not be met on this CPU with the 4B model); warn if the model spends tokens on hidden "thinking" (the client ignores reasoning tokens); allow extra server flags in `config.json`; try the 2B model or a different thread count. `llama-bench` in the llama.cpp download measures raw prompt and generation speed.

### Model management (ST-09)

- Sizes and hashes in the manifest are from Hugging Face's file metadata: embedding 639,150,592 bytes, 2B model 1,280,835,840 bytes, 4B model 2,740,937,888 bytes. A first real download should confirm the checksums.
- The language models are community quantizations. If Qwen later publishes official Qwen3.5 GGUF files, switching the manifest to them is a change of URL and hash only.
- Downloads resume across a dropped connection or Ctrl+C because data is kept in `file.part` until its hash matches; a server that ignores range requests just restarts the file.
- `GC.GetGCMemoryInfo().TotalAvailableMemoryBytes` is used for RAM; under WSL it reported about 7 GB on an 8-core PC, so tier detection there reflects the WSL limit, not the Windows machine.
- The wizard (ST-12) and Settings (ST-18) will call these same classes; the "downloads allowed" switch is part of ST-18.

### Open items

- Run the whole of M0 with the real `llama-server` and Qwen models, then the three integration tests, and measure indexing speed and chunking time (ST-08).
- Check retrieval on a question worded differently from the passage that answers it. Keyword search would miss the passage; meaning-based search is needed to find it. Chunks are about 500 tokens (several paragraphs), so each hit carries real context; this is untested until ST-06.
- Run the Windows build (`vec0.dll`, `icu.dll`, Windows paths) once; so far everything ran on Linux. Run `dotnet test` and the real-model steps on a Windows machine after each story.
- Decide whether to merge tiny sections and strip the Gutenberg license text before indexing.
- Decide how to keep Contents and other front-matter chunks out of search results (see Search).
- Decide how to speed up Summarize on CPU and fix its answer language (see Evaluation, First real Summarize run), then re-run `bookcli bench --summarize`.

## M3 backlog and release checklist

M3 items are optional and get full tasks and tests only when promoted into a milestone; v1 ships when the checklist below passes.

**M3 backlog**

| Item | Requirement | Notes |
| --- | --- | --- |
| GPU offload and enhanced tier (Qwen3.5-9B) | NFR-3 GPU target, hardware tiers | Ship the llama.cpp Vulkan build; detect GPU; watch installer size |
| Follow-up questions and query rewriting | US-10, FR-28 | Needs the LLM to rewrite each follow-up into a standalone query |
| Re-ranking of top results | FR-22 | Small local cross-encoder after fusion; check the latency budget |
| Notes and Markdown export | FR-29 | Removed from user stories; keep only if wanted later |
| Watched folders | FR-10 | Removed from user stories; keep only if wanted later |
| Book tags | FR-9 | Still open: keep for organizing, or drop |

**v1 release checklist** (from the requirements' acceptance criteria)

- [ ] All Must and Should requirements pass their stories' unit tests and manual E2E.
- [ ] Fresh install to first search results in under 15 minutes, excluding download time.
- [ ] Works end to end with networking disabled after setup.
- [ ] Packet capture during normal use shows no outbound traffic.
- [ ] Removing a book removes all of its chunks and vectors.
- [ ] Out-of-library questions return the not-covered response (NFR-8).
- [ ] Indexing survives a forced shutdown and resumes without duplicates.
- [ ] Installer SHA-256 published next to the download.
