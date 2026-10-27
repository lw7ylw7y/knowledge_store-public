<p align="center"><img src="docs/logo/docent-256.png" alt="Docent" width="128"></p>

# Docent

Ask your own bookshelf. A Windows desktop app that answers topic questions using only the books you add (EPUB, PDF with a text layer, TXT, MD), running fully offline on your own PC. Search finds passages by meaning as well as by keyword and shows them with book, chapter and page; a small local model writes a short summary of each passage, from that passage and nothing else.

Nothing leaves the machine: books, index, questions and answers stay local, and every component (models, database, libraries) is free and open source.

## Status

The project is in **M0, the console proof of concept**, with M1 (the desktop app) started. The pipeline is a command-line tool, `bookcli`, so quality and speed are known before more UI work; the desktop app so far has the window, first-run wizard and empty pages (see [Desktop app](#desktop-app-windows)).

| Story | State |
| --- | --- |
| ST-01 Solution skeleton and database | Done |
| ST-02 Text extraction and reject rules | Done |
| ST-03 Chunker | Done |
| ST-04 Model server client and embeddings | Done (real model not run yet) |
| ST-05 Ingest pipeline | Done (real model not run yet) |
| ST-06 Hybrid search | Done (real model not run yet) |
| ST-07 Summarize | Done (real model not run yet) |
| ST-08 Evaluation and M0 exit | Tooling done; real-model run and go/no-go pending |
| ST-09 Model manifest, tier detection and download | Done |
| ST-10 Model server supervisor | Code and unit tests done; Windows job object not run |
| ST-11 Background worker and job queue | Code and unit tests done |
| ST-12 App shell and first-run wizard | Built and run on Windows; wizard panels confirmed |
| ST-13 Library page | Code written, **not yet built or run on Windows** |
| ST-14 Ask page: search results | Code written, **not yet built or run on Windows** |
| ST-15 Summarize in the app | Code written, **not yet built or run on Windows** |
| ST-16 Reader page | Code written, **not yet built or run on Windows** |
| ST-23 Background summary and search history (a change request) | Code written, **not yet built or run on Windows** |
| ST-17 Automatic book scoping | Built, then **removed** by ST-25 (it worked badly) |
| ST-25 Tags and the search filter (a change request) | Code written, **not yet built or run on Windows** |
| ST-26 Summaries from contexts, one paragraph each (a change request; replaced by ST-34) | Code written, **not yet built or run on Windows** |
| ST-30 One context per book, longer paragraph (a change request; replaced by ST-34) | Code written, **not yet built or run on Windows** |
| ST-31 Follow-ups rewritten to stand on their own (a change request) | Code written, **not yet built or run on Windows** |
| ST-32 Retry the newest question, ST-33 Highlight only rare question words (change requests) | Code written, **not yet built or run on Windows** |
| ST-34 One summary per result (a change request) | Code written, **not yet built or run on Windows** |
| ST-35 Read a book from the Library (a change request) | Code written, **not yet built or run on Windows** (spec: `docs/UX Spec - Library reading.md`) |
| ST-36 UX alignment (the owner's decisions of 2026-10-13) | Code written, **not yet built or run on Windows** |
| ST-38 Search in the other languages of the library (a change request) | Code written, **not yet built or run on Windows** |
| ST-18 to ST-21 (settings, language and accessibility checks, installer scripts) | Code written, **not yet built or run on Windows** |
| ST-22 Release validation | Tooling only; the measurements are yours to make |

Details and measured results are in [Implementation notes](docs/Stories%20and%20Test%20Plan.md#implementation-notes-m0).

## How it works

```
book file → extract text (structure, pages) → split into sentences → chunks of ~500 tokens
          → embed each chunk (Qwen3-Embedding-0.6B via llama.cpp) → SQLite file:
            chunk text + FTS5 keyword index + sqlite-vec vectors
question  → (written in the library's other languages by the chat model) → embed → vector search + keyword search → fuse → top passages (→ optional short summary of each passage from a small local model)
```

- One SQLite database holds everything (library, keyword index, vectors).
- Models run as `llama-server` child processes bound to 127.0.0.1 with a random API key.
- Ingestion is crash-safe: each batch of chunks is written with its progress marker in one transaction, and an interrupted book resumes where it stopped.

## Repository layout

| Path | Contents |
| --- | --- |
| `src/BookSearch.Core` | Database and repositories, chunker, ingest service |
| `src/BookSearch.Parsing` | EPUB, PDF, TXT and MD extraction, reject rules, language detection |
| `src/BookSearch.Models` | `llama-server` process, embedding and chat clients, config |
| `src/BookSearch.Worker` | The background indexer program |
| `src/BookSearch.App.Logic` | Settings, start-up decision, wizard and page view models (plain .NET, tested) |
| `src/BookSearch.App` | The WinUI 3 desktop app (Windows only; not in `BookSearch.sln`) |
| `src/bookcli` | The M0 command-line tool |
| `tests/BookSearch.Tests` | xUnit tests |
| `tests/fixtures` | Test books: three Project Gutenberg EPUBs, plus files generated by `make_fixtures.py` |
| `docs` | Requirements, Technical Design, Stories and Test Plan (with implementation notes) |

`BookSearch.sln` builds everything that builds on Linux; `windows\BookSearch.Windows.sln` adds the WinUI app. The installer arrives in a later story.

## Build and test

Needs the [.NET 8 SDK](https://dotnet.microsoft.com/download). The target platform is Windows x64.

### Windows (PowerShell)

Run every command from the repository root, the folder that contains `BookSearch.sln`. Build from a normal Windows folder (such as `C:\Users\<you>\knowledge_store`), not from a `\\wsl$\...` path.

**1. Install the tools** (once), then close and reopen PowerShell:

```powershell
winget install Microsoft.DotNet.SDK.8
winget install Git.Git
dotnet --version        # should print 8.x
```

**2. Get the code:**

```powershell
cd $HOME
git clone https://github.com/lw7ylw7y/knowledge_store-public.git
cd knowledge_store
```

Later, `git pull` brings in new changes.

**3. Build and run the tests:**

```powershell
dotnet build
dotnet test
```

The summary line ends with `Failed: 0`. A few tests show as skipped: they need the real embedding model (see "Set up the model" and "Run the real-model tests" below). To run only some tests:

```powershell
dotnet test --filter "FullyQualifiedName~SearchTests"      # one test class
dotnet test --filter "FullyQualifiedName~Search_Hybrid"    # one test by name
dotnet test --logger "console;verbosity=normal"            # list every test as it runs
```

Tests that don't need a model (extraction, chunking, ingest, search with fake vectors) are the same ones that run on Linux. On Windows the chunker's sentence splitting uses the system `icu.dll`, so its ICU tests run without any extra setup.

### Linux / WSL

- Without `libicu` installed, set `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1` so .NET itself starts.
- The chunker also uses ICU for sentence splitting (a versioned `libicuuc.so.N` on Linux). Without it a punctuation-based splitter is used and the ICU tests skip. To run them, unpack a `libicu` package into a folder and set `LD_LIBRARY_PATH` to it.
- Five integration tests need the real models and skip unless `BOOKSEARCH_M0_CONFIG` points at a config file.

Regenerate the self-made fixtures with `pip install reportlab pypdf` then `python tests/fixtures/make_fixtures.py`.

## Continuous integration

Every push and pull request runs `.github/workflows/ci.yml` on GitHub Actions: build and all unit tests on Linux, and on Windows the same plus `scripts\publish-app.ps1`, which compiles the WinUI 3 app and its worker and checks that `BookSearch.exe`, `BookSearch.Worker.exe` and `vec0.dll` are in the publish folder. The run page shows a test summary and keeps the `.trx` result files for 14 days. The workflow does not run any real model, so it does not replace `bookcli eval`, `bench` or `grounding`.

## Desktop app (Windows)

The app is **WinUI 3, unpackaged and self-contained**, so it needs no separate runtime. An installer can be built with `scripts\build-release.ps1` (see [Build the installer](#build-the-installer)). It has a first-run wizard (privacy note, hardware check and model size, download or import of the models and the llama.cpp program that runs them) and a shell with Library, Ask and Settings pages. The **Library** adds books (file picker or drag and drop), shows what is not ready, removes books, lets you give each book tags (**Tags…** in a book's "⋯" menu, or **Select** several and **Add tags to N books**), finds a book by title or author, and shows indexing once at the top with **Pause**; the background worker does the indexing. **Reading is its main job:** clicking a book that is ready, or its **Read** / **Continue** button, opens it in the Reader at the chapter and paragraph where you stopped; a book that is still being indexed (or was rejected) has an **Open file** button that opens the original in its default program. The **Ask** page searches your books (keywords plus meaning; if your books are in more than one language, the language model also writes your question in those languages (at most three) and all versions are searched, shown as "Also searched: …"; the **Search in the other languages of my books** setting turns this off; a **Filter** button next to the question box limits it to books with chosen tags or to books you pick, and the filter stays until you clear it) and shows ranked passages with the matched terms highlighted (only words that are rare in your library, underlined, so everyday words such as "people" are not marked); clicking one shows the passage. The page is a **conversation**: each question is followed by its results (3 by default; Settings changes it) in a collapsible **Results** list, and **each result has its own summary** of 2 to 3 sentences under it, written from that passage and a little text around it (**Settings** chooses how much) in the background; a passage that doesn't cover your question stays in the list and says so. A follow-up that starts with a pronoun or connector, or has a pronoun such as "it", "this" or "they" in it ("and his sister?", "Why does it break down?"), is rewritten by the language model into a standalone question using your earlier question(s) (1 by default; 0 to 3 in Settings), shown as a chip you can remove; the pencil on the newest question puts it back in the box to change it and search again (the new search replaces it), and the refresh icon next to it asks the same question again in place (a rewrite that worked is kept); **New thread** starts a fresh conversation, and the last one is restored when you reopen the app. The summaries of a question are written one after another, and a new search never stops one that is being written; **Esc** stops the newest summary that is waiting or being written (press it again for the one before). Automatic summaries can be turned off in Settings (results are then shown without summaries; there is no button to write one). Continuing the conversation that is already on screen from the History page leaves it as it is, results and all; a conversation reopened after a restart, or a different one, shows only its saved summary text. The first summary starts the language model, which takes a while on a CPU. The **History** page lists your conversations (first question, number of questions, time of the last) with every question and summary, **Continue this thread**, and a close button that deletes the whole thread, plus a **Clear all history** button in Settings; summaries still unfinished when you close the app are completed the next time you open it. Clicking a result opens the **Reader**: the chapter as text, scrolled to the passage with it highlighted (and the question's rare words underlined), previous and next chapter, and Open file for the original.

**Indexing and meaning-based search need `llama-server.exe`.** The first-run wizard downloads the pinned llama.cpp build (about 19 MB, checked against its SHA-256) together with the models, or imports it from disk; it lands in `bin` under the data folder. To use another build instead, put it in `bin` under the data folder, exactly as in [Set up the model](#set-up-the-model) step 2 (`%LOCALAPPDATA%\BookSearch\bin\llama-server.exe`, or under the `BOOKSEARCH_DATA` folder if you use one). Without it the Library shows a warning and books wait as Queued, and Ask can only show keyword matches.

**Start the app normally, not "as administrator".** Windows blocks dragging files onto an elevated app (the Add books button still works), and the Library shows a warning if it detects this.

**Windows 11 Smart App Control** blocks unsigned apps such as this build, with no "Run anyway" button. If the app is blocked, check Windows Security → App & browser control → Smart App Control; turning it off cannot be undone without reinstalling Windows.

**Build it** (needs the .NET 8 SDK for x64; Visual Studio should not be required, but this has not been tried):

```powershell
powershell -ExecutionPolicy Bypass -File scripts\publish-app.ps1
```

This writes `publish\BookSearch\BookSearch.exe`; run it from there. (Alternatively open `windows\BookSearch.Windows.sln` in Visual Studio 2022 with the ".NET desktop development" workload and run `BookSearch.App`.)

**Try it from a clean profile** without touching your real data folder:

```powershell
$env:BOOKSEARCH_DATA = "$env:TEMP\booksearch-fresh"
& .\publish\BookSearch\BookSearch.exe
```

The app keeps its files in `%LOCALAPPDATA%\BookSearch` (or the `BOOKSEARCH_DATA` folder): `settings.json`, `library.db`, `models\` and `logs\crash.log`. Models that `bookcli models download` already put in `models\` are picked up. A startup problem (such as a missing `vec0.dll`) shows an error page with details; crashes are written to `logs\crash.log` (error type, message and stack only, never book text).

Steps to verify it are in ST-12 in the [Stories and Test Plan](docs/Stories%20and%20Test%20Plan.md).

## Build the installer

A per-user installer (no administrator rights, Start menu shortcut, an uninstaller that asks whether to keep your library and models) is built on Windows with the .NET 8 SDK and [Inno Setup 6](https://jrsoftware.org/isinfo.php) (free; `winget install JRSoftware.InnoSetup`):

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build-release.ps1 -Version 0.1.0
```

The installer does not contain llama.cpp; the app fetches it on first run (see above). This writes `publish\installer\Docent-Setup-0.1.0.exe` and `Docent-Setup-0.1.0.exe.sha256` (compare with `certutil -hashfile <installer> SHA256`), and fails if the installer is over 200 MB. The build is unsigned, so Windows SmartScreen warns: choose **More info**, then **Run anyway**. The GitHub workflow "Release installer" does the same on a Windows runner (Actions, Run workflow) and keeps the files as a build artifact.

## Set up the model

`add`, `resume`, `embed`, `serve embed`, `summarize` and (for meaning-based results) `search` need the llama.cpp server and an embedding model. Everything below goes under `%LOCALAPPDATA%\BookSearch`, which is where the tools look for `config.json` by default.

**1. Create the folders:**

```powershell
$dir = "$env:LOCALAPPDATA\BookSearch"
New-Item -ItemType Directory -Force "$dir\bin", "$dir\models" | Out-Null
```

**2. Get `llama-server`.** Download the latest Windows CPU x64 zip (a name like `llama-<build>-bin-win-cpu-x64.zip`) from the [llama.cpp releases](https://github.com/ggml-org/llama.cpp/releases), then unzip it into the `bin` folder. llama.cpp numbers its releases as builds (`b1234`), not 1.0-style versions; any recent build works, so note the one you used.

```powershell
Expand-Archive -Path "$HOME\Downloads\llama-*-bin-win-cpu-x64.zip" -DestinationPath "$dir\bin" -Force
& "$dir\bin\llama-server.exe" --version
```

**3. Download the models.** `bookcli` picks the language model for your memory (the 2.55 GB 4B model with 12 GB of RAM or more, otherwise the 1.19 GB 2B model) and downloads it with the 0.6 GB embedding model. Every file is checked against a pinned SHA-256, and an interrupted download resumes when you run the command again.

```powershell
dotnet run --project src/bookcli -- models list        # your memory, the chosen tier, and what is installed
dotnet run --project src/bookcli -- models download    # download what this machine needs into $dir\models
dotnet run --project src/bookcli -- models verify      # re-check every installed file
```

It ends by printing the two config lines to use below. `--tier minimum` or `--tier recommended` forces a size, and `--only embedding` or `--only llm` downloads one model. The embedding model comes from the official Qwen repository; Qwen publishes no official Qwen3.5 GGUF files, so the language models are community quantizations by unsloth (Apache-2.0). For an offline machine, copy the `.gguf` files over and run `models import <file>`: it accepts only files whose checksum is in the list.

**4. Create `config.json`.** `ConvertTo-Json` takes care of the backslashes. Use the file name of the language model you downloaded (`Qwen3.5-4B-Q4_K_M.gguf` or `Qwen3.5-2B-Q4_K_M.gguf`); `llmModel` is only needed for `summarize`:

```powershell
@{
  serverExe      = "$dir\bin\llama-server.exe"
  embeddingModel = "$dir\models\Qwen3-Embedding-0.6B-Q8_0.gguf"
  llmModel       = "$dir\models\Qwen3.5-4B-Q4_K_M.gguf"
  threads        = 4          # about half your CPU cores
} | ConvertTo-Json | Set-Content "$dir\config.json"
```

To keep `config.json` somewhere else, pass `--config <path>` to the commands.

**5. Check it works:**

```powershell
dotnet run --project src/bookcli -- embed "hello world"
```

Expected: `Dimension: 1024` and a timing line (the first run is slower while the model loads).

## Try it in PowerShell

```powershell
dotnet run --project src/bookcli -- add tests/fixtures/alice.epub
dotnet run --project src/bookcli -- list
dotnet run --project src/bookcli -- search "a young girl falls into a deep burrow after a hurrying creature"
dotnet run --project src/bookcli -- search "quantum chromodynamics"
dotnet run --project src/bookcli -- stats
dotnet run --project src/bookcli -- summarize "Why does Alice grow and shrink?"   # needs llmModel
```

- The first `search` question uses mostly different words from the book's (it never says "burrow" or "creature"). A result from "CHAPTER I. Down the Rabbit-Hole" means the meaning-based search is working.
- The second question is not about anything in the book. It should print "doesn't seem to cover this topic" and list the nearest passages.
- `stats` should end with "Indexes consistent".
- `summarize` prints one streamed summary per result (or says it doesn't cover the question), then the times. Press Ctrl+C to stop it.
- `dotnet run` rebuilds each time. After `dotnet build` you can run the tool directly, which is faster: `.\src\bookcli\bin\Debug\net8.0\bookcli.exe search "your question"`.
- Quote questions that contain spaces or special characters.

To start over, delete `%LOCALAPPDATA%\BookSearch\library.db*` (keep `config.json`, `bin` and `models`), or use `--data <folder>` to work in a scratch library.

### Measure quality and speed (M0 exit)

```powershell
dotnet run --project src/bookcli -- eval --keyword-only      # no model needed: the baseline
dotnet run --project src/bookcli -- eval                     # with the embedding model (indexes the fixture books first, a few minutes)
dotnet run --project src/bookcli -- bench --summarize        # speed against the targets (needs llmModel)
dotnet run --project src/bookcli -- grounding --count 20     # sheet to check by hand, then:
dotnet run --project src/bookcli -- grounding --score eval-reports\grounding-<date>.csv
```

Reports are written to `eval-reports\`; `baseline-keyword-only.md` is the keyword-only result to beat. `eval` ends with a suggested coverage threshold, to be copied into the search options once chosen.

### Run the real-model tests

Five integration tests (similar sentences, cross-language matching, tokenizing, a paraphrased search, and the Summarize injection test, which also needs `llmModel`) run only when `BOOKSEARCH_M0_CONFIG` points at your config file:

```powershell
$env:BOOKSEARCH_M0_CONFIG = "$env:LOCALAPPDATA\BookSearch\config.json"
dotnet test --filter "FullyQualifiedName~EmbeddingIntegrationTests|FullyQualifiedName~Search_ParaphrasedQuestion|FullyQualifiedName~Summary_InjectedInstruction"
Remove-Item Env:BOOKSEARCH_M0_CONFIG        # back to skipping them
```

### If something goes wrong

| Symptom | What to check |
| --- | --- |
| `dotnet` is not recognized | Reopen PowerShell after installing the SDK; check `dotnet --version`. |
| `Config not found` | `config.json` must be in `%LOCALAPPDATA%\BookSearch` (or pass `--config`). The message prints a sample. |
| `serverExe points to a missing file` | The path in `config.json` is wrong; check that `llama-server.exe` is directly inside `bin`. |
| `llama-server did not become ready` | The message includes the server's last log lines. Run `& "$dir\bin\llama-server.exe" -m "$dir\models\Qwen3-Embedding-0.6B-Q8_0.gguf" --embedding --pooling last --port 8089` to see the full error. |
| `Embedding has N dimensions, expected 1024` | The model file is not Qwen3-Embedding-0.6B. |
| Windows blocks `llama-server.exe` | Right-click the file, choose Properties, then tick Unblock; or `Get-ChildItem "$dir\bin" -Recurse \| Unblock-File`. |
| A test fails | Copy the failing test name and message; the project is still being verified on Windows. |

## Using `bookcli`

Run from the repository root with `dotnet run --project src/bookcli -- <command>` (the `--` separates dotnet's options from the tool's). The library commands (`init`, `add`, `resume`, `remove`, `list`, `stats`, `search`) accept `--data <folder>` (default `%LOCALAPPDATA%\BookSearch`, or `~/.local/share/BookSearch` on Linux). The commands that start a model server (`add`, `resume`, `search`, `embed`, `serve embed`) accept `--config <config.json>`.

| Command | What it does |
| --- | --- |
| `init` | Create or migrate `library.db` and print the schema and sqlite-vec versions |
| `extract <file> [--page N]` | Show title, author, language, chapters and a preview; or one PDF page. Rejected files print the reason |
| `chunk <file> [--show N] [--stats]` | Show the chunks a book would be split into, and size statistics |
| `add <files...>` | Hash, extract, chunk, embed and store books, with progress |
| `resume` | Finish interrupted or failed books |
| `remove <id>` | Delete a book with its chunks and vectors |
| `list`, `stats` | Show the library and index consistency |
| `search <question> [--top N] [--keyword-only]` | Hybrid search: ranked passages with highlights, book, chapter and page, per-step timings; says when the library doesn't cover the topic |
| `summarize <question> [--passages N]` | Search, then stream a short summary of each of the top results, one after another (or "doesn't cover the question"). Needs `llmModel`. Not offered when the library doesn't cover the topic |
| `eval [--keyword-only] [--use-library] [--threshold X]` | Index the fixture books, run the 40 evaluation questions, and write a dated report (recall@5, MRR, not-covered accuracy, a coverage-threshold sweep) to `eval-reports/`. `--keyword-only` needs no model |
| `bench [--summarize] [--runs N] [--use-library --data <folder>]` | Measure indexing speed, search p95 and (with `--summarize`) Summarize first-token and total time against the targets; `--use-library` times the searches on an existing library (the installed app's data) and skips the indexing measurement |
| `corpus [--out <folder>] [--count N] [--language en]` | Download a test library of public-domain books (default 200, text only, one every 2 s) from Project Gutenberg through gutendex.com, for the release measurements |
| `grounding [--count N]`, `grounding --score <sheet.csv>` | Write a CSV of summary sentences to check by hand against the passage each was written from, then score it (target: 90% supported) |
| `textdensity <pdf...>` | Show characters per page and whether a PDF would be rejected as image-only |
| `models list`, `download`, `verify`, `import <files>` | Choose the models for your memory, download them (resumable, checksum-verified), re-check them, or import files for an offline setup |
| `embed <text> [--query]`, `serve embed` | Try the embedding server |

`extract`, `chunk`, `init`, `list`, `stats` and `remove` work without any model, and `search` falls back to keyword results with a warning when no model is configured.

## Your own books

Put books you want to try in a `Books\` folder at the repository root; it is git-ignored so they are never committed. On Windows, add them with, for example:

```powershell
dotnet run --project src/bookcli -- add "Books\My Book.pdf"
```

Supported: EPUB, PDF with a text layer, TXT and MD. DRM-protected, password-protected and scanned (image-only) files are rejected with a reason.

## Documentation

- [Software Requirements](docs/Software%20Requirements.md): goals, requirements, architecture and decisions
- [Technical Design](docs/Technical%20Design.md): processes, schema, ingestion, search, summarize, security
- [Release validation](docs/Release%20Validation.md): how to measure the finished app against the requirements (200-book library, memory, privacy, crash safety, usability test), with tables for the results
- [Stories and Test Plan](docs/Stories%20and%20Test%20Plan.md): the implementation stories with unit and manual tests, a result note for each finished story, and implementation notes on what was learned building M0
