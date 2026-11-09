# Link previews Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A link from any tab to a different host opens as a near-fullscreen preview over the current tab. The preview can be closed, or expanded into a real tab without reloading.

**Architecture:**
- **The preview page** is an ordinary child-window `WebView2Page`, owned by a `PeekSession` in `Zing.Core` instead of a tab record.
- **The dim** is a native layered owned popup with a `SetWindowRgn` hole and no WebView2 in it.
- **The buttons** are a small renderer popup (`#peek`).
- **Expand** hands the existing page to a new tab record and lays it out in the tab slot.
- **Decisions** (preview or not) are a pure function, `PeekRules`, fed by the DevTools navigation reason and the modifier keys.

**Tech Stack:**
- C# .NET 9: `Zing.Core`, xUnit, and `Zing.Host` on Win32 and WebView2 1.0.4258.31
- React and TS: `src/renderer`, `src/shared`, vitest
- Playwright e2e through `e2e/zing.ts`

**Spec:** `docs/superpowers/specs/2026-11-08-link-preview-design.md`. Spike evidence: `spikes/link-preview/README.md`.

## Global Constraints

- **Code style:** tabs, early returns, specific exception types, no silent fallbacks, no scope creep.
- **Commits:** short and lowercase, ending `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. Work on a branch. Never push to `main`.
- **Where logic lives:** in `host/Zing.Core` (no UI, xUnit). `host/Zing.Host` stays thin.
- **Overlays:** a new overlay is a new `OverlayLevel`, never a layer that raises itself.
- **Async:** every `await` resumes on the UI thread. After an `await` on a view or page, check it is still there (`ViewGoneException`). Delayed work uses `IWindowHost.Scheduler`.
- **Placement** comes from `ChromeApi.Insets()` and `WindowLayout.PageRect()` only.
- **Startup:** previews are optional and must never fail startup.
- **e2e:** `ZING_E2E=1`, a scratch `ZING_USER_DATA`, no clipboard, no real keyboard. Modifiers go through a test seam. Real input goes in `real-input.spec.ts`, tagged `@real-input`.
- **Privacy:** previews store nothing. No privacy-banner change.
- **Excluded sign-in hosts:** `accounts.google.com`, `login.microsoftonline.com`, `login.live.com`, `appleid.apple.com`, and `github.com` paths `/login` and `/login/oauth*`.
- **Excluded meeting hosts:** `meet.google.com`, `zoom.us` and `*.zoom.us`, `teams.microsoft.com`, `teams.live.com`.
- **Search sources** (no preview from their results page): Google `google.*/search`, DuckDuckGo `duckduckgo.com/` with `q=`, Bing `bing.com/search`, Brave `search.brave.com/search`, Kagi `kagi.com/search`, Startpage `startpage.com/sp/search` and `/do/search`.
- **Preview rectangle:** the page area inset horizontally by `max(6% of width, 48 DIP)` and vertically by 24 DIP, at most 1400 DIP wide, centred.
- **Popup layers:** each inset a further 16 DIP. The cap is 3, but a popup always opens above the cap.
- **Dim:** black, alpha 115/255. Under high contrast, alpha 160 plus a 2 px border.
- **Buttons:** two 32 DIP round buttons in the right margin, top-aligned with the preview, close above open-as-tab. Accessible names "Close preview" and "Open as tab". Live region text "Preview of <host>. Press Escape to close."
- **Keys:** Ctrl+O expands. Esc (only when the page did not use it) and Ctrl+W close.
- **Settings:** `linkPreviews`, `linkPreviewsShiftClick` and `linkPreviewsFromNewWindows`, all flags, all default `true`.

## Review Focus

1. **A preview whose source tab closes or crashes underneath it** (closed from the rail with the mouse). Expect the preview to close with it, leaving no orphan scrim. Test: Task 5 `ClosingTheSourceTabClosesItsPreview`.
2. **The window minimised, restored or moved to another monitor while a preview is open.** Expect the scrim and its hole to follow the preview exactly, never covering the preview's page. Test: Task 6 `PlaceScrim` region recomputed on `WM_SIZE`, `WM_MOVE` and `WM_DPICHANGED`, plus hand check H2.
3. **A preview page that calls `window.close()` on itself** (some OAuth landing pages do). Expect the preview to close as if X were pressed, with the URL on the reopen stack. Test: Task 5 `PreviewPageClosingItselfClosesPreview`.
4. **A link inside a preview that would itself preview** (cross-host), and Shift-click inside a preview. Expect it to navigate the preview, never nest. Test: Task 2 row `fromPreview` and Task 5 `LinksInsideAPreviewNavigateIt`.
5. **A held new window that never reaches a real document** (it stays `about:blank`, or is closed by its opener). Expect nothing shown, and its page closed when its opener's tab closes or after 10 s. Test: Task 5 `PendingPopupThatNeverNavigatesIsDropped`.

---

## File structure

**New in `host/Zing.Core/LinkPreview/`:**
- `PeekRules.cs`: the pure decision.
- `PeekSession.cs`: the layer stack and lifecycle of one window's preview.
- `PeekLayout.cs`: the preview rectangle and button column from the page area.
- `PendingPopup.cs`: a new window held until its first real document.

**New elsewhere:**
- `host/Zing.Host/PeekScrim.cs`: the native dim with a hole.
- `host/Zing.Host/PeekControlsLayer.cs`: the renderer popup for the buttons.
- `src/renderer/src/overlay/PeekControls.tsx` and its test.
- `e2e/link-preview.spec.ts`.

**Changed:**
- `TabService.cs` (`IPageEvents`, `IPageHost`, `TabPageEvents`, expand)
- `WebView2Page.cs` (DevTools reason, modifiers, held windows)
- `MainWindow.cs` (layout and focus)
- `OverlayHost.cs`
- `TestHooks.cs`
- `ChromeApi.cs`
- the settings schema and codegen
- `src/shared/commands/registry.ts`
- `main.tsx`
- the settings page
- `PARITY.md`

Tests:
- `host/Zing.Core.Tests/LinkPreview/*Tests.cs`
- `TabServiceTests.cs`
- `ChromeApiTests.cs`
- `SettingsTests.cs`
- the vitest files beside the TS they test

---

### Task 1: Settings keys

**Files:**
- Modify: `src/shared/settings/schema.ts` (`SETTINGS_SCHEMA`)
- Modify: `scripts/host-defaults.ts` (rules map)
- Regenerate: `npm run gen:host-defaults`, which updates `host/Zing.Core/Defaults/*` and `Settings.cs` where generated
- Modify: the settings page under `src/renderer/src/settings/`, in the section with the other link and tab behaviour switches
- Test: `src/shared/settings/schema.test.ts` (or the existing settings test file) and `host/Zing.Core.Tests/SettingsTests.cs`

**Interfaces:**
- Produces: the settings `linkPreviews`, `linkPreviewsShiftClick` and `linkPreviewsFromNewWindows` (bool). They are readable in C# the same way as `passwordAutofill`.

- [ ] **Step 1: Write the failing tests.** In vitest, `DEFAULT_SETTINGS.linkPreviews === true`, `linkPreviewsShiftClick === true` and `linkPreviewsFromNewWindows === true`, and a string value for each is rejected. In xUnit, `Settings` defaults `true` for all three.
- [ ] **Step 2:** Run `npm test -- settings` and `npm run test:host -- --filter Settings`. Expected: FAIL (unknown keys).
- [ ] **Step 3:**
  - Add the three `flag(true)` entries, each with a one-line doc comment ("Zing: links to another host open as a preview").
  - Add the three `flag` rules in `host-defaults.ts`.
  - Run `npm run gen:host-defaults`.
  - Add three switch rows on the settings page. Labels: "Open links to other sites as a preview", "Shift-click previews any link", "Links that open a new window preview too". The second and third rows are disabled when the first is off.
- [ ] **Step 4:** Run both test commands, then `npm run typecheck` and `npm run lint`. Expected: PASS.
- [ ] **Step 5:** `git commit -m "settings: link preview switches"`

### Task 2: `PeekRules`

**Files:**
- Create: `host/Zing.Core/LinkPreview/PeekRules.cs`
- Test: `host/Zing.Core.Tests/LinkPreview/PeekRulesTests.cs`

**Interfaces:**
- Consumes: `RegistrableDomain` is **not** used. The comparison is by host.
- Produces:
```csharp
namespace Zing.Core.LinkPreview;
public enum LinkTrigger { Navigation, NewWindow }
public enum NavigationCause { AnchorClick, Other }
public readonly record struct Modifiers(bool Shift, bool Ctrl, bool Middle);
public sealed record PeekSettings(bool Enabled, bool ShiftClick, bool FromNewWindows);
public sealed record LinkRequest(string From, string To, LinkTrigger Trigger, NavigationCause Cause, Modifiers Modifiers, bool HasPopupFeatures, bool FromPreview);
public enum PeekVerdict { Normal, Preview, Undecided }
public static class PeekRules
{
	public static PeekVerdict Decide(LinkRequest request, PeekSettings settings);
	/// <summary>google.com/url?q=… and ?url=… give their target; anything else is returned unchanged.</summary>
	public static string Unwrap(string url);
	public static bool SameHost(string a, string b); // case-insensitive, one leading "www." ignored on each side
}
```

- [ ] **Step 1: Write the failing table test.** Write `[Theory] Decides(string name, LinkRequest req, PeekSettings s, PeekVerdict expected)`, using `On = new(true, true, true)`. Rows (From → To, trigger, cause, modifiers → expected):
  - `https://mail.google.com/mail/u/0/` → `https://docs.google.com/document/d/x`, Navigation, AnchorClick → **Preview**
  - `https://github.com/a` → `https://github.com/b`, Navigation, AnchorClick → **Normal**
  - `https://bbc.co.uk/` → `https://www.bbc.co.uk/news`, AnchorClick → **Normal**
  - The same Gmail → Docs navigation with Cause `Other` (script, form or redirect) → **Normal**
  - Gmail → Docs, NewWindow, no modifiers → **Preview**
  - Gmail → `https://www.google.com/url?q=https%3A%2F%2Fdocs.google.com%2Fx`, NewWindow → **Preview** (judged on the unwrapped target, which is a different host)
  - `https://a.test/x` → `https://a.test/y`, NewWindow → **Normal** (same host, so a tab as today)
  - Same-host NewWindow with Shift → **Preview**. The same with `ShiftClick: false` → **Normal**
  - Cross-host NewWindow with Ctrl → **Normal**. With Middle → **Normal**
  - Cross-host NewWindow with `HasPopupFeatures` → **Normal**
  - NewWindow → `about:blank` → **Undecided**
  - Cross-host NewWindow with `FromNewWindows: false` → **Normal**
  - Any row with `Enabled: false` → **Normal**
  - To `https://accounts.google.com/signin` → **Normal**. Likewise `login.microsoftonline.com`, `login.live.com` and `appleid.apple.com`
  - To `https://github.com/login/oauth/authorize?x` → **Normal**, while `https://github.com/wong04` from another host → **Preview**
  - To `https://meet.google.com/abc`, `https://us02web.zoom.us/j/1`, `https://teams.microsoft.com/l/x` → **Normal**
  - From `https://www.google.com/search?q=x` → `https://example.com`, AnchorClick → **Normal**. Likewise `duckduckgo.com/?q=x`, `bing.com/search?q=x`, `search.brave.com/search?q=x`, `kagi.com/search?q=x` and `startpage.com/sp/search`
  - From `https://www.google.com/maps` → `https://example.com` → **Preview** (Google but not a results page)
  - To `mailto:a@b.c`, `javascript:void 0`, `blob:…`, `data:…` or `zing://settings`, or from `zing://newtab` → **Normal**
  - To the same URL with a different `#fragment` → **Normal**
  - `FromPreview: true`, cross-host AnchorClick → **Normal**, and the same with Shift → **Normal**
- [ ] **Step 2:** Run `dotnet test host/Zing.Core.Tests --filter PeekRules`. Expected: FAIL (types missing).
- [ ] **Step 3:** Implement `PeekRules`. The checks go in this order:
  1. `!Enabled` → Normal
  2. `FromPreview` → Normal
  3. non-http(s) `From` or `To` → Normal, except `about:blank` on NewWindow → Undecided
  4. unwrap `To`
  5. excluded target host/path → Normal
  6. same URL without fragment → Normal
  7. NewWindow: Ctrl or Middle → Normal, popup features → Normal, Shift → `ShiftClick` ? Preview : Normal, `!FromNewWindows` → Normal
  8. Navigation: Cause must be AnchorClick
  9. search source → Normal
  10. `SameHost` → Normal
  11. otherwise Preview

  Keep each host list as a `static readonly` array with a one-line comment giving its spec decision number.
- [ ] **Step 4:** Run the same filter. Expected: PASS.
- [ ] **Step 5:** `git commit -m "core: peek rules"`

### Task 3: `PeekLayout`

**Files:**
- Create: `host/Zing.Core/LinkPreview/PeekLayout.cs`
- Test: `host/Zing.Core.Tests/LinkPreview/PeekLayoutTests.cs`

**Interfaces:**
- Produces: `public static PeekPlacement PeekLayout.Place(Rect pageArea, int depth)`. It returns `record PeekPlacement(Rect Page, Rect Buttons)`, in DIPs. Use the same `Rect` type as `Layout.cs` and `WindowLayout`.

- [ ] **Step 1: Write the failing tests.**
  - For `pageArea (0, 80, 1200, 720)`: the page is `x = 72` (6% of 1200), `y = 104`, `w = 1056`, `h = 672`.
  - For width 600: the side inset is 48.
  - For width 3000: the width is 1400, centred.
  - For depth 1: each side is inset 16 more.
  - The buttons rect is `x = Page.Right + 8`, `y = Page.Top`, `w = 32`, `h = 32 * 2 + 8`.
- [ ] **Step 2:** Run with `--filter PeekLayout`. Expected: FAIL.
- [ ] **Step 3:** Implement it with the Global Constraints values. Any right inset below 48 is raised to 48.
- [ ] **Step 4:** Run. Expected: PASS.
- [ ] **Step 5:** `git commit -m "core: peek layout"`

### Task 4: `PeekSession` and `PendingPopup`

**Files:**
- Create: `host/Zing.Core/LinkPreview/PeekSession.cs` and `host/Zing.Core/LinkPreview/PendingPopup.cs`
- Test: `host/Zing.Core.Tests/LinkPreview/PeekSessionTests.cs`, using `FakePage` from `Fakes.cs`

**Interfaces:**
- Produces:
```csharp
public sealed class PeekSession(string sourceTabId)
{
	public const int LayerCap = 3;
	public string SourceTabId { get; }
	public IReadOnlyList<PeekLayer> Layers { get; } // bottom first; never empty while open
	public PeekLayer Top { get; }
	public bool CanExpand { get; } // false when Top.IsPopup
	public void Push(IPage page, bool isPopup); // non-popup pushes beyond the cap throw PeekLayerCapException
	public PeekLayer Pop(); // returns the removed top; throws InvalidOperationException when one layer is left (use CloseAll)
	public IReadOnlyList<PeekLayer> CloseAll(); // top first
}
public sealed record PeekLayer(IPage Page, bool IsPopup);
public sealed class PeekLayerCapException() : Exception($"A preview holds at most {PeekSession.LayerCap} layers");
public sealed class PendingPopup(IPage page, string openerTabId, Func<string, PeekVerdict> decide)
{
	/// <summary>Called from the page's first Navigating; returns null while the URL is not yet http(s).</summary>
	public PeekVerdict? Navigating(string to);
}
```

- [ ] **Step 1: Write the failing tests.**
  - `PushingPopupsAboveTheCapIsAllowed`: three layers plus a popup gives four.
  - `PushingAPageAboveTheCapThrows`.
  - `CanExpandIsFalseUnderAPopup`.
  - `PopRemovesTheTopOnly`.
  - `CloseAllReturnsTopFirst`.
  - `PendingPopupWaitsForARealDocument`: `about:blank` gives null, then `https://x.test` gives the decision.
- [ ] **Step 2:** Run with `--filter PeekSession`. Expected: FAIL.
- [ ] **Step 3:** Implement. Neither class closes pages; the caller owns that.
- [ ] **Step 4:** Run. Expected: PASS.
- [ ] **Step 5:** `git commit -m "core: peek session and pending popup"`

### Task 5: `TabService` drives previews

**Files:**
- Modify: `host/Zing.Core/TabService.cs`, which covers `IPageEvents`, `IPageHost`, `TabPageEvents`, `AdoptPopup`, `Activate`, `Close`, `Sleep`, `Open` and `Navigate`
- Modify: `host/Zing.Core.Tests/Fakes.cs`, so the fake host records `ShowPreview` calls
- Modify: every `IPageEvents` and `IPageHost` implementer the compiler lists
- Test: `host/Zing.Core.Tests/TabServiceTests.cs`, in a new region "link previews"

**Interfaces:**
- Consumes: Task 2 (`PeekRules`, `LinkRequest`, `NavigationCause`, `Modifiers`, `PeekSettings`) and Task 4.
- Changes:
  - `IPageEvents.Navigating(string from, string to)` becomes `Navigating(string from, string to, NavigationCause cause)`.
  - `IPageEvents.Popup(string url, bool background)` becomes `IPage Popup(PopupRequest request)`, with `public sealed record PopupRequest(string Url, Modifiers Modifiers, bool HasPopupFeatures)`. "Background" becomes `Modifiers.Ctrl || Modifiers.Middle`, so the existing hard-coded `background: false` goes.
  - `IPageHost` gains `void ShowPreview(IReadOnlyList<IPage> layers)` (bottom first; an empty list removes the preview) and `void FocusPreview()`.
- Produces on `TabService`:
  - `PeekSession? Preview`
  - `void ClosePreview()`, `void CloseTopLayer()` and `TabRecord ExpandPreview()`
  - `event Action? PreviewChanged`
  - the constructor gains a `Func<PeekSettings> peekSettings` argument

- [ ] **Step 1: Write the failing tests.** Use the fake host and fake pages. `Navigating` is called as the host would call it.
  - `CrossHostAnchorClickOpensAPreviewNotATab`: the decision is `Cancel`, `Preview.SourceTabId` is the tab, the tab count is unchanged, `ShowPreview` gets one page, and that page was asked to navigate to the target.
  - `SameHostClickNavigatesInPlace`.
  - `NewWindowToAnotherHostReturnsThePreviewPage`: the page `Popup` returns is the preview's page, which keeps `window.opener`.
  - `CtrlNewWindowStillOpensABackgroundTab`.
  - `LinksInsideAPreviewNavigateIt`: the preview page's own `Navigating` returns Allow for cross-host, and no second layer appears.
  - `PopupFromAPreviewStacksALayer`: Popup from the preview page pushes `isPopup: true`, and `CanExpand` is false.
  - `PopupLayerClosingItselfShowsTheLayerBelow`.
  - `PreviewPageClosingItselfClosesPreview`: the closed URL is on the reopen stack.
  - `ClosePreviewPushesReopenableTab`: `ReopenClosed()` gives an unpinned tab with the preview's URL and title.
  - `ActivatingAnotherTabClosesThePreview`. Do the same for `Open`, for `Navigate(sourceId, …)`, and for `ClosingTheSourceTabClosesItsPreview`.
  - `SourceTabCannotSleepWhilePreviewing`: `Sleep(source)` throws `ActiveTabSleepException`.
  - `ExpandAdoptsTheSamePage`: the new record is right after the source, `PageOf(newId)` is the same `FakePage` instance, `Navigate` was not called again, `Preview` is null, the new tab is active, and closing it returns to the source.
  - `ExpandUnderAPopupThrows` (`InvalidOperationException`).
  - `PendingPopupThatNeverNavigatesIsDropped`: closing the opener's tab closes the held page, and so does the 10 s timer through `IWindowHost.Scheduler`. If `TabService` has no scheduler, take one in the constructor the same way the other delayed work does.
  - `HeldWindowThatBecomesSameHostOpensAsTab`.
- [ ] **Step 2:** Run `dotnet test host/Zing.Core.Tests --filter TabService`. Expected: FAIL.
- [ ] **Step 3: Implement.**
  - **Preview pages** get a private `PeekPageEvents(TabService, PeekLayer)`, which:
    - forwards `Navigated` and `TitleChanged` to `PreviewChanged`
    - returns `Allow` from `Navigating`, after `LinkCleaner` like tabs do
    - pushes a popup layer from `Popup`
    - closes its layer, or the whole preview at the bottom, from `Closed`
    - leaves `Focused` empty
  - **Expand** needs the page's events swapped to the new tab's `TabPageEvents`. Add `void Rebind(IPageEvents events)` to `IPage`, so `WebView2Page` keeps one `_events` field that can be replaced.
  - **Pending windows:** a held page is created with a `PendingPageEvents` that asks `PendingPopup.Navigating`. Once it decides, the page is rebound to a new tab (same as `AdoptPopup`) or to a preview, and only then shown.
- [ ] **Step 4:** Run `npm run test:host`. Expected: all PASS, existing tests included.
- [ ] **Step 5:** `git commit -m "tabs: open, close and expand link previews"`

### Task 6: Host plumbing (reason, modifiers, held windows, scrim, layout)

**Files:**
- Modify: `host/Zing.Host/WebView2Page.cs` (`Wire`, the `NavigationStarting` handler, `OnNewWindowRequested`, `OpenPopupAsync`, `Rebind`)
- Create: `host/Zing.Host/PeekScrim.cs`
- Modify: `host/Zing.Host/OverlayHost.cs`, adding `PeekScrim` and `PeekControls` before `PageUi`
- Modify: `host/Zing.Host/MainWindow.cs` (`ShowPreview`, `FocusPreview`, the relayout path that handles `WM_SIZE`, `WM_MOVE` and `WM_DPICHANGED`, and focus return)
- Modify: `host/Zing.Host/TestHooks.cs`, adding the actions `set-modifiers` and `peek-state`, and a `scrim` entry in `Layers`
- Test: these are host types, so they are covered by e2e in Task 9. Plus `host/Zing.Core.Tests` for anything moved to Core.

**Interfaces:**
- Consumes: Task 3 `PeekLayout.Place` and Task 5's `IPageHost` additions.
- Produces:
  - `sealed class PeekScrim : IDisposable` with `PeekScrim(nint owner, bool highContrast)`, `void Place(Rect pageAreaPx, Rect holePx)`, `event Action? Clicked` and `nint Handle`.
  - The `TestHooks` actions `{action: 'set-modifiers', shift, ctrl, middle}` and `{action: 'peek-state', file}`. The second writes `{ open, layers: [{url}], canExpand, sourceTabId }`.

- [ ] **Step 1: Navigation reason.**
  - In `Wire`, call `Page.enable` and subscribe to `Page.frameRequestedNavigation`. Keep `_lastReason = (url, reason)` for main-frame entries only; skip a `frameId` that isn't the main frame's.
  - In `NavigationStarting`, pass `NavigationCause.AnchorClick` when `_lastReason.url == e.Uri && reason == "anchorClick" && !e.IsRedirected`, otherwise `Other`. Clear `_lastReason` after use.
  - The decision stays synchronous: the spike showed there is no deferral.
- [ ] **Step 2: Modifiers and held windows.**
  - `OnNewWindowRequested` builds `PopupRequest`:
    - `Modifiers` from `Native.IsDown(VK_SHIFT/VK_CONTROL/VK_MBUTTON)`, or from the e2e seam when one is set. The seam is cleared after one use.
    - `HasPopupFeatures` is `e.WindowFeatures.HasSize || e.WindowFeatures.HasPosition`.
  - The returned page is set as `e.NewWindow` as today. A held page's view stays hidden until `ShowPreview` or `Show` places it.
  - **Download from a preview:** when a preview page's `DownloadStarting` fires before it has committed any document, the download proceeds as usual and the page reports `Closed()`, so the preview closes (spec: "the preview closes if it never showed a document").
- [ ] **Step 3: Scrim.** Port `Win.CreateScrim` and `Win.PlaceScrim` from `spikes/link-preview/Program.cs`:
  - styles `WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE`
  - `MA_NOACTIVATE`
  - `WM_LBUTTONDOWN` raises `Clicked`
  - region = page area minus hole, rebuilt on every `Place`
  - `SetWindowRgn` returning 0 throws `Win32Exception`
  - the high-contrast alpha from Global Constraints, read via `SPI_GETHIGHCONTRAST` once per show
  - `WM_PAINT` draws the 1 px border (2 px under high contrast) and an 8 px darkening band just outside the hole edge, which is the "soft shadow". Both are under the scrim's constant alpha.
- [ ] **Step 4: `MainWindow.ShowPreview(layers)`.**
  - Create the scrim on first show.
  - For each layer `i`, place its page view at `PeekLayout.Place(PageRect(), i).Page` and bring it to the top among the page child windows.
  - Place the scrim with the top layer's rect as the hole.
  - Tell `OverlayHost.Show(OverlayLevel.PeekScrim)`. The scrim's `Clicked` calls `TabService.ClosePreview()`.
  - An empty list destroys the scrim and returns focus through `DefaultFocus` to the source tab's page.
  - `FocusPreview` focuses the top layer.
  - Relayout re-runs this whenever the page rect changes.
  - In split view, `PageRect()` is the whole split area.
- [ ] **Step 5: Run `npm run build:host` and `npm run test:host`.** Expected: the build succeeds and tests PASS.
- [ ] **Step 6:** `git commit -m "host: preview scrim, navigation reason and modifiers"`

### Task 7: Preview buttons (`#peek`)

**Files:**
- Create: `src/renderer/src/overlay/PeekControls.tsx` and `PeekControls.test.tsx`
- Modify: `src/renderer/src/main.tsx` (`isPeek = location.hash === '#peek'`, included in `isOverlay`)
- Create: `host/Zing.Host/PeekControlsLayer.cs`, modelled on `PageUiLayer` (a lazy `RendererView` with `overlay: true`, registered at `OverlayLevel.PeekControls`)
- Modify: the bridge or channel contract the renderer and `ChromeApi` already use for overlay state (see `ChannelContractTests.cs`): add `peek` state `{ host: string, canExpand: boolean, highContrast: boolean } | null` and the actions `preview.close` and `preview.expand`
- Test: `PeekControls.test.tsx` and `ChannelContractTests.cs`

**Interfaces:**
- Consumes: Task 5's `TabService.Preview`, `PreviewChanged`, `ClosePreview` and `ExpandPreview`, and Task 3's `PeekPlacement.Buttons`.
- Produces: the `preview.close` and `preview.expand` actions, handled in `ChromeApi`. Task 8 reuses them.

- [ ] **Step 1: Write the failing test.**
  - It renders two buttons named "Close preview" and "Open as tab", with tooltips "Close preview (Esc)" and "Open as tab (Ctrl+O)".
  - "Open as tab" is `disabled` when `canExpand` is false.
  - The live region reads "Preview of docs.google.com. Press Escape to close."
  - Clicks send `preview.close` and `preview.expand`.
- [ ] **Step 2:** Run `npm test -- PeekControls`. Expected: FAIL.
- [ ] **Step 3: Implement.**
  - The buttons are 32 DIP, round, and use the theme and accent tokens like the other overlays. They have visible focus rings.
  - Under `prefers-reduced-motion`, there is no fade-and-scale. Otherwise it is 120 ms.
  - The layer's view is placed at `Buttons`. The border and shadow are the scrim's job (Task 6).
- [ ] **Step 4:** Run `npm test`, `npm run typecheck`, `npm run lint` and `npm run test:host`. Expected: PASS.
- [ ] **Step 5:** `git commit -m "renderer: preview buttons"`

### Task 8: Keys

**Files:**
- Modify: `src/shared/commands/registry.ts`, adding `{ id: 'preview.expand', label: 'Open Preview as Tab', shortcuts: [ctrl('KeyO')] }` and `{ id: 'preview.close', label: 'Close Preview', shortcuts: [] }`, then regenerate the host defaults
- Modify: `host/Zing.Core/ChromeApi.cs` (command switch near `"tab.close"`)
- Modify: `host/Zing.Host/WebView2Page.cs`, adding an Esc listener script for preview pages only
- Test: `host/Zing.Core.Tests/ChromeApiTests.cs` and `src/shared/commands/*.test.ts`

**Interfaces:**
- Consumes: Task 5 and Task 7.

- [ ] **Step 1: Write the failing tests.**
  - `TabCloseClosesThePreviewFirst`: with a preview open, `tab.close` closes the preview and the tab stays.
  - `PreviewExpandWithNoPreviewIsNotApplicable`.
  - `PreviewExpandUnderPopupIsNotApplicable`.
  - `PreviewCloseClosesTopLayer`.
  - `PageStopStillStopsWhenNoPreview`.
  - In vitest, `Ctrl+O` resolves to `preview.expand`, and nothing else is bound to `Ctrl+O`.
- [ ] **Step 2:** Run. Expected: FAIL.
- [ ] **Step 3: Implement.**
  - `tab.close` closes the preview when one is open.
  - **Esc:** a preview page gets a document-created script. It adds a bubble-phase `keydown` listener on `window`, and when the event is `Escape`, `!defaultPrevented` and `!document.fullscreenElement`, it posts `{ kind: 'peek-escape' }`. `WebView2Page` turns that into `TabService.CloseTopLayer()`.
  - Zing does **not** bind Esc for previews in the accelerator path, so the page always sees Esc first.
  - If this proves unreliable in the hand check, remove the script and note it in `PARITY.md`, as the spec allows.
- [ ] **Step 4:** Run `npm test` and `npm run test:host`. Expected: PASS.
- [ ] **Step 5:** `git commit -m "commands: preview keys"`

### Task 9: e2e

**Files:**
- Create: `e2e/link-preview.spec.ts`
- Modify: `e2e/zing.ts`, adding `peekState(chrome)` and `setModifiers(chrome, m)` next to `layers()`
- Modify: `e2e/real-input.spec.ts`, adding two `@real-input` tests

**Interfaces:**
- Consumes: Task 6's `TestHooks` actions.

Use a local `createServer` serving the same pages on `127.0.0.1` and `localhost` (two hosts), as `core.spec.ts` does. The fixture pages are:
- `/a` with a cross-host link, a same-host link, `target=_blank`, `window.open(url)` and `window.open('about:blank')` then set location
- `/b`, a tall page with an input
- `/opener` and `/popup` (copy `core.spec.ts`'s)

- [ ] **Step 1: Write the tests.** Real clicks go through `page.click` on the content page's CDP target. That is a trusted click, so it gives `anchorClick`, as the spike showed.
  1. `a cross-host link opens a preview, not a tab`: `peekState.open`, the tab count is unchanged, and `layers()` has the preview view at `PeekLayout`'s rect within 1 DIP, a visible `scrim`, and the `#peek` view.
  2. `a same-host link navigates in place`.
  3. `the close button and Ctrl+W close it and leak nothing`: `liveCounts` equals the value before opening, and `peekState.open` is false.
  4. `expand keeps the same page`: set `window.__marker` and read `performance.timeOrigin` in the preview, then send `preview.expand`. There is one more tab, its URL is `/b`, the marker survives, `timeOrigin` is equal, and the scrim is gone.
  5. `a popup from a preview keeps window.opener`: the popup's title is "Popup with opener", and after `window.close()` the preview shows `/opener` again.
  6. `with linkPreviews off a cross-host link navigates in place`.
  7. `shift-click previews a same-host link` (`setModifiers({shift: true})`, then trigger `window.open` same-host).
  8. `a blank window set later becomes a preview`.
  9. `switching tabs closes the preview and Ctrl+Shift+T reopens it as a tab`.
  10. `closing the window during preview start leaks nothing`: add a checkpoint to `teardown.spec.ts`'s list.
- [ ] **Step 2:** Run `npm run test:e2e -- link-preview teardown`. Expected: PASS. Fix the product code, not the tests, if anything fails.
- [ ] **Step 3:** Add the `@real-input` tests "real shift-click previews" and "Esc closes the preview, and Esc in a page dialog does not". Run them by hand with `npm run test:e2e:real-input`.
- [ ] **Step 4:** Run the full suite: `npm test`, `npm run test:host`, `npm run lint`, `npm run typecheck` and `npm run test:e2e`. Expected: all PASS.
- [ ] **Step 5:** `git commit -m "e2e: link previews"`

### Task 10: Hand checks, parity row, screenshots

**Files:**
- Modify: `PARITY.md`. Add a `new` row, "Link previews (Arc Peek)", with a new GitHub issue made with `gh issue create --title "[ID] Link previews (Arc Peek)"`, closed only with evidence.
- Modify: `README.md`, adding a feature line.
- Add: light and dark screenshots under `docs/screenshots/`, taken the way `theme-screenshots.spec.ts` takes them.

- [ ] **Step 1: Hand checks**, written down in the PR:
  - H1: the dim and the hole at 100% and 150% DPI.
  - H2: dragging the window to a monitor with different scaling while a preview is open.
  - H3: live drag-resize, with no flicker that exposes the dim over the preview.
  - H4: Narrator reads "Close preview" and "Open as tab" and the live region.
  - H5: Alt+Tab away and back, and focus returns to the preview page.
  - H6: high contrast.
  - H7: Gmail → a Docs link, and a Google search result loads in place.
  - H8: `Page.enable` cost. Open 20 tabs and compare memory and CPU with the previous build, and note it.
- [ ] **Step 2:** Add the screenshots and the PARITY row, open the issue, and run the full suite again.
- [ ] **Step 3:** `git commit -m "docs: link previews parity row and screenshots"`

---

## Deferred (spec non-goals)

The split-view button, find inside a preview, previews that survive a tab switch, reopen as a preview, rounded corners, and Little Arc.
