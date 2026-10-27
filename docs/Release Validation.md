# Release validation (ST-22, the M2 exit)

How to measure the app against the requirements, with a place to write the results. Everything here is done by hand on a real PC; nothing can be measured in CI. Fill in the tables and copy the final numbers into `docs/Software Requirements.md` (the NFR rows) when done. Log every miss in "Gaps" with what will be done about it.

**Machines.** The targets are for the **minimum spec: 8 GB RAM, no GPU, CPU only**. Also run the quick checks on a stronger PC, but the table below is for the minimum machine. Use a machine (or VM) with a standard, non-admin user.

| | Minimum-spec machine | Notes |
| --- | --- | --- |
| CPU, cores | | |
| RAM | | |
| Windows version | | |
| App version / installer | | |
| llama.cpp build | b11430 (the manifest pin) | |

## 1. Fresh install to first search (manual E2E 1)

1. On a clean user profile, run the installer (`scripts\build-release.ps1`, then `Docent-Setup-<version>.exe`). Check the checksum with `certutil -hashfile <installer> SHA256` against the `.sha256` file.
2. Start the clock when the installer starts. Go through the wizard, **excluding** the download time (note it separately), add one book, ask a question.
3. Stop the clock at the first search results. Target: **15 minutes or less** excluding downloads.

## 2. The 200-book library (NFR-4, NFR-2)

```powershell
dotnet src\bookcli\bin\Release\net8.0\bookcli.dll corpus --out bench-library --count 200     # public domain, Project Gutenberg, text only (~0.5 MB each)
```

1. Open the app, **Library**, drag the whole `bench-library` folder in. Note the time from the drop to the last book being Ready (leave the PC alone; the worker runs at below-normal priority).
2. Run `bookcli stats --data %LOCALAPPDATA%\BookSearch` for the totals. Indexing speed is `chunks / seconds`; one average 100k-word book is about 260 chunks. Target: **one average book in 3 minutes or less** (about 1.5 chunks/s or more).
3. Close the app. Time the searches on that library:

```powershell
dotnet src\bookcli\bin\Release\net8.0\bookcli.dll bench --use-library --data "$env:LOCALAPPDATA\BookSearch" --config "$env:LOCALAPPDATA\BookSearch\config.json" --runs 3
```
   The report (`eval-reports\bench-*.md`) lists p50 and p95. Target: **p95 within 10 s, typical 2 s or less**. The integration test `Bench_200Books_SearchP95Under10s` does the same check: set `BOOKSEARCH_BENCH_DATA` and `BOOKSEARCH_M0_CONFIG` and run `dotnet test --filter Bench_200Books`.
4. Summarize timing on the minimum machine: `bookcli bench --summarize` (fixture books). Targets: first token **15 s or less**, whole summary (about 120 words) **30 s or less**, once the model is loaded.

## 3. Memory and responsiveness (NFR-6, NFR-1)

Start the app, then in a second PowerShell window:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\measure-app.ps1 -Minutes 3 -Label idle-start       # right after start, library open
powershell -ExecutionPolicy Bypass -File scripts\measure-app.ps1 -Minutes 3 -Label idle-after       # after 12 minutes with no use: the model servers should be gone
powershell -ExecutionPolicy Bypass -File scripts\measure-app.ps1 -Minutes 10 -Label all-at-once     # see below
```

**All at once (manual E2E 6), 8 GB machine:** add 5 books so the worker is indexing, turn automatic summaries on, then keep asking questions while it runs. Watch for: the window stays responsive (typing, scrolling, switching pages), searches still return, a summary completes, nothing is reported out of memory, and the total peak.

Target: **idle app 300 MB or less** with the models unloaded (add the model servers when they are loaded, expected about 1 GB for the embedding model plus about 3 GB for the 4B language model, which is why the 8 GB machine uses the 2B model).

## 4. Privacy and offline (NFR-9, NFR-10, NFR-16)

1. After setup (models and llama.cpp installed), **turn off networking** (disable the adapter). Add a book, search, summarize, open History, change Settings, **Delete all data (keep models)**: everything works.
2. With networking on, start the app and run for 10 minutes of normal use:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\check-network.ps1 -Minutes 10
```
   Expected: *No connection to anything but this PC*. (The first-run downloads are the only allowed exception.) Repeat once with **Wireshark** capturing on the active adapter, filtered to the app's processes' ports or with the PC otherwise idle: no outbound traffic from the app.
3. Open `%LOCALAPPDATA%\BookSearch\logs` after the session: no book text and no questions in any file.

## 5. Crash safety (NFR-7, manual E2E 4)

1. Add 3 books. While one is being indexed, **power off** (or end the app and worker in Task Manager, or pull the plug on a VM).
2. Start again. Indexing continues; no book is duplicated; the library opens.
3. Run `bookcli stats --data %LOCALAPPDATA%\BookSearch`: the chunk, full-text and vector counts must be equal.

## 6. Usability test with 3 people (manual E2E 5)

Pick three people who have not seen the app and read books. Give each the installed app (models already installed), with 3 books on the desktop. Say only: "This app lets you ask questions about your own books." Do not help. Note where they hesitate; time each task; stop a task after 3 minutes and mark it failed.

| Task | Done unaided? (P1 / P2 / P3) | Time | What they said or where they stalled |
| --- | --- | --- | --- |
| 1. Add the three books and see them become ready | | | |
| 2. Ask a question about one book | | | |
| 3. Open a result and read the passage in the Reader, then go back | | | |
| 4. Read the summary and open one of its citations | | | |
| 5. Ask a follow-up question ("and ...?") | | | |
| 6. Find the question again in History | | | |
| 7. Turn automatic summaries off in Settings | | | |

Exit: all three complete tasks 1 to 4 unaided.

## Results

| Measure | Target | Result | Pass? |
| --- | --- | --- | --- |
| Fresh install to first search (excluding download) | 15 min or less | | |
| Installer size | 200 MB or less | | |
| Indexing speed, 100k-word book | 3 min or less | | |
| 200 books indexed in | (no target; note it) | | |
| Search p95, 200 books | 10 s or less (typical 2 s) | | |
| Summarize first token (model loaded) | 15 s or less | | |
| Summarize whole summary | 30 s or less | | |
| Idle memory, app only, models unloaded | 300 MB or less | | |
| Peak memory, indexing + search + summary, 8 GB | no out-of-memory, UI responsive | | |
| Outbound connections in 10 min of use | none | | |
| Works with networking off | yes | | |
| Resume after power-off, no duplicates | yes | | |
| Usability, tasks 1 to 4 unaided by 3 of 3 | yes | | |

## Gaps

| What missed | Measured | Decision (fix, accept, or move the target) |
| --- | --- | --- |
| | | |
