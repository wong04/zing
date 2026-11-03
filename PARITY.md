# Parity checklist

Every Krackn feature and every new Zing feature, one row each, one GitHub issue each. A row closes only with evidence linked in its issue: a ported test, or a screenshot for UI (light + dark). Something Windows genuinely can't match is moved to **Known differences on Windows** in the README rather than silently dropped.

- **Baseline:** Krackn [`9ceaaa8`](https://github.com/yityler/Krackn/commit/9ceaaa8c920acc941ac61044bfb6c6f9f04d083e) (also in `reference/KRACKN_SHA`). Newer Krackn commits arrive as an `upstream` issue every Monday. Port or skip each one, then bump the SHA.
- **Board:** [issues by milestone](https://github.com/wong04/zing/milestones)
- **Types:** `parity` exists in Krackn · `port-logic` pure logic ported with its Krackn tests · `new` new in Zing · `infra` build/CI/release

## Replaced, not ported

These are macOS-only and have a Windows replacement tracked above:

| Krackn | Zing |
|---|---|
| Keychain AutoFill bridge | Built-in vault (V1–V3) |
| `.mobileconfig` DNS profile | PowerShell DoH script (X5) |
| `sips`, `textutil`, PDFKit | sharp, mammoth/pandoc, printToPDF (X6, X7) |
| AVKit, QuickLook | `<video>`, metadata fallback (X15; SceneKit and 3D models dropped) |
| Vision background removal | ONNX Runtime (X16) |
| Traffic-light placement | Frameless window + titleBarOverlay (S2) |
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
| L12 | Port DownloadPolicy | port-logic | Features/Browser/DownloadPolicy.swift; Tests/download | [#23](https://github.com/wong04/zing/issues/23) | Partial |
| L13 | Port DevTools pure logic | port-logic | Features/Utilities/DevTools.swift, UtilityTools.swift | [#24](https://github.com/wong04/zing/issues/24) | Done |
| L14 | Port accent generator + WCAG AA test | port-logic | App/Accent.swift; Tests/theme | [#25](https://github.com/wong04/zing/issues/25) | Done |
| L15 | Port DropRouter | port-logic | App/DropRouter.swift; Tests/drop | [#26](https://github.com/wong04/zing/issues/26) | Done |
| L16 | Port tracking-parameter stripper | port-logic | Features/Browser/BrowserTab.swift, UtilityTools.swift | [#27](https://github.com/wong04/zing/issues/27) | Done |

**L rows, 2026-11-02:** re-verified by reading each port and its tests; the shared suites passed locally (1,265 tests) and CI passed on `7edc3dd`. Done here means the logic is ported and tested, not that the app uses it yet:
- **Waiting for the toolbox:** L4 (X11–X13), L5 (X3), L6 (X18), L7 (X17), L9 (X22), L13 (X19) and L15 (X23) have no caller until those rows are built. L4–L7, L9 and L13 exist only in TypeScript.
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
| B25 | Default browser + links from other apps | parity | App/KracknApp.swift (AppGate, LinkInbox) | [#52](https://github.com/wong04/zing/issues/52) | Partial |
| B26 | Confirm before quit | parity | App/AppSettings.swift (confirmQuit) | [#53](https://github.com/wong04/zing/issues/53) | Dropped |
| B27 | Clean user agent | parity | Features/Browser/UserAgent.swift | [#54](https://github.com/wong04/zing/issues/54) | Dropped |
| B28 | App sidebar with edge reveal | parity | App/Sidebar.swift | [#55](https://github.com/wong04/zing/issues/55) | Done |
| B29 | Floating tool panels | parity | App/FloatingPanels.swift | [#56](https://github.com/wong04/zing/issues/56) | Partial |
| B30 | Settings sheet | parity | Features/Settings/SettingsView.swift | [#57](https://github.com/wong04/zing/issues/57) | Done |
| B31 | Account gate | parity | Features/Account/AccountStore.swift, AuthView.swift | [#58](https://github.com/wong04/zing/issues/58) | Done |
| B32 | Sleeping tabs | new | - | [#59](https://github.com/wong04/zing/issues/59) | Done |
| B33 | Lazy session restore | new | - | [#60](https://github.com/wong04/zing/issues/60) | Done |
| B34 | Shared UI components | parity | App/Components.swift, FileComponents.swift, ScrollFade.swift | [#61](https://github.com/wong04/zing/issues/61) | Partial |

**B rows, 2026-11-01:** re-verified on the WebView2 host. Evidence for each Done row is in its issue, and every cited test passed on Windows CI ([run 41289375469](https://github.com/wong04/zing/actions/runs/41289375469), the whole e2e suite plus vitest and xUnit). Partial rows, still open:
- B11: domain blocking is Edge Strict, untested; full blocking waits on uBO Lite
- B25: default-browser registration waits on the installer (Phase 8)
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
| X1 | Open a Link: four methods | parity | Features/Unblock/RelayEngine.swift, PinnedHTTPClient.swift, DoHResolver.swift, UnblockView.swift | [#72](https://github.com/wong04/zing/issues/72) | Open |
| X2 | Relayed pages with subresources | parity | Features/Unblock/RelayedWebContent.swift | [#73](https://github.com/wong04/zing/issues/73) | Open |
| X3 | Diagnose | parity | Features/Unblock/DiagnoseView.swift, BlockDiagnostics.swift | [#74](https://github.com/wong04/zing/issues/74) | Open |
| X4 | DoH provider setting | parity | App/AppSettings.swift (dohProvider) | [#75](https://github.com/wong04/zing/issues/75) | Open |
| X5 | Network & VPN for Windows | parity | Features/Network/DoHProfile.swift, NetworkView.swift | [#76](https://github.com/wong04/zing/issues/76) | Open |
| X6 | Convert images | parity | Features/Convert/ConversionEngine.swift | [#77](https://github.com/wong04/zing/issues/77) | Open |
| X7 | Convert documents | parity | Features/Convert/ConversionEngine.swift | [#78](https://github.com/wong04/zing/issues/78) | Open |
| X8 | Convert audio/video + format matrix | parity | Features/Convert/FormatCatalog.swift, ConvertView.swift | [#79](https://github.com/wong04/zing/issues/79) | Open |
| X9 | Media download + Save page | parity | Features/Download/DownloadView.swift, DownloadErrors.swift | [#80](https://github.com/wong04/zing/issues/80) | Open |
| X10 | Engines on first use | parity | Core/ToolInstaller.swift, Features/Utilities/ToolSetupView.swift | [#81](https://github.com/wong04/zing/issues/81) | Open |
| X11 | Link inspection before load | parity | Features/Security/ThreatScanner.swift (securityRealtime) | [#82](https://github.com/wong04/zing/issues/82) | Open |
| X12 | Domain reputation via filtering DoH | parity | Features/Security/ThreatScanner.swift (securityDomainBlocking) | [#83](https://github.com/wong04/zing/issues/83) | Dropped |
| X13 | Download checks for Windows | parity | Features/Security/ThreatScanner.swift (securityDownloadScan, securitySignatureCheck) | [#84](https://github.com/wong04/zing/issues/84) | Dropped |
| X14 | System posture for Windows | parity | Features/Security/SecurityView.swift | [#85](https://github.com/wong04/zing/issues/85) | Dropped |
| X15 | File viewer (3D models dropped) | parity | Features/Viewer/FileViewerView.swift, PreviewBackends.swift | [#86](https://github.com/wong04/zing/issues/86) | Open |
| X16 | Background remover | parity | Features/Utilities/BackgroundRemover.swift; Tests/bgremove | [#87](https://github.com/wong04/zing/issues/87) | Open |
| X17 | Graphing calculator | parity | Features/Utilities/GraphingCalculator.swift, GraphSurface.swift | [#88](https://github.com/wong04/zing/issues/88) | Open |
| X18 | Calculator, units, number base | parity | Features/Utilities/UtilitiesView.swift | [#89](https://github.com/wong04/zing/issues/89) | Open |
| X19 | Developer tools | parity | Features/Utilities/DevToolViews.swift | [#90](https://github.com/wong04/zing/issues/90) | Open |
| X20 | Text & design tools | parity | Features/Utilities/UtilitiesView.swift | [#91](https://github.com/wong04/zing/issues/91) | Open |
| X21 | Password generator + link cleaner | parity | Features/Utilities/UtilityTools.swift | [#92](https://github.com/wong04/zing/issues/92) | Open |
| X22 | Toolbox page, search and recents | parity | App/Navigation.swift, ToolboxSearch.swift | [#93](https://github.com/wong04/zing/issues/93) | Open |
| X23 | Drag files between tools | parity | App/DropRouter.swift, FileComponents.swift | [#94](https://github.com/wong04/zing/issues/94) | Open |
| X24 | Links page | new | - | - | Open |

## M6b Password vault

| ID | Item | Type | Krackn source | Issue | Status |
|---|---|---|---|---|---|
| V1 | Vault storage + Windows Hello unlock | new | Features/Browser/KeychainAutofill.swift (replaced) | [#95](https://github.com/wong04/zing/issues/95) | Open |
| V2 | Sign-in field detection and fill | parity | Features/Browser/KeychainAutofill.swift | [#96](https://github.com/wong04/zing/issues/96) | Open |
| V3 | Save prompt, manage page, privacy banner | new | App/AppSettings.swift (passwordAutofill) | [#97](https://github.com/wong04/zing/issues/97) | Open |

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
| BR1 | Zing logo concepts | new | App/KracknLogo.swift | [#102](https://github.com/wong04/zing/issues/102) | Open |
| BR2 | Icon generator + logo test | parity | Tools/make-icon.swift, Tools/make-icon.sh; Tests/logo | [#103](https://github.com/wong04/zing/issues/103) | Open |
| BR3 | Name collision check for 'Zing' | new | - | [#104](https://github.com/wong04/zing/issues/104) | Open |

## M8 Packaging + release

| ID | Item | Type | Krackn source | Issue | Status |
|---|---|---|---|---|---|
| P1 | NSIS installer | infra | bundle.sh | [#105](https://github.com/wong04/zing/issues/105) | Open |
| P2 | Auto-update from GitHub Releases | infra | - | [#106](https://github.com/wong04/zing/issues/106) | Open |
| P3 | Code signing | infra | - | [#107](https://github.com/wong04/zing/issues/107) | Open |
| P4 | winget manifest | infra | - | [#108](https://github.com/wong04/zing/issues/108) | Open |
| P5 | Credits and third-party notices | infra | - | [#109](https://github.com/wong04/zing/issues/109) | Open |
| P6 | Web-compat smoke tests in CI | infra | - | [#110](https://github.com/wong04/zing/issues/110) | Open |
| P7 | Dependency updates (Windows App SDK, .NET, npm); Chromium security fixes now come with the Evergreen WebView2 runtime | infra | - | [#111](https://github.com/wong04/zing/issues/111) | Open |

## M9 Parity audit

| ID | Item | Type | Krackn source | Issue | Status |
|---|---|---|---|---|---|
| A1 | Parity audit and remove reference/krackn | infra | - | [#112](https://github.com/wong04/zing/issues/112) | Open |
