# Browser Import (Phase 6c) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Import bookmarks (Chrome/Edge/Brave/Arc on this PC, or a file) and passwords (through the browser's own export, or a CSV) into Zing. Offer it through a New Tab nudge and a Settings → Import section.

**Architecture:** The parsing and merge logic moves into `host/Zing.Core` as C#, held to the existing TS modules in `src/shared/import` by shared `*.vectors.json`. The host reads profiles and files, merges into `LibraryStore`, and reaches passwords through the existing `VaultStore.ImportAsync`. The renderer gets a Settings Import section. The `zing://home` page (C# `StartPage` plus its TS twin) gets the nudge card, whose buttons are `zing://import/…` navigations the host intercepts.

**Tech Stack:** C# / .NET 9 (Zing.Core, xUnit), TypeScript / React (vitest), Playwright e2e over CDP.

**Spec:** `docs/superpowers/specs/2026-11-10-browser-import-design.md`. Read it before any task.

## Global Constraints

- Tabs, LF line endings. Short lowercase commit messages ending with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- Never start `Zing.exe` outside the e2e harness. Run Playwright only through `bash C:/Users/USER/AppData/Local/Temp/z6/.superpowers/sdd/phase-6-plan/pw.sh <args>`, in the background. Never run `test:e2e:real-input`.
- Never read a real browser profile, the clipboard, or the real registry default in tests. Fixtures are synthetic and live under `src/shared/import/fixtures/`.
- Logic lives in `Zing.Core`, and Zing.Host stays thin. Every `await` resumes on the UI thread. Delayed or periodic work goes through `IWindowHost.Scheduler`, never `Task.Delay` or `FileSystemWatcher`.
- Errors are explicit with specific types. No silent fallbacks. A broken source shows as an error on its row, never an exception out of the section. Optional features never fail startup.
- Shared files with Phase 8 (`ipc.ts`, `channels.json`, `ChromeApi.cs`, `rows.ts`, `CustomRows.tsx`, `schema.ts`, `TestHooks.cs`, `Program.cs`, `package.json`, `PARITY.md`): append only, never reorder or reformat. Never touch `RendererLocation.cs`, `RendererView.cs`, `Zing.Host.csproj`, `.github/workflows/`, `scripts/pack.ps1`, `.config/`, `winget/`.
- e2e seams: `ZING_LOCALAPPDATA` (browser profile root) and `ZING_DEFAULT_BROWSER` (a ProgId string; the name is shared with Phase 8). Each throws in an e2e run when it's unset and the code needs it, modelled on `ZING_DOWNLOADS_DIR` in `PageFeatures.cs`.
- Checks after every task: `npm test`, `npm run test:host`, `npm run lint`, `npm run typecheck`. Run e2e (through pw.sh) at the end of each PR.

## PR layout

Four stacked PRs, one branch each. Each branch starts from the previous one and is rebased onto main after the earlier PR merges:

1. `phase-6c/core`: Tasks 1–6
2. `phase-6c/settings`: Tasks 7–9
3. `phase-6c/passwords`: Tasks 10–12
4. `phase-6c/nudge`: Tasks 13–15, then Task 16 (PARITY and hand check)

## Review Focus

1. **Bookmarks files that aren't UTF-8, or are huge.** Safari's and old IE's exports can be Windows-1252, and a 50 MB HTML file is possible. Read through the same rules as `VaultCsv.ReadFile` (10 MB cap, strict UTF-8). Fail with a named error the row shows, never mojibake. The tests go in Task 3.
2. **A profile whose `Bookmarks` file is missing or corrupt, or a Chromium browser that's running and mid-write.** Show that row with an error and keep the other rows. Read the file with `FileShare.ReadWrite` so a running Chrome doesn't block it. The tests go in Task 5.
3. **Re-import after the user edited the imported folder:** a link moved out, a sub-folder renamed, a link deleted. Re-import adds only URLs that aren't present and never removes or renames anything. A link the user deleted comes back. That's accepted: Zing can't tell "deleted" from "never had". The vectors go in Task 2.
4. **The Downloads folder already holds an older `Chrome Passwords.csv`** when the watch starts, or a non-password CSV arrives. Only a password CSV whose write time is after the watch started, and whose size is stable across two polls, is taken. The tests go in Task 10.
5. **A web page links to `zing://import/bookmarks?source=…`.** It's blocked unless the navigation comes from `zing://home`, the same rule as edit-shortcuts. The vectors go in Task 13.

---

### Task 1: `importedFrom` on library nodes

**Files:**
- Modify: `src/shared/library/library.ts` (add `importedFrom?: string` to `BookmarkLink` and `BookmarkFolder`, accept it in `parseNode`, add `holdsImports(library)`)
- Modify: `host/Zing.Core/Library.cs` (records gain `string? ImportedFrom = null` as the last positional member; `ParseNode` reads it; the converter writes it only when non-null; add `Library.HoldsImports`)
- Modify: `src/shared/library/library.vectors.json` (append cases), `host/Zing.Core.Tests/LibraryTests.cs`, `src/shared/library/library.test.ts`

**Interfaces:**
- Produces: TS `holdsImports(library: BrowserLibrary): boolean`, C# `Library.HoldsImports(BrowserLibrary) -> bool`. True if any node at any depth has `importedFrom`.
- Produces: the wire shape `{id,title,url,updatedAt,importedFrom?}` / `{id,title,children,updatedAt,importedFrom?}`. When `importedFrom` is absent, the shape is byte-identical to today's.

- [ ] **Step 1:** Append vectors:
  - `parseNode` cases: a link and a folder carrying `importedFrom: "Chrome (Personal)"` round-trip, and `importedFrom: 5` is rejected.
  - `holdsImports` cases: an empty library → false; an imported link nested two deep in a user folder → true; an imported folder on the bar → true.
- [ ] **Step 2:** Add vitest and xUnit tests that read these cases (`holdsImports` theory over `V.GetProperty("holdsImports")`). Run `npm test -- library` and `npm run test:host -- --filter LibraryTests`. Expect FAIL.
- [ ] **Step 3:** Implement in TS and C#. Existing `library.json` files with no `importedFrom` must load unchanged.
- [ ] **Step 4:** Run both suites. Expect PASS.
- [ ] **Step 5:** Commit `library: mark imported bookmarks with their source`.

### Task 2: re-import rules in `mergeImport` (TS)

**Files:**
- Modify: `src/shared/import/importResult.ts`
- Create: `src/shared/import/merge.vectors.json`
- Modify: `src/shared/import/import.test.ts`

**Interfaces:**
- Produces: `mergeImport(library: BrowserLibrary, result: BookmarkImport, source: string, now: number, newId: () => string): MergeOutcome`, where `interface MergeOutcome { library: BrowserLibrary; addedToBar: number; addedToFolder: number; folderTitle: string }`. `folderTitle` is `` `Imported from ${source}` ``.
- `source` is a display key: `"Chrome (Personal)"`, `"Arc"`, or a file name.

Rules, from the spec (the vectors pin each one):
- **Bar links:** add a link unless its URL is already anywhere among the bar's direct children. It gets `importedFrom: source`.
- **Bar folders:** match an existing bar folder with the same title and `importedFrom === source`. If one matches, recurse and add only links whose URL isn't already in that folder (same rule, one level down). Otherwise add the whole folder, marking every node.
- **`other`:** find a top-level folder with `importedFrom === source`, matched on `importedFrom` only, not title, so a renamed import folder still matches. If there is none, append a new folder titled `folderTitle` with every node marked. If there is one, merge into it recursively with the bar-folder rule.
- Never remove or rename an existing node. Count only links added.

- [ ] **Step 1:** Write `merge.vectors.json` with cases:
  - `first import`: bar and other both land, everything marked.
  - `same import twice adds nothing`: counts 0/0, library deep-equal.
  - `bar folder imported twice is not doubled`.
  - `two profiles of one browser keep separate folders`: sources "Chrome (Personal)" and "Chrome (Work)".
  - `renamed import folder still receives new links`.
  - `user-added link inside import folder survives re-import`.
  - `link moved out by the user comes back on re-import` (Review Focus 3).
  - `deleted import folder is recreated`.
  - `bar link already on bar from the user's own bookmarks is skipped`.

  Ids come from a counter `newId` (`"n1"`, `"n2"`…), with `now: 7`.
- [ ] **Step 2:** Point `import.test.ts` at the vectors; drop the old `mergeImport` tests that asserted replace-by-title. Run `npm test -- import`. Expect FAIL.
- [ ] **Step 3:** Implement. `newId` replaces the `crypto.randomUUID()` calls inside `link`/`folder` for imported nodes, so the vectors are deterministic.
- [ ] **Step 4:** `npm test -- import`. Expect PASS.
- [ ] **Step 5:** Commit `import: re-import adds only what's new and never removes`.

### Task 3: port the bookmark parsers to C#

**Files:**
- Create: `host/Zing.Core/Import/BookmarkImport.cs` (the `ImportedNode` records, `BookmarkImport(IReadOnlyList<ImportedNode> Bar, IReadOnlyList<ImportedNode> Other, IReadOnlyList<string> Skipped)`, `AcceptsBookmarkUrl`, `CountLinks`)
- Create: `host/Zing.Core/Import/ChromiumBookmarks.cs`, `NetscapeBookmarks.cs`, `ArcSidebar.cs`, `ImportFile.cs`
- Create: `src/shared/import/parsers.vectors.json`, and fixtures under `src/shared/import/fixtures/` (`chromium-bookmarks.json`, `netscape-chrome.html`, `netscape-safari.html`, `netscape-firefox.html`, `arc-sidebar.json`, `not-bookmarks.txt`)
- Create: `host/Zing.Core.Tests/Import/ParserTests.cs`
- Modify: `src/shared/import/import.test.ts` (read the same vectors and fixtures)

**Interfaces:**
- Produces:
  - `ChromiumBookmarks.Parse(string json) -> BookmarkImport`, `NetscapeBookmarks.Parse(string html) -> BookmarkImport`, `ArcSidebar.Parse(string json) -> BookmarkImport`.
  - Exceptions: `ChromiumBookmarksFormatException`, `ArcSidebarFormatException` (the names match the TS error classes).
  - `ImportFile.ReadText(string path) -> string`: 10 MB cap and strict UTF-8, reusing `VaultCsv.ReadFile`'s checks. Extract the shared check into `ImportFile`, and have `VaultCsv.ReadFile` call it.
  - `ImportFile.ParseBookmarks(string path) -> BookmarkImport`: sniffs the content, not the extension. Leading `{` with `"roots"` → Chromium; `{` with `"sidebar"` → Arc; `<!DOCTYPE NETSCAPE-Bookmark-file-1>` (case-insensitive, after whitespace) → Netscape. Anything else throws `UnknownBookmarksFileException(fileName)`.

- [ ] **Step 1:** Write the fixtures by hand, synthetic only. Safari's export has the "Favorites" bar as an `<H3>` without `PERSONAL_TOOLBAR_FOLDER`: check `netscapeBookmarks.ts` for how it detects the bar, and pin it. Write `parsers.vectors.json` as `{fixture, expected: BookmarkImport}` per fixture, plus error cases: invalid JSON, Arc with no spaces, a file over 10 MB, a Windows-1252 byte sequence. For the size and encoding cases, generate the file in a temp dir inside the test.
- [ ] **Step 2:** Make vitest assert the existing TS parsers against the vectors. Run `npm test -- import`. Expect PASS, since the TS is already correct. If a case fails, the vector is wrong: fix the vector, not the parser, unless the parser breaks the spec.
- [ ] **Step 3:** Write `ParserTests` (a theory over the vectors). Run `npm run test:host -- --filter ParserTests`. Expect FAIL (types missing).
- [ ] **Step 4:** Port the three parsers line for line from the TS. Use `System.Text.Json` for the JSON parsers. Keep the Netscape parser's tokenizer approach; don't switch to an HTML library.
- [ ] **Step 5:** Run the host tests. Expect PASS. Commit `import: port bookmark parsers to the host`.

### Task 4: `LibraryStore.Import`

**Files:**
- Create: `host/Zing.Core/Import/ImportMerge.cs` (C# port of Task 2's `mergeImport`)
- Modify: `host/Zing.Core/LibraryStore.cs` (add `Import`)
- Create: `host/Zing.Core.Tests/Import/MergeTests.cs` (theory over `merge.vectors.json`)

**Interfaces:**
- Consumes: Task 1 `ImportedFrom`, Task 2 vectors, Task 3 `BookmarkImport`.
- Produces: `ImportMerge.Merge(BrowserLibrary, BookmarkImport, string source, long now, Func<string> newId) -> MergeOutcome`, `sealed record MergeOutcome(BrowserLibrary Library, int AddedToBar, int AddedToFolder, string FolderTitle)`, and `LibraryStore.Import(BookmarkImport result, string source) -> MergeOutcome`. `Import` calls `Merge` with the store's own `_now`/`_newId`, then `Set(...)`, which persists through the existing writer and notifies listeners.

- [ ] **Step 1:** Write `MergeTests` against `merge.vectors.json`, plus one store test: `ImportSavesAndNotifies` (the save callback and the listener are each called once with the merged library). Run. Expect FAIL.
- [ ] **Step 2:** Implement.
- [ ] **Step 3:** Run. Expect PASS. Commit `library: import merges bookmarks into the store`.

### Task 5: find import sources on this PC

**Files:**
- Create: `host/Zing.Core/Import/ImportSources.cs`
- Create: `src/shared/import/sources.vectors.json` (`Local State` name cases ported from `profiles.node.ts` tests)
- Create: `host/Zing.Core.Tests/Import/ImportSourcesTests.cs`
- Delete: `src/shared/import/profiles.node.ts`, and its tests in `import.test.ts`. Move its `profileNames` cases into the vectors.

**Interfaces:**
- Produces:
  - `enum ImportBrowser { Chrome, Edge, Brave, Arc }`
  - `sealed record ImportSource(string Id, ImportBrowser Browser, string? Profile, string Display, string Path, int? Bookmarks, string? Error, bool LastUsed)`
    - `Id`: `"chrome:Default"`, `"edge:Profile 1"`, `"arc"`.
    - `Display` (the merge source key): `"Chrome (Personal)"`, or `"Arc"`. `Profile` is null for Arc.
    - `Path`: the `Bookmarks` file or `StorableSidebar.json`. Host-only; never sent to the renderer.
    - `Bookmarks`: the link count from parsing. On a read or parse failure it's null and `Error` holds the message.
    - `LastUsed`: from `Local State` `profile.last_used`.
  - `static IReadOnlyList<ImportSource> ImportSources.Find(string localAppData)`
  - `static string ImportSources.ProfileNames(...)`: ported as `IReadOnlyDictionary<string,string> ProfileNames(string localStateJson)`
  - `static BookmarkImport ImportSources.Read(ImportSource source)`
- Paths: the Chromium dirs from `profiles.node.ts` `USER_DATA_DIRS`, plus Arc at `Packages\TheBrowserCompany.Arc_*\LocalCache\Local\Arc\StorableSidebar.json` (first match). Skip `Guest Profile` and `System Profile`. Profile dirs are the `info_cache` keys whose folder has a `Bookmarks` file. A profile listed with no `Bookmarks` file gets no row; Chromium creates that file on first bookmark.
- Open files with `FileShare.ReadWrite | FileShare.Delete`.

- [ ] **Step 1:** Write tests over a temp-dir fake `LocalAppData`, copying in Task 3's fixtures:
  - `FindsEachChromiumProfileWithItsDisplayName`
  - `SkipsGuestAndSystemProfiles`
  - `FindsArcUnderItsPackageFolder`
  - `ACorruptBookmarksFileIsARowWithAnError`
  - `NoBrowsersFindsNothing`
  - `MarksTheLastUsedProfile`
  - `ReadsAFileAnotherProcessHoldsOpenForWriting` (open the file with `FileShare.ReadWrite` in the test, then `Find`)
  - plus the `ProfileNames` vectors

  Run. Expect FAIL.
- [ ] **Step 2:** Implement. Run. Expect PASS. Run `npm test` (the Node file is gone). Commit `import: find chrome, edge, brave and arc on this pc`.

### Task 6: default browser to source

**Files:**
- Create: `host/Zing.Core/Import/DefaultBrowserSource.cs`
- Create: `host/Zing.Core.Tests/Import/DefaultBrowserSourceTests.cs`

**Interfaces:**
- Produces:
  - `static ImportBrowser? DefaultBrowserSource.BrowserFor(string? progId)`: `ChromeHTML` → Chrome, `MSEdgeHTM` → Edge, `BraveHTML` → Brave, `Arc` ProgIds → Arc. Anything else, or null, → null.
  - `static ImportSource? DefaultBrowserSource.Pick(string? progId, IReadOnlyList<ImportSource> sources)`: that browser's `LastUsed` profile, or its first profile without an `Error`. Null when there's none.
- Arc's ProgId isn't known yet. Match ProgIds starting with `"Arc"` (case-insensitive), and record this as **to verify in Task 16** in a comment.
- **The registry read:** if `host/Zing.Core/DefaultBrowser.cs` (Phase 8) is on main when this task runs, call its raw-ProgId method. If it isn't, add `IDefaultBrowserReader { string? HttpsProgId(); }` in this file, with a Zing.Host implementation reading `HKCU\Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice\ProgId`, and an e2e implementation reading `ZING_DEFAULT_BROWSER`. Then tell the parent session (`local_2e2ccd91-0496-4bf4-9828-8777cbe3af6a`) that the follow-up to merge the two reads is due.

- [ ] **Step 1:** Write the tests:
  - `MapsKnownProgIds` (theory)
  - `ZingOrUnknownProgIdHasNoSource`
  - `PicksTheLastUsedProfile`
  - `SkipsAProfileWithAnError`
  - `DefaultBrowserNotInstalledHasNoSource`

  Run. Expect FAIL.
- [ ] **Step 2:** Implement. Run. Expect PASS. Commit `import: name the default browser's profile`.
- [ ] **Step 3 (end of PR 1):** Run the full checks, push `phase-6c/core`, and open PR "phase 6c: import core (parsers, merge, sources)". The body links the spec and lists the vectors.

### Task 7: import channels for bookmarks

**Files:**
- Modify: `src/shared/ipc.ts`, `src/shared/zingApi.ts`, `host/Zing.Core/Defaults/channels.json` (append)
- Create: `host/Zing.Core/Import/ImportApi.cs` (channel handlers, kept out of `ChromeApi.cs` except for the registration lines)
- Modify: `host/Zing.Core/ChromeApi.cs` (register), `host/Zing.Host/Program.cs` (pass the LocalAppData root; at most two lines)
- Create: `host/Zing.Core.Tests/Import/ImportApiTests.cs`

**Interfaces (TS, in `ipc.ts`; C# mirrors them):**
```ts
interface ImportSourceView { id: string; browser: 'chrome' | 'edge' | 'brave' | 'arc'; display: string; bookmarks?: number; error?: string; passwords: boolean }
interface BookmarkImportOutcome { folderTitle: string; addedToBar: number; addedToFolder: number; skipped: string[] }
'import:sources': { args: []; result: ImportSourceView[] }
'import:bookmarks': { args: [sourceId: string]; result: BookmarkImportOutcome }
'import:bookmarks-file': { args: [token: string]; result: BookmarkImportOutcome }
```
- `passwords` is true for Chrome, Edge and Brave, and false for Arc.
- For the file route, `display` (the merge source) is the picked file's name.
- Add `"bookmarks"` to `FileKinds` (`html`, `htm`, `json`), so `pickFiles(['bookmarks'])` works.
- An unknown source id throws `UnknownImportSourceException(id)`.
- The LocalAppData root comes from `ZING_LOCALAPPDATA` when set. In an e2e run with it unset, it throws. Otherwise it's `Environment.SpecialFolder.LocalApplicationData`.

- [ ] **Step 1:** Write `ImportApiTests` over a fake root:
  - `SourcesListsRowsWithoutPaths` (serialized JSON contains no `Path`)
  - `ImportingASourceMergesIntoTheLibrary`
  - `ImportingAPickedFileUsesItsNameAsTheSource`
  - `UnknownSourceThrows`

  Also add the channels to the contract test data. Run. Expect FAIL.
- [ ] **Step 2:** Implement. Run the checks. Expect PASS. Commit `import: channels for sources and bookmark import`.

### Task 8: Settings → Import section, and the privacy condition

**Files:**
- Modify: `src/shared/shell/shellState.ts`, `host/Zing.Core/PanelLayout.cs`, `src/renderer/src/settings/rows.ts`, `SettingsSheet.tsx` (category `import`, title "Import", icon: download-tray glyph in the style of `ICONS`)
- Modify: `rows.ts` `CustomId` (append `'import'`), `CustomRows.tsx` (append the case)
- Create: `src/renderer/src/settings/ImportSection.tsx`, `src/renderer/src/settings/importText.ts`, `importText.test.ts`
- Modify: `src/renderer/src/settings/privacy.ts` (add `{ id: 'imported', active: () => libraryHoldsImports, what: 'imported bookmarks', plural: true }` and `setLibraryHoldsImports(holds: boolean)`), `privacy.test.ts`
- Modify: wherever the Settings sheet already subscribes to `onLibraryState` (or add the subscription in `SettingsSheet.tsx`) to call `setLibraryHoldsImports(holdsImports(state.library))`

**Interfaces:**
- Consumes: Task 7 channels, Task 1 `holdsImports`.
- Produces:
  - `importText.ts`: `outcomeLine(o: BookmarkImportOutcome): string`, e.g. "Added 412 bookmarks to Imported from Chrome (Personal), 8 to the favourites bar; skipped 3." Singular and plural are handled. All zero gives "Nothing new to import."
  - `fileHelp: readonly { app: string; steps: string }[]` for Dia, Safari and Firefox. This text is checked in Task 16.

Layout:
- **On this PC:** one row per source. Shows `display` · "412 bookmarks", or the error in the error style, plus [Import bookmarks]. [Import passwords] appears when `passwords` is true; it's wired in Task 11 and hidden until then.
- **From a file:** [Bookmarks file…], [Passwords file…], then the `fileHelp` lines.
- **Result line:** under the row that ran. The skipped entries go in a `<details>`.

- [ ] **Step 1:** Write the tests:
  - `importText.test.ts`: zero, singular, plural, and bar-only outcomes.
  - `privacy.test.ts`: `imported bookmarks flip the banner and clearing them restores it`.
  - `rows.test.ts`: coverage still passes with the new custom row.
  - `VaultCsvTests` and `passwords.vectors.json`: append a Safari export (`Title,URL,Username,Password,Notes,OTPAuth`) and a Dia export (Chromium header). Each must import. If Safari's header isn't detected, add it as its own format in both TS and C#, rather than reading it as 1Password.

  Run `npm test`. Expect FAIL.
- [ ] **Step 2:** Implement. The [Passwords file…] button reuses the vault's existing pick → `importPasswordsCsv` path from `VaultSection.tsx`, extracted into a shared hook rather than copied, and shows `{added, merged, skipped}` the same way. Run `npm test`, lint, typecheck. Expect PASS.
- [ ] **Step 3:** Commit `settings: import section`.

### Task 9: e2e for Settings → Import

**Files:**
- Create: `e2e/import.spec.ts`
- Modify: `e2e/zing.ts` only if `launchZing` needs a `localAppData` option. Set `ZING_LOCALAPPDATA` to `<userData>/localappdata` by default, like downloads.

**Tests** (each builds its fake tree from `src/shared/import/fixtures/`):
- `Settings Import lists Chrome profiles and Arc found on this PC, and importing one adds its bar and an Imported folder`: assert through `zing.getLibrary()`, plus the privacy banner reading the warning.
- `importing the same profile twice adds nothing the second time`
- `a bookmarks file from Safari imports into a folder named after the file`: uses the `pick-files` test message.
- `a broken profile shows its error and the others still import`
- Screenshots `import-section-light.png` and `import-section-dark.png` into `docs/screenshots/`.

- [ ] **Step 1:** Write the spec. Build, then run it in the background through pw.sh: `bash .../pw.sh e2e/import.spec.ts`. Expect PASS.
- [ ] **Step 2:** Run the full checks and full e2e through pw.sh. Commit `e2e: settings import`. Push `phase-6c/settings` and open PR "phase 6c: settings import for bookmarks", based on `phase-6c/core`.

### Task 10: password export watch (core)

**Files:**
- Create: `host/Zing.Core/Import/PasswordExportWatch.cs`
- Create: `host/Zing.Core.Tests/Import/PasswordExportWatchTests.cs`

**Interfaces:**
- Produces: `sealed class PasswordExportWatch(IScheduler scheduler, string downloadsDir, Func<DateTimeOffset> now, Action<string> found)`. Use the existing scheduler interface `IWindowHost.Scheduler` exposes; check its name and use it.
  - `Start()` records the start time and polls every 1000 ms.
  - A candidate is a `*.csv` in `downloadsDir` (top level only) with `LastWriteTimeUtc >= start` whose length is the same at two consecutive polls, and whose header `VaultCsv.ParsePasswords` accepts. Catch `UnknownPasswordCsvException` and `CsvSyntaxException` and ignore that file for the rest of the watch.
  - On the first candidate, call `found(path)` once and stop.
  - `Stop()` cancels.
  - After 10 minutes with nothing found, stop and call `timedOut()`. That's a second constructor `Action`; the renderer then shows "Didn't see the file. [Choose the file myself…]".

- [ ] **Step 1:** Write tests with a fake scheduler and clock over a temp dir:
  - `AnOlderExportAlreadyThereIsIgnored`
  - `ANonPasswordCsvIsIgnored`
  - `AFileStillGrowingIsTakenOnlyOnceStable`
  - `TheFirstExportIsReportedOnceAndTheWatchStops`
  - `StopCancelsPolling`
  - `TimesOutAfterTenMinutes`

  Run. Expect FAIL.
- [ ] **Step 2:** Implement. Run. Expect PASS. Commit `import: watch downloads for a password export`.

### Task 11: launch the export page, channels, trash

**Files:**
- Create: `host/Zing.Core/Import/ExportPages.cs`
  - `static string ExportPages.Url(ImportBrowser b)`: `chrome://password-manager/settings`, `edge://wallet/passwords`, `brave://password-manager/settings`. Arc throws `NoPasswordExportException`.
  - `interface IBrowserLauncher { void Open(ImportBrowser browser, string url); }`
  - `interface IFileTrash { void Recycle(string path); }`
- Create: `host/Zing.Host/BrowserLauncher.cs`
  - Finds the exe through `HKLM`/`HKCU` `SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{chrome.exe|msedge.exe|brave.exe}`, then starts it with the URL as the argument.
  - In e2e it calls `TestHooks.RecordDialog("launch:" + browser, url)` instead.
  - Not found throws `BrowserNotInstalledException`.
- Create: `host/Zing.Host/FileTrash.cs`
  - `Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin)`.
  - In e2e it does `File.Delete` plus `TestHooks.RecordDialog("recycle", fileName)`, so a test never fills the real Recycle Bin.
- Modify: `ipc.ts`, `zingApi.ts`, `channels.json`, `ImportApi.cs`, `ChromeApi.cs` (register), `Program.cs` (construct the two implementations; at most two lines)

**Interfaces (append to `ipc.ts`):**
```ts
'import:watch-passwords': { args: [sourceId: string]; result: void }   // opens the export page, starts the watch
'import:cancel-watch': { args: []; result: void }
'import:recycle': { args: [token: string]; result: void }
// events
'import:password-export': { token: string; name: string }   // found: a files:pick-style grant
'import:watch-timed-out': {}
```
- The renderer imports the found file through the existing `vault:import-csv` (token). One watch at a time: a new `watch-passwords` stops the old one.
- **Verify first** (Step 0): on the owner's go-ahead in Task 16, or by reading Chromium's docs now, check whether each browser opens an internal URL passed on its command line. If one doesn't, `ExportPages.Url` returns null for it, and the renderer shows that browser's menu path instead (copy in `importText.ts`). Record the finding in this plan's Task 11 as a note when done.
  - **Note (2026-11-11, from reading only, no launch):** I found no source saying Chromium refuses a `chrome://`-style URL given as a plain argument to its exe. The failures reported go through ShellExecute/`start` or the registered protocol handler. Windows has no handler for `chrome://` and can drop the colon. So `BrowserLauncher` starts the exe directly with the URL as its argument, and every browser keeps its URL. The owner's hand check (Task 16) confirms this per browser.

- [ ] **Step 1:** Write xUnit tests for `ExportPages` and for `ImportApi` with fake launcher, trash and scheduler:
  - `WatchOpensTheExportPageAndReportsTheFoundFileAsAToken`
  - `ArcHasNoExportPage`
  - `RecycleResolvesTheToken`
  - `ANewWatchStopsTheOld`

  Run. Expect FAIL.
- [ ] **Step 2:** Implement. Run. Expect PASS. Commit `import: open the browser's password export and catch the file`.

### Task 12: renderer password flow, and its e2e

**Files:**
- Create: `src/renderer/src/settings/PasswordImportFlow.tsx`
- Modify: `ImportSection.tsx` (show [Import passwords]), `importText.ts` (+ tests)
- Modify: `e2e/import.spec.ts`

Flow states (pure reducer `passwordImportStep` in `importText.ts`, unit-tested):

`idle` → (vault off) `confirm-vault` → `waiting` → `importing` → `imported` → `offer-recycle` → `done`

There are also two side states:
- `timed-out`, offering [Choose the file myself…]
- `error(message)`

Rules:
- `confirm-vault` copy: "Importing passwords turns on Zing's password vault, which keeps them on this PC." [Turn on and continue] [Cancel]. It sets `passwordAutofill: true` through the existing settings channel.
- `waiting` copy: "In {Browser}, click **Export passwords**, confirm, and save. Zing will pick up the file." [Choose the file myself…] [Cancel].
- `offer-recycle` copy: "The exported file has every password in plain text." [Move it to the Recycle Bin] [Keep it].
- The file-picker route enters at `importing` and also ends with `offer-recycle`.

- [ ] **Step 1:** Write the reducer tests (every transition above, including cancel from `waiting` calling `cancelWatch`). Run `npm test`. Expect FAIL. Implement. Expect PASS.
- [ ] **Step 2:** Write the e2e tests:
  - `Import passwords opens Chrome's export page, imports the CSV that lands in Downloads, and recycles it on request`: `zing.dialogs()` holds the `launch:chrome` record with `chrome://password-manager/settings`. The test writes a fixture CSV into `<userData>/downloads`, then sends `advance-clock` twice. Assert the vault's entries and that the file is gone. Hello in e2e works as `vault.spec.ts` does it.
  - `with the vault off, Import passwords asks first and turns it on`
  - `a passwords file chosen by hand imports and offers to recycle it`

  Run through pw.sh. Expect PASS.
- [ ] **Step 3:** Run the full checks. Commit `settings: guided password import`. Push `phase-6c/passwords` and open its PR, based on `phase-6c/settings`.

### Task 13: nudge card on the start page (TS + C#)

**Files:**
- Modify: `src/shared/browser/startPage.ts`, `host/Zing.Core/StartPage.cs`, `src/shared/browser/startPage.vectors.json` (append), and both test files.

**Interfaces:**
- Produces:
  - `StartPageInput` gains `nudge?: ImportNudge` (C#: `ImportNudge? Nudge = null`).
  - `type ImportNudge = { kind: 'offer'; browser: string; profile?: string; bookmarks: number; sourceId: string; passwords: boolean } | { kind: 'generic' } | { kind: 'result'; browser: string; added: number; sourceId: string; passwords: boolean }`
  - With no nudge, the page HTML is byte-identical to today's (the existing vectors stay green).
- Card copy (exact):
  - **offer:** heading "Bring your stuff from {browser}?". Body: "{n} bookmarks and your favourites bar{ in "{profile}"}. Passwords need a quick export, and Zing walks you through it." When `passwords` is false, drop that last sentence. Buttons: [Import from {browser}] → `zing://import/bookmarks?source={id}`; [Other browser…] → `zing://import/settings`; [Not now] → `zing://import/dismiss`.
  - **generic:** "Bring your bookmarks from another browser?" [Choose…] → `zing://import/settings`, [Not now] → `zing://import/dismiss`.
  - **result:** "Imported {n} bookmarks." [Import passwords too] → `zing://import/passwords?source={id}` (only when `passwords`), and [Done] → `zing://import/dismiss`.
  - Escape every value with `EscapeHtml`; URL-encode the source id.
- Navigation: `ClassifyNavigation` gains `StartPageNavigation.Import(string Action, string? SourceId)` for host `import`, allowed only from the start page. Otherwise it returns `Block` (Review Focus 5). Add the TS twin.
- Styling: the card uses the page's existing palette variables. Its CSS is appended to `PageCss` inside a selector that only matches when the card is present, so the no-nudge output stays identical.

- [ ] **Step 1:** Append vectors:
  - Html cases: offer with a profile, offer with `passwords: false` (Arc), generic, result.
  - `classifyNavigation` cases: each `zing://import/...` from home → Import(action, source); the same from `https://evil.example` → Block.

  Run vitest and xUnit. Expect FAIL.
- [ ] **Step 2:** Implement TS first, regenerate the expected HTML in the vectors from the TS, then port to C#. Run both. Expect PASS. Commit `start page: import nudge card`.

### Task 14: nudge wiring

**Files:**
- Modify: `src/shared/settings/schema.ts` and `host/Zing.Core/Defaults/settings.json` (append `importNudgeDone: flag(false)`)
- Modify: `host/Zing.Core/ChromeApi.cs` (`InterceptNavigation` handles `StartPageNavigation.Import`), `ImportApi.cs` (nudge state), the start-page `Handle` call site (pass the nudge)
- Modify: `ipc.ts`, `zingApi.ts`, `channels.json` (append `'import:take-pending': { args: []; result: { action: 'passwords'; sourceId: string } | null }`)
- Modify: `ImportSection.tsx` (on mount, `takePending()`; if passwords, start the flow for that source)
- Create: `host/Zing.Core.Tests/Import/NudgeTests.cs`

**Interfaces:**
- Produces: `ImportApi.Nudge() -> ImportNudge?`
  - Null when `importNudgeDone` is set and there's no in-memory result.
  - The result card while one is held.
  - Otherwise an offer, from `DefaultBrowserSource.Pick(reader.HttpsProgId(), ImportSources.Find(root))`, or generic when that's null.
- Actions:
  - `bookmarks`: import the source, hold the result in memory, set `importNudgeDone`, reload every start page (`ForEachStartPage`).
  - `settings`: `Shell.OpenSettings("import")`.
  - `passwords`: set pending `{passwords, sourceId}`, clear the result, then `OpenSettings("import")`.
  - `dismiss`: set `importNudgeDone`, clear the result, reload the start pages.
- A failed import from the card puts `{kind:'generic'}` back with the error under it. Extend the offer type with `error?: string`, and add a vector for it.
- Finding sources reads disk, so cache the nudge per launch, refreshed on `bookmarks` and `dismiss`. That keeps it off every New Tab's critical path.

- [ ] **Step 1:** Write `NudgeTests` with fakes:
  - `OfferNamesTheDefaultBrowsersLastUsedProfile`
  - `ZingAsDefaultGivesTheGenericCard`
  - `DismissHidesItForGood`
  - `ImportShowsTheResultThenDoneHidesIt`
  - `PasswordsTooLeavesAPendingFlowForSettings`

  Run. Expect FAIL.
- [ ] **Step 2:** Implement. Run the checks. Expect PASS. Commit `import: new tab nudge`.

### Task 15: nudge e2e

**Files:** Modify: `e2e/import.spec.ts`

- `the first New Tab offers the default browser and Import brings its bookmarks in`: `ZING_DEFAULT_BROWSER=ChromeHTML`, fake profile. Assert the card text, click Import, assert the result card, the library, and the banner warning.
- `Not now hides the card on every later New Tab and after a restart`
- `Import passwords too opens Settings Import already waiting for Chrome's export`
- `with Zing as the default browser the card asks generically and Choose opens Settings Import`
- Screenshots `import-nudge-light.png`, `import-nudge-dark.png`.

- [ ] **Step 1:** Write the tests. Run through pw.sh. Expect PASS. Run the full e2e through pw.sh. Commit `e2e: new tab import nudge`. Push `phase-6c/nudge` and open its PR, based on `phase-6c/passwords`.

### Task 16: hand check and PARITY

- [ ] **Step 1:** Ask the owner for OK before reading anything real. With it:
  - Run Zing on a scratch `ZING_USER_DATA`, without e2e, as a hand check the owner asked for.
  - Import their real Chrome profile (and Arc, if installed: confirm the package path and Arc's ProgId, and fix Task 5/6 if they differ).
  - Run the password export with Chrome. Record whether `chrome.exe chrome://password-manager/settings` opened the page, and the same for Edge if installed.
  - Have them bring a Dia and/or Safari export from the Mac and check the `fileHelp` steps against the real menus.

  Nothing from their profiles goes into the repo or a report.
- [ ] **Step 2:** Fix anything found, with a failing test first. Update `PARITY.md` I1–I4 to Done, with the evidence under the table in the existing style: test names, e2e names, screenshots. Commit `parity: import rows`. Push to the nudge branch.
