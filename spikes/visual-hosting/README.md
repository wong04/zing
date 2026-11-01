# W-6 spike: visual hosting (decision record)

> **Decision (2026-11-01): not adopted; this spike is kept as the record.** Zing keeps owned popups, with their order and focus held by `OverlayHost` (`docs/architecture-review.md` §4 W-6). The popup model passes every e2e test and the real-input tier on Windows. Switching would cost 4–6 weeks, with Narrator, IME and drag-drop unproven, and the hand checks below were never run. Revisit only if Phase 4's split view brings the overlay bugs back.
>
> **Automated checks, run on Windows CI:** On `windows-latest` ([w6-checks run 41290750480](https://github.com/wong04/zing/actions/runs/41290750480)), 5 of the 6 checks in `checks.spec.ts` pass:
> - the layout;
> - the overlay draws over the page and is see-through where it has no content (window pixels);
> - **a DevTools click on an overlay row reaches the overlay's DOM** (today's popup model fails this);
> - host-routed clicks land on the topmost view that wants the point;
> - focus moves to the clicked view, and each view then takes keys.
>
> **The cursor check fails** ("Arrow" where "Hand" is expected). This is the known gap under "What Zing would have to own": the C#/WinRT projection gives no system cursor id, so Zing would need its own cursor mapping (`SystemCursorId` via the interop interface, or the CSS cursor from the page). IME, drag-drop from Explorer, Narrator, DPI and real mouse behaviour are still the hand checks below. The decision waits on them.
>
> For comparison, today's popup model passed its own real-input tier on the same runner: a real click on a stack dropdown row and focus after closing a tab (`e2e/real-input.spec.ts`).

This is the spike for **W-6** in `docs/architecture-review.md` §4: should Zing move from windowed WebView2 controllers with owned transparent popup overlays (today, proved by `spikes/overlay-window`) to **visual hosting**, where every view is a visual in one window's composition tree?

## What was built

One top-level Win32 window (`WS_EX_NOREDIRECTIONBITMAP`) with a Windows.UI.Composition tree (`Compositor` + `DesktopWindowTarget`). Three `CoreWebView2CompositionController`s, each with its `RootVisualTarget` set to its own `ContainerVisual`:

| View | Visual order | Content (`web/`, virtual host `https://spike.zing/`) |
|---|---|---|
| chrome | 1 (bottom) | Strip at the top, 56 DIP: an input, plus the test-message relay |
| page | 2 | Red page: link, text, an input with `lang=ja`, a file drop zone, and a yellow block under the overlay's panel |
| overlay | 3 (top) | Transparent and covering the whole page. It holds one dropdown panel: blue header, filter input, four rows |

The app owns the following:

- **Mouse forwarding** (`SpikeWindow.OnMouse`).
  - Covers move, all buttons down/up/double-click, X buttons, wheel and horizontal wheel (screen to client), and Leave when the pointer moves from one view to another or leaves the window (`TrackMouseEvent`).
  - Capture on button down, so a drag stays with the view where it started. Capture is released when no button is held.
  - Points are sent relative to the view.
- **Hit-testing** (`HitTest`, `CompositedView.Wants`). The topmost view that wants the point gets it.
  - The overlay wants a point only inside rectangles it reports over `postMessage` (`[data-hit]` elements, re-sent from a `ResizeObserver` and on toggle). Elsewhere input falls through to the page.
- **Cursor.** On `CursorChanged` the view's `CoreCursor` is mapped to a system `HCURSOR`. `WM_SETCURSOR` shows the cursor of the view under the pointer.
- **Keyboard focus.**
  - `MoveFocus` on the clicked view at button down.
  - Focus goes back to the last view on `WM_ACTIVATE`/`WM_SETFOCUS`.
  - `MoveFocusRequested` handles Tab across views (chrome → page → overlay).
  - Every view's `AcceleratorKeyPressed` is logged, and Escape toggles the overlay panel.
- **DPI.** Per-monitor v2 is set at startup. Visual offsets and `Bounds` are in physical pixels, and each view gets `RasterizationScale`. `ShouldDetectMonitorScaleChanges=false`, so the window drives the scale from `WM_DPICHANGED`.
- **Resize.** `WM_SIZE` lays out all three views: the visual offset and size, plus `Bounds` with the same offset (the WebView2APISample pattern).
- **Drag-and-drop.** The window's OLE `IDropTarget` is a hand-built COM object. It forwards to `ICoreWebView2CompositionControllerInterop2::DragEnter/DragOver/DragLeave/Drop` of the view under the pointer, and switches views in the middle of a drag.
- **UI Automation, minimal version.** `WM_GETOBJECT(UiaRootObjectId)` returns one view's provider (`ICoreWebView2CompositionControllerInterop::get_AutomationProvider`, QI'd to `IRawElementProviderSimple`): the focused view, or the overlay while it has content, or else the page. What is missing is listed below.
- **Test seams** (only with `ZING_E2E=1`). The chrome view relays `state`, `pixel`, `move` and `click` test messages.
  - `move` and `click` post real `WM_MOUSEMOVE`/`WM_LBUTTONDOWN`/`WM_LBUTTONUP` messages to the window, so they take the same WndProc → hit-test → `SendMouseInput` path as a real click. Only the OS's choice of window is skipped.
  - `pixel` reads the composed window through `PrintWindow(PW_RENDERFULLCONTENT)`.

Files:
- `Program.cs`: window, input, focus, drag-drop routing, UIA root, test seams.
- `CompositedView.cs`: one composition-hosted view.
- `Interop.cs`: dispatcher queue, `DesktopWindowTarget`, the WebView2 interop vtable, `IDropTarget`, P/Invoke.
- `web/`: the three pages.
- `checks.spec.ts` and `playwright.config.ts`.

The spike uses no Windows App SDK. Like Zing.Host, it uses the **C#/WinRT projection** of WebView2 (`WebView2EnableCsWinRTProjection`), so its types (`Windows.Foundation.Rect`, `CoreCursor`) are the ones Zing would use.

### Run it (Windows)

```
dotnet build -c Release spikes/visual-hosting
npx playwright test -c spikes/visual-hosting/playwright.config.ts      # automated checks (window off-screen)
spikes\visual-hosting\bin\Release\net9.0-windows10.0.22621.0\win-x64\VisualHostingSpike.exe   # hand checks
```
The log is in `%TEMP%\zing-visual-spike.log`: focus, clicks, cursors, drags and failures.

## Go/no-go criteria (from the review) and results

| Criterion | How the spike does it | Automated check (`checks.spec.ts`) | Result |
|---|---|---|---|
| Overlay draws over the page, see-through elsewhere | Visual order; overlay `DefaultBackgroundColor` alpha 0 | Window pixels: overlay header is blue, a bare page corner is the page's red, the chrome is brown | **to check on Windows** |
| DevTools input reaches the overlay (the gap today) | Overlay is an ordinary composition view, no popup HWND | `locator.click()` on row 2 → overlay title `chose:2`, host relays to the page | **to check on Windows** |
| Real clicks land on dropdown rows | `HitTest` + `SendMouseInput` | Host-routed click on row 3 reaches the overlay, and the page element under it gets nothing. Host-routed click on the page link reaches the page | **automated part to check; real mouse by hand** |
| Keys reach the focused view | `MoveFocus` on click | Host click on the page input → `GotFocus` page, `activeElement` is the field, CDP keys land. Same for the overlay filter, then the chrome | **automated part to check; real keyboard by hand** |
| Cursor shapes | `CursorChanged` → `CoreCursor` → `HCURSOR` | Host move over link → `Hand`, text → `IBeam`, overlay row → `Hand` | **to check on Windows** |
| IME works | WebView2's own input window (no app code) | none (CDP can't drive an IME) | **by hand** |
| Drag-drop works | OLE `IDropTarget` → Interop2 | none (CDP drag events bypass OLE) | **by hand** |
| Narrator reads overlays | Minimal UIA root = focused view's provider | none | **by hand; expected partial** (see below) |

Note on the automated keyboard check: CDP `keyboard.type` sends keys to one view's renderer whatever has OS focus. So the check proves that focus moves (`GotFocus`, `activeElement`) and that each view accepts keys. It does not prove that the OS sends real keystrokes to the focused view. That part is a hand check.

To compare with today's popup model, run the same DevTools-click check against Zing. Today it is the known failing case (`#overlay-stack`). The other criteria pass in the popup model today, except where focus and z-order bugs bite.

## Hand-check list (Windows, real mouse and keyboard)

Start the exe without `ZING_E2E` and keep `%TEMP%\zing-visual-spike.log` open.

1. **Real clicks.**
   - Click each overlay row. The page shows "chosen N" and the log shows `mouse 0x201 -> overlay`.
   - Click the yellow block's visible edge (outside the white panel). The page gets it.
   - Click between panel rows and on the panel's shadow. The overlay should get only points inside the panel rectangle. The shadow belongs to the page.
   - Double-click text in the page. A word is selected.
   - Right-click the page. The context menu opens at the pointer, which checks the `Bounds` offset.
   - Wheel-scroll the page. Make the page longer with devtools if needed.
   - Press in the page, drag over the overlay, and release. The selection stays in the page (capture).
2. **Keys.**
   - Click the page field and type. Click the overlay filter and type. Click the chrome input and type. Each time, only the clicked view gets the text.
   - Alt+Tab away and back. Typing continues in the same view.
   - Tab through the views: chrome → page → overlay → chrome.
   - Press Escape. The overlay panel hides, and clicks there now reach the yellow block. Press Escape again to bring it back.
3. **IME (Japanese).** Add Microsoft IME (Japanese) and switch to it with Alt+\`. In the page field, type `nihongo`, then press Space and Enter.
   - The composition is underlined inline.
   - The candidate window sits under the caret, not at the screen's top-left (the `Bounds` offset).
   - The committed text is 日本語.
   - Repeat in the overlay filter.
4. **Drag-drop.**
   - Drag a file from Explorer onto "Drop a file here". The zone shows `dropped: <name>`, and the log shows `drag enter page` and then `drop page`.
   - Drag over the overlay panel. The log shows `drag enter overlay` and the panel refuses the drop.
   - Drag out of the window. The log shows no drop.
   - Drag text out of the page into Notepad. Dragging *from* a view isn't forwarded by the spike (see costs).
5. **Narrator** (Win+Ctrl+Enter).
   - Click the overlay filter, then press Caps Lock+→ to move through the panel. Narrator should read "Overlay dropdown", the rows and "option".
   - Click the page field. Narrator should read the page.
   - Scan mode, Inspect.exe or Accessibility Insights: note whether the tree shows all three views or only the one that was focused when UIA first asked. **Expected: only one view at a time** (see below).
6. **Cursor.** Check each cursor in turn:
   - Arrow over blank page.
   - Hand over the link and the overlay rows.
   - I-beam over text and inputs.
   - Resize arrows on the window edges, which Windows draws.
   - Arrow again over the chrome's blank part.
7. **DPI and resize.**
   - Drag the window between monitors at 100% and 150%. Text stays sharp and the panel stays aligned with the yellow block.
   - Resize quickly. Nothing lags behind or shows gaps worse than with windowed controllers.
   - Maximize.
8. **Compare** the same steps against Zing today. That covers focus after Alt+Tab, a dropdown open while switching apps, and DevTools.

## Unverified assumptions (most likely to break on the first run)

- `RootVisualTarget = ContainerVisual` (a C#/WinRT object) is accepted by the C#/WinRT WebView2 projection. Both sides are WinRT objects, so the assignment should marshal. WebView2 documents Windows.UI.Composition `ContainerVisual` and `IDCompositionVisual` as accepted, and is silent on the App SDK's Microsoft.UI.Composition.
- QI of the projected controller's native object for `ICoreWebView2CompositionControllerInterop2` (`6b47bbe1-…`, from `WebView2Interop.idl` in the package) succeeds, and its vtable slots are 3–9 as in the IDL.
- `PrintWindow(PW_RENDERFULLCONTENT)` captures DesktopWindowTarget content for a window placed off-screen. If it doesn't, the pixel check needs Windows.Graphics.Capture, or `ZING_SPIKE_ONSCREEN=1`.
- `GotFocus` fires for `MoveFocus` on composition controllers while the window is inactive (test windows never activate).
- Keyboard input and IME need no forwarding. WebView2 composition hosting is documented to forward pointer input only, with keyboard going through its own focused input window. Confirm with Spy++ which HWND has focus.

## What Zing would have to own if it switches

Today's windowed controllers do all of this inside WebView2. With visual hosting, each item is Zing's code in `host/Zing.Host`, and most of it has no automated test without a real-input tier:

1. **Pointer input** (the spike has the mouse part).
   - Mouse forwarding, capture, leave, wheel, double-click, X buttons.
   - **Touch and pen:** `WM_POINTER*` → `SendPointerInput` with `CoreWebView2PointerInfo` filled from `GetPointerInfo` / `GetPointerPenInfo` / `GetPointerTouchInfo`. *Not implemented.* Windows promotes touch to mouse messages, so taps work, but pinch-zoom, pen pressure and touch scrolling don't.
2. **Non-client hit-testing.** Zing's chrome strip drags the window by CSS `app-region: drag` and extends into the title bar with `AppWindow`. With composition, `WM_NCHITTEST` has to ask `GetNonClientRegionAtPoint` (or `QueryNonClientRegion`) of the chrome view. *Not implemented.* It is also untested whether `AppWindow.TitleBar.ExtendsContentIntoTitleBar` and `WS_EX_NOREDIRECTIONBITMAP` work together.
3. **Cursor.** The C#/WinRT projection gives a `CoreCursor` and has no `SystemCursorId` or `HCURSOR`. So CSS `cursor: url(...)` and other custom cursors come back as `Custom` and show as an arrow. Fixing that needs the COM `ICoreWebView2CompositionController::get_Cursor`, which the interop interfaces don't expose.
4. **Focus.** Covers which view is focused, focus restore on activation, Tab between views, and focus when an overlay closes. This replaces today's `GotFocus` + `WM_SETFOCUS` tracking (`HostWindow.cs`) and removes the owned-popup activation logic.
5. **Drag-drop.**
   - Into views: an `IDropTarget` and Interop2 forwarding, as in the spike.
   - Out of views: `ICoreWebView2CompositionControllerInterop3::add_DragStarting` to start an OLE drag, or WebView2's default behaviour, to verify. *Not implemented.*
6. **UI Automation, the largest piece.** The minimal root in the spike is Microsoft's single-view pattern. With three or more views, UIA needs one tree:
   - a fragment root for the window (`IRawElementProviderSimple` + `IRawElementProviderFragmentRoot`, host provider from `UiaHostProviderFromHwnd`), with each view's provider as a child
   - `Navigate` between siblings, which WebView2's providers can't do because they don't know each other. That probably means proxy fragments that delegate to each view's provider, or `IRawElementProviderHwndOverride` + `ICoreWebView2EnvironmentInterop::GetAutomationProviderForWindow` as the WebView2 AutomationProvider spec describes for walking parents.
   - `ElementProviderFromPoint` with the same hit-test as the mouse, so the overlay wins only over its content
   - `GetFocus`, and `UiaRaiseAutomationEvent(UIA_AutomationFocusChangedEventId)` when focus moves between views
   - `BoundingRectangle` checks under DPI scaling

   There is no Microsoft sample for several WebView2s in one composition tree. Treat this as the part that decides go/no-go.
7. **DPI.** Set `RasterizationScale` on every view on `WM_DPICHANGED`, and keep all layout in physical pixels. The layout math moves from `Bounds` alone to visual offset + `Bounds`.
8. **Threading.** Windows.UI.Composition needs a **system** `DispatcherQueue` on the UI thread. Zing's `Program.Main` creates the App SDK's `Microsoft.UI.Dispatching` queue today. The spike creates the system one with `CreateDispatcherQueueController` and its own `SynchronizationContext`. Zing would do the same, or confirm whether the App SDK queue also satisfies Windows.UI.Composition. It does not work with Microsoft.UI.Composition unless WebView2 accepts those visuals.
9. **Overlays as views.** Each overlay layer (surface, notices, page UI, stack dropdown, overview) becomes a composition view in the main window's tree instead of an owned popup. Each must report its hit regions; the React overlay-stack would post its rectangles, as `overlay.html` does. A notice or dropdown that should extend *outside* the window can't, because visuals are clipped to the client area. Popups stay for that case.
10. **What WebView2 still opens as its own windows:** `<select>` popups, context menus, the IME candidate window, autofill, permission bubbles, DevTools. They are placed from `Bounds`, so `Bounds` must always carry the view's real offset.
11. **Tests.** The e2e harness gains `TestHooks` messages like the spike's (host-routed clicks, cursor and focus state), and the real-input smoke tier from W-6 "either way" becomes required, not optional.

What goes away: owned popup windows and their z-order (`Raise()` per layer, `MainWindow.cs:392`), the activation and focus-steal dance, the `Cursor` foreground-HWND check (`MainWindow.cs:207`), the black-overlay class of bug, and the DevTools input gap. Split view (Phase 4) becomes more visuals in one tree instead of more popups to keep in order.

## Rejected variant

Keep windowed page controllers and put only the overlays in a topmost `DesktopWindowTarget` (`isTopmost: true` draws visuals above child HWNDs). It draws correctly, but the OS sends clicks to the page's child HWND, which belongs to WebView2, so the overlay never gets them. That loses the main gain.

## Recommendation

**Decided 2026-11-01: keep owned popups (see the status at the top).** The rule below is kept for a future revisit, which would start with the hand checks:

- **Go (visual hosting)** if real clicks, keys after focus moves, IME placement, file drop and the cursor all pass, and Narrator reads the focused overlay through the minimal root. Do the UIA fragment root (item 6 above) **first**, as its own gated step, because it is the unproven part. If Narrator can't walk all the views in one tree after about a week on that step, stop and go to no-go.
- **No-go (keep popups, centralise them in one `OverlayHost`)** if IME placement, drag-drop or Narrator fails in a way the interop can't fix. The popup model passes those criteria today.

**Effort, honestly estimated, for one person who knows the host:**

| Work | Estimate |
|---|---|
| Composition window + `HostedView` on composition controllers, layout, DPI, system dispatcher queue | 2–3 days |
| Mouse input, capture, cursor, non-client hit-test for the title bar, touch/pen via `SendPointerInput` | 3–5 days |
| Focus model (click, activation, Tab between views, overlay close) | 1–2 days |
| Drag-drop in and out | 1–2 days |
| UIA fragment root with several views, focus events, Narrator testing | 4–8 days (largest uncertainty) |
| Move each overlay layer from popup to view, with hit-region reporting from React | 3–4 days |
| e2e seams + real-input smoke tier, hand-testing and fixes | 4–6 days |
| **Total** | **about 4–6 weeks**, compared with about 1 week for the centralised `OverlayHost` |

My lean, given the evidence so far: visual hosting fixes the bug *class* (z-order and focus across top-level windows) rather than individual bugs, and split view would multiply that class. So it is worth the cost **if** the UIA step works. Most of the cost is in UIA, and the risk is in UIA plus the title-bar interaction. Doing the "either way" items from W-6 first (split `MainWindow`, add the real-input tier) is useful under both outcomes, and makes the switch cheaper if it happens.
