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
| Gatekeeper, notarization, quarantine, XProtect | Authenticode, Mark-of-the-Web, Defender (X13) |
| SIP, FileVault status | Secure Boot, BitLocker, SmartScreen (X14) |
| `sips`, `textutil`, PDFKit | sharp, mammoth/pandoc, printToPDF (X6, X7) |
| SceneKit, AVKit, QuickLook | three.js, `<video>`, metadata fallback (X15) |
| Vision background removal | ONNX Runtime (X16) |
| Traffic-light placement | Frameless window + titleBarOverlay (S2) |
| `brew install` | `winget install` (X10) |

## Dropped

WebKit-only internals with no Chromium equivalent: `WebKitTuning.swift` (private WebKit switches), `BrowserDiagnostics.swift` (canvas blur diagnostics), `Migration.swift` (one-time "All in One" rename).

## M0 Repo + tracking

| ID | Item | Type | Krackn source | Issue | Status |
|---|---|---|---|---|---|
| O1 | Weekly upstream Krackn watch | infra | - | [#1](https://github.com/wong04/zing/issues/1) | Done |

## M1 Shell on Windows

| ID | Item | Type | Krackn source | Issue | Status |
|---|---|---|---|---|---|
| S1 | Electron + TS + React skeleton with Zing identity | new | - | [#2](https://github.com/wong04/zing/issues/2) | Open |
| S2 | Frameless window chrome for Windows 11 | parity | App/Components.swift (WindowChrome, WindowDragArea) | [#3](https://github.com/wong04/zing/issues/3) | Open |
| S3 | Overlay layer for UI drawn over the page | parity | Features/Browser/WebViewHost.swift (shielded) | [#4](https://github.com/wong04/zing/issues/4) | Open |
| S4 | Tab record model decoupled from webContents | new | Features/Browser/TabOrganizer.swift (TabSnapshot) | [#5](https://github.com/wong04/zing/issues/5) | Open |
| S5 | In-memory session passed as a parameter | parity | Features/Browser/BrowserTab.swift | [#6](https://github.com/wong04/zing/issues/6) | Open |
| S6 | Command registry + Windows shortcut map | parity | App/Commands.swift | [#7](https://github.com/wong04/zing/issues/7) | Open |
| S7 | Typed settings store with registered defaults | parity | App/AppSettings.swift | [#8](https://github.com/wong04/zing/issues/8) | Open |
| S8 | CSS theme tokens from Krackn's palette | parity | App/Theme.swift, App/Accent.swift | [#9](https://github.com/wong04/zing/issues/9) | Open |
| S9 | CI on windows-latest | infra | run-tests.sh | [#10](https://github.com/wong04/zing/issues/10) | Open |
| S10 | Performance budget suite in CI | infra | - | [#11](https://github.com/wong04/zing/issues/11) | Open |

## M2 Pure logic port

| ID | Item | Type | Krackn source | Issue | Status |
|---|---|---|---|---|---|
| L1 | Port TabOrganizer + SiteGroup | port-logic | Features/Browser/TabOrganizer.swift; Tests/taborg | [#12](https://github.com/wong04/zing/issues/12) | Open |
| L2 | Port TabReorder drag geometry | port-logic | Features/Browser/TabReorder.swift; Tests/tabs | [#13](https://github.com/wong04/zing/issues/13) | Open |
| L3 | Port AddressInput go-or-search rule | port-logic | Features/Browser/AddressInput.swift; Tests/unit | [#14](https://github.com/wong04/zing/issues/14) | Open |
| L4 | Port ThreatModel heuristics | port-logic | Features/Security/ThreatModel.swift; Tests/security | [#15](https://github.com/wong04/zing/issues/15) | Open |
| L5 | Port BlockDiagnostics.classify | port-logic | Features/Unblock/BlockDiagnostics.swift; Tests/unit | [#16](https://github.com/wong04/zing/issues/16) | Open |
| L6 | Port Expression parser | port-logic | Features/Utilities/Expression.swift; Tests/calc | [#17](https://github.com/wong04/zing/issues/17) | Open |
| L7 | Port GraphEngine | port-logic | Features/Utilities/GraphEngine.swift; Tests/graph | [#18](https://github.com/wong04/zing/issues/18) | Open |
| L8 | Port PanelLayout collision resolution | port-logic | App/PanelLayout.swift; Tests/panels | [#19](https://github.com/wong04/zing/issues/19) | Open |
| L9 | Port ToolboxSearch + keyword table | port-logic | App/ToolboxSearch.swift; Tests/toolbox | [#20](https://github.com/wong04/zing/issues/20) | Open |
| L10 | Port StartShortcuts | port-logic | Features/Browser/StartShortcuts.swift; Tests/unit | [#21](https://github.com/wong04/zing/issues/21) | Open |
| L11 | Port FaviconChoice | port-logic | Features/Browser/FaviconChoice.swift | [#22](https://github.com/wong04/zing/issues/22) | Open |
| L12 | Port DownloadPolicy | port-logic | Features/Browser/DownloadPolicy.swift; Tests/download | [#23](https://github.com/wong04/zing/issues/23) | Open |
| L13 | Port DevTools pure logic | port-logic | Features/Utilities/DevTools.swift, UtilityTools.swift | [#24](https://github.com/wong04/zing/issues/24) | Open |
| L14 | Port accent generator + WCAG AA test | port-logic | App/Accent.swift; Tests/theme | [#25](https://github.com/wong04/zing/issues/25) | Open |
| L15 | Port DropRouter | port-logic | App/DropRouter.swift; Tests/drop | [#26](https://github.com/wong04/zing/issues/26) | Open |
| L16 | Port tracking-parameter stripper | port-logic | Features/Browser/BrowserTab.swift, UtilityTools.swift | [#27](https://github.com/wong04/zing/issues/27) | Open |

## M3 Browser parity

| ID | Item | Type | Krackn source | Issue | Status |
|---|---|---|---|---|---|
| B1 | Core tab actions | parity | App/Commands.swift, Features/Browser/BrowserModel.swift | [#28](https://github.com/wong04/zing/issues/28) | Open |
| B2 | Pinned tabs | parity | Features/Browser/BrowserView.swift | [#29](https://github.com/wong04/zing/issues/29) | Open |
| B3 | Drag-to-reorder tabs | parity | Features/Browser/TabReorder.swift, BrowserView.swift | [#30](https://github.com/wong04/zing/issues/30) | Open |
| B4 | Tab arrangements | parity | Features/Browser/TabOrganizer.swift | [#31](https://github.com/wong04/zing/issues/31) | Open |
| B5 | Tab stacks (Separate / Dropdown) | parity | Features/Browser/BrowserView.swift (TabStackChip, TabStackDropdown) | [#32](https://github.com/wong04/zing/issues/32) | Open |
| B6 | Auto-close tabs | parity | Features/Browser/TabOrganizer.swift | [#33](https://github.com/wong04/zing/issues/33) | Open |
| B7 | Popups keep window.opener (OAuth) | parity | Features/Browser/BrowserTab.swift (createWebViewWith) | [#34](https://github.com/wong04/zing/issues/34) | Open |
| B8 | Downloads | parity | Features/Browser/DownloadPolicy.swift, BrowserChrome.swift | [#35](https://github.com/wong04/zing/issues/35) | Open |
| B9 | Fullscreen video | parity | Features/Browser/WebViewHost.swift | [#36](https://github.com/wong04/zing/issues/36) | Open |
| B10 | Keeps-nothing privacy model | parity | README 'Privacy, stated precisely' | [#37](https://github.com/wong04/zing/issues/37) | Open |
| B11 | Tracker + ad blocking | parity | README (48 tracker domains) | [#38](https://github.com/wong04/zing/issues/38) | Open |
| B12 | Opt-in persistent profile and tab restore | parity | App/AppSettings.swift (persistentProfile, restoreTabs), SettingsView.swift | [#39](https://github.com/wong04/zing/issues/39) | Open |
| B13 | Bookmarks, Reading List, Favourites bar | parity | Features/Browser/BrowserLibrary.swift, LibraryStore.swift | [#40](https://github.com/wong04/zing/issues/40) | Open |
| B14 | Find in page | parity | Features/Browser/BrowserTabActions.swift | [#41](https://github.com/wong04/zing/issues/41) | Open |
| B15 | Reader mode | parity | Features/Browser/BrowserTabActions.swift | [#42](https://github.com/wong04/zing/issues/42) | Open |
| B16 | Zoom and text size | parity | Features/Browser/BrowserTabActions.swift | [#43](https://github.com/wong04/zing/issues/43) | Open |
| B17 | Print | parity | Features/Browser/BrowserTabActions.swift | [#44](https://github.com/wong04/zing/issues/44) | Open |
| B18 | Tab overview | parity | Features/Browser/BrowserChrome.swift | [#45](https://github.com/wong04/zing/issues/45) | Open |
| B19 | Back/forward history menus | parity | Features/Browser/BrowserChrome.swift | [#46](https://github.com/wong04/zing/issues/46) | Open |
| B20 | Start page | parity | Features/Browser/StartPage.swift, StartShortcuts.swift | [#47](https://github.com/wong04/zing/issues/47) | Open |
| B21 | Search engine and homepage settings | parity | App/AppSettings.swift | [#48](https://github.com/wong04/zing/issues/48) | Open |
| B22 | Memory-only favicons | parity | Features/Browser/FaviconStore.swift | [#49](https://github.com/wong04/zing/issues/49) | Open |
| B23 | Camera, mic and permission prompts | parity | Features/Browser/BrowserTab.swift | [#50](https://github.com/wong04/zing/issues/50) | Open |
| B24 | Multiple windows | parity | App/KracknApp.swift (WindowSession) | [#51](https://github.com/wong04/zing/issues/51) | Open |
| B25 | Default browser + links from other apps | parity | App/KracknApp.swift (AppGate, LinkInbox) | [#52](https://github.com/wong04/zing/issues/52) | Open |
| B26 | Confirm before quit | parity | App/AppSettings.swift (confirmQuit) | [#53](https://github.com/wong04/zing/issues/53) | Open |
| B27 | Clean user agent | parity | Features/Browser/UserAgent.swift | [#54](https://github.com/wong04/zing/issues/54) | Open |
| B28 | App sidebar with edge reveal | parity | App/Sidebar.swift | [#55](https://github.com/wong04/zing/issues/55) | Open |
| B29 | Floating tool panels | parity | App/FloatingPanels.swift | [#56](https://github.com/wong04/zing/issues/56) | Open |
| B30 | Settings sheet | parity | Features/Settings/SettingsView.swift | [#57](https://github.com/wong04/zing/issues/57) | Open |
| B31 | Account gate | parity | Features/Account/AccountStore.swift, AuthView.swift | [#58](https://github.com/wong04/zing/issues/58) | Open |
| B32 | Sleeping tabs | new | - | [#59](https://github.com/wong04/zing/issues/59) | Open |
| B33 | Lazy session restore | new | - | [#60](https://github.com/wong04/zing/issues/60) | Open |
| B34 | Shared UI components | parity | App/Components.swift, FileComponents.swift, ScrollFade.swift | [#61](https://github.com/wong04/zing/issues/61) | Open |

## M4 Tab bar position + split view

| ID | Item | Type | Krackn source | Issue | Status |
|---|---|---|---|---|---|
| T1 | Tab bar position setting: top / left / right | new | - | [#62](https://github.com/wong04/zing/issues/62) | Open |
| T2 | Vertical tab strip | new | - | [#63](https://github.com/wong04/zing/issues/63) | Open |
| T3 | Fold app sidebar into a left tab rail | new | App/Sidebar.swift | [#64](https://github.com/wong04/zing/issues/64) | Open |
| T4 | Virtualized tab list | new | - | [#65](https://github.com/wong04/zing/issues/65) | Open |
| T5 | Split view | new | - | [#66](https://github.com/wong04/zing/issues/66) | Open |

## M5 Themes

| ID | Item | Type | Krackn source | Issue | Status |
|---|---|---|---|---|---|
| TH1 | Full theme model | new | App/Theme.swift, App/Accent.swift | [#67](https://github.com/wong04/zing/issues/67) | Open |
| TH2 | Theme presets | new | - | [#68](https://github.com/wong04/zing/issues/68) | Open |
| TH3 | Custom theme from a seed colour | new | App/Accent.swift | [#69](https://github.com/wong04/zing/issues/69) | Open |
| TH4 | Mica tint + Windows contrast themes | new | - | [#70](https://github.com/wong04/zing/issues/70) | Open |
| TH5 | Switch theme without rebuilding state | parity | App/KracknApp.swift (WindowSession), StartPage.swift (restyleStartPages) | [#71](https://github.com/wong04/zing/issues/71) | Open |

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
| X10 | Tool setup via winget | parity | Core/ToolInstaller.swift, Features/Utilities/ToolSetupView.swift | [#81](https://github.com/wong04/zing/issues/81) | Open |
| X11 | Link inspection before load | parity | Features/Security/ThreatScanner.swift (securityRealtime) | [#82](https://github.com/wong04/zing/issues/82) | Open |
| X12 | Domain reputation via filtering DoH | parity | Features/Security/ThreatScanner.swift (securityDomainBlocking) | [#83](https://github.com/wong04/zing/issues/83) | Open |
| X13 | Download checks for Windows | parity | Features/Security/ThreatScanner.swift (securityDownloadScan, securitySignatureCheck) | [#84](https://github.com/wong04/zing/issues/84) | Open |
| X14 | System posture for Windows | parity | Features/Security/SecurityView.swift | [#85](https://github.com/wong04/zing/issues/85) | Open |
| X15 | File viewer | parity | Features/Viewer/FileViewerView.swift, PreviewBackends.swift | [#86](https://github.com/wong04/zing/issues/86) | Open |
| X16 | Background remover | parity | Features/Utilities/BackgroundRemover.swift; Tests/bgremove | [#87](https://github.com/wong04/zing/issues/87) | Open |
| X17 | Graphing calculator | parity | Features/Utilities/GraphingCalculator.swift, GraphSurface.swift | [#88](https://github.com/wong04/zing/issues/88) | Open |
| X18 | Calculator, units, number base | parity | Features/Utilities/UtilitiesView.swift | [#89](https://github.com/wong04/zing/issues/89) | Open |
| X19 | Developer tools | parity | Features/Utilities/DevToolViews.swift | [#90](https://github.com/wong04/zing/issues/90) | Open |
| X20 | Text & design tools | parity | Features/Utilities/UtilitiesView.swift | [#91](https://github.com/wong04/zing/issues/91) | Open |
| X21 | Password generator + link cleaner | parity | Features/Utilities/UtilityTools.swift | [#92](https://github.com/wong04/zing/issues/92) | Open |
| X22 | Toolbox page, search and recents | parity | App/Navigation.swift, ToolboxSearch.swift | [#93](https://github.com/wong04/zing/issues/93) | Open |
| X23 | Drag files between tools | parity | App/DropRouter.swift, FileComponents.swift | [#94](https://github.com/wong04/zing/issues/94) | Open |

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
| P7 | Renovate for Electron security releases | infra | - | [#111](https://github.com/wong04/zing/issues/111) | Open |

## M9 Parity audit

| ID | Item | Type | Krackn source | Issue | Status |
|---|---|---|---|---|---|
| A1 | Parity audit and remove reference/krackn | infra | - | [#112](https://github.com/wong04/zing/issues/112) | Open |
