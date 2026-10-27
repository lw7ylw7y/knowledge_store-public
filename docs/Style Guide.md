# Docent: style guide

Status: **Draft: proposed, not yet agreed.** It writes down the look the app already has (WinUI 3 Fluent defaults, theme resources only) and adds a few rules so new screens match. The visual direction and reference apps are yours to set (the requirements' User experience section says "not yet chosen"). React to the mock-ups in `docs/mockups/` and the sample in section 9, and I will adjust.
Source: the built pages in `src/BookSearch.App/Pages/*.xaml` (values counted from them) and `docs/Software Requirements.md` (NFR-22).
Follows from: `docs/UX Design.md` (screens and patterns). Screen specs refer to this file instead of repeating it.

## 1. Direction

**Calm and text-first, like a good e-reader**: plain surfaces, one accent colour, generous margins around reading text, and compact rows where lists get long. It should feel like a quiet Windows 11 app (Fluent), not a dashboard: no illustrations, no gradients, no custom chrome.

## 2. Type scale

Use only these WinUI text styles; never set a size by hand except the Reader's text.

| Name | WinUI style | Used for |
| --- | --- | --- |
| Page title | `TitleTextBlockStyle` | One per page ("Library", "Settings", the book title in the Reader) |
| Section heading | `SubtitleTextBlockStyle` | Card headings in Settings, wizard tier names, empty-state headings, the chapter title in the Reader |
| Strong body | `BodyStrongTextBlockStyle` | Titles of list items (a book, a question, a result), field labels |
| Body | default | Text, summaries, notes |
| Secondary | default + `TextFillColorSecondaryBrush` | Authors, status lines, times, explanations under a control |
| Reading text | 16 px, line height 26 (fixed, Reader only) | The chapter text; at most 820 px wide, left-aligned |

Rules: sentence case everywhere; no all-caps; no italics except where a book's own text has them.

## 3. Spacing

**Base unit 4 px.** Values in use and what they are for:

| Value | Use |
| --- | --- |
| 2 | Lines of one item (title / author / status) |
| 4 | Related items inside a row |
| 8 | Between controls in a group; button groups; between rows' parts |
| 12 | Between page sections (title, notices, list); chips' vertical rhythm |
| 16 | Between cards; inside a card; between wizard fields |
| 24 | Between questions in the Ask conversation |
| 32 | Page padding (left, right, top); 48 / 32 on the wizard and start-up error |
| 640 | Maximum width of a form page (Settings, wizard, start-up error) |
| 820 | Maximum width of reading text |
| 1200 | Maximum width of Ask and History |

Do not introduce other values. A control group is separated from the next by the next step up (8 → 12 → 16).

## 4. Colour and theme

Theme resources only. A test scans the XAML and fails on a fixed colour, so both themes and high contrast are handled by the platform.

| Role | Resource | Use |
| --- | --- | --- |
| Text | default foreground | Primary text |
| Secondary text | `TextFillColorSecondaryBrush` | Authors, status, notes, times (25 uses today) |
| Accent | `AccentFillColorDefaultBrush` / accent button style | The primary button; underlined rare words in the Reader |
| Accent text | `AccentTextFillColorPrimaryBrush` | Tag lines, the "Recommended" badge: always with the word, never alone |
| Surface | `CardBackgroundFillColorDefaultBrush` + `CardStrokeColorDefaultBrush` | Cards (Settings groups, restored summaries) |
| Chip | `ControlFillColorSecondaryBrush` | Question bubbles and chips |
| Highlight | `SystemFillColorCautionBackgroundBrush` | The opened passage in the Reader |
| Critical | `SystemFillColorCriticalBrush` | Not used for messages any more: errors are InfoBars (decided 2026-10-13) |

**Meaning never rests on colour alone:** status is always a word ("Ready", "Indexing 42%"); errors and warnings are InfoBars with an icon and text; matched words are semibold and underlined, not only coloured; a tick or selection state is announced.

## 5. Density

| View kind | Rule | Examples |
| --- | --- | --- |
| Reading | Roomy: 16 px text, 26 px line height, an 820 px column centred in the window, 4 px between paragraphs | Reader |
| List | Compact: 8 px above and below each row, up to five short lines per item | Library, History, Ask results |
| Form | Comfortable: 16 px between fields, each group in a card, notes in secondary text under the control | Settings, wizard |

## 6. Hierarchy rules

1. **One primary action per view**, drawn with the accent button style. Everything else is a standard button, a link or an icon button.
2. **State decides the accent where two candidates compete.** On Library, the accent **Add books** button is in the empty state only; with books, only **Continue** (a book in progress) is the accent and **Read** is standard, so a long list is not a column of accent buttons (decided 2026-10-13). A list row may carry at most one accent button, and only for the one thing in progress.
3. **Quiet by default.** A list row shows its content and one main action; secondary actions go in a "⋯" menu, state that is normal ("Ready") is not written out, and a mode (selection) shows its controls only while it is on.
4. **Group by proximity first, by a card second.** Cards only for settings groups and restored text; do not nest cards.
5. **Secondary information is grey text, not smaller text.** The same size, the secondary brush.
6. **Destructive actions are last and set apart** (Delete all data is at the bottom of Settings, alone); they never take the accent.
7. **Page title at the top left; page-level actions at the top right** (Library, Ask: New thread); the main input is at the bottom (Ask).
8. Buttons that fill the same slot in different rows (Read, Continue, Open file) have the same minimum width.
9. Disabled controls stay visible and are explained by a note when the reason is not obvious.

## 7. Components and icons

| Need | Control | Notes |
| --- | --- | --- |
| Page commands | `CommandBar`, icon and label right, automatic overflow to "…"; its left `Content` holds a find box | Library header; use it when a page has three or more commands |
| Secondary actions of a list row | One "⋯" button opening a `MenuFlyout` (also the row's context menu) | Library rows: Tags…, Remove |
| Choosing several | A **Select** mode: tick boxes appear only while it is on, the header shows the count and the action | Library tagging |
| Navigate | `NavigationView`, left, compact, **icons only with tooltips** (decided 2026-10-13) | Icons: Find (Ask), Clock (History), Library (Library), Setting (Settings) |
| Notice | `InfoBar` | Severities: Informational, Warning, Error; always closable |
| Confirm | `ContentDialog`, primary button repeats the verb, secondary is Cancel | |
| Choice of one | `ComboBox` for 3+, `ToggleSwitch` for on/off | Header names the setting |
| Number | `NumberBox` with inline spin buttons, min and max set | |
| List | `ListView` (selectable or clickable) or `ItemsControl` (static) | |
| Fold away | `Expander` | History threads; results of a question |
| Icon button | `SymbolIcon` in a transparent button with a tooltip and a spoken name | Edit, Refresh, Cancel (close) |
| Text with a state | Chip: rounded (16) background with secondary fill | "Searched as…", the filter |

Icons come from the Segoe Fluent set through `SymbolIcon`; an icon-only button must have a tooltip and a spoken name. No custom icon art inside the app other than the app icon.

## 8. Motion

The app adds no animation of its own; the platform's control transitions apply. With "reduce motion" on nothing changes. Progress is shown with progress bars, not animation for its own sake.

## 9. Sample: the Ask page in this style (react to this)

```text
┌─────────────────────────────────────────────────────────────────────────────────────┐
│ ⌕ Ask                                                                 [ New thread ] │   Title (28 px), action top right
│ ⌕                                                                                    │
│ ◷   ╭──────────────────────────────────────────────────────────────────╮ ✎ ⟳         │   question: chip surface,
│ ▤   │ Why did Elizabeth refuse the first proposal?                      │             │   strong text, icons at right
│ ⚙   ╰──────────────────────────────────────────────────────────────────╯             │
│     Searched as: Why did Elizabeth Bennet refuse Mr Collins's proposal?   ( × )       │   chip, secondary
│     ▾ Results                                                                        │
│     Pride and Prejudice                                                              │   strong body
│     Jane Austen · Chapter XIX · pp. 112–115                                          │   secondary
│     Elizabeth refuses him because she does not respect him; she says she could      │   body: the summary
│     not make him happy and he could not make her so.                                 │
│     … he was not the man to be kept back by a refusal. "Do not consider me …"        │   excerpt: secondary,
│                                                                                      │   rare words underlined
│     ────────────────────────────────────────────────────────────────────────────    │
│     [ Filter… ]  Searching in: novels   [ Clear ]                                    │   secondary text
│     [ Ask a question                                              ] [ Search* ]      │   * the one accent button
└─────────────────────────────────────────────────────────────────────────────────────┘
```

Proposed reading of the sample: one accent button on the page (**Search**); question and chips use the chip surface; the summary is body text, the excerpt is secondary so the summary is read first; the navigation bar shows icons only. The real drawn pages are in `docs/mockups/` (`ask.png`, `library.png`, `reader.png`, `history.png`, `settings.png`, `wizard-models.png`, dark and 800 × 600 variants).

## 10. Checks

- [ ] No fixed colours and no fixed sizes except the Reader's text (a test covers colours).
- [ ] One accent button per view, by the state rule in section 6.
- [ ] Every status, error and highlight has a word or shape as well as colour.
- [ ] Spacing uses only the values in section 3.
- [ ] Icon-only buttons have a tooltip and a spoken name.
- [ ] Looks right at 200 % text, at the smallest window, in dark and high-contrast themes (needs screenshots from Windows).
