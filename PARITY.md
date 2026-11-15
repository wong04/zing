# Parity checklist

Every Zing feature, one row each, one GitHub issue each. A row closes only with evidence linked in its issue: a test, or a screenshot for UI (light + dark). Something Windows genuinely can't match is moved to **Known limits** in the README rather than silently dropped.

- **Board:** [issues by milestone](https://github.com/wong04/zing/milestones)
- **Types:** `parity` carried over from the macOS design · `port-logic` pure logic ported with its tests · `new` new in Zing · `infra` build/CI/release

## Replaced, not ported

These were macOS-only in the original design and have a Windows replacement tracked below:

| macOS | Windows |
|---|---|
| Keychain AutoFill bridge | Built-in vault with encrypted sync (V1–V5) |
| `.mobileconfig` DNS profile | PowerShell DoH script (X5) |
| `sips`, `textutil`, PDFKit | ImageMagick, pandoc, WebView2 PDF printing (X6, X7) |
| AVKit, QuickLook | `<video>`, metadata fallback (X15; SceneKit and 3D models dropped) |
| Vision background removal | ONNX Runtime (X16) |
| Traffic-light placement | WinUI title bar with Windows' own caption buttons (S2) |
| `brew install` | Engines downloaded on first use (X10) |

## Dropped

WebKit-only internals with no Chromium equivalent: `WebKitTuning.swift` (private WebKit switches), `BrowserDiagnostics.swift` (canvas blur diagnostics), `Migration.swift` (one-time "All in One" rename).

Also dropped:
- **B26 Confirm before quit.** The `confirmQuit` switch was never read, so it never did anything. Zing removed it (2026-11-01); a settings file that still holds it is read without it (`RETIRED_SETTING_KEYS`).
- **X12 Domain reputation via filtering DoH, X13 Download checks, X14 System posture.** The owner didn't choose them, and Windows does part of X13 itself. The Toolbox has no Security section; the link inspection switch (X11) is in Settings.
- **X15, part: 3D models.** The viewer does not open 3D model files.
- **B27 Clean user agent.** WebView2 presents Edge's own user agent, which Google accepts for sign-in, so there is nothing to clean.

## M0 Repo + tracking

| ID | Item | Type | Issue | Status |
|---|---|---|---|---|
| O1 | Weekly upstream watch | infra | [#1](https://github.com/wong04/zing/issues/1) | Done |

## M1 Shell on Windows

| ID | Item | Type | Issue | Status |
|---|---|---|---|---|
| S1 | App skeleton with Zing identity (Electron, then the WinUI 3 + WebView2 host from Phase W) | new | [#2](https://github.com/wong04/zing/issues/2) | Done |
| S2 | Frameless window chrome for Windows 11 | parity | [#3](https://github.com/wong04/zing/issues/3) | Partial |
| S3 | Overlay layer for UI drawn over the page | parity | [#4](https://github.com/wong04/zing/issues/4) | Done |
| S4 | Tab record model decoupled from webContents | new | [#5](https://github.com/wong04/zing/issues/5) | Done |
| S5 | In-memory session passed as a parameter | parity | [#6](https://github.com/wong04/zing/issues/6) | Done |
| S6 | Command registry + Windows shortcut map | parity | [#7](https://github.com/wong04/zing/issues/7) | Done |
| S7 | Typed settings store with registered defaults | parity | [#8](https://github.com/wong04/zing/issues/8) | Done |
| S8 | CSS theme tokens | parity | [#9](https://github.com/wong04/zing/issues/9) | Done |
| S9 | CI on windows-latest | infra | [#10](https://github.com/wong04/zing/issues/10) | Done |
| S10 | Performance budget suite in CI | infra | [#11](https://github.com/wong04/zing/issues/11) | Open: the 300-tab test (`e2e/theme-speed.spec.ts`) is its first step |

**S rows, 2026-11-02:** S6–S9 re-verified against the code and tests. S10 is really open: there is no `test:perf` script, perf spec or CI step, only the budgets described in [docs/PLAN.md](docs/PLAN.md).

## M2 Pure logic port

| ID | Item | Type | Issue | Status |
|---|---|---|---|---|
| L1 | Port TabOrganizer + SiteGroup | port-logic | [#12](https://github.com/wong04/zing/issues/12) | Done |
| L2 | Port TabReorder drag geometry | port-logic | [#13](https://github.com/wong04/zing/issues/13) | Done |
| L3 | Port AddressInput go-or-search rule | port-logic | [#14](https://github.com/wong04/zing/issues/14) | Done |
| L4 | Port ThreatModel heuristics | port-logic | [#15](https://github.com/wong04/zing/issues/15) | Done |
| L5 | Port BlockDiagnostics.classify | port-logic | [#16](https://github.com/wong04/zing/issues/16) | Done |
| L6 | Port Expression parser | port-logic | [#17](https://github.com/wong04/zing/issues/17) | Done |
| L7 | Port GraphEngine | port-logic | [#18](https://github.com/wong04/zing/issues/18) | Done |
| L8 | Port PanelLayout collision resolution | port-logic | [#19](https://github.com/wong04/zing/issues/19) | Done |
| L9 | Port ToolboxSearch + keyword table | port-logic | [#20](https://github.com/wong04/zing/issues/20) | Done |
| L10 | Port StartShortcuts | port-logic | [#21](https://github.com/wong04/zing/issues/21) | Done |
| L11 | Port FaviconChoice | port-logic | [#22](https://github.com/wong04/zing/issues/22) | Partial |
| L12 | Port DownloadPolicy | port-logic | [#23](https://github.com/wong04/zing/issues/23) | Done: the host consults `DownloadPolicy` for frame downloads ([#175](https://github.com/wong04/zing/pull/175)); host `DownloadsTests`; e2e in `e2e/page.spec.ts` (main-frame response it can't show downloads, a frame marked as a file downloads, the same response in a frame doesn't) |
| L13 | Port DevTools pure logic | port-logic | [#24](https://github.com/wong04/zing/issues/24) | Done |
| L14 | Port accent generator + WCAG AA test | port-logic | [#25](https://github.com/wong04/zing/issues/25) | Done |
| L15 | Port DropRouter | port-logic | [#26](https://github.com/wong04/zing/issues/26) | Done |
| L16 | Port tracking-parameter stripper | port-logic | [#27](https://github.com/wong04/zing/issues/27) | Done |

**L rows, 2026-11-02:** re-verified by reading each port and its tests; the shared suites passed locally (1,265 tests) and CI passed on `7edc3dd`. Done here means the logic is ported and tested, not that the app uses it yet:
- **Waiting for the toolbox:** L15 (X23) has no caller until its row is built. L6, L7, L9 and L13 now have callers in the Toolbox, and exist only in TypeScript. L4 (X11) and L5 (X1–X3) now run as C# ports in the host; their TypeScript versions have no caller, and shared vectors keep the pairs in step.
- **L11, Partial:** `faviconChoice.ts` has no caller and no C# port. `WebView2Page` takes the engine's `FaviconUri`, so the best-icon rule isn't applied.
- **L12, Partial:** `DownloadPolicy` is ported to both sides but nothing calls it. `DownloadHost` relies on the engine's own `DownloadStarting` decision. Either wire it in or confirm the engine decides the same way.

## M3 Browser parity

| ID | Item | Type | Issue | Status |
|---|---|---|---|---|
| B1 | Core tab actions | parity | [#28](https://github.com/wong04/zing/issues/28) | Done |
| B2 | Pinned tabs | parity | [#29](https://github.com/wong04/zing/issues/29) | Done |
| B3 | Drag-to-reorder tabs | parity | [#30](https://github.com/wong04/zing/issues/30) | Done |
| B4 | Tab arrangements | parity | [#31](https://github.com/wong04/zing/issues/31) | Done |
| B5 | Tab stacks (Separate / Dropdown) | parity | [#32](https://github.com/wong04/zing/issues/32) | Done |
| B6 | Auto-close tabs | parity | [#33](https://github.com/wong04/zing/issues/33) | Done |
| B7 | Popups keep window.opener (OAuth) | parity | [#34](https://github.com/wong04/zing/issues/34) | Done |
| B8 | Downloads | parity | [#35](https://github.com/wong04/zing/issues/35) | Done |
| B9 | Fullscreen video | parity | [#36](https://github.com/wong04/zing/issues/36) | Done |
| B10 | Keeps-nothing privacy model | parity | [#37](https://github.com/wong04/zing/issues/37) | Done |
| B11 | Tracker + ad blocking | parity | [#38](https://github.com/wong04/zing/issues/38) | Done: `e2e/trackers.spec.ts` (the built-in tracker domains refused as third parties and parameters stripped; an EasyList ad server, an EasyPrivacy tracker and a built-in tracker domain refused with the switch on and loaded with it off), `FilterRulesTests`, `TrackerRulesTests`; by server only, see README Known limits |
| B12 | Opt-in persistent profile and tab restore | parity | [#39](https://github.com/wong04/zing/issues/39) | Done |
| B13 | Bookmarks, Reading List, Favourites bar | parity | [#40](https://github.com/wong04/zing/issues/40) | Done |
| B14 | Find in page | parity | [#41](https://github.com/wong04/zing/issues/41) | Done |
| B15 | Reader mode | parity | [#42](https://github.com/wong04/zing/issues/42) | Done |
| B16 | Zoom and text size | parity | [#43](https://github.com/wong04/zing/issues/43) | Done |
| B17 | Print | parity | [#44](https://github.com/wong04/zing/issues/44) | Done |
| B18 | Tab overview | parity | [#45](https://github.com/wong04/zing/issues/45) | Done |
| B19 | Back/forward history menus | parity | [#46](https://github.com/wong04/zing/issues/46) | Done |
| B20 | Start page | parity | [#47](https://github.com/wong04/zing/issues/47) | Done |
| B21 | Search engine and homepage settings | parity | [#48](https://github.com/wong04/zing/issues/48) | Done |
| B22 | Memory-only favicons | parity | [#49](https://github.com/wong04/zing/issues/49) | Done: tabs' favicons stay in memory. One exception: when a page loads at a bookmarked address (any bookmark or Favourites item), its icon is saved as a small PNG (64 KB cap) in `bookmark-icons` beside `library.json`, refreshed on later visits and deleted with the bookmark. An address matches when host (minus `www.`), port and path agree; scheme, trailing `/`, query and fragment are ignored (`BookmarkIcons.MatchKey`). Nothing is ever fetched for a bookmark that wasn't visited, so imported ones stay blank until opened. Saved icons flip the privacy banner. Host tests `BookmarkIconsTests`; e2e in `e2e/bookmark-icons.spec.ts` |
| B23 | Camera, mic and permission prompts | parity | [#50](https://github.com/wong04/zing/issues/50) | Done |
| B24 | Multiple windows | parity | [#51](https://github.com/wong04/zing/issues/51) | Done |
| B25 | Default browser + links from other apps | parity | [#52](https://github.com/wong04/zing/issues/52) | Done: owner's hand check passed 2026-11-11 (Default apps, links from other apps, [docs/release.md](docs/release.md#hand-check)); host tests `DefaultBrowserTests`, `ChromeApiDefaultBrowserTests`; e2e in `e2e/default-browser.spec.ts` and `e2e/open-with.spec.ts`. Open with for .pdf/.htm/.html came after that check ([#188](https://github.com/wong04/zing/pull/188)) and still needs a hand re-check |
| B26 | Confirm before quit | parity | [#53](https://github.com/wong04/zing/issues/53) | Dropped |
| B27 | Clean user agent | parity | [#54](https://github.com/wong04/zing/issues/54) | Dropped |
| B28 | App sidebar with edge reveal | parity | [#55](https://github.com/wong04/zing/issues/55) | Done |
| B29 | Floating tool panels | parity | [#56](https://github.com/wong04/zing/issues/56) | Partial |
| B30 | Settings sheet | parity | [#57](https://github.com/wong04/zing/issues/57) | Done |
| B31 | Account gate | parity | [#58](https://github.com/wong04/zing/issues/58) | Done |
| B32 | Sleeping tabs | new | [#59](https://github.com/wong04/zing/issues/59) | Done |
| B33 | Lazy session restore | new | [#60](https://github.com/wong04/zing/issues/60) | Done |
| B34 | Shared UI components | parity | [#61](https://github.com/wong04/zing/issues/61) | Done: components in `src/renderer/src/components/` with `scrollEdges.test.ts`, `sliderMath.test.ts`, `toolDrag.test.ts`, `filePath.test.ts`; light + dark screenshots in `docs/screenshots/phase-6/` and `docs/screenshots/b30-settings-{light,dark}.png` |
| B35 | Link previews (Arc Peek) | new | [#160](https://github.com/wong04/zing/pull/160) | Done: e2e in `e2e/link-preview.spec.ts`; hand checks H1-H9 passed (2026-11-08 and 2026-11-09, real-input tier 8/8), recorded in [the design](docs/superpowers/specs/2026-11-08-link-preview-design.md); screenshots in `docs/screenshots/link-preview/` |

**B rows, 2026-11-01:** re-verified on the WebView2 host. Evidence for each Done row is in its issue, and every cited test passed on Windows CI ([run 41289375469](https://github.com/wong04/zing/actions/runs/41289375469), the whole e2e suite plus vitest and xUnit). Partial rows, still open:
- B25: the installer registers Zing as a browser and Settings offers to make it the default (Phase 8); a real install, Default apps and a link from another app wait on the owner's hand check in [docs/release.md](docs/release.md#hand-check)
- B29: no e2e; the toolbox isn’t there yet
- B34: mostly visual; needs light + dark screenshots

**Phase 3 close-out, 2026-11-01:**
- B3: `e2e/tabs.spec.ts` drags a tab along the strip with pointer events.
- B6: `e2e/lifecycle.spec.ts` closes a tab left alone past its rule on the test clock. It found a race: a tab closed while the sleeper read its page was recorded as a fault (`TabSleeper.TrySleep`, now a test in `TabLifecycleTests`).
- B9: a fullscreen element gives the page the whole window and the window goes fullscreen (`MainWindow.FollowFullScreen`); `e2e/page.spec.ts` checks the layout. The window's fullscreen presenter itself is a hand check, since test windows stay off-screen.
- B13: `e2e/core.spec.ts` adds to the Reading List, marks it read and deletes it in the panel.
- B16: Zoom In, Zoom Out and Actual Size step the tab's zoom through the fixed steps in `Zoom`; `e2e/page.spec.ts` checks the page's scale, across its next page.
- B17: Print opens Edge's print preview for the page on screen; an e2e run records it instead of opening it (`ZING_DIALOGS_FILE`). The preview itself is a hand check.
- B21: `e2e/core.spec.ts` checks Home with an empty and a set homepage.
- B23: `e2e/page.spec.ts` allows a notification, then the camera and microphone (the engine's fake devices in e2e).

## M4 Tab bar position + split view

| ID | Item | Type | Issue | Status |
|---|---|---|---|---|
| T1 | Tab bar position setting: top / left / right | new | [#62](https://github.com/wong04/zing/issues/62) | Done |
| T2 | Vertical tab strip | new | [#63](https://github.com/wong04/zing/issues/63) | Done |
| T3 | Fold app sidebar into a left tab rail | new | [#64](https://github.com/wong04/zing/issues/64) | Done |
| T4 | Virtualized tab list | new | [#65](https://github.com/wong04/zing/issues/65) | Done |
| T5 | Split view | new | [#66](https://github.com/wong04/zing/issues/66) | Done |
| T6 | Favourite tiles in the tab rail (saved tabs) | new | - | Done |
| T7 | Pinned items in folders (saved tabs) | new | - | Done |

## M5 Themes

| ID | Item | Type | Issue | Status |
|---|---|---|---|---|
| TH1 | Full theme model | new | [#67](https://github.com/wong04/zing/issues/67) | Done |
| TH2 | Theme presets | new | [#68](https://github.com/wong04/zing/issues/68) | Done |
| TH3 | Custom theme from a seed colour | new | [#69](https://github.com/wong04/zing/issues/69) | Done |
| TH4 | Mica tint + Windows contrast themes | new | [#70](https://github.com/wong04/zing/issues/70) | Partial: contrast themes are done; the owner dropped Mica (README, Known limits) |
| TH5 | Switch theme without rebuilding state | parity | [#71](https://github.com/wong04/zing/issues/71) | Done |

## M6 Toolbox parity

| ID | Item | Type | Issue | Status |
|---|---|---|---|---|
| X1 | Open a Link: four methods | parity | [#72](https://github.com/wong04/zing/issues/72) | Done |
| X2 | Relayed pages with subresources | parity | [#73](https://github.com/wong04/zing/issues/73) | Done |
| X3 | Diagnose | parity | [#74](https://github.com/wong04/zing/issues/74) | Done |
| X4 | DoH provider setting | parity | [#75](https://github.com/wong04/zing/issues/75) | Done |
| X5 | Network & VPN for Windows | parity | [#76](https://github.com/wong04/zing/issues/76) | Done |
| X6 | Convert images | parity | [#77](https://github.com/wong04/zing/issues/77) | Done |
| X7 | Convert documents | parity | [#78](https://github.com/wong04/zing/issues/78) | Done |
| X8 | Convert audio/video + format matrix | parity | [#79](https://github.com/wong04/zing/issues/79) | Done |
| X9 | Media download + Save page | parity | [#80](https://github.com/wong04/zing/issues/80) | Done |
| X10 | Engines on first use | parity | [#81](https://github.com/wong04/zing/issues/81) | Done |
| X11 | Link inspection before load | parity | [#82](https://github.com/wong04/zing/issues/82) | Done |
| X12 | Domain reputation via filtering DoH | parity | [#83](https://github.com/wong04/zing/issues/83) | Dropped |
| X13 | Download checks for Windows | parity | [#84](https://github.com/wong04/zing/issues/84) | Dropped |
| X14 | System posture for Windows | parity | [#85](https://github.com/wong04/zing/issues/85) | Dropped |
| X15 | File viewer (3D models dropped) | parity | [#86](https://github.com/wong04/zing/issues/86) | Done (no 3D models) |
| X16 | Background remover | parity | [#87](https://github.com/wong04/zing/issues/87) | Done |
| X17 | Graphing calculator | parity | [#88](https://github.com/wong04/zing/issues/88) | Done |
| X18 | Calculator, units, number base | parity | [#89](https://github.com/wong04/zing/issues/89) | Done |
| X19 | Developer tools | parity | [#90](https://github.com/wong04/zing/issues/90) | Done |
| X20 | Text & design tools | parity | [#91](https://github.com/wong04/zing/issues/91) | Done |
| X21 | Password generator + link cleaner | parity | [#92](https://github.com/wong04/zing/issues/92) | Done |
| X22 | Toolbox page, search and recents | parity | [#93](https://github.com/wong04/zing/issues/93) | Done |
| X23 | Drag files between tools | parity | [#94](https://github.com/wong04/zing/issues/94) | Done |
| X24 | Links page | new | [#141](https://github.com/wong04/zing/issues/141) | Done |
| X25 | PDF tools | new | [#142](https://github.com/wong04/zing/issues/142) | Done |

## M6b Password vault

| ID | Item | Type | Issue | Status |
|---|---|---|---|---|
| V1 | Vault storage + Windows Hello unlock | new | [#95](https://github.com/wong04/zing/issues/95) | Done |
| V2 | Sign-in field detection and fill | parity | [#96](https://github.com/wong04/zing/issues/96) | Done |
| V3 | Save prompt, manage page, privacy banner | new | [#97](https://github.com/wong04/zing/issues/97) | Done |
| V4 | Encrypted password sync between PCs (Railway server, Google sign-in, merge with password history) | new | - | Partial: awaiting the owner's check against the deployed server |
| V5 | Restore on a new PC with the Zing password or a recovery key | new | - | Partial: awaiting the owner's check against the deployed server |

Evidence (e2e/vault.spec.ts unless noted; screenshots in [docs/screenshots/phase-6b](docs/screenshots/phase-6b)):

- V1: host tests `VaultStoreTests` (`SavedPasswordReadsBackAfterReopening`, `AKeyHelloCantOpenAsksToSetTheVaultUpAgainOnEveryPathThatNeedsIt`), `VaultCryptoTests` (`KdfParamsOutsideTheFloorAndCapsAreRefused`), `SettingsTests.ThePasswordVaultIsOffUntilThePersonTurnsItOn`; e2e `without Windows Hello the page says so and a password cannot be shown`, `after a reset Windows Hello key the page says to set the vault up again on this PC when a password is shown`, `WebView2 password saving and general autofill are off in every view, in the chrome and in pages`. Screenshots: `vault-hello-error-light.png`, `vault-hello-error-dark.png`.
- V2: e2e `focusing the username box shows the saved username under it, and choosing it fills both boxes`, `the picker follows real keys: arrows move, Enter chooses, Esc closes; page-relayed keys do nothing`, `an iframe from another local port gets no picker, even posting a forged message itself, while the top page still does`, `with the vault off, no picker appears, even with a login saved for the page`.
- V3: e2e `a successful sign-in to a new site prompts Save, and Save stores the login after Hello`, `a changed password prompts an update that names the saved login, and Update replaces it`, `Never for this site stops every later prompt for it, saving and updating alike, and is kept in the vault`, `the Settings switch turns the vault on, and the privacy banner warns once it holds an entry`, `showing a password asks Windows Hello, a cancel shows and copies nothing, and a passed Hello is not asked again for 5 minutes`, `export warns first, writes Chrome-format CSV to Downloads after Hello, and a fresh vault re-imports the same entries`. Screenshots: `vault-empty-*.png`, `vault-list-*.png` (light and dark).
- V4: host tests `SyncEngineTests` (`AnEditOnAAppearsOnB`, `BothChangedDifferentFieldsAndBothPcsEndWithBothChanges`, `BothChangedThePasswordSoTheNewerWinsAndTheOlderIsInTheHistoryOnBothPcs`, `APushWhoseAnswerIsLostIsNotUploadedTwice`, `ARefreshWhoseAnswerIsLostIsRetriedWithinTwoMinutesSoNoPcIsSignedOut`, `APulledPageThatCouldNotBeStoredIsPulledAgainAndNotTakenAsWithheld`), `RollbackGuardTests`, `SyncSetupTests`, `SyncClientTests`, `MergeTests`, and the server's `RefreshFamilyTests`; e2e (e2e/sync.spec.ts) `Turn on sync signs in against the sync server, stays signed in after a restart, and Sign out forgets it`, `turning on sync shows the recovery key, and nothing else of the vault, until it is confirmed saved, even after a restart, and then the account can’t be deleted`, `a Zing password too weak for sync is changed to a suggested passphrase in the same flow`, `a login saved on one PC reaches the other, and an edit made there comes back`, `an edit made on each PC while one was offline merges field by field, and a delete reaches the other PC`. No issue yet: the rows were added with this work, and get one when the owner opens it.
- V5: host tests `SyncRestoreTests`, and `SyncBridgeTests` (`OnAFreshProfileRestoreFromSyncTurnsTheVaultOnAndKeepsTheSignInOnlyOnceTheAccountIsMade`, `ARestoreStepWhoseRefreshAnswerIsLostIsRetriedWithinTwoMinutesSoNoPcIsSignedOut`); e2e (e2e/sync.spec.ts) `restoring from sync on a new PC refuses a wrong password and makes nothing, then the password brings back every login`, `restoring with the recovery key asks for a new password at once, and the old password stops working on the next restore`, `a password changed on one PC makes the other ask for the new one once, at its next start`. No issue yet, as for V4.
- The rows stay marked pending until the owner has checked the real Windows Hello prompt: save, fill, reveal, a cancel, the picker's arrow keys, Enter and Esc, and the copied password missing from Win+V with the 30 s clear leaving a newer copy alone. Remove the marker and these lines' hedging once that is done.

## M6c Import

| ID | Item | Type | Issue | Status |
|---|---|---|---|---|
| I1 | Import from Chrome, Edge, Brave | new | [#98](https://github.com/wong04/zing/issues/98) | Partial: awaiting the owner's hand check |
| I2 | Import from Dia | new | [#99](https://github.com/wong04/zing/issues/99) | Partial: awaiting the owner's hand check |
| I3 | Import from Arc | new | [#100](https://github.com/wong04/zing/issues/100) | Partial: awaiting the owner's hand check |
| I4 | Generic bookmark and password import | new | [#101](https://github.com/wong04/zing/issues/101) | Partial: awaiting the owner's hand check |

Evidence (e2e/import.spec.ts unless noted; screenshots in [docs/screenshots](docs/screenshots)). The parsers and merge are held to the TypeScript twins by `src/shared/import/*.vectors.json`, read by `src/shared/import/import.test.ts`, `passwords.test.ts` and the host tests.

- I1: host tests `ImportSourcesTests` (`FindsEachChromiumProfileWithItsDisplayName`, `MarksTheLastUsedProfile`, `ReadsAFileAnotherProcessHoldsOpenForWriting`, `ACorruptBookmarksFileIsARowWithAnError`, `ErrorsNeverCarryAPath`), `ParserTests.ReadsWhatTheTypeScriptParsersRead`, `MergeTests.Merge_matches_the_shared_vectors`, `DefaultBrowserSourceTests`, `PasswordExportWatchTests`, `ImportPasswordWatchTests`, `ExportPagesTests`, `NudgeTests`; e2e `Settings Import lists Chrome profiles and Arc found on this PC, and importing one adds its bar and an Imported folder`, `importing the same profile twice adds nothing the second time`, `a broken profile shows its error and the others still import`, `Import passwords opens Chrome's export page, imports the CSV that lands in Downloads, and recycles it on request`, `with the vault off, Import passwords asks first and turns it on`, `the first New Tab offers the default browser and Import brings its bookmarks in`, `Not now hides the card on every later New Tab and after a restart`, `Import passwords too opens Settings Import already waiting for Chrome's export`. Screenshots: `import-section-light.png`, `import-section-dark.png`, `import-nudge-light.png`, `import-nudge-dark.png`.
- I2: Dia's bookmarks read as Chromium JSON or Netscape HTML (vitest `Chromium Bookmarks JSON (Chrome, Edge, Brave, Dia)`, `Netscape bookmarks HTML (every browser, incl. Dia on Mac)`), and its passwords CSV by a pinned vector in `passwords.vectors.json`; e2e `a passwords file chosen by hand imports and offers to recycle it`.
- I3: host tests `ImportSourcesTests.FindsArcUnderItsPackageFolder`, `APackagesFolderThatCannotBeListedIsAnArcRowWithAnErrorAndChromiumIsStillFound`, `ParserTests.AnArcSidebarExactlyAtTheItemCapStillParses`, `AnArcSidebarJustOverTheItemCapIsRefused`, `AnArcSidebarWithATabTitledRootsIsStillReadAsArc`, `ExportPagesTests.ArcHasNoExportPage`; e2e `Settings Import lists Chrome profiles and Arc found on this PC, and importing one adds its bar and an Imported folder` (the Arc row).
- I4: host tests `ParserTests` (`SniffsTheFormatFromTheContentNotTheExtension`, `AWindows1252FileIsRefusedAsNotUtf8`, `AFileOverTheLimitIsRefusedBeforeItIsRead`, `SafarisFavoritesFolderIsNotTheBarBecauseItCarriesNoToolbarMark`), `ImportApiTests.ImportingAPickedFileUsesItsNameAsTheSource`; e2e `a bookmarks file from Safari imports into a folder named after the file`, `a passwords file chosen by hand imports and offers to recycle it`, `with Zing as the default browser the card asks generically and Choose opens Settings Import`.
- The rows stay marked pending until the owner has run [docs/import-hand-check.md](docs/import-hand-check.md): real Chrome, Edge, Brave and Arc profiles, each browser's password export page opened from Zing, and Dia and Safari exports from the Mac. Then set them to Done and close #98–#101.

## M7 Logo + branding

| ID | Item | Type | Issue | Status |
|---|---|---|---|---|
| BR1 | Zing logo concepts | new | [#102](https://github.com/wong04/zing/issues/102) | Done |
| BR2 | Icon generator + logo test | parity | [#103](https://github.com/wong04/zing/issues/103) | Done |
| BR3 | Name collision check for 'Zing' | new | [#104](https://github.com/wong04/zing/issues/104) | Done |

- **BR1:** the logo is the C3 Slice. The first concepts (A–D) are in `assets/brand/concepts/`, and the Slice versions in `assets/brand/concepts/slice/`. The chosen tiles are `assets/brand/zing.svg` (frosted) and `assets/brand/zing-dark-tile.svg`.
- **BR2:** `npm run make-icons` draws the SVGs with headless Edge into `build/icons*` (PNGs 16–1024 and a `.ico` with 16–256) and into `build/installer` (Velopack's splash image and the two MSI bitmaps). `src/shared/brand/logo.test.ts` is the port of `Tests/logo`. `e2e/brand.spec.ts` checks the live window and `Zing.exe`, and its screenshots are in `docs/screenshots/phase-7/`. There's no `.icns`, because Zing is Windows-only.
- **BR3:** `assets/brand/NAME-CHECK.md`. No browser is called Zing, but the word is a crowded trademark (medium-high risk).

## M8 Packaging + release

| ID | Item | Type | Issue | Status |
|---|---|---|---|---|
| P1 | Velopack installer | infra | [#105](https://github.com/wong04/zing/issues/105) | Done: owner's hand check passed 2026-11-11 (install, uninstall keeps `%APPDATA%\Zing`, [docs/release.md](docs/release.md#hand-check)); `scripts/pack.ps1`, `.github/workflows/release.yml` |
| P2 | Auto-update from GitHub Releases | infra | [#106](https://github.com/wong04/zing/issues/106) | Done: owner's hand check passed 2026-11-11 (0.9.0 to 0.9.1 with data intact, [docs/release.md](docs/release.md#hand-check)); host tests `UpdaterTests`, `ChromeApiUpdatesTests`; e2e in `e2e/update.spec.ts` |
| P3 | Code signing | infra | [#107](https://github.com/wong04/zing/issues/107) | Open: decided to sign later, with Azure Trusted Signing (about $10/month, identity check) or SignPath Foundation (free for open source); `release.yml` has a placeholder sign step that turns on with an `AZURE_SIGNING_ACCOUNT` secret ([docs/release.md](docs/release.md#signing)) |
| P4 | winget manifest | infra | [#108](https://github.com/wong04/zing/issues/108) | Partial: `winget/manifests/` passes `winget validate`; submission is turned on at 1.0 ([docs/release.md](docs/release.md#winget-at-10)) |
| P5 | Credits and third-party notices | infra | [#109](https://github.com/wong04/zing/issues/109) | Done: `scripts/notices.test.ts`, host tests `ChromeApiCreditsTests`, and the Credits assertion in `e2e/update.spec.ts` |
| P6 | Web-compat smoke tests in CI | infra | [#110](https://github.com/wong04/zing/issues/110) | Open |
| P7 | Dependency updates (Windows App SDK, .NET, npm); Chromium security fixes now come with the Evergreen WebView2 runtime | infra | [#111](https://github.com/wong04/zing/issues/111) | Done: `.github/dependabot.yml` (weekly npm, NuGet and GitHub Actions); Chromium security fixes arrive with the Evergreen WebView2 runtime |

**Phase 8, 2026-11-11:** the installer, updater, default-browser registration and credits are built and tested; the release guide is [docs/release.md](docs/release.md).
- P1: `npm run pack` built 0.9.0 and 0.9.1 into one local feed (`artifacts/releases`); the `release` workflow packs on every PR that touches packaging and drafts a release on a `v*` tag.
- P2: host tests `UpdaterTests` and `ChromeApiUpdatesTests`; e2e `e2e/update.spec.ts` (Check now finds a ready update, every window shows the pill, clicking it restarts once). e2e runs use a fake update source and never contact GitHub.
- B25: host tests `DefaultBrowserTests` and `ChromeApiDefaultBrowserTests`; e2e `e2e/default-browser.spec.ts` (the offer waits for a second launch, opens Default apps, and stops after Don't ask again; the Settings row by install state).
- B25, files from Open with: the installer also registers `ZingPDF` for `.pdf` (as Edge and Chrome do) beside `ZingHTML`, and Zing opens a `.pdf`, `.htm` or `.html` path it is handed: each in a tab at its file: address (a PDF in the engine's PDF viewer, so it has the whole window and fullscreen), cold or into a running Zing. A dropped PDF still opens in the Files viewer. Host tests `LaunchFilesTests` and `DefaultBrowserTests`; e2e `e2e/open-with.spec.ts`. The owner's first hand check (2026-11-11) passed install, update, Default apps, a link from another app and uninstall; it found `.pdf` missing from Open with, so Open with on a `.pdf` and an `.html` is re-checked.
- P5: `scripts/notices.test.ts`, `ChromeApiCreditsTests`, and the Credits region in `e2e/update.spec.ts`.
- Screenshots, light and dark, in `docs/screenshots/phase-8/`: About, the update pill (alone and in a window), the default-browser offer and the Settings row.
- P1, P2 and B25 stay Partial until the owner runs the hand check in [docs/release.md](docs/release.md#hand-check) (install 0.9.0, make Zing the default, update to 0.9.1 from the local feed, uninstall and keep the data).

## M9 Parity audit

| ID | Item | Type | Issue | Status |
|---|---|---|---|---|
| A1 | Parity audit and remove the reference source | infra | [#112](https://github.com/wong04/zing/issues/112) | Open |
