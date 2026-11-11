# Parity checklist

Every Krackn feature and every new Zing feature, one row each, one GitHub issue each. A row closes only with evidence linked in its issue: a ported test, or a screenshot for UI (light + dark). Something Windows genuinely can't match is moved to **Known differences on Windows** in the README rather than silently dropped.

- **Baseline:** Krackn [`9ceaaa8`](https://github.com/yityler/Krackn/commit/9ceaaa8c920acc941ac61044bfb6c6f9f04d083e) (also in `reference/KRACKN_SHA`). Newer Krackn commits arrive as an `upstream` issue every Monday. Port or skip each one, then bump the SHA.
- **Board:** [issues by milestone](https://github.com/wong04/zing/milestones)
- **Types:** `parity` exists in Krackn · `port-logic` pure logic ported with its Krackn tests · `new` new in Zing · `infra` build/CI/release

## Replaced, not ported

These are macOS-only and have a Windows replacement tracked above:

| Krackn | Zing |
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
- **B26 Confirm before quit.** Krackn has the `confirmQuit` switch but never reads it, so it never did anything. Zing removed it (2026-11-01); a settings file that still holds it is read without it (`RETIRED_SETTING_KEYS`).
- **X12 Domain reputation via filtering DoH, X13 Download checks, X14 System posture.** The owner didn't choose them, and Windows does part of X13 itself. The Toolbox has no Security section; the link inspection switch (X11) is in Settings.
- **X15, part: 3D models.** The viewer does not open 3D model files.
- **B27 Clean user agent.** WebView2 presents Edge's own user agent, which Google accepts for sign-in, so there is nothing to clean. Listed under Known differences in the README.

## M0 Repo + tracking

| ID | Item | Type | Krackn source | Issue | Status |
|---|---|---|---|---|---|
| O1 | Weekly upstream Krackn watch | infra | - | [#1](https://github.com/wong04/zing/issues/1) | Done |

## M1 Shell on Windows

| ID | Item | Type | Krackn source | Issue | Status |
|---|---|---|---|---|---|
| S1 | App skeleton with Zing identity (Electron, then the WinUI 3 + WebView2 host from Phase W) | new | - | [#2](https://github.com/wong04/zing/issues/2) | Done |
| S2 | Frameless window chrome for Windows 11 | parity | App/Components.swift (WindowChrome, WindowDragArea) | [#3](https://github.com/wong04/zing/issues/3) | Partial |
| S3 | Overlay layer for UI drawn over the page | parity | Features/Browser/WebViewHost.swift (shielded) | [#4](https://github.com/wong04/zing/issues/4) | Done |
| S4 | Tab record model decoupled from webContents | new | Features/Browser/TabOrganizer.swift (TabSnapshot) | [#5](https://github.com/wong04/zing/issues/5) | Done |
| S5 | In-memory session passed as a parameter | parity | Features/Browser/BrowserTab.swift | [#6](https://github.com/wong04/zing/issues/6) | Done |
| S6 | Command registry + Windows shortcut map | parity | App/Commands.swift | [#7](https://github.com/wong04/zing/issues/7) | Done |
| S7 | Typed settings store with registered defaults | parity | App/AppSettings.swift | [#8](https://github.com/wong04/zing/issues/8) | Done |
| S8 | CSS theme tokens from Krackn's palette | parity | App/Theme.swift, App/Accent.swift | [#9](https://github.com/wong04/zing/issues/9) | Done |
| S9 | CI on windows-latest | infra | run-tests.sh | [#10](https://github.com/wong04/zing/issues/10) | Done |
| S10 | Performance budget suite in CI | infra | - | [#11](https://github.com/wong04/zing/issues/11) | Open: the 300-tab test (`e2e/theme-speed.spec.ts`) is its first step |

**S rows, 2026-11-02:** S6–S9 re-verified against the code and tests. S10 is really open: there is no `test:perf` script, perf spec or CI step, only the budgets described in [docs/PLAN.md](docs/PLAN.md).

## M2 Pure logic port

| ID | Item | Type | Krackn source | Issue | Status |
|---|---|---|---|---|---|
| L1 | Port TabOrganizer + SiteGroup | port-logic | Features/Browser/TabOrganizer.swift; Tests/taborg | [#12](https://github.com/wong04/zing/issues/12) | Done |
| L2 | Port TabReorder drag geometry | port-logic | Features/Browser/TabReorder.swift; Tests/tabs | [#13](https://github.com/wong04/zing/issues/13) | Done |
| L3 | Port AddressInput go-or-search rule | port-logic | Features/Browser/AddressInput.swift; Tests/unit | [#14](https://github.com/wong04/zing/issues/14) | Done |
| L4 | Port ThreatModel heuristics | port-logic | Features/Security/ThreatModel.swift; Tests/security | [#15](https://github.com/wong04/zing/issues/15) | Done |
| L5 | Port BlockDiagnostics.classify | port-logic | Features/Unblock/BlockDiagnostics.swift; Tests/unit | [#16](https://github.com/wong04/zing/issues/16) | Done |
| L6 | Port Expression parser | port-logic | Features/Utilities/Expression.swift; Tests/calc | [#17](https://github.com/wong04/zing/issues/17) | Done |
| L7 | Port GraphEngine | port-logic | Features/Utilities/GraphEngine.swift; Tests/graph | [#18](https://github.com/wong04/zing/issues/18) | Done |
| L8 | Port PanelLayout collision resolution | port-logic | App/PanelLayout.swift; Tests/panels | [#19](https://github.com/wong04/zing/issues/19) | Done |
| L9 | Port ToolboxSearch + keyword table | port-logic | App/ToolboxSearch.swift; Tests/toolbox | [#20](https://github.com/wong04/zing/issues/20) | Done |
| L10 | Port StartShortcuts | port-logic | Features/Browser/StartShortcuts.swift; Tests/unit | [#21](https://github.com/wong04/zing/issues/21) | Done |
| L11 | Port FaviconChoice | port-logic | Features/Browser/FaviconChoice.swift | [#22](https://github.com/wong04/zing/issues/22) | Partial |
| L12 | Port DownloadPolicy | port-logic | Features/Browser/DownloadPolicy.swift; Tests/download | [#23](https://github.com/wong04/zing/issues/23) | Done: the host consults `DownloadPolicy` for frame downloads ([#175](https://github.com/wong04/zing/pull/175)); host `DownloadsTests`; e2e in `e2e/page.spec.ts` (main-frame response it can't show downloads, a frame marked as a file downloads, the same response in a frame doesn't) |
| L13 | Port DevTools pure logic | port-logic | Features/Utilities/DevTools.swift, UtilityTools.swift | [#24](https://github.com/wong04/zing/issues/24) | Done |
| L14 | Port accent generator + WCAG AA test | port-logic | App/Accent.swift; Tests/theme | [#25](https://github.com/wong04/zing/issues/25) | Done |
| L15 | Port DropRouter | port-logic | App/DropRouter.swift; Tests/drop | [#26](https://github.com/wong04/zing/issues/26) | Done |
| L16 | Port tracking-parameter stripper | port-logic | Features/Browser/BrowserTab.swift, UtilityTools.swift | [#27](https://github.com/wong04/zing/issues/27) | Done |

**L rows, 2026-11-02:** re-verified by reading each port and its tests; the shared suites passed locally (1,265 tests) and CI passed on `7edc3dd`. Done here means the logic is ported and tested, not that the app uses it yet:
- **Waiting for the toolbox:** L15 (X23) has no caller until its row is built. L6, L7, L9 and L13 now have callers in the Toolbox, and exist only in TypeScript. L4 (X11) and L5 (X1–X3) now run as C# ports in the host; their TypeScript versions have no caller, and shared vectors keep the pairs in step.
- **L11, Partial:** `faviconChoice.ts` has no caller and no C# port. `WebView2Page` takes the engine's `FaviconUri`, so the best-icon rule isn't applied.
- **L12, Partial:** `DownloadPolicy` is ported to both sides but nothing calls it. `DownloadHost` relies on the engine's own `DownloadStarting` decision. Either wire it in or confirm the engine decides the same way.

## M3 Browser parity

| ID | Item | Type | Krackn source | Issue | Status |
|---|---|---|---|---|---|
| B1 | Core tab actions | parity | App/Commands.swift, Features/Browser/BrowserModel.swift | [#28](https://github.com/wong04/zing/issues/28) | Done |
| B2 | Pinned tabs | parity | Features/Browser/BrowserView.swift | [#29](https://github.com/wong04/zing/issues/29) | Done |
| B3 | Drag-to-reorder tabs | parity | Features/Browser/TabReorder.swift, BrowserView.swift | [#30](https://github.com/wong04/zing/issues/30) | Done |
| B4 | Tab arrangements | parity | Features/Browser/TabOrganizer.swift | [#31](https://github.com/wong04/zing/issues/31) | Done |
| B5 | Tab stacks (Separate / Dropdown) | parity | Features/Browser/BrowserView.swift (TabStackChip, TabStackDropdown) | [#32](https://github.com/wong04/zing/issues/32) | Done |
| B6 | Auto-close tabs | parity | Features/Browser/TabOrganizer.swift | [#33](https://github.com/wong04/zing/issues/33) | Done |
| B7 | Popups keep window.opener (OAuth) | parity | Features/Browser/BrowserTab.swift (createWebViewWith) | [#34](https://github.com/wong04/zing/issues/34) | Done |
| B8 | Downloads | parity | Features/Browser/DownloadPolicy.swift, BrowserChrome.swift | [#35](https://github.com/wong04/zing/issues/35) | Done |
| B9 | Fullscreen video | parity | Features/Browser/WebViewHost.swift | [#36](https://github.com/wong04/zing/issues/36) | Done |
| B10 | Keeps-nothing privacy model | parity | README 'Privacy, stated precisely' | [#37](https://github.com/wong04/zing/issues/37) | Done |
| B11 | Tracker + ad blocking | parity | README (48 tracker domains) | [#38](https://github.com/wong04/zing/issues/38) | Partial |
| B12 | Opt-in persistent profile and tab restore | parity | App/AppSettings.swift (persistentProfile, restoreTabs), SettingsView.swift | [#39](https://github.com/wong04/zing/issues/39) | Done |
| B13 | Bookmarks, Reading List, Favourites bar | parity | Features/Browser/BrowserLibrary.swift, LibraryStore.swift | [#40](https://github.com/wong04/zing/issues/40) | Done |
| B14 | Find in page | parity | Features/Browser/BrowserTabActions.swift | [#41](https://github.com/wong04/zing/issues/41) | Done |
| B15 | Reader mode | parity | Features/Browser/BrowserTabActions.swift | [#42](https://github.com/wong04/zing/issues/42) | Done |
| B16 | Zoom and text size | parity | Features/Browser/BrowserTabActions.swift | [#43](https://github.com/wong04/zing/issues/43) | Done |
| B17 | Print | parity | Features/Browser/BrowserTabActions.swift | [#44](https://github.com/wong04/zing/issues/44) | Done |
| B18 | Tab overview | parity | Features/Browser/BrowserChrome.swift | [#45](https://github.com/wong04/zing/issues/45) | Done |
| B19 | Back/forward history menus | parity | Features/Browser/BrowserChrome.swift | [#46](https://github.com/wong04/zing/issues/46) | Done |
| B20 | Start page | parity | Features/Browser/StartPage.swift, StartShortcuts.swift | [#47](https://github.com/wong04/zing/issues/47) | Done |
| B21 | Search engine and homepage settings | parity | App/AppSettings.swift | [#48](https://github.com/wong04/zing/issues/48) | Done |
| B22 | Memory-only favicons | parity | Features/Browser/FaviconStore.swift | [#49](https://github.com/wong04/zing/issues/49) | Done |
| B23 | Camera, mic and permission prompts | parity | Features/Browser/BrowserTab.swift | [#50](https://github.com/wong04/zing/issues/50) | Done |
| B24 | Multiple windows | parity | App/KracknApp.swift (WindowSession) | [#51](https://github.com/wong04/zing/issues/51) | Done |
| B25 | Default browser + links from other apps | parity | App/KracknApp.swift (AppGate, LinkInbox) | [#52](https://github.com/wong04/zing/issues/52) | Done: owner's hand check passed 2026-11-11 (Default apps, links from other apps, [docs/release.md](docs/release.md#hand-check)); host tests `DefaultBrowserTests`, `ChromeApiDefaultBrowserTests`; e2e in `e2e/default-browser.spec.ts` and `e2e/open-with.spec.ts`. Open with for .pdf/.htm/.html came after that check ([#188](https://github.com/wong04/zing/pull/188)) and still needs a hand re-check |
| B26 | Confirm before quit | parity | App/AppSettings.swift (confirmQuit) | [#53](https://github.com/wong04/zing/issues/53) | Dropped |
| B27 | Clean user agent | parity | Features/Browser/UserAgent.swift | [#54](https://github.com/wong04/zing/issues/54) | Dropped |
| B28 | App sidebar with edge reveal | parity | App/Sidebar.swift | [#55](https://github.com/wong04/zing/issues/55) | Done |
| B29 | Floating tool panels | parity | App/FloatingPanels.swift | [#56](https://github.com/wong04/zing/issues/56) | Partial |
| B30 | Settings sheet | parity | Features/Settings/SettingsView.swift | [#57](https://github.com/wong04/zing/issues/57) | Done |
| B31 | Account gate | parity | Features/Account/AccountStore.swift, AuthView.swift | [#58](https://github.com/wong04/zing/issues/58) | Done |
| B32 | Sleeping tabs | new | - | [#59](https://github.com/wong04/zing/issues/59) | Done |
| B33 | Lazy session restore | new | - | [#60](https://github.com/wong04/zing/issues/60) | Done |
| B34 | Shared UI components | parity | App/Components.swift, FileComponents.swift, ScrollFade.swift | [#61](https://github.com/wong04/zing/issues/61) | Done: components in `src/renderer/src/components/` with `scrollEdges.test.ts`, `sliderMath.test.ts`, `toolDrag.test.ts`, `filePath.test.ts`; light + dark screenshots in `docs/screenshots/phase-6/` and `docs/screenshots/b30-settings-{light,dark}.png` |
| B35 | Link previews (Arc Peek) | new | - | [#160](https://github.com/wong04/zing/pull/160) | Done: e2e in `e2e/link-preview.spec.ts`; hand checks H1-H9 passed (2026-11-08 and 2026-11-09, real-input tier 8/8), recorded in [the design](docs/superpowers/specs/2026-11-08-link-preview-design.md); screenshots in `docs/screenshots/link-preview/` |

**B rows, 2026-11-01:** re-verified on the WebView2 host. Evidence for each Done row is in its issue, and every cited test passed on Windows CI ([run 41289375469](https://github.com/wong04/zing/actions/runs/41289375469), the whole e2e suite plus vitest and xUnit). Partial rows, still open:
- B11: domain blocking is Edge Strict, untested; full blocking waits on uBO Lite
- B25: the installer registers Zing as a browser and Settings offers to make it the default (Phase 8); a real install, Default apps and a link from another app wait on the owner's hand check in [docs/release.md](docs/release.md#hand-check)
- B29: no e2e; the toolbox isn’t there yet
- B34: mostly visual; needs light + dark screenshots

**Phase 3 close-out, 2026-11-01:**
- B3: `e2e/tabs.spec.ts` drags a tab along the strip with pointer events.
- B6: `e2e/lifecycle.spec.ts` closes a tab left alone past its rule on the test clock. It found a race: a tab closed while the sleeper read its page was recorded as a fault (`TabSleeper.TrySleep`, now a test in `TabLifecycleTests`).
- B9: a fullscreen element gives the page the whole window and the window goes fullscreen (`MainWindow.FollowFullScreen`); `e2e/page.spec.ts` checks the layout. The window's fullscreen presenter itself is a hand check, since test windows stay off-screen.
- B13: `e2e/core.spec.ts` adds to the Reading List, marks it read and deletes it in the panel.
- B16: Zoom In, Zoom Out and Actual Size step the tab's zoom by Krackn's steps (`Zoom`); `e2e/page.spec.ts` checks the page's scale, across its next page.
- B17: Print opens Edge's print preview for the page on screen; an e2e run records it instead of opening it (`ZING_DIALOGS_FILE`). The preview itself is a hand check.
- B21: `e2e/core.spec.ts` checks Home with an empty and a set homepage.
- B23: `e2e/page.spec.ts` allows a notification, then the camera and microphone (the engine's fake devices in e2e).

## M4 Tab bar position + split view

| ID | Item | Type | Krackn source | Issue | Status |
|---|---|---|---|---|---|
| T1 | Tab bar position setting: top / left / right | new | - | [#62](https://github.com/wong04/zing/issues/62) | Done |
| T2 | Vertical tab strip | new | - | [#63](https://github.com/wong04/zing/issues/63) | Done |
| T3 | Fold app sidebar into a left tab rail | new | App/Sidebar.swift | [#64](https://github.com/wong04/zing/issues/64) | Done |
| T4 | Virtualized tab list | new | - | [#65](https://github.com/wong04/zing/issues/65) | Done |
| T5 | Split view | new | - | [#66](https://github.com/wong04/zing/issues/66) | Done |
| T6 | Favourite tiles in the tab rail (saved tabs) | new | - | - | Done |
| T7 | Pinned items in folders (saved tabs) | new | - | - | Done |

## M5 Themes

| ID | Item | Type | Krackn source | Issue | Status |
|---|---|---|---|---|---|
| TH1 | Full theme model | new | App/Theme.swift, App/Accent.swift | [#67](https://github.com/wong04/zing/issues/67) | Done |
| TH2 | Theme presets | new | - | [#68](https://github.com/wong04/zing/issues/68) | Done |
| TH3 | Custom theme from a seed colour | new | App/Accent.swift | [#69](https://github.com/wong04/zing/issues/69) | Done |
| TH4 | Mica tint + Windows contrast themes | new | - | [#70](https://github.com/wong04/zing/issues/70) | Partial: contrast themes are done; the owner dropped Mica (README, Known differences) |
| TH5 | Switch theme without rebuilding state | parity | App/KracknApp.swift (WindowSession), StartPage.swift (restyleStartPages) | [#71](https://github.com/wong04/zing/issues/71) | Done |

## M6 Toolbox parity

| ID | Item | Type | Krackn source | Issue | Status |
|---|---|---|---|---|---|
| X1 | Open a Link: four methods | parity | Features/Unblock/RelayEngine.swift, PinnedHTTPClient.swift, DoHResolver.swift, UnblockView.swift | [#72](https://github.com/wong04/zing/issues/72) | Done |
| X2 | Relayed pages with subresources | parity | Features/Unblock/RelayedWebContent.swift | [#73](https://github.com/wong04/zing/issues/73) | Done |
| X3 | Diagnose | parity | Features/Unblock/DiagnoseView.swift, BlockDiagnostics.swift | [#74](https://github.com/wong04/zing/issues/74) | Done |
| X4 | DoH provider setting | parity | App/AppSettings.swift (dohProvider) | [#75](https://github.com/wong04/zing/issues/75) | Done |
| X5 | Network & VPN for Windows | parity | Features/Network/DoHProfile.swift, NetworkView.swift | [#76](https://github.com/wong04/zing/issues/76) | Done |
| X6 | Convert images | parity | Features/Convert/ConversionEngine.swift | [#77](https://github.com/wong04/zing/issues/77) | Done |
| X7 | Convert documents | parity | Features/Convert/ConversionEngine.swift | [#78](https://github.com/wong04/zing/issues/78) | Done |
| X8 | Convert audio/video + format matrix | parity | Features/Convert/FormatCatalog.swift, ConvertView.swift | [#79](https://github.com/wong04/zing/issues/79) | Done |
| X9 | Media download + Save page | parity | Features/Download/DownloadView.swift, DownloadErrors.swift | [#80](https://github.com/wong04/zing/issues/80) | Done |
| X10 | Engines on first use | parity | Core/ToolInstaller.swift, Features/Utilities/ToolSetupView.swift | [#81](https://github.com/wong04/zing/issues/81) | Done |
| X11 | Link inspection before load | parity | Features/Security/ThreatScanner.swift (securityRealtime) | [#82](https://github.com/wong04/zing/issues/82) | Done |
| X12 | Domain reputation via filtering DoH | parity | Features/Security/ThreatScanner.swift (securityDomainBlocking) | [#83](https://github.com/wong04/zing/issues/83) | Dropped |
| X13 | Download checks for Windows | parity | Features/Security/ThreatScanner.swift (securityDownloadScan, securitySignatureCheck) | [#84](https://github.com/wong04/zing/issues/84) | Dropped |
| X14 | System posture for Windows | parity | Features/Security/SecurityView.swift | [#85](https://github.com/wong04/zing/issues/85) | Dropped |
| X15 | File viewer (3D models dropped) | parity | Features/Viewer/FileViewerView.swift, PreviewBackends.swift | [#86](https://github.com/wong04/zing/issues/86) | Done (no 3D models) |
| X16 | Background remover | parity | Features/Utilities/BackgroundRemover.swift; Tests/bgremove | [#87](https://github.com/wong04/zing/issues/87) | Done |
| X17 | Graphing calculator | parity | Features/Utilities/GraphingCalculator.swift, GraphSurface.swift | [#88](https://github.com/wong04/zing/issues/88) | Done |
| X18 | Calculator, units, number base | parity | Features/Utilities/UtilitiesView.swift | [#89](https://github.com/wong04/zing/issues/89) | Done |
| X19 | Developer tools | parity | Features/Utilities/DevToolViews.swift | [#90](https://github.com/wong04/zing/issues/90) | Done |
| X20 | Text & design tools | parity | Features/Utilities/UtilitiesView.swift | [#91](https://github.com/wong04/zing/issues/91) | Done |
| X21 | Password generator + link cleaner | parity | Features/Utilities/UtilityTools.swift | [#92](https://github.com/wong04/zing/issues/92) | Done |
| X22 | Toolbox page, search and recents | parity | App/Navigation.swift, ToolboxSearch.swift | [#93](https://github.com/wong04/zing/issues/93) | Done |
| X23 | Drag files between tools | parity | App/DropRouter.swift, FileComponents.swift | [#94](https://github.com/wong04/zing/issues/94) | Done |
| X24 | Links page | new | - | [#141](https://github.com/wong04/zing/issues/141) | Done |
| X25 | PDF tools | new | - | [#142](https://github.com/wong04/zing/issues/142) | Done |

## M6b Password vault

| ID | Item | Type | Krackn source | Issue | Status |
|---|---|---|---|---|---|
| V1 | Vault storage + Windows Hello unlock | new | Features/Browser/KeychainAutofill.swift (replaced) | [#95](https://github.com/wong04/zing/issues/95) | Done |
| V2 | Sign-in field detection and fill | parity | Features/Browser/KeychainAutofill.swift | [#96](https://github.com/wong04/zing/issues/96) | Done |
| V3 | Save prompt, manage page, privacy banner | new | App/AppSettings.swift (passwordAutofill) | [#97](https://github.com/wong04/zing/issues/97) | Done |
| V4 | Encrypted password sync between PCs (Railway server, Google sign-in, merge with password history) | new | - | - | Partial: awaiting the owner's check against the deployed server |
| V5 | Restore on a new PC with the Zing password or a recovery key | new | - | - | Partial: awaiting the owner's check against the deployed server |

Evidence (e2e/vault.spec.ts unless noted; screenshots in [docs/screenshots/phase-6b](docs/screenshots/phase-6b)):

- V1: host tests `VaultStoreTests` (`SavedPasswordReadsBackAfterReopening`, `AKeyHelloCantOpenAsksToSetTheVaultUpAgainOnEveryPathThatNeedsIt`), `VaultCryptoTests` (`KdfParamsOutsideTheFloorAndCapsAreRefused`), `SettingsTests.ThePasswordVaultIsOffUntilThePersonTurnsItOn`; e2e `without Windows Hello the page says so and a password cannot be shown`, `after a reset Windows Hello key the page says to set the vault up again on this PC when a password is shown`, `WebView2 password saving and general autofill are off in every view, in the chrome and in pages`. Screenshots: `vault-hello-error-light.png`, `vault-hello-error-dark.png`.
- V2: e2e `focusing the username box shows the saved username under it, and choosing it fills both boxes`, `the picker follows real keys: arrows move, Enter chooses, Esc closes; page-relayed keys do nothing`, `an iframe from another local port gets no picker, even posting a forged message itself, while the top page still does`, `with the vault off, no picker appears, even with a login saved for the page`.
- V3: e2e `a successful sign-in to a new site prompts Save, and Save stores the login after Hello`, `a changed password prompts an update that names the saved login, and Update replaces it`, `Never for this site stops every later prompt for it, saving and updating alike, and is kept in the vault`, `the Settings switch turns the vault on, and the privacy banner warns once it holds an entry`, `showing a password asks Windows Hello, a cancel shows and copies nothing, and a passed Hello is not asked again for 5 minutes`, `export warns first, writes Chrome-format CSV to Downloads after Hello, and a fresh vault re-imports the same entries`. Screenshots: `vault-empty-*.png`, `vault-list-*.png` (light and dark).
- V4: host tests `SyncEngineTests` (`AnEditOnAAppearsOnB`, `BothChangedDifferentFieldsAndBothPcsEndWithBothChanges`, `BothChangedThePasswordSoTheNewerWinsAndTheOlderIsInTheHistoryOnBothPcs`, `APushWhoseAnswerIsLostIsNotUploadedTwice`, `ARefreshWhoseAnswerIsLostIsRetriedWithinTwoMinutesSoNoPcIsSignedOut`, `APulledPageThatCouldNotBeStoredIsPulledAgainAndNotTakenAsWithheld`), `RollbackGuardTests`, `SyncSetupTests`, `SyncClientTests`, `MergeTests`, and the server's `RefreshFamilyTests`; e2e (e2e/sync.spec.ts) `Turn on sync signs in against the sync server, stays signed in after a restart, and Sign out forgets it`, `turning on sync shows the recovery key, and nothing else of the vault, until it is confirmed saved, even after a restart, and then the account can’t be deleted`, `a Zing password too weak for sync is changed to a suggested passphrase in the same flow`, `a login saved on one PC reaches the other, and an edit made there comes back`, `an edit made on each PC while one was offline merges field by field, and a delete reaches the other PC`. No issue yet: the rows were added with this work, and get one when the owner opens it.
- V5: host tests `SyncRestoreTests`, and `SyncBridgeTests` (`OnAFreshProfileRestoreFromSyncTurnsTheVaultOnAndKeepsTheSignInOnlyOnceTheAccountIsMade`, `ARestoreStepWhoseRefreshAnswerIsLostIsRetriedWithinTwoMinutesSoNoPcIsSignedOut`); e2e (e2e/sync.spec.ts) `restoring from sync on a new PC refuses a wrong password and makes nothing, then the password brings back every login`, `restoring with the recovery key asks for a new password at once, and the old password stops working on the next restore`, `a password changed on one PC makes the other ask for the new one once, at its next start`. No issue yet, as for V4.
- The rows stay marked pending until the owner has checked the real Windows Hello prompt: save, fill, reveal, a cancel, the picker's arrow keys, Enter and Esc, and the copied password missing from Win+V with the 30 s clear leaving a newer copy alone. Remove the marker and these lines' hedging once that is done.

## M6c Import

| ID | Item | Type | Krackn source | Issue | Status |
|---|---|---|---|---|---|
| I1 | Import from Chrome, Edge, Brave | new | - | [#98](https://github.com/wong04/zing/issues/98) | Open |
| I2 | Import from Dia | new | - | [#99](https://github.com/wong04/zing/issues/99) | Open |
| I3 | Import from Arc | new | - | [#100](https://github.com/wong04/zing/issues/100) | Open |
| I4 | Generic bookmark and password import | new | - | [#101](https://github.com/wong04/zing/issues/101) | Open |

## M7 Logo + branding

| ID | Item | Type | Krackn source | Issue | Status |
|---|---|---|---|---|---|
| BR1 | Zing logo concepts | new | App/KracknLogo.swift | [#102](https://github.com/wong04/zing/issues/102) | Done |
| BR2 | Icon generator + logo test | parity | Tools/make-icon.swift, Tools/make-icon.sh; Tests/logo | [#103](https://github.com/wong04/zing/issues/103) | Done |
| BR3 | Name collision check for 'Zing' | new | - | [#104](https://github.com/wong04/zing/issues/104) | Done |

- **BR1:** the logo is the C3 Slice. The first concepts (A–D) are in `assets/brand/concepts/`, and the Slice versions in `assets/brand/concepts/slice/`. The chosen tiles are `assets/brand/zing.svg` (frosted) and `assets/brand/zing-dark-tile.svg`.
- **BR2:** `npm run make-icons` draws the SVGs with headless Edge into `build/icons*` (PNGs 16–1024 and a `.ico` with 16–256) and into `build/installer` (Velopack's splash image and the two MSI bitmaps). `src/shared/brand/logo.test.ts` is the port of `Tests/logo`. `e2e/brand.spec.ts` checks the live window and `Zing.exe`, and its screenshots are in `docs/screenshots/phase-7/`. There's no `.icns`, because Zing is Windows-only.
- **BR3:** `assets/brand/NAME-CHECK.md`. No browser is called Zing, but the word is a crowded trademark (medium-high risk).

## M8 Packaging + release

| ID | Item | Type | Krackn source | Issue | Status |
|---|---|---|---|---|---|
| P1 | Velopack installer | infra | bundle.sh | [#105](https://github.com/wong04/zing/issues/105) | Done: owner's hand check passed 2026-11-11 (install, uninstall keeps `%APPDATA%\Zing`, [docs/release.md](docs/release.md#hand-check)); `scripts/pack.ps1`, `.github/workflows/release.yml` |
| P2 | Auto-update from GitHub Releases | infra | - | [#106](https://github.com/wong04/zing/issues/106) | Done: owner's hand check passed 2026-11-11 (0.9.0 to 0.9.1 with data intact, [docs/release.md](docs/release.md#hand-check)); host tests `UpdaterTests`, `ChromeApiUpdatesTests`; e2e in `e2e/update.spec.ts` |
| P3 | Code signing | infra | - | [#107](https://github.com/wong04/zing/issues/107) | Open: decided to sign later, with Azure Trusted Signing (about $10/month, identity check) or SignPath Foundation (free for open source); `release.yml` has a placeholder sign step that turns on with an `AZURE_SIGNING_ACCOUNT` secret ([docs/release.md](docs/release.md#signing)) |
| P4 | winget manifest | infra | - | [#108](https://github.com/wong04/zing/issues/108) | Partial: `winget/manifests/` passes `winget validate`; submission is turned on at 1.0 ([docs/release.md](docs/release.md#winget-at-10)) |
| P5 | Credits and third-party notices | infra | - | [#109](https://github.com/wong04/zing/issues/109) | Done: `scripts/notices.test.ts`, host tests `ChromeApiCreditsTests`, and the Credits assertion in `e2e/update.spec.ts` |
| P6 | Web-compat smoke tests in CI | infra | - | [#110](https://github.com/wong04/zing/issues/110) | Open |
| P7 | Dependency updates (Windows App SDK, .NET, npm); Chromium security fixes now come with the Evergreen WebView2 runtime | infra | - | [#111](https://github.com/wong04/zing/issues/111) | Done: `.github/dependabot.yml` (weekly npm, NuGet and GitHub Actions); Chromium security fixes arrive with the Evergreen WebView2 runtime |

**Phase 8, 2026-11-11:** the installer, updater, default-browser registration and credits are built and tested; the release guide is [docs/release.md](docs/release.md).
- P1: `npm run pack` built 0.9.0 and 0.9.1 into one local feed (`artifacts/releases`); the `release` workflow packs on every PR that touches packaging and drafts a release on a `v*` tag.
- P2: host tests `UpdaterTests` and `ChromeApiUpdatesTests`; e2e `e2e/update.spec.ts` (Check now finds a ready update, every window shows the pill, clicking it restarts once). e2e runs use a fake update source and never contact GitHub.
- B25: host tests `DefaultBrowserTests` and `ChromeApiDefaultBrowserTests`; e2e `e2e/default-browser.spec.ts` (the offer waits for a second launch, opens Default apps, and stops after Don't ask again; the Settings row by install state).
- B25, files from Open with: the installer also registers `ZingPDF` for `.pdf` (as Edge and Chrome do) beside `ZingHTML`, and Zing opens a `.pdf`, `.htm` or `.html` path it is handed: a PDF in the Files viewer, an HTML file in a tab, cold or into a running Zing. Host tests `LaunchFilesTests` and `DefaultBrowserTests`; e2e `e2e/open-with.spec.ts`. The owner's first hand check (2026-11-11) passed install, update, Default apps, a link from another app and uninstall; it found `.pdf` missing from Open with, so Open with on a `.pdf` and an `.html` is re-checked.
- P5: `scripts/notices.test.ts`, `ChromeApiCreditsTests`, and the Credits region in `e2e/update.spec.ts`.
- Screenshots, light and dark, in `docs/screenshots/phase-8/`: About, the update pill (alone and in a window), the default-browser offer and the Settings row.
- P1, P2 and B25 stay Partial until the owner runs the hand check in [docs/release.md](docs/release.md#hand-check) (install 0.9.0, make Zing the default, update to 0.9.1 from the local feed, uninstall and keep the data).

## M9 Parity audit

| ID | Item | Type | Krackn source | Issue | Status |
|---|---|---|---|---|---|
| A1 | Parity audit and remove reference/krackn | infra | - | [#112](https://github.com/wong04/zing/issues/112) | Open |
