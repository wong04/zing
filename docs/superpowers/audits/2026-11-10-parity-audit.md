# Parity audit, first pass (2026-11-10)

The first half of Phase 9 in [docs/PLAN.md](../../PLAN.md): walk `PARITY.md` and find what each row still needs to close. Phase 9 defines closed as: "Every row is closed with a linked test or screenshot, or listed in a 'Known differences on Windows' section in the README."

- **Read against:** `origin/main` at `d12250b` (PR #166, Phase 6b sync UX).
- **Read-only.** This report changes nothing else. The `PARITY.md` and README edits wait until Phase 6c and Phase 8 merge, because both are editing `PARITY.md` now.
- **Method.** Every row that isn't Done was checked against the code, the tests, the docs and its GitHub issue. 20 of the 90 Done rows were spot-checked, spread across the M tables. No test was run. Evidence was confirmed by reading the files and finding the named tests.

Categories:

- **(a)** Done in fact. PARITY is stale.
- **(b)** Needs a little work.
- **(c)** Needs real work.
- **(d)** A candidate for "Known differences on Windows" in the README.
- **(e)** In progress elsewhere. Not assessed.

## Summary

| Row | Status now | Category | Next step |
|---|---|---|---|
| B34 Shared UI components | Partial | (a) | Mark Done. Cite the unit tests in `src/renderer/src/components/` and the light + dark tool screenshots in `docs/screenshots/phase-6/`. |
| S2 Frameless window chrome | Partial | (b) | Add a real-input drag test: the strip's empty space moves the window, a tab drag doesn't. Mica is already in the README. |
| L12 DownloadPolicy | Partial | (b) | Add two e2e cases showing the engine decides as Krackn's rule does. Then mark Done, or wire the port in. |
| B29 Floating tool panels | Partial | (b) | Add one e2e: three panels open, a fourth is refused, minimise to the sidebar and restore. Add a minimised-panel screenshot. |
| TH4 Mica tint + contrast themes | Partial | (b) | Add a forced-colours screenshot pair. Mark Done and point Mica at the README. |
| V4 Encrypted password sync | Partial | (b) | Owner's hand check against the deployed server. Open an issue for the row. |
| V5 Restore on a new PC | Partial | (b) | Same hand check as V4. Open an issue. |
| P7 Dependency updates | Open | (b) | Add `.github/dependabot.yml` (npm, NuGet, Actions). Fix the issue text, which still says Electron. |
| B11 Tracker + ad blocking | Partial | (c) | The `blockTrackers` switch does nothing. Wire it in, block Krackn's 48 domains testably, and leave ad blocking to uBO Lite (Phase 11). |
| S10 Performance budget suite | Open | (c) | Build `test:perf` against WebView2. The budgets still name Electron's `app.getAppMetrics()`. |
| P6 Web-compat smoke tests | Open | (c) | A scheduled workflow against live Google and YouTube. Passkeys stay a hand check. |
| L11 FaviconChoice | Partial | (d) | Edge's engine picks the page's icon. Add one e2e for the per-page rule, delete the unused TS port, and add a README line. |
| X12 Domain reputation | Dropped | (d) | Needs a README line. |
| X13 Download checks | Dropped | (d) | Needs a README line. |
| X14 System posture | Dropped | (d) | Needs a README line. |
| X15 3D models (part) | Done (no 3D models) | (d) | Needs a README line. |
| B26 Confirm before quit | Dropped | none | No README line needed: Krackn's switch did nothing. Close the issue as is. |
| B27 Clean user agent | Dropped | none | Already in the README. Close the issue. |
| I1–I4 Import | Open | (e) | Phase 6c session. |
| P1–P5 Packaging | Open | (e) | Phase 8 session. |
| B25 Default browser | Partial | (e) | Phase 8 finishes it (design: "finishes B25"). |
| B35 Link previews | Open | (e) | PR #167. |
| Two-step sign-in save prompts | (no row; V3 area) | (e) | `feat/two-step-sign-in` session. |
| A1 Parity audit | Open | (e) | This report is its first half. |
| X5 Network & VPN (Done, spot-check) | Done | (b) | Owner runs the DNS script as administrator once, then Undo. PR #144 says it has never run elevated. |
| X16 Background remover (Done, spot-check) | Done | (b) | Krackn's backdrop choice (Transparent, White, Black, Beige) is missing. Add it, or add a README line. |
| B14 Find in page (Done, spot-check) | Done | (b) | Add a light + dark screenshot of the find bar. |
| B18 Tab overview (Done, spot-check) | Done | (b) | Add a light + dark screenshot of the overview. |
| B31 Account gate (Done, spot-check) | Done | (b) | Add a light + dark screenshot of the gate. |
| Other 15 spot-checked Done rows | Done | OK | Evidence exists and covers the row. Housekeeping: 7 closed issues have no evidence comment, and T6/T7 have no issue. |

**Counts:** (a) 1 · (b) 12 (7 non-Done rows and 5 Done rows from the spot-check) · (c) 3 · (d) 5 · (e) 6 lines (I1–I4, P1–P5, B25, B35, two-step sign-in, A1) · no category 2 (B26, B27).

## (a) Done in fact

### B34 Shared UI components

The PARITY note says "mostly visual; needs light + dark screenshots". Those screenshots already exist.

- **Components.** Every component the issue names is in `src/renderer/src/components/`: `PillButton` and `IconButton` (`PillButton.tsx`), `SegmentedControl`, `PillField`, `PillSlider`, `Toggle`, `FileRow` and `FileDropZone` (`files.tsx`), and the scroll edge fade (`FadingScroll`, in `ScrollFade.tsx`).
- **Logic tests.** `scrollEdges.test.ts` (the fade), `sliderMath.test.ts` (PillSlider), `toolDrag.test.ts` and `filePath.test.ts`.
- **Screenshots.** `docs/screenshots/phase-6/` has 30 light + dark pairs of tool panels built from these components. For example, `convert-*.png` shows the drop zone and PillButton, and `unit-converter-*.png` and `pdf-tools-*.png` show the segmented control and fields. Toggles are in `docs/screenshots/b30-settings-{light,dark}.png`.
- **Next step.** Mark Done and cite these.

## (b) Needs a little work

### S2 Frameless window chrome (small)

- **What exists:**
  - Windows draws the caption buttons, Snap Layouts included (`host/Zing.Host/HostWindow.cs`).
  - CSS `app-region: drag` areas sit in `base.css`, `rail.css` and `sidebar.css`.
  - e2e: `real-input.spec.ts` › `a real click on the window's maximise button maximises it`, and `themes.spec.ts` › `the caption buttons match what they sit on, whatever the theme and tab position`.
  - The `docs/screenshots/phase-5/` light + dark set shows the chrome at all three tab positions.
  - Mica is in the README's Known differences.
- **What's missing:** Krackn's real gotcha (`Components.swift`, `WindowDragArea`): dragging a tab must not move the window, while the empty strip must. No test covers it. `e2e/real-input.ps1` sends only clicks.
- **Work:**
  1. Add a press-move-release step to `real-input.ps1`.
  2. Add one test to `real-input.spec.ts`: dragging the strip's empty space moves the window, and dragging a tab reorders it without moving the window.
  3. That tier runs only by hand (`.github/workflows/real-input.yml`), so link the run.
- **Size:** about half a day.

### L12 DownloadPolicy (small)

- **What exists:** `downloadPolicy.ts` and `DownloadPolicy` in `host/Zing.Core/Downloads.cs` match Krackn's `DownloadPolicy.swift` exactly. Nothing calls them. `DownloadHost.Starting` acts on whatever `CoreWebView2.DownloadStarting` raises.
- **Why the engine's decision is probably enough:** Chromium already downloads a response with `Content-Disposition: attachment`, in a frame too, and a main-frame response it can't render. That is Krackn's rule. WebKit needed the policy only because it had no default.
- **What's covered:** `e2e/page.spec.ts` › `a download lands in the downloads folder, shows in the list and numbers on collision` covers the attachment case only.
- **Work:**
  1. Add two cases to `e2e/page.spec.ts`:
     - a main-frame response of a type the engine can't show, with no disposition (for example `application/zip`), downloads;
     - an iframe serving that type, with no disposition, doesn't.
  2. Mark Done "decided by the engine, matching DownloadPolicy". Then either delete both ports, or keep them only as the spec.
- **Size:** about an hour.

### B29 Floating tool panels (small)

- **What exists:** The PARITY note ("no e2e; the toolbox isn't there yet") is out of date. The Toolbox shipped in Phase 6.
  - The panel model is in C#: `host/Zing.Core/ToolPanels.cs`, `PanelLayout.cs`.
  - Shared vectors, run by `ToolPanelsTests.Shared_panel_scenario` (`host/Zing.Core.Tests/vectors/panels.vectors.json`), cover every Krackn rule: `shows at most three panels and refuses the fourth`, `minimizing frees a slot, and restoring into a full window is refused`, `opening a minimized panel restores it`, `keeps visible panels apart after a release, moving the others`, `refuses panels the browser must keep to itself`.
  - Tear-off is in `src/renderer/src/panels/tearOff.ts` and `useTearOff.ts`, wired from `Sidebar.tsx`.
  - `e2e/panels.spec.ts` has three panel tests, but none for the count limit, minimise or tear-off.
- **Work:**
  - One e2e in `panels.spec.ts`: open three tools, check that a fourth is refused, minimise one to the sidebar, then restore it.
  - Optionally, tear a recent tool off the sidebar by holding it.
  - A light + dark screenshot of a minimised panel in the sidebar.
- **Size:** half a day.

### TH4 Mica tint + Windows contrast themes (small)

- **What exists:**
  - The contrast part is built and tested: `e2e/themes.spec.ts` › `in a contrast theme cards are plain outlined rows`, run under `forcedColors: 'active'`.
  - Mica is already in the README's Known differences.
- **What's missing:** a UI screenshot of a contrast theme.
- **Work:** add a forced-colours pair (a dark one such as Night sky and a light one such as Desert) to `e2e/theme-screenshots.spec.ts`, using `emulateMedia({ forcedColors: 'active' })` with each colour scheme. Then mark Done and point Mica at the README.
- **Size:** about an hour.

### V4 / V5 Password sync and restore (owner action)

- **What exists:** The code and tests are thorough. The evidence list under the M6b table checks out:
  - host: `SyncEngineTests`, `SyncRestoreTests`, `SyncBridgeTests` and the others;
  - e2e: `e2e/sync.spec.ts`.
- **What's missing:** only the hand check written at `PARITY.md` line 207 (the real Windows Hello prompt, the picker keys, Win+V and the 30 s clear), against the deployed Railway server. Neither row has a GitHub issue yet.
- **Work:**
  - The owner runs the hand check.
  - Open issues for V4 and V5, and paste the results into them.
  - Remove the hedging from the M6b notes.
- **Note:** the two-step sign-in session touches the V3 save prompt. Re-read the V3 evidence list after it merges.
- **Size:** about an hour of the owner's time.

### P7 Dependency updates (small)

- **What exists:** No `.github/dependabot.yml` or Renovate config.
  - Pins are hand-maintained: `Microsoft.WindowsAppSDK` 2.5.1 and `Microsoft.Web.WebView2` 1.0.4258.31 in `host/Zing.Host/Zing.Host.csproj`, npm in `package.json`.
  - Issue #111's "Zing approach" still says "Ship Electron security bumps within ~7 days".
- **Work:**
  - Add a Dependabot config with weekly updates for npm, NuGet (`host/`, `server/`) and GitHub Actions. Group minor and patch updates.
  - Rewrite the issue text for WebView2 (Evergreen runtime) and Windows App SDK.
  - Close the row with the config as evidence. The row is a process, so the config is the closable part.
- **Size:** about an hour.

### Documentation found stale in passing (small, do with the PARITY edit)

- **`PARITY.md` "Replaced, not ported" table.** It still names the Electron-era stack:
  - `sharp, mammoth/pandoc, printToPDF` should be ImageMagick (`MagickPolicy.cs`), pandoc, and WebView2's PDF printing (`HtmlPdf.cs`, `HtmlPdfPrinter.cs`);
  - `Frameless window + titleBarOverlay (S2)` should be the WinUI title bar with Windows' own caption buttons.
- **[docs/PLAN.md](../../PLAN.md) Efficiency section.** It says budgets are read with `app.getAppMetrics()` and work runs in `utilityProcess` workers. Both are Electron. S10's issue (#11) says the same.
- **Issue bodies.** Several still describe Electron approaches: #3 (`titleBarOverlay`, Mica), #11, #38 (Ghostery engine), #111. Update them when closing.

## (c) Needs real work

### B11 Tracker + ad blocking

- **The bug: the switch does nothing.** Settings shows "Block trackers" (`src/renderer/src/settings/rows.ts`, "Refuses tracking domains and strips tracking parameters"), but the host never reads `blockTrackers`.
  - `WebView2Page` sets `PreferredTrackingPreventionLevel = Strict` for every page.
  - `LinkCleaner.StripTrackers` runs on every navigation (`TabService.cs`, `Address.cs`).
  - Krackn honours the switch for both, in `BrowserModel.installTrackerBlocking` and `BrowserTab.swift` lines 304 and 644.
- **No test covers domain blocking.**
- **Work:**
  1. Wire `blockTrackers` to the tracking-prevention level and to stripping.
  2. Make blocking testable. Either port Krackn's 48-domain `TrackerRules` as a `WebResourceRequested` filter, which an e2e can check against a host-mapped fixture, or show with an e2e that Edge's Strict list loads in WebView2 and refuses a known tracker.
  3. Leave ad blocking (EasyList and the like) to uBlock Origin Lite under Phase 11 extensions, and put a README line meanwhile:

> **Ad blocking.** Zing blocks trackers with Edge's Strict tracking prevention but has no ad blocker of its own yet; that arrives with Chrome extension support (uBlock Origin Lite).

- **Size:** one to two days.

### S10 Performance budget suite in CI

- **What exists:** only `e2e/theme-speed.spec.ts` › `switching tabs and themes is quick with 300 tabs and 16 favourites`. There is no `test:perf` script, no perf spec, and no CI step.
- **The budgets are out of date.** They are written for Electron (`app.getAppMetrics()`, "Chrome UI renderer < 60 MB", "sleeping tab ~0 MB (no renderer)").
- **Work:**
  - Re-derive the budgets for WebView2. Process memory can come from `CoreWebView2Environment.GetProcessInfos` plus the host's working set. Cold start can be measured from launch to the chrome's first paint, with tab switch alongside it.
  - Write the suite.
  - Decide how to hold the 10% regression rule on shared CI runners, for example by comparing against a stored baseline with a median of several runs.
  - The installer budget (< 120 MB) needs P1 first.
- **Size:** two to four days.

### P6 Web-compat smoke tests in CI

- **What exists:** Nothing hits a real site. OAuth popups keeping `window.opener` are tested locally (`core.spec.ts` › `a popup opens as a tab and keeps window.opener`), and there is a one-off screenshot, `docs/screenshots/google-signin-in-zing.png`.
- **Work:**
  - A scheduled, non-blocking workflow on live sites: Google's sign-in page loads with no "browser not secure" message, and a YouTube video plays and goes fullscreen. It must not block PRs, because live sites are flaky and may treat CI as a bot.
  - Passkeys through Windows Hello can't run on a CI runner. Keep them as a recorded hand check.
- **Size:** one to two days, then upkeep.

## (d) Candidates for "Known differences on Windows"

The README section has two entries today (Mica and User agent). Draft lines for each candidate:

### L11 FaviconChoice

- **Why it doesn't apply:** Krackn needed `FaviconChoice` because it looked icons up per site, so Gmail's icon showed on Docs. WebView2 hands Zing the page's own icon (`CoreWebView2.FaviconUri` and `GetFaviconAsync` in `WebView2Page.cs`), so that bug can't happen.
- **What does differ:** the choice among a page's several declared icons follows Edge's rules, not Krackn's "nearest to 32 px, prefer SVG" score.
- **Dead code:** `src/shared/browser/faviconChoice.ts` and its tests have no caller and no C# port.
- **Work:**
  1. Add an e2e to `page.spec.ts`: two pages on one host that declare different icons show different tab icons.
  2. Delete the unused port.
  3. Add:

> **Tab icons.** Edge's engine chooses which of a page's declared icons to show. Krackn preferred the one nearest 32 px; Zing shows the engine's pick.

### X12 Domain reputation via filtering DoH

> **Domain reputation.** Krackn could check every domain against a filtering DNS provider before loading it. Zing doesn't; its link inspection (Settings) checks the address itself, and Edge's SmartScreen still screens pages.

- **To confirm:** the SmartScreen claim. Zing doesn't set `IsReputationCheckingRequired` (default on), so check that it is on in the shipped build before writing it.

### X13 Download checks

> **Download checks.** Krackn scanned downloads and checked code signatures itself. On Windows, Zing marks each download with its source (Mark of the Web), so Windows Defender, SmartScreen and Office check it when it is opened.

- **Evidence:** `DownloadHost.MarkAsync` and `MarkOfTheWeb.HoldAsync` in `host/Zing.Host/DownloadHost.cs`.

### X14 System posture

> **Security overview.** Krackn's Toolbox had a page summarising the Mac's security settings (firewall, FileVault, Gatekeeper). Zing has none; Windows Security shows the same on Windows.

### X15 3D models (part of the File viewer row)

> **3D models.** Krackn's file viewer opened 3D models with SceneKit. Zing's viewer doesn't open 3D model files.

**B26** needs no README line: the switch never did anything in Krackn, so nothing is lost. **B27** is already listed.

## (e) In progress elsewhere (not assessed)

- **I1–I4** import: Phase 6c session.
- **P1–P5** packaging: Phase 8 session. Its design (`docs/superpowers/specs/2026-11-10-phase-8-packaging-design.md` on `phase-8/packaging`) also says it "finishes B25", and leaves P6 and P7 Open.
- **B25** default browser: links from other apps already work and are tested (`e2e/windows.spec.ts`). Phase 8 adds the registration and the default-browser prompt.
- **B35** link previews: PR #167.
- **Two-step sign-in save prompts:** `feat/two-step-sign-in` session (V3 area).
- **A1** parity audit: this report is the first half. The second half is the PARITY and README edits and deleting `reference/krackn/`.

## Done rows, spot-checked

20 of the 90 Done rows, spread across M1–M7: S3, S7, L2, L8, L14, B4, B7, B14, B18, B22, B31, T4, T6, TH5, X5, X9, X16, X20, X24, BR2.

- All 20 issues are closed. T6 has none.
- Every cited test, file and screenshot exists, and each test title was found by grep. Nothing cited is missing.
- Result: 14 OK, 6 weak, 0 broken. The two claims behind the most serious findings (X16's missing backdrops, X5's script never run as administrator) were re-checked by hand.

### Weak (now in (b))

- **X5 Network & VPN.**
  - Covered: `e2e/unblock.spec.ts` (script shown before it runs, Apply then Undo, the VPN guide), `host/Zing.Core.Tests/Net/DnsScriptTests.cs`, `DnsServiceTests.cs`, `src/shared/network/dohScript.test.ts`, and the `network-vpn-{light,dark}.png` screenshots.
  - Missing: PR #144 states that the DNS script "has never run as administrator", so applying and undoing it on real Windows is unproven. This is an owner hand check. Issue #76 has no evidence comment.
- **X16 Background remover.**
  - Krackn offers a backdrop behind the cutout: Transparent, White, Black or Beige (`BackgroundRemover.swift`, `enum Background`). Zing has no backdrop choice, and the README doesn't mention one.
  - The port of `Tests/bgremove` (cutout, transparency, size) runs only opt-in: `e2e/bgremove-real.spec.ts` under `ZING_REAL_ENGINES=1`, and `src/shared/convert/bgremove.real.test.ts`, which skips without the model.
  - Either add the backdrop picker (small) or add a Known-differences line.
- **B14 Find in page.** Covered by `page.spec.ts` › `find in page shows a match count and steps through matches` and `CommandsTests.Find_opens_the_bar_searches_the_page_and_shows_its_results`. It is UI with no light + dark screenshot.
- **B18 Tab overview.** Covered by `tabs.spec.ts` › `the tab overview shows a card per tab with pictures, and closes again`. It has no screenshot, and the thumbnail cache's LRU cap isn't checked by any test.
- **B31 Account gate.** Covered by `shell.spec.ts` (gate covers the window, no plaintext secret, Skip for now), `userdata.spec.ts` and `AccountTests.cs`. It has no screenshot.
- **T6 Favourite tiles.** The evidence is solid: `e2e/saved-tabs.spec.ts` (22 tests), `SavedTabsTests.cs`, and `docs/screenshots/sidebar-empty/` (light + dark). But T6 and T7 have no issue and no evidence note in PARITY. Add a note citing PRs #132 and #155.

### OK, with minor notes

- **S3, B22, TH5, X9, X20, X24.** OK as cited.
- **S7.** `settings.vectors.json` `defaultsSubset` pins 27 Krackn defaults. `autoCloseEnabled`, `autoCloseAtTimeEnabled`, `defaultUserAgent` and `recentFeatures` exist but aren't pinned. Optional: pin them.
- **L2.** All 27 geometry and width checks from Swift `Tests/tabs` are ported. The 9 AppKit-only checks (page shield, fullscreen container) aren't, which is correct.
- **L8.** All 19 checks in Swift `Tests/panels` are matched.
- **L14.** All 21 checks in Swift `Tests/theme` are covered. One deliberate change: malformed hex is refused instead of falling back to black.
- **B4.** Krackn has 5 arrangements. The e2e checks only A–Z and newest, and `recentlyUsed` has one vector case. Adequate, but thin.
- **B7.** Local OAuth popups are tested. Real Google OAuth belongs with P6.
- **T4.** The 300-tab e2e and the `phase4-rail-*` screenshots exist.
- **BR2.** The logo test is adapted to the new logo, since headless Edge draws the icons.

### Housekeeping

The project rule is that a row closes only with evidence linked in its issue. These closed issues have no evidence comment ("Done in #143" or nothing at all): #65 (T4), #76 (X5), #80 (X9), #87 (X16), #91 (X20), #141 (X24), #103 (BR2). The evidence does exist; paste it into each issue during the PARITY edit.
