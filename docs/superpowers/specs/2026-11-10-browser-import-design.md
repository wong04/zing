# Phase 6c: import from other browsers

Design for PARITY M6c, rows I1–I4 (issues #98–#101). Agreed with the owner on 2026-11-10.

## Why

Someone who switches to Zing wants their bookmarks and passwords in it without doing any homework. The owner moves between Chrome, Arc, Dia, Safari and Edge, so import has to cover all of them. It also has to work for people who have never exported anything. Success: on a new PC, the owner presses one button on the first New Tab and their bookmarks are there. Passwords follow a few clicks later, guided by Zing.

## Goals

- **Bookmarks** from Chrome, Edge and Brave profiles on this PC, read directly from disk. That includes the favourites bar.
- **Arc** (Windows, on this PC): pinned tabs, folders and Spaces from `StorableSidebar.json`. Each Space becomes a bookmark folder.
- **Passwords** from Chrome, Edge and Brave through a guided export (below). The user never has to find the export setting on their own.
- **From a file**, for browsers on another machine (Dia and Safari on the owner's Mac, Firefox, anything else):
  - bookmarks as Netscape HTML, a Chromium `Bookmarks` JSON file, or Arc's `StorableSidebar.json`
  - passwords as a Chrome, Edge, Brave, Dia, Safari, Bitwarden, 1Password or Firefox CSV
- **Where it lives:** a gentle nudge on the first New Tab, and a new **Import** section in Settings that works at any time.

## Non-goals

- History. The owner wants address-bar autofill from history some day, but Zing keeps no history, and that is its own opt-in feature. Not this phase.
- Cookies, open tabs or the last session (the owner said bookmarks are enough), extensions, passkeys, payment cards.
- Reading a browser's encrypted password store directly.

## Anti-goals

- **Anything that works like malware.** On Windows, Chrome 127+ protects saved passwords with app-bound encryption: only Chrome's own program can unlock them. The ways around it (injecting into Chrome, or running as admin to unwrap the key) are what password stealers do, and antivirus flags them. Zing never does this.
- **A wizard standing between the user and the browser on first run.** The owner chose the nudge over a one-screen or step-by-step wizard.
- **Leaving a plain-text password CSV behind silently.** Zing always offers to delete the CSV after importing it.
- **Scattering duplicates.** Importing twice must not double the user's bookmarks.

## The experience

### First New Tab: the nudge

Zing opens straight to browsing. The first New Tab (`zing://home`) shows one card:

> **Bring your stuff from Chrome?**
> 412 bookmarks and your favourites bar in "Personal". Passwords need a quick export, and Zing walks you through it.
> [Import from Chrome] [Other browser…] [Not now]

- **The browser named is the user's default browser** (owner's decision), read from the Windows https `UserChoice` ProgId: `ChromeHTML` → Chrome, `MSEdgeHTM` → Edge, `BraveHTML` → Brave, Arc's ProgId → Arc. The count comes from that browser's profile (if there are several, the last-used one, from `Local State`).
- *Builder's call:* if the default browser is Zing, is unrecognised, or has no profile on this PC, the card reads "Bring your bookmarks from another browser?" with [Choose…] [Not now]. [Choose…] opens Settings → Import.
- **Import from Chrome** imports that profile's bookmarks straight away, then turns the card into a result: "Imported 412 bookmarks. [Import passwords too] [Done]". **Import passwords too** opens Settings → Import and starts the password flow there for that profile. *Builder's call:* the multi-step password flow is built once, in the renderer, and is never hosted on `zing://home`. That page has no bridge and is mirrored in `startPage.ts` and its vectors. When the source is Arc, there's no **Import passwords too** button: Arc has no export page here. Its passwords come in as a file.
- **Other browser…** opens Settings → Import.
- The card shows on every New Tab until the user presses **Not now** or finishes an import (as played back to the owner). After that it never comes back. Closing the tab doesn't count. That's a settings key (`importNudgeDone`). It holds no user data, so it doesn't flip the privacy banner.
- The card's buttons are `zing://import/…` navigations that `ClassifyNavigation` intercepts, like `zing://edit-shortcuts` does today. They're honoured only from `zing://home`, so no website can trigger them.

### Settings → Import

This is a new Settings section, the same flow at any time. It has three parts:

1. **On this PC**: one row per detected source. Each Chrome, Edge and Brave profile is a row ("Chrome · Personal · 412 bookmarks"), plus Arc if it's installed. Each row has [Import bookmarks]; the Chromium rows also have [Import passwords].
2. **From a file**: [Bookmarks file…] and [Passwords file…]. Under them, a short line on how to get the file from Dia, Safari or Firefox (the menu path for each, as text). *Builder's call:* this text lives in the renderer and is checked against the real apps during the hand check.
3. **Result line** after each import: "Added 412 bookmarks to *Imported from Chrome*, 8 to the favourites bar; skipped 3 (javascript: links)." The skipped entries can be expanded. They are never dropped silently, as the existing parsers already promise.

### Passwords: the guided export

The owner approved this:

1. The user presses **Import passwords** (from the card or a Chromium row in Settings).
2. If the vault is off, Zing says so in one line: importing turns on Zing's password vault, and the vault stores data. The user confirms. This is the same switch as Settings → passwords; the existing vault rules (Windows Hello, privacy banner) apply unchanged.
3. Zing opens that browser's own export page in that browser: `chrome://password-manager/settings`, `edge://wallet/passwords`, or `brave://password-manager/settings`. Zing then shows: "In Chrome, click **Export passwords**, confirm, and save. Zing will pick up the file."
4. Zing watches the Downloads folder (`WindowsDownloads.Directory()`) for a new `.csv` whose header parses as a password CSV. It only watches while the waiting message is showing, which has a [Cancel] and a [Choose the file myself…] fallback for people who saved elsewhere.
5. When the file appears, Zing imports it through the existing `VaultStore.ImportAsync`: one Windows Hello prompt, deduped on (origin, username), with history kept on changed passwords. Then it shows "Imported 57 passwords (3 already saved, 2 skipped)".
6. Zing then asks: "The exported file has every password in plain text. [Move it to the Recycle Bin] [Keep it]". The Recycle Bin is used rather than a hard delete, so the user can still recover the file.

**To verify first (builder):** whether each browser opens its internal page when launched as `chrome.exe <url>`. Some Chromium builds refuse internal URLs from the command line. If one does, step 3 opens the browser plainly and the message shows the exact menu path instead. Record what each browser does in the plan.

The file-picker route (Settings → From a file → Passwords file…) is the same flow from step 5 on, including the offer to delete.

### Privacy banner

Imported data counts as stored (PLAN 6c), so the banner flips:

- Passwords already flip it through the vault condition.
- Bookmarks: register a `StoresDataCondition` with id `imported` and `what: 'imported bookmarks'` (the hook in `privacy.ts` already anticipates it). It is active while the library contains a folder created by import or a favourites-bar item added by import.
- *Builder's call on how to know that:* imported nodes carry an `importedFrom` source field in the library model, added to `library.ts`, `LibraryStore` and the vectors. When the user deletes every imported item, the banner returns to traceless. This keeps the banner honest without a flag that can go stale.

## How it's built (decided)

- **Logic in `Zing.Core`, in C#.** The library, the vault and their CSV import already live in the host, so the bookmark parsers (`chromiumBookmarks`, `netscapeBookmarks`, `arcSidebar`) and `mergeImport` are ported to C#. Each TS module keeps its tests, and new `*.vectors.json` files hold both sides to the same behaviour (the pattern from `passwords.vectors.json`, `library.vectors.json`). This updates PLAN 6c's "parsers stay in TS". The codebase moved logic to C# after that line was written, and the vault's CSV import was already ported.
- **Profile detection in C#.** `profiles.node.ts` is Electron-era Node code with no callers. Port its logic (`Local State` names, skip Guest/System) to `Zing.Core`. Delete the Node file when the port lands, keeping `profileNames` vectors.
- **Arc on Windows** is a packaged app. Look for `%LOCALAPPDATA%\Packages\TheBrowserCompany.Arc_*\LocalCache\Local\Arc\StorableSidebar.json`. Sources disagree on the path, so confirm it on the owner's PC during the hand check (with their OK).
- **Bridge:** new channels for listing sources, importing bookmarks from a source or a picked file token, starting and cancelling the password watch, and trashing the CSV. Paths never reach the renderer, only source ids and file tokens, as `files:pick` does today. Channel names go in `channels.json`.
- **Saving:** `LibraryStore` gets an import entry point that applies the merge and persists through the existing debounced writer.
- **Re-import never destroys anything and never doubles anything.** *Builder's call:* this changes `mergeImport`, which today replaces the import folder by title and re-adds bar folders. Change it in TS and C# together, under the shared vectors. The rules:
  - **Source key is per profile or per file:** "Chrome (Personal)", "Edge (Work)", "Arc", or the picked file's name ("Imported from bookmarks_11_10_26.html"). Two profiles of one browser never collide.
  - **Favourites bar:** a link is added unless its URL is already on the bar. A folder is matched to an existing bar folder with the same title and the same `importedFrom`, and only its missing links are added, recursively.
  - **Everything else** goes into the "Imported from <source>" folder. If one with the same `importedFrom` exists, only links whose URL isn't already in it are added, into the matching sub-folder by title. Nothing the user moved, renamed or added is removed or overwritten. If the user deleted the folder, a re-import creates it fresh.
- **The nudge** is rendered by `StartPage.cs` (and its TS twin `startPage.ts`, held together by `startPage.vectors.json`). It only shows the card and its bookmark result; everything else hands off to Settings → Import.
- **Test seams:** `ZING_LOCALAPPDATA` (where to look for browser profiles) and `ZING_DEFAULT_BROWSER` (stands in for the registry; the name is shared with Phase 8). Both are modelled on `ZING_DOWNLOADS_DIR`, and both throw in e2e if unset. Launching the other browser goes through a hook that e2e records instead of running, the way `Shell.OpenExternal` already refuses in e2e.

## Constraints

- Owner rules for agents still hold: Zing runs only under the e2e harness, except for the owner's hand check. No clipboard, no real browser profile reads without the owner's OK, nothing from real profiles goes into the repo.
- Optional features never fail startup. A missing or corrupt browser profile shows as a row with an error message; it never throws out of the import section.
- Errors are explicit, with specific types (e.g. `ArcSidebarFormatException`, `UnknownPasswordCsvException`), shown in the result line.
- Phase 8 (packaging) runs in parallel. This work stays out of build, release and installer files, and keeps any `Program.cs` change to wiring a line or two.

## Coordination with Phase 8 (packaging)

Phase 8 is building in parallel on `phase-8/packaging`. Its plan is `docs/superpowers/plans/2026-11-10-phase-8-packaging.md`.

- **Default browser:** Phase 8 owns `host/Zing.Core/DefaultBrowser.cs` and the registry read of the https `UserChoice` ProgId, and exposes the raw ProgId through a small public method. Import keeps its own ProgId-to-browser mapping (`ChromeHTML` → Chrome and so on) in an import file. It reads the ProgId through Phase 8's method if that has landed on main. If it hasn't, import reads the ProgId in its own file, and a small follow-up merges the two reads. Both phases use the e2e seam name `ZING_DEFAULT_BROWSER`.
- **Two first-run prompts (owner's decision):** the import nudge comes first. It has a fresh profile's first run to itself. Phase 8 holds its default-browser offer until the second launch. Import needs no code for this.
- **Shared files:** both phases edit `ipc.ts`, `channels.json`, `ChromeApi.cs`, `rows.ts`, `CustomRows.tsx`, `schema.ts`, `TestHooks.cs`, `Program.cs`, `package.json` and `PARITY.md`. Import only appends to them; it never reorders or reformats existing entries. Import stays its own Settings section. Whichever PR merges second rebases on main and resolves the conflicts.
- **Off limits:** `RendererLocation.cs`, `RendererView.cs`, `Zing.Host.csproj`, `.github/workflows/`, `scripts/pack.ps1`, `.config/`, `winget/`.
- Any change to these areas is passed on to Phase 8 through the parent session, never directly.

## Testing and done

- **xUnit:** each parser and the merge, on fixtures in the repo (synthetic, never copied from real profiles), plus the shared vectors. Also profile detection over a fake `LocalAppData` tree, default-browser ProgId mapping, and the Downloads watch picking only a password CSV that arrives after the start.
- **vitest:** the TS parsers keep their tests and read the same vectors. The privacy condition and the Import section rows are covered too.
- **e2e:**
  - The first New Tab shows the nudge naming the fake default browser. Import from it adds bookmarks and the favourites bar, and the banner flips.
  - Not now hides the card for good.
  - Settings → Import from a bookmarks HTML file and from a password CSV: the password import uses the file picker hook, and the offer to delete works.
  - The guided export records the browser launch with the right URL and imports a CSV dropped into the fake Downloads.
- **Hand check by the owner:** a real import from their Chrome (and Arc if installed) on this PC, plus a Dia or Safari export brought from the Mac. Screenshots light and dark for the card and the Import section.
- **PARITY:** I1–I4 updated with the evidence.

## Left to the builder

- Exact copy text, icons and layout of the card and the section, within the existing settings and start-page styles.
- How the PRs split (suggested: core port and vectors; Settings Import with file and on-PC bookmarks; guided password export; the New Tab nudge).
- Channel names and payload shapes.
