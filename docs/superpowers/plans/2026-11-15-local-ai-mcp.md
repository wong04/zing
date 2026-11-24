# Local AI over MCP Implementation Plan

> **Not part of v1. This is post-v1 work and must not block or delay the v1.0.0 release.** Do not start it, branch it into `main`, or add it to the v1 checklist before v1.0.0 ships.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** an opt-in MCP server inside Zing, so the owner's local AI tools can read and drive tabs the owner has shared.

**Architecture:**
- The tool logic lives in a new `Zing.Core.Mcp` namespace, tested with xUnit against a fake `IPageDriver`: JSON-RPC dispatch, share registry, snapshot refs, site policy, confirm gate.
- One app-level endpoint, owned by `ZingApp` (not by `MainWindow`, since Zing has many windows), serves loopback HTTP and a per-profile named pipe. It marshals every request onto the UI thread's `DispatcherQueue` before touching Core.
- `CdpPageDriver` in `Zing.Host` calls `CoreWebView2.CallDevToolsProtocolMethodAsync` on one shared tab's view, plus `Target.attachToTarget` sessions for the PDF guest.
- `zing-mcp.exe` is a stdio-to-pipe relay for the Claude desktop app.

**Tech Stack:** .NET 9, WebView2, `ModelContextProtocol` C# SDK if it fits Task 6 (otherwise hand-written JSON-RPC), React renderer, vitest, xUnit, Playwright.

**Spec:** `docs/superpowers/specs/2026-11-15-local-ai-mcp-design.md`

**Review:** a Fable 5.2 architecture review on 2026-11-15 found the issues fixed here: URL scheme reach, the submit-gate bypass, the vault-fill entry point and leak, background tabs, threading and multi-window, frame reach, pipe/port collisions, interfaces that didn't exist, and task order.

## Global Constraints

- Off by default. The setting is `aiControl.enabled` (default `false`). Turning it off closes the listener and the pipe, and unshares every tab.
- **Threading.** All Core objects are touched on the UI thread only. `HttpListener`, pipe and SDK callbacks hop to the UI thread through `DispatcherQueue.TryEnqueue` and await the result there.
- **HTTP.**
  - It binds to `127.0.0.1` only.
  - The `Host` header must be `127.0.0.1:<port>` or `localhost:<port>`.
  - `Origin` must be absent or loopback.
  - The bearer token is 32 random bytes, sealed with DPAPI, and compared in constant time.
- **Pipe:** `\\.\pipe\zing-mcp-<profile key>-<session id>`, built the way `ProfileOwner.PipeName` is, with an ACL that allows only the current user SID. `zing-mcp.exe --profile <key>` picks the pipe, and Settings puts the key in the snippet.
- **Tool names** are only `list_tabs`, `snapshot`, `read_text`, `screenshot`, `open_tab`, `close_tab`, `navigate`, `back`, `forward`, `click`, `type`, `select`, `scroll`, `press_key`, `wait_for`, `download` and `fill_login`. There is no evaluate tool, and no tool reads cookies, storage, vault entries, Settings, history or bookmarks.
- **URLs.** Only `http`/`https` URLs can be shared, navigated to or opened. `navigate` and `open_tab` go through `TabService`, never CDP `Page.navigate`.
- **Tabs.** Tab ids are the existing `string` ids. Shares are keyed by `(windowId, tabId)`.
- **Site levels:** `askSubmit` (the default), `askAll`, `never`. They are keyed by `PublicSuffix` site, not `RegistrableDomain`.
- **Confirms.** The confirm timeout is 2 minutes, and a timeout returns `Denied`. After every confirm await, re-check the share, the URL and the snapshot generation.
- **Error codes:** `NotShared`, `StaleRef`, `Denied`, `NoSavedLogin`, `TabGone`, `Disabled`, `BlockedUrl`, `Covered` (the click point isn't the ref's node), `DialogOpen`, `NotVisible`, `Timeout` (every CDP call has a 15 s limit).
- Every await on a view is followed by a still-there check (`ViewGoneException`). Optional-feature failures never stop startup.
- While `aiControl.enabled` is true, the Settings privacy banner shows a warning.
- Tabs for indentation, and the repo's existing style.

## Review Focus

1. **A shared tab navigates to `zing://home` or `file:///`.** Expected: it is unshared before any tool runs against it. Test in Task 2.
2. **The Buy button carries `aria-label="Close"`, or sits under an overlay.** Expected: the overlay click fails `Covered`; the mislabelled one is caught by the form-POST backstop. Tests in Tasks 3 and 10.
3. **The owner clicks Stop while a call is waiting on the confirm bar.** Expected: `Denied`, and the bar closes. Test in Task 5.
4. **A request comes with `Origin: https://evil.example` or `Host: evil.example`.** Expected: 403. Test in Task 7.
5. **A tab closes mid-call, or a JS `alert` is open.** Expected: `TabGone` / `DialogOpen`, with nothing reaching `ZING_ERRORS_FILE`. Test in Task 8.

---

### Task 1: Settings keys, token store, privacy banner

**Files:**
- Modify: `host/Zing.Core/Defaults/settings.json`, `settings-schema.json`, the matching `src/shared/settings` schema, `src/renderer/src/settings/privacy.ts`
- Create: `host/Zing.Core/Mcp/AiToken.cs`
- Test: `src/renderer/src/settings/privacy.test.ts`, `host/Zing.Core.Tests/Mcp/AiTokenTests.cs`, `SettingsTests.cs`

**Interfaces:**
- Produces:
  - settings `aiControl.enabled: boolean` (default `false`), `aiControl.port: number` (default `47821`, range 1024–65535);
  - `sealed class AiToken(ICipher dpapi, string path)`, with `byte[] Current()` (created on first use), `byte[] Regenerate()`, and `event Action Regenerated`.

- [ ] Write the tests:
  - `"AI control on turns the banner to a warning"`;
  - `AiControlDefaultsOff`;
  - `TokenSurvivesReload`;
  - `RegenerateChangesTokenAndRaises`.
- [ ] Run `npm test -- privacy` and `npm run test:host -- --filter "AiControl|AiToken"`. Expected: fail.
- [ ] Implement it. The token file sits beside the other userData files, uses `DpapiCipher`, and is written with `JsonFiles`.
- [ ] Run them. Expected: pass.
- [ ] Commit: `ai control settings and token`.

### Task 2: Share registry (`AiShares`)

**Files:**
- Create: `host/Zing.Core/Mcp/AiShares.cs`, `host/Zing.Core/Mcp/AiUrls.cs`
- Test: `host/Zing.Core.Tests/Mcp/AiSharesTests.cs`

**Interfaces:**
- Produces:
  - `readonly record struct TabRef(string WindowId, string TabId)`;
  - `static bool AiUrls.Allowed(Uri url)`, true only for `http`/`https`;
  - `sealed class AiShares`, with:
    - `string Share(TabRef tab, Uri url)`, which throws `NotShareableException` when `!AiUrls.Allowed`;
    - `void Unshare(TabRef)`, `void UnshareAll()`;
    - `bool TryResolve(string aiId, out TabRef tab)`;
    - `void Navigated(TabRef, Uri)`, `void PopupFrom(TabRef opener, TabRef popup, Uri url)`;
    - `bool IsShared(TabRef)`;
    - `IReadOnlyList<(string AiId, TabRef Tab)> List()`;
    - `event Action<TabRef> Unshared`.
  - `aiId` values are opaque random strings.

- [ ] Write the tests:
  - `SharingZingOrFilePageThrows` (for `zing://home`, `file:///C:/`, `javascript:x`);
  - `NavigatingToZingUnsharesBeforeReturning` (Review Focus 1);
  - `PopupFromSharedTabIsShared`;
  - `AiIdIsNotTheTabId`;
  - `UnshareAllRaisesUnsharedForEach`.
- [ ] Run them. Expected: fail.
- [ ] Implement it.
- [ ] Run them. Expected: pass.
- [ ] Commit: `ai share registry`.

### Task 3: Submit detector and site policy

**Files:**
- Create: `host/Zing.Core/Mcp/SubmitDetector.cs`, `host/Zing.Core/Mcp/SitePolicy.cs`, `host/Zing.Core.Tests/Mcp/submit.vectors.json`
- Test: `SubmitDetectorTests.cs`, `SitePolicyTests.cs`

**Interfaces:**
- Produces:
  - `enum AiAction { Click, Type, TypeSubmit, Select, Scroll, PressKey, Navigate, Download, FillLogin, OpenTab, CloseTab }`;
  - `record AxNode(string Role, string Name, bool Editable, bool InForm, string? InputType)`;
  - `static bool SubmitDetector.IsSubmit(AiAction action, AxNode target, string? key)`;
  - `enum SiteLevel { AskSubmit, AskAll, Never }`;
  - `sealed class SitePolicy(string path)`, which is loaded and saved through the static `JsonFiles`. It has `SiteLevel LevelFor(Uri)`, `void Set(Uri url, SiteLevel)` (keyed by `PublicSuffix` site), `bool NeedsConfirm(Uri, AiAction, AxNode, string? key)`, and `IReadOnlyDictionary<string, SiteLevel> All()`.
  - Stored rules flip the privacy banner, as other stored data does.

- [ ] Write the vectors:
  - the name words `pay`, `buy`, `purchase`, `order`, `checkout`, `send`, `post`, `publish`, `submit`, `transfer`, `delete` and `confirm`, matched whole-word and case-insensitive on the accessible name;
  - `{"role":"button","name":"Place order"}` → true;
  - `{"role":"link","name":"Order history"}` → false;
  - Enter in any `Editable` node → true, form or not (Gmail/Slack).

  Add `SubmitDetectorTests.MatchesVectors`.
- [ ] Write the policy tests:
  - `DefaultIsAskSubmit`;
  - `NeverSkipsAllConfirms`;
  - `AskAllConfirmsScroll`;
  - `GithubIoUsersAreSeparateSites`.
- [ ] Run them. Expected: fail.
- [ ] Implement it.
- [ ] Run them. Expected: pass.
- [ ] Commit: `submit detector and per-site ai levels`.

### Task 4: Snapshot and refs

**Files:**
- Create: `host/Zing.Core/Mcp/Snapshot.cs`
- Test: `SnapshotTests.cs`, with fixture `host/Zing.Core.Tests/Mcp/ax-login.json`

**Interfaces:**
- Produces:
  - `sealed record Snapshot(int Generation, string Text, IReadOnlyDictionary<string, long> BackendNodeIds)`;
  - `static Snapshot Snapshot.Build(JsonElement axTree, int generation, IReadOnlySet<long> maskedNodes)`;
  - `bool TryRef(string reference, int currentGeneration, out long backendNodeId)`.

- [ ] Write the tests:
  - `InteractiveNodesGetRefs`;
  - `PasswordFieldValuesAreMasked`;
  - `MaskedNodesStayMaskedAsTextInputs` (a filled field switched to `type=text`);
  - `RefFromOlderGenerationIsStale`;
  - `IgnoredNodesAreLeftOut`.
- [ ] Run them. Expected: fail.
- [ ] Implement it.
- [ ] Run them. Expected: pass.
- [ ] Commit: `ax tree snapshot with refs`.

### Task 5: Confirm gate

**Files:**
- Create: `host/Zing.Core/Mcp/ConfirmGate.cs`
- Test: `ConfirmGateTests.cs`

**Interfaces:**
- Consumes: `IScheduler` (via `IWindowHost.Scheduler`), `AiShares`, `SitePolicy`.
- Produces:
  - `sealed class ConfirmGate(IScheduler scheduler, AiShares shares, SitePolicy policy)`;
  - `Task<bool> Ask(TabRef tab, Uri url, string description)`;
  - `event Action<TabRef, string> Shown`, `event Action<TabRef> Closed`;
  - `void Answer(TabRef, ConfirmAnswer)`, where `enum ConfirmAnswer { Allow, Deny, AlwaysAllowSite }`;
  - `IReadOnlyList<TabRef> Waiting()`.

- [ ] Write the tests:
  - `TimesOutAsDeniedAfterTwoMinutes`;
  - `UnshareWhileWaitingDenies` (Review Focus 3);
  - `AlwaysAllowSetsSiteNever`.
- [ ] Run them. Expected: fail.
- [ ] Implement it.
- [ ] Run them. Expected: pass.
- [ ] Commit: `ai confirm gate`.

### Task 6: Tool dispatch (`AiTools`)

**Files:**
- Create: `host/Zing.Core/Mcp/IPageDriver.cs`, `AiTools.cs`, `AiErrors.cs`
- Test: `AiToolsTests.cs`, with `FakePageDriver` in `Fakes.cs`

**Interfaces:**
- Consumes: Tasks 2–5.
- Produces:
  - `interface IPageDriver` (all `TabRef`-scoped), with:
    - `Task<JsonElement> AxTree`, `Task<string> MainText`, `Task<byte[]> Screenshot(TabRef, long? node, IReadOnlySet<long> blackOut)`;
    - `Task<long?> NodeAtCentre(TabRef, long node)`;
    - `Task Click`, `Task Type`, `Task Select`, `Task Scroll`, `Task PressKey`;
    - `Task Navigate`, `Task Back`, `Task Forward`;
    - `Task<TabRef> OpenTab(Uri)`, `Task CloseTab`;
    - `Task Download(TabRef, Uri)`;
    - `Task<(FillResult Result, IReadOnlySet<long> FilledNodes)> FillLogin(TabRef)`, where `enum FillResult { Filled, Denied, NoSavedLogin }`;
    - `Uri UrlOf(TabRef)`.
  - `sealed class AiTools`, with `IReadOnlyList<ToolDef> List()` and `Task<JsonNode> Call(string name, JsonObject args)`. It tracks the masked nodes per tab, from `FillLogin`.

Behaviour:
- `click` fails with `Covered` unless `NodeAtCentre` returns the ref's node.
- `navigate`/`open_tab` with a disallowed URL return `BlockedUrl`.
- After a confirm, it re-checks the share, the URL and the generation.

- [ ] Decide on the SDK: use `ModelContextProtocol` only if it can run over a caller-supplied stream and handler without ASP.NET, with calls dispatched through Zing's own UI-thread marshaller. Otherwise write JSON-RPC (`initialize`, `tools/list`, `tools/call`) by hand. Record the choice in the commit message.
- [ ] Write the tests:
  - `ListHasExactlyTheSpecTools`;
  - `UnsharedTabIsNotShared`;
  - `ClickOnPlaceOrderWaitsForConfirm`;
  - `CoveredClickFails`;
  - `NavigateToFileIsBlocked`;
  - `UnsharedDuringConfirmReturnsNotShared`;
  - `ScreenshotBlacksOutFilledNodes`;
  - `DisabledReturnsDisabled`.
- [ ] Run them. Expected: fail.
- [ ] Implement it.
- [ ] Run them. Expected: pass.
- [ ] Commit: `ai tool dispatch`.

### Task 7: App-level endpoint (HTTP and pipe) and the request check

**Files:**
- Create: `host/Zing.Core/Mcp/AiRequestCheck.cs`, `host/Zing.Host/AiEndpoint.cs` (HTTP plus pipe, one per process)
- Modify: `ZingApp` (it owns `AiShares`, `SitePolicy`, `ConfirmGate`, `AiTools` and `AiEndpoint`, and starts and stops them on the setting)
- Test: `AiRequestCheckTests.cs`

**Interfaces:**
- Produces:
  - `static AiRequestCheck.Result Check(string? host, string? origin, string? authorization, int port, ReadOnlySpan<byte> token)`, where `Result` is `Ok`, `Forbidden` or `Unauthorized`;
  - `AiEndpoint.Start(int port, string pipeName, Func<string, Task<string>> handleOnUiThread)` / `Stop()`.

- [ ] Write the tests:
  - `ForeignOriginIsForbidden`;
  - `RebindHostIsForbidden`;
  - `MissingOriginIsAllowed`;
  - `WrongTokenIsUnauthorized`;
  - `LoopbackWithTokenIsOk`.
- [ ] Run them. Expected: fail.
- [ ] Implement it.
  - HTTP runs on `HttpListener` at `http://127.0.0.1:<port>/mcp/` with the MCP Streamable HTTP transport, plus an `Mcp-Session-Id` header.
  - The pipe gets a user-only ACL.
  - Each request is enqueued on the `DispatcherQueue`.
  - A port that's in use shows an error in Settings and doesn't stop startup.
  - `AiToken.Regenerated` drops the open HTTP sessions.
- [ ] Run them. Expected: pass.
- [ ] Commit: `ai mcp endpoint`.

### Task 8: `CdpPageDriver`, reading (milestone 1), with a test share hook

**Files:**
- Create: `host/Zing.Host/CdpPageDriver.cs`
- Modify: `host/Zing.Host/TestHooks.cs` (an `aiShare` message in `TryHandle`, e2e only), `TabService` (shared tabs are exempt from the sleeper, suspender and auto-closer)
- Test: `e2e/ai-read.spec.ts`, plus the helper `e2e/mcp.ts` (MCP over HTTP, with the port from `ZING_E2E` env)

Behaviour:
- It uses the tab's own `CoreWebView2` only, and the PDF guest through the `ViewerGuests` session approach. Milestone 1 covers the main frame and the PDF guest; cross-origin iframes are left out of the snapshot and say so.
- A tab with no page yet is created on demand.
- An open JS dialog returns `DialogOpen`.
- Each call has a 15 s `Timeout`.
- `screenshot` on a hidden tab follows the spec's background-tab rule: badge the tab and wait up to 2 minutes for the owner to switch, then `NotVisible`. Never activate the tab.
- `read_text` is new code: `Accessibility` text for pages, and the PDF guest's text layer for PDFs.

- [ ] Write the e2e tests, through `%TEMP%\zing-pw.lock`:
  - `list_tabs shows only shared tabs`;
  - `snapshot of a fixture login page has refs and no password value`;
  - `screenshot returns a PNG`;
  - `read_text reads a fixture PDF`;
  - `a tab closed mid-call returns TabGone and records no error`;
  - `an open alert returns DialogOpen`.
- [ ] Run `npm run test:e2e -- ai-read`. Expected: fail.
- [ ] Implement it.
- [ ] Run it. Expected: pass.
- [ ] Commit: `ai read tools over per-tab cdp`.

### Task 9: Action tools (milestone 2)

**Files:**
- Modify: `host/Zing.Host/CdpPageDriver.cs`
- Test: `e2e/ai-act.spec.ts`

Behaviour:
- `Click`: `DOM.scrollIntoViewIfNeeded`, then `DOM.getBoxModel`, then `DOM.getNodeForLocation` (for `NodeAtCentre`), then `Input.dispatchMouseEvent` in viewport coordinates.
- `Type`: focus, then `Input.insertText`.
- `PressKey`: `Input.dispatchKeyEvent`. Input goes to the page, not the host's key handling.
- `Navigate`/`OpenTab` go through `TabService`.
- The backstop pauses a top-level form POST navigation that follows an AI action, on ask sites (spec, "Never reachable").

- [ ] Write the e2e tests:
  - `type then click submits a React fixture form only after Allow`;
  - `Deny leaves the form unsent`;
  - `a button under an overlay fails Covered`;
  - `a mislabelled buy button's form POST still asks`;
  - `a Don't ask site submits without a bar`.
- [ ] Run them. Expected: fail.
- [ ] Implement it.
- [ ] Run them. Expected: pass.
- [ ] Commit: `ai action tools`.

### Task 10: Share UI, AI strip, confirm bar, Settings

**Files:**
- Modify: the renderer tab context menu and tab outline styles, `src/renderer/src/settings/rows.ts`
- Create: `host/Zing.Host/AiStripLayer.cs` and `AiConfirmLayer.cs`, each with its own `OverlayLevel`
- Test: `e2e/ai-ui.spec.ts`, plus screenshots `ai-strip-*.png`, `ai-confirm-*.png`, `ai-settings-*.png` (light and dark)

Copy:
- strip: "AI is using this tab" with **Stop**;
- confirm bar: "AI wants to {description}" with **Allow** / **Deny** / **Always allow on this site**;
- a waiting confirm on a background tab shows a badge on the tab;
- Settings shows the switch, the port, the token (show, copy, regenerate), the Connect Claude snippets, the per-site list and **Stop all**;
- the snippets: `claude mcp add --transport http zing http://127.0.0.1:{port}/mcp/ --header "Authorization: Bearer {token}"`, and a Claude desktop JSON entry running `zing-mcp.exe --profile {key}`.

- [ ] Write the e2e tests:
  - `the context menu shares a tab and draws the outline`;
  - `zing pages have no share item`;
  - `Stop unshares and denies a waiting confirm`;
  - `a waiting confirm on a background tab badges it`;
  - `regenerating the token cuts off the old one`.
- [ ] Run them. Expected: fail.
- [ ] Implement it.
- [ ] Run them, and take the light and dark screenshots.
- [ ] Commit: `ai share ui and settings`.

### Task 11: Downloads and `fill_login`

**Files:**
- Create: an AI fill entry point in Core beside `Autofill` (`Autofill.FillForAi(TabRef) -> Task<(FillResult, IReadOnlySet<long>)>`). It uses the exact-origin match and Hello, needs no picker focus or click guard, and asks the owner with the picker when there is more than one login.
- Modify: `host/Zing.Host/DownloadHost.cs`. An AI download uses `Page.setDownloadBehavior` or a forced download request, so a renderable URL still downloads. It saves to Downloads only and shows in the list.
- Test: `e2e/ai-act.spec.ts` (fake Hello), `AutofillTests`

- [ ] Write the tests:
  - `download of a PDF url downloads instead of navigating`;
  - `fill_login with one login fills after Hello`;
  - `two logins ask the owner`;
  - `the response never holds credential text`;
  - `a show-password toggle after fill still masks snapshot and screenshot`.
- [ ] Run them. Expected: fail.
- [ ] Implement it.
- [ ] Run them. Expected: pass.
- [ ] Commit: `ai downloads and vault fill`.

### Task 12: `zing-mcp.exe`

**Files:**
- Create: `host/Zing.Mcp/Zing.Mcp.csproj`, `host/Zing.Mcp/Program.cs`
- Modify: the Velopack packaging to ship it next to `Zing.exe`
- Test: `PipeRelayTests.cs`, `e2e/ai-stdio.spec.ts`

Behaviour:
- `--profile <key>` builds the pipe name the same way as Task 7.
- The relay copies newline-delimited JSON-RPC.
- If there's no pipe, it writes an MCP error, `"Zing isn't running or AI control is off"`, and exits 1.
- It never launches Zing.

- [ ] Write the tests:
  - `RelayRoundTrips`;
  - `NoPipeWritesErrorAndExits1`;
  - `stdio list_tabs works through zing-mcp.exe`.
- [ ] Run them. Expected: fail.
- [ ] Implement it.
- [ ] Run the full suite: `npm test`, `npm run test:host`, `npm run lint`, `npm run typecheck`, `npm run test:e2e`.
- [ ] Commit: `zing-mcp stdio relay`.
