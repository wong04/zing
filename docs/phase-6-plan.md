# Phase 6 Toolbox Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the "Toolbox isn't implemented yet" notice with a working Toolbox: small offline tools, a links page, getting past blocked sites, and on-machine file tools.

**Architecture:** The Toolbox is a sheet in the surface view, and each tool is a code-split React component in a floating panel, which is the window `PanelHost` already reserves for it. Pure logic stays in `src/shared`, where most is already ported. Everything that needs sockets, processes or files moves to C# behind the bridge, because the renderer has no Node. Engines are separate programs downloaded on first use.

**Tech Stack:** TypeScript, React, Vitest, Playwright over CDP; C#, xUnit, WebView2; FFmpeg, ImageMagick, Pandoc, yt-dlp, onnxruntime-web.

**Spec:** `docs/phase-6.md`.

## Global Constraints

- Tabs for indentation; early returns; raise specific errors; no silent fallbacks. Match the surrounding code.
- Test runs stay off-screen with no focus or dialogs (`launchZing` does this). Never use the real pointer. e2e never downloads an engine from the internet or calls an outside service.
- Nothing about what the user does in a tool is stored. Opening the Toolbox makes no network request. A tool that needs no network makes none.
- Every tool loads on first open. The Toolbox adds nothing to startup; S10's budgets hold.
- File names and URLs reach engines through `ProcessStartInfo.ArgumentList`, never a shell. A URL follows `--`.
- Never imitate Chrome and never bypass a bot check.
- Pure logic ported to C# shares `*.vectors.json` with its TypeScript twin, as `LinkCleaner` does.
- Before each PR: `npm run lint`, `npm run typecheck`, `npm test`, `npm run test:host`, `npm run test:e2e`. During tasks, run only the spec a task names. Commit messages short and lowercase.
- Three PRs, so CI runs e2e only on those: A (Tasks 1–7) on `phase-6/toolbox`; B (Tasks 8–13) on `phase-6/unblock`; C (Tasks 14–21) on `phase-6/files`. B and C branch from `main` after A merges and don't depend on each other.

## Decisions this plan makes

For the owner to check:

- **Tools float over the page rather than opening as tabs.** The design suggested a `zing://toolbox` tab. Tabs run websites and have no bridge, and giving a tab one is the risk this avoids. The sheet and floating panels already exist, stay above any page, and leave the page live behind them.
- **ImageMagick is a fifth engine,** replacing `sharp` (Node only). It reads HEIC and RAW, so no separate image library. `backends.ts` swaps `sharp` for `magick`.
- **Convert drops 3D models** (`meshio` needs Python). Archives use Windows' own `tar.exe`.
- **FFmpeg build:** gyan.dev's versioned essentials zip, which includes HEVC decoding, x264 and MP3. It is a GPL build, which is fine because Zing doesn't ship it: the user's machine fetches it from that project. The Settings row names the source.
- **Background removal runs in the renderer** with `onnxruntime-web`, so the host gains no native dependency. Starting model: `isnet-general-use` (Apache-2.0, about 170 MB) from rembg's releases.
- **Pinned TLS is a `SocketsHttpHandler` with a `ConnectCallback`,** which .NET allows directly, so Krackn's hand-written HTTP client isn't ported. `src/platform/net` and `src/shared/unblock/pinnedHTTPClient.ts` go once nothing imports them.
- **Output files go to the Downloads folder** with numbered collisions. No save dialog.

## Review Focus

1. A download of an engine interrupted or failing its checksum: nothing half-installed is left, the tool says what failed, and a retry works.
2. A file named `-rf.mov` or `my film é (1).heic`: it converts correctly and is never read as an option.
3. A web page linking to `zing://visit-anyway?...` or the relay scheme: no warning is skipped, and no page reaches Zing's bridge.
4. Closing a panel or the window during a long conversion: the process ends and its temporary files go.
5. A regex that backtracks badly, or a huge JSON paste: the tool says it gave up, and Zing doesn't hang.

---

### Task 0: Spikes (throwaway, `spikes/phase-6/`, not merged)

Run first, one report line per spike. **If one fails, stop that piece and bring the owner the options.**

- [ ] **S1 Pinned TLS and browser DoH:** a console app fetches `https://www.cloudflare.com` through `SocketsHttpHandler.ConnectCallback` to an address from a DoH answer, and refuses a wrong certificate. Also test whether `--enable-features=DnsOverHttps` with `--dns-over-https-templates` changes a WebView2 page's lookups. Report which, because it decides Task 12's DoH wording.
- [ ] **S2 Relay scheme:** a page WebView2 loads from a custom scheme (`CoreWebView2CustomSchemeRegistration`, `WebResourceRequested`) with relative URLs resolving back into the scheme, its own origin, and no `chrome.webview` host objects.
- [ ] **S3 HEVC MOV:** a real HEVC `.mov` converted by FFmpeg to fragmented H.264 MP4 (`-movflags frag_keyframe+empty_moov`) plays in a `<video>` in an overlay window while conversion runs, served by `WebResourceRequested` with range support. Report seconds to first frame.
- [ ] **S4 Dropped file paths:** a file dropped on an overlay view reaches the host as a path through `chrome.webview.postMessageWithAdditionalObjects`.

---

## PR A: Toolbox page and small tools

### Task 1: Toolbox sheet, tool registry, and dropped rows

**Files:**
- Modify: `host/Zing.Core/ShellModel.cs` (`OpenToolbox`, shell state), `host/Zing.Core/ToolPanels.cs`, `src/shared/toolbox/catalog.ts`, `panelContent.ts`, `src/shared/shell/shellState.ts`, `src/renderer/src/surface/Surface.tsx`, `src/renderer/src/panels/PanelHost.tsx`, `README.md`, `docs/PLAN.md`, `PARITY.md`
- Create: `src/renderer/src/toolbox/ToolboxSheet.tsx`, `src/renderer/src/toolbox/toolbox.css`, `src/renderer/src/tools/registry.ts`, one `src/renderer/src/tools/{math,dev,text}/index.ts` per later task
- Test: `e2e/toolbox.spec.ts`, `host/Zing.Core.Tests/ShellModelTests.cs`

**Interfaces:** Produces `toolComponent(content: PanelContent): LazyExoticComponent<ComponentType> | null` in `registry.ts`; null renders today's `ToolEmptyState`. Each group's `index.ts` exports `Record<string, () => Promise<{ default: ComponentType }>>`, keyed by `panelId`, so parallel tasks touch only their own file.

- [ ] **Step 1: Write the failing tests** in `e2e/toolbox.spec.ts`:
	- `the Toolbox button opens the sheet, not a notice`: click the rail's Toolbox button; `getByRole('dialog', { name: 'Toolbox' })` is visible and no notice appears; Escape closes it.
	- `search finds a tool by keyword and Enter opens it`: type `sha`; first result is `Hashes`; Enter gives a `float-panel` titled `Hashes`.
	- `opening a tool records it in recents`: open `Hashes`; `getSettings().recentFeatures` contains `tool:Hashes`.
	- `opening the Toolbox makes no network request`: record requests on the surface view while it opens; all are to `app.zing`.
- [ ] **Step 2: Run** `npx playwright test e2e/toolbox.spec.ts`. Expected: FAIL.
- [ ] **Step 3: Build it.** `ShellModel.OpenToolbox` sets `toolboxOpen` in the shell state and raises `ShellChanged`. Remove `ToolboxNotice`. The sheet groups tiles by `toolboxGroups` and searches with `toolboxSearch`. Remove `Section.security` from TS and C# (X14 is dropped; link inspection's switch is in Settings). `catalog.ts` keeps the Security group for passwords, hashes, HMAC and JWT. Add the Links tile in Task 2.
- [ ] **Step 4: Docs.** Remove the "No AI" line from `README.md` and `docs/PLAN.md` line 35 and any other mention. In `PARITY.md`, mark X12, X13, X14 Dropped with the reasons in `docs/phase-6.md`, note 3D under X15, and add rows `X24 Links page` and change X10's title to `Engines on first use`.
- [ ] **Step 5: Run** the spec plus `npm test` and `npm run test:host`. Expected: PASS.
- [ ] **Step 6: Commit** `toolbox sheet and tool registry`.

### Task 2: Links page (X24)

**Files:** Modify `src/shared/settings/schema.ts`, `settings.vectors.json`, `panelContent.ts`, `catalog.ts`, C# twins. Create `src/renderer/src/tools/links/LinksTool.tsx`. Test: `src/shared/toolbox/links.test.ts`, `e2e/toolbox.spec.ts`.

**Interfaces:** Produces `Section.links = 'Links'`, in the `web` group; setting `toolLinks: { title: string; url: string }[]`; `parseToolLink(input: string): string` throwing `InvalidLinkError`.

- [ ] **Step 1: Tests:** `parseToolLink` accepts `figma.com` as `https://figma.com/`, rejects `javascript:alert(1)`, `file:///c:/x` and an empty string. e2e `a link tile opens a tab, and an added link survives a relaunch`. e2e `the default list holds Desmos, Figma, Notion, ChatGPT, Claude, Gemini`.
- [ ] **Step 2: Run** them. Expected: FAIL.
- [ ] **Step 3: Build** tiles with add and remove. The page carries this line verbatim: "Sites here sign you out when Zing closes, unless you turn on Stay signed in to sites in Settings."
- [ ] **Step 4: Run.** Expected: PASS. **Commit** `links page`.

### Task 3: Math tools (X17, X18) *(parallel with 4, 5)*

**Files:** Create `src/renderer/src/tools/math/{CalculatorTool,UnitsTool,NumberBaseTool,GraphingTool}.tsx`, `index.ts`. Test: `e2e/tools-math.spec.ts`.

**Interfaces:** Consumes `evaluate`, `format`, `makeFunction`, `AngleMode` (`expression.ts`), `graphEngine.ts`, `units.ts`, `numberBase.ts`.

- [ ] **Step 1: Tests:** `2+2*3` shows `8`; `sin(` shows the `UnbalancedParenthesesError` message and no result; `1 mi` in km shows `1.609`; `255` in hex shows `ff`; plotting `x^2` draws a canvas with non-background pixels, and a wheel zoom changes the viewport label.
- [ ] **Step 2: Run** `npx playwright test e2e/tools-math.spec.ts`. Expected: FAIL.
- [ ] **Step 3: Build** the four components, matching Krackn's layouts in `reference/krackn/.../Utilities`. Graph on a `<canvas>` with several functions, pan and zoom.
- [ ] **Step 4: Run.** Expected: PASS. **Commit** `math tools`.

### Task 4: Developer tools (X19) *(parallel with 3, 5)*

**Files:** Create `src/renderer/src/tools/dev/*`, `index.ts`, `src/renderer/src/tools/dev/regex.worker.ts`. Modify `host/Zing.Core/ChromeApi.cs`. Create `host/Zing.Core/Hashing.cs`. Test: `e2e/tools-dev.spec.ts`, `host/Zing.Core.Tests/HashingTests.cs`.

**Interfaces:** Produces bridge `hash:text(text: string, algorithm: string): string` and `hash:file(path: string, algorithm: string): string` (MD5 isn't in SubtleCrypto; `hash.node.ts` can't run in the renderer, so remove it if nothing else imports it). Algorithms come from `hashAlgorithm.ts`.

- [ ] **Step 1: Tests:** JSON `{"a":1}` formats and bad JSON shows the line of the error; Base64 round-trips URL-safe unpadded input; SHA-256 of `abc` is `ba7816bf…15ad`; a JWT decodes its payload; a cron line shows its next run. Review Focus 5: regex `(a+)+$` against 40 `a`s and a `!` shows "gave up after 2 seconds" and the tool still responds; a 5 MB JSON paste doesn't block the chrome from answering a `getTabs` call within 1 s.
- [ ] **Step 2: Run** them. Expected: FAIL.
- [ ] **Step 3: Build.** Regex runs in the worker, terminated after 2000 ms. JSON formats in a worker above 1 MB.
- [ ] **Step 4: Run.** Expected: PASS. **Commit** `developer tools`.

### Task 5: Text, design and small privacy tools (X20, X21) *(parallel with 3, 4)*

**Files:** Create `src/renderer/src/tools/text/*`, `index.ts`. Add `qrcode-generator` (MIT). Test: `e2e/tools-text.spec.ts`.

**Interfaces:** Consumes `textCase.ts`, `textStats.ts`, `colorConverter.ts`, `password.ts`, `uuid.ts`, `linkCleaner.ts` (`cleanLink`).

- [ ] **Step 1: Tests:** `Hello World` converts to `hello_world`; stats count graphemes; `#ff0000` shows `rgb(255, 0, 0)`; a QR canvas renders; generated passwords are the chosen length and differ between clicks; the link cleaner turns `https://a.com/?utm_source=chatgpt.com&x=1` into `https://a.com/?x=1` and lists `utm_source` as removed; invalid input shows `InvalidLinkError`'s message.
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Build** the components.
- [ ] **Step 4: Run.** Expected: PASS. **Commit** `text and privacy tools`.

### Task 6: AI link tags

**Files:** Modify `src/shared/browser/linkCleaner.vectors.json`, and `linkCleaner.ts` with `host/Zing.Core/LinkCleaner.cs` only if a vector needs it.

- [ ] **Step 1: Add vectors:** `utm_source=chatgpt.com`, `utm_source=openai`, `utm_source=perplexity`, `utm_source=copilot.com`, `utm_source=claude.ai` removed; `?source=` keeps its existing behaviour. Check what these services actually add now and add any parameter that isn't `utm_*`, with its source noted in the commit.
- [ ] **Step 2: Add e2e** `a link with an AI tag loses it on navigation` in `e2e/toolbox.spec.ts`: navigate to a local page with `?utm_source=chatgpt.com`; the tab's URL has no query.
- [ ] **Step 3: Run** `npm test`, `npm run test:host`, the spec. Expected: PASS. **Commit** `strip the tags ai tools add`.

### Task 7: PR A

- [ ] **Step 1:** Whole-branch review; run all checks in Global Constraints; light and dark screenshots of the sheet and each tool in `docs/screenshots/phase-6/`.
- [ ] **Step 2:** Mark X17–X22, X24 Done in `PARITY.md`; L6, L7, L9, L13 now have callers. Push and open the PR. Merging is the owner's call.

---

## PR B: Getting past blocks

### Task 8: Network core and Diagnose (X3)

**Files:** Create `host/Zing.Core/Net/{DohClient,PinnedHttp,BlockDiagnostics,Probes,Diagnose}.cs`, `src/renderer/src/tools/web/{index.ts,DiagnoseTool.tsx}`, `src/shared/unblock/blockDiagnostics.vectors.json`. Modify `ChromeApi.cs`, `blockDiagnostics.test.ts`. Delete what nothing imports in `src/platform/net`. Test: `host/Zing.Core.Tests/Net/*`, `e2e/unblock.spec.ts`.

**Interfaces:** Produces `DohClient.ResolveAsync(string host, string provider, CancellationToken): Task<IReadOnlyList<string>>`; `PinnedHttp.GetAsync(Uri, IPAddress, CancellationToken): Task<HttpResponseMessage>` with `UseCookies = false`; bridge `net:diagnose(host: string): DiagnosticReport`. `IProbes` takes the system resolver, TCP and TLS checks so tests can fake them.

- [ ] **Step 1: Tests:** the 32 `classify` combinations as shared vectors; `PinnedHttp` against a local TLS server refuses a certificate for another name (`AuthenticationException`); `DohClient` raises `MalformedDoHResponseError` on bad JSON; a fake `IProbes` per verdict gives the right report. e2e `Diagnose reports a local server as not blocked`.
- [ ] **Step 2: Run** `npm run test:host`. Expected: FAIL.
- [ ] **Step 3: Build** with the Spike S1 result. The tool shows the verdict with `blockTitle`, `blockDetail` and `recommendedStrategies`, plus a button to open the recommended method.
- [ ] **Step 4: Run.** Expected: PASS. **Commit** `network core and diagnose`.

### Task 9: Open a Link engine (X1)

**Files:** Create `host/Zing.Core/Net/RelayEngine.cs`, `src/shared/unblock/relayEngine.vectors.json`. Modify `ChromeApi.cs`. Test: `host/Zing.Core.Tests/Net/RelayEngineTests.cs`.

**Interfaces:** Produces `RelayEngine.OpenAsync(string input, RelayConsent consent, CancellationToken): Task<RelayResult>`, trying `RelayStrategy` direct, pinned, readerRelay, wayback in order and reporting each attempt; `RelayConsent` holds whether third parties are allowed this session; bridge `relay:open(input)` pushing `relay:attempt`.

- [ ] **Step 1: Tests:** with fake transports: direct success stops there; direct failing then pinned success reports both; without consent, the third-party strategies aren't called and the result says consent is needed; a request to a relay carries no `Cookie` or `Authorization`; all failing raises `AllStrategiesFailedError` listing each reason; `BadURLError` on `not a url`.
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Build,** starting from the relay services in `src/platform/net/relayStrategies.ts`, confirming each still answers.
- [ ] **Step 4: Run.** Expected: PASS. **Commit** `open a link engine`.

### Task 10: Relayed pages (X2)

**Files:** Modify `host/Zing.Host/Program.cs` (scheme registration), `WebView2Page.cs`. Create `host/Zing.Core/Net/RelayedContent.cs`, `relayedWebContent.vectors.json`. Modify `relayedWebContent.ts` (`relayScheme` becomes `zing-relay`). Test: `e2e/unblock.spec.ts`.

**Interfaces:** Produces `TabService.OpenRelayed(string url)`.

- [ ] **Step 1: Tests:** e2e, with the local test server standing in for the relay: `a relayed page loads its stylesheet through the relay`; Review Focus 3: `a relayed page has no bridge and a web page can't navigate to the relay scheme` (`window.chrome.webview` absent; a link from an `http://` page to `zing-relay://…` is cancelled); `an app-like page says it can't work here` using `isAppLikeFailure`.
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Build** per Spike S2: a tab's relay navigation is allowed only when Zing started it.
- [ ] **Step 4: Run.** Expected: PASS. **Commit** `relayed pages`.

### Task 11: Open a Link tool and the failed-load offer

**Files:** Create `src/renderer/src/tools/web/OpenLinkTool.tsx`. Create `host/Zing.Core/Net/BlockLike.cs`. Modify `TabService.cs` (`LoadFailed`), `NoticeLayer.cs` and the notice renderer. Test: `e2e/unblock.spec.ts`, `host/Zing.Core.Tests/Net/BlockLikeTests.cs`.

**Interfaces:** Produces `BlockLike.Is(string webErrorStatus): bool`, true for name-not-resolved, cannot-connect, connection reset, TLS and timeout statuses, false for all others.

- [ ] **Step 1: Tests:** `Is` per status, with `HostNameNotResolved` true and `Unknown` false; e2e `a failed load offers Open a Link and Diagnose, and nothing runs without a click`; e2e `the first third-party method asks once per session and says why`; e2e `declining stops at the methods that need no third party`.
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Build.** The consent dialog uses this text: "This asks an outside service to fetch the page for you, so that service sees the address, and for a reader relay the page too. Zing doesn't send it your cookies or sign-ins. If this network belongs to a school or employer, getting around its filter may break their rules. That's your call." The tool lists each attempt with its result.
- [ ] **Step 4: Run.** Expected: PASS. **Commit** `open a link tool and failed load offer`.

### Task 12: Network & VPN (X4, X5)

**Files:** Create `host/Zing.Core/Net/DnsScript.cs`, `src/renderer/src/tools/web/NetworkTool.tsx`, `host/Zing.Host/DnsSetup.cs`. Test: `host/Zing.Core.Tests/Net/DnsScriptTests.cs`, `e2e/unblock.spec.ts`.

**Interfaces:** Produces `DnsScript.Apply(string provider, IReadOnlyList<DnsAdapter> adapters): string`, `DnsScript.Undo(IReadOnlyList<DnsAdapter> before): string`; `dns:preview`, `dns:apply`, `dns:undo`.

- [ ] **Step 1: Tests:** the generated script names only the up adapters passed in; an adapter name containing `'` is quoted safely; `Undo` restores static servers and DHCP adapters as they were; e2e `the script is shown before it can run`; e2e, with `dns:apply` stubbed by the test hook, `Apply then Undo returns the page to its first state`. e2e never runs the real script or an admin prompt.
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Build.** Apply runs elevated by `runas` after a click. The previous settings are saved in `%LOCALAPPDATA%` until Undo. The page keeps Krackn's VPN guide (Windows wording) and "What beats what" table from `NetworkView.swift`. State what S1 found about browser DoH.
- [ ] **Step 4: Run.** Expected: PASS. **Commit** `network and vpn page`.

### Task 13: Link inspection (X11), then PR B

**Files:** Create `host/Zing.Core/ThreatModel.cs`, `src/shared/security/threatModel.vectors.json`. Modify `threatModel.test.ts`, `TabService.cs` (`_intercept`), `StartPage.cs` (internal pages), `src/renderer/src/settings/rows.ts`; remove X12's domain logic from `threatScanner.ts` where unused. Test: `host/Zing.Core.Tests/ThreatModelTests.cs`, `e2e/security.spec.ts`.

**Interfaces:** Produces `ThreatModel.InspectUrl(Uri): ThreatVerdict`, `ThreatGate.Check(string from, string to): NavigationDecision?`, internal pages `zing://warning?to=` and `zing://visit-anyway?to=`.

- [ ] **Step 1: Tests:** Krackn's cases as shared vectors, including the 16 must-be-clean sites; a benchmark test: 10,000 `InspectUrl` calls under 100 ms. e2e `a flagged link shows a warning; Visit anyway loads it and doesn't warn again this session`; e2e `securityRealtime off shows no warning`. Review Focus 3: e2e `a web page linking to zing://visit-anyway is refused`, because the gate accepts it only when `from` is the warning page.
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Build.** The session's allowed hosts live in memory only.
- [ ] **Step 4: Run.** Expected: PASS. **Commit** `link inspection`.
- [ ] **Step 5: PR B.** Whole-branch review, all checks, screenshots, X1–X5 and X11 Done in `PARITY.md`. Push and open. The owner checks a blocked site on a school or work network.

---

## PR C: Files and media

### Task 14: Engines on first use (X10)

**Files:** Create `host/Zing.Core/Engines/{EngineCatalog,EngineStore}.cs`, `src/renderer/src/tools/files/EnginesTool.tsx`, test project `host/Zing.FakeEngine/`. Test: `host/Zing.Core.Tests/Engines/*`, `e2e/engines.spec.ts`.

**Interfaces:** Produces `record Engine(string Id, string Version, Uri Url, string Sha256, long Bytes, string Executable)` for `ffmpeg`, `magick`, `pandoc`, `ytdlp`, `bgmodel`; `EngineStore.EnsureAsync(string id, IProgress<double>, CancellationToken): Task<string>` returning the executable's path, raising `EngineDownloadException(id, url, reason)`; `Remove(id)`; `UpdateYtDlpAsync(CancellationToken)`; bridge `engines:list|ensure|remove|update-ytdlp`. Under `ZING_E2E`, `ZING_ENGINE_BASE_URL` points the catalog at a local fixture serving `Zing.FakeEngine`, which echoes its arguments and writes a named output file.

- [ ] **Step 1: Tests,** with a local HTTP server. Review Focus 1: a truncated download raises `EngineDownloadException` and leaves no engine folder; a wrong checksum does the same; a retry then succeeds; install moves into place atomically from a temp folder. `EnsureAsync` twice downloads once. `UpdateYtDlpAsync` checks against the release's `SHA2-256SUMS`, and on a mismatch keeps the old version. e2e `the Engines panel lists sizes and Remove frees one`; e2e `opening Zing downloads nothing`.
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Build.** Pin real URLs and hashes, computing each hash from the downloaded file, with the licence of each noted in `EngineCatalog.cs`. The blocked-download error names the engine and gives its URL to fetch by hand. Settings gets an Engines row that opens this panel; the Toolbox has no tile for it, as `catalog.ts` already says.
- [ ] **Step 4: Run.** Expected: PASS. **Commit** `engines on first use`.

### Task 15: Running engines and file access

**Files:** Create `host/Zing.Core/Engines/EngineRunner.cs`. Modify `ChromeApi.cs`, `src/shared/zingApi.ts`, `src/renderer/src/components/files.tsx`. Test: `host/Zing.Core.Tests/Engines/EngineRunnerTests.cs`, `e2e/engines.spec.ts`.

**Interfaces:** Produces `engines:run(engineId: string, args: string[]): jobId`, push `engines:output(jobId, line)` and `engines:exit(jobId, code)`, `engines:cancel(jobId)`; `files:pick(kinds)`, `files:open-dropped` (Spike S4). `EngineRunner` accepts only catalog ids.

- [ ] **Step 1: Tests:** Review Focus 2: `-rf.mov` and `my film é (1).heic` arrive at `Zing.FakeEngine` as single unchanged arguments. An unknown id raises `UnknownEngineException`. Review Focus 4: closing the owning panel or window kills the job's process tree; a job writing output after cancel leaves no output file.
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Build** with a Win32 job object, so children die with Zing. `files:pick` has a test hook, so e2e shows no dialog.
- [ ] **Step 4: Run.** Expected: PASS. **Commit** `engine runner and file access`.

### Task 16: Convert (X6–X8) *(parallel with 17–19 once 15 is merged to the branch)*

**Files:** Create `src/renderer/src/tools/files/ConvertTool.tsx`. Modify `src/shared/convert/backends.ts`, `formatCatalog.ts`, `commands.ts`, `convert.test.ts`. Test: `e2e/convert.spec.ts`.

**Interfaces:** Consumes `ffmpegArgs`, `magickArgs`, `pandocArgs`, `tarArgs`, `uniqueOutputName`, `inputSupport`.

- [ ] **Step 1: Tests:** `backends` has no `sharp` or `meshio`, and HEIC reads by `magick`; `heic` to `jpg` runs `magick` with the right arguments against the fake engine and reports the numbered output when a file exists; an engine's non-zero exit shows `explainFfmpegFailure`'s text; output uses `uniqueOutputName`; HTML to PDF uses `PrintToPdfAsync`; a local test (`*.network.test` style, not in CI) converts real HEIC and MOV fixtures with real engines.
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Build** with drop, pick and the format menu.
- [ ] **Step 4: Run.** Expected: PASS. **Commit** `convert`.

### Task 17: File viewer (X15)

**Files:** Create `src/renderer/src/tools/files/ViewerTool.tsx`, `host/Zing.Host/ViewerFiles.cs`, `src/shared/convert/viewable.ts`. Test: `src/shared/convert/viewable.test.ts`, `e2e/viewer.spec.ts`.

**Interfaces:** Produces `viewPlan(ext: string): { kind: 'native' | 'image' | 'video' | 'audio' | 'unsupported' }`; `viewer:open(path): { url, jobId? }` serving through `WebResourceRequested` on `https://view.zing/<token>` with range support, per Spike S3.

- [ ] **Step 1: Tests:** `viewPlan`: `heic` and `cr2` are `image`, `mov` and `mkv` are `video`, `pdf` and `mp4` are `native`, `obj` is `unsupported`. e2e: a fixture MP4 plays, seeking works; with the fake engine, opening a MOV shows progress and then its video; `closing the file deletes the temporary copy`; `temporary copies left by a crash go at next start`; a token for a closed file answers 404; a `view.zing` URL from a page tab answers 404.
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Build.** Temporary copies live in a Zing-owned folder under `%TEMP%`. Unsupported files say so, with an offer to convert.
- [ ] **Step 4: Run.** Expected: PASS. **Commit** `file viewer`.

### Task 18: Background remover (X16)

**Files:** Create `src/renderer/src/tools/files/BgRemoveTool.tsx`, `bgremove.worker.ts`, `src/shared/convert/alphaMask.ts`. Add `onnxruntime-web`. Test: `src/shared/convert/alphaMask.test.ts`, `e2e/bgremove.spec.ts`.

**Interfaces:** Produces `applyMask(rgba: Uint8ClampedArray, mask: Float32Array, width: number, height: number): Uint8ClampedArray`.

- [ ] **Step 1: Tests:** `applyMask` sets alpha from the mask and keeps colour, and throws on a size mismatch; e2e: with no model present, the tool states the size and downloads only on a click; onnxruntime's chunks load only on opening this tool. A local test (not CI) runs the real model on a fixture.
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Build:** inference in a worker. Save a PNG to Downloads, with Copy (Task 21).
- [ ] **Step 4: Run.** Expected: PASS. **Commit** `background remover`.

### Task 19: Media download and Save page (X9)

**Files:** Create `src/renderer/src/tools/files/DownloadTool.tsx`, `src/shared/convert/ytdlpArgs.ts`. Modify `ChromeApi.cs` for `page:save`. Test: `src/shared/convert/ytdlpArgs.test.ts`, `e2e/download.spec.ts`.

**Interfaces:** Produces `ytdlpArgs(url: string, mode: 'mp3' | 'mp4', outputDir: string, ffmpegPath: string): string[]`; `parseProgress(line: string): number | null`.

- [ ] **Step 1: Tests:** `ytdlpArgs` puts the URL after `--` and a URL starting with `-` stays one argument; MP3 uses `-x --audio-format mp3`; `parseProgress` reads `[download]  42.0% of ...`; yt-dlp's errors from `DownloadErrors.swift` map to its messages, including an out-of-date hint with an Update button; e2e with the fake engine: progress shows, and a finished file is listed; e2e `Save page writes the page's HTML, through the relay when asked`.
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Build.** Starts only on a click. No browser cookies are handed to yt-dlp. A bot check is reported, not worked around.
- [ ] **Step 4: Run.** Expected: PASS. **Commit** `media download and save page`.

### Task 20: PDF tools (X25) *(parallel with 16–19)*

**Files:** Modify `panelContent.ts`, `catalog.ts`, `ToolPanels.cs`, `toolboxSearch.ts`, `dropRouter.ts` (`acceptsFiles`), `PARITY.md`. Create `src/shared/pdf/pdfEdit.ts`, `src/renderer/src/tools/files/PdfTool.tsx`. Add `pdf-lib` and `pdfjs-dist` (both permissive licences; loaded only by this tool). Test: `src/shared/pdf/pdfEdit.test.ts`, `e2e/pdf.spec.ts`.

**Interfaces:** Produces `UtilityTool.pdf = 'PDF Tools'` in the `files` group; `class PdfEditError extends Error`; in `pdfEdit.ts`, working on `Uint8Array`: `mergePdfs(files)`, `splitPdf(file, ranges: PageRange[])`, `reorderPages(file, order: number[])`, `rotatePages(file, pages: number[], degrees: 90 | 180 | 270)`, `deletePages(file, pages: number[])`, `addOverlay(file, overlay: PdfOverlay)`, `fillForm(file, values: Record<string, string>)`, `parsePageRanges(input: string, pageCount: number): PageRange[]`.

- [ ] **Step 1: Tests:** merging a 2-page and a 3-page fixture gives 5 pages in order; `parsePageRanges('1-2, 5', 6)` gives two ranges, and `'9'` on 6 pages throws `PdfEditError`; deleting every page throws; rotation adds to a page's existing rotation; an overlay leaves page count unchanged and adds content; a filled form field reads back; an encrypted or corrupt PDF throws `PdfEditError` naming the file. e2e: `merge two PDFs and the output lands in Downloads with a numbered name`; `a PDF from Convert dragged in joins the list` (with Task 21); `pdf.js loads only when this tool opens`; page thumbnails draw on a canvas; `reorder by keyboard works`.
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Build.** Render with pdf.js in its worker. Convert gains PDF to PNG and JPEG by rendering pages through pdf.js, and images and documents to PDF through the existing paths, so a result opens here with one click. The original file is never changed. Page edits work on a copy held in memory until saved. Undo covers the session's edits.
- [ ] **Step 4: Run.** Expected: PASS. **Commit** `pdf tools`.

### Task 21: Drag between tools and copy (X23), then PR C

**Files:** Modify `PanelHost.tsx`, `dropRouter.ts`, `components/files.tsx`, `ChromeApi.cs`. Create `host/Zing.Host/FileClipboard.cs`. Test: `e2e/drag-tools.spec.ts`.

**Interfaces:** Consumes `planDrop`, `acceptsFiles`. Produces `files:copy-to-clipboard(path: string)`.

- [ ] **Step 1: Tests:** e2e: a Convert output dragged to the Viewer panel opens there, and to Background Remover loads there; a dragged text onto a file tool is refused; `Copy file` puts the file on the clipboard in `CF_HDROP` (checked in the test hook, which reads it back).
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Build.**
- [ ] **Step 4: Run.** Expected: PASS. **Commit** `drag between tools and copy file`.
- [ ] **Step 5: PR C.** Whole-branch review, all checks, screenshots, `PARITY.md` X6–X10, X15, X16, X23, X25 Done (X15 noting no 3D). README's Status and feature text match what shipped. Push and open. The owner checks by eye: a real iPhone HEIC and MOV, and a YouTube MP3 and MP4.
