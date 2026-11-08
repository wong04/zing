# Link preview spike (throwaway)

This spike tests the risky parts of `docs/superpowers/specs/2026-11-08-link-preview-design.md` before the plan. It is a plain Win32 window with child-window WebView2s (Zing's model) and a local server on two hosts (`127.0.0.1` and `localhost`, so cross-host is real). It runs the checks by itself, writes `results.json` and screenshots next to the exe, and exits.

```
dotnet build spikes/link-preview -c Debug
ZING_E2E=1 ZING_USER_DATA=<scratch dir> spikes/link-preview/bin/Debug/net9.0-windows10.0.22621.0/win-x64/LinkPreviewSpike.exe [--stay]
```

The window has to be on screen, because the check samples pixels from the screen.

## Results (2026-11-08, WebView2 SDK 1.0.4258.31, Windows 11 26300)

### A. Scrim with a hole: works (design A is a go)

- A `WS_EX_LAYERED | WS_EX_NOACTIVATE` owned popup with alpha 115 and a `SetWindowRgn` rectangle-minus-hole region dims the live child-window page. The preview page shows undimmed through the hole.
- The dim covers only the page area. The 48 DIP "toolbar" strip stays clear.
- Hit-testing: `WindowFromPoint` in the hole returns the preview's `Chrome_RenderWidgetHostHWND`, and on the dim it returns the scrim.
- The page behind keeps running (its timer advanced while dimmed).
- After a window resize, rebuilding the region on `WM_SIZE` puts the dim and the hole in the right place.
- Sampled colours match the expected 45% dim once the display's colour transform is allowed for. The green channel is `0x56` against `0x57` expected.

Not covered (hand checks): a DPI change between monitors, flicker during a live drag-resize, and activation and focus while the user clicks around.

### B. Expand without reload: works, both ways

- **Moving the preview's child window into the tab slot** (design A's expand) keeps the page as it was: `window.__marker`, typed input, `scrollY` 700 and `performance.timeOrigin` all survive, with no `NavigationStarting`.
- **Re-parenting the controller** (`controller.ParentWindow = CreateFromWindowHandle(other)`) keeps everything too. So design B's fallback would also be workable.

### C. Telling a link click from other navigations: works, so plain-click previews are in v1

`Page.frameRequestedNavigation` (after `Page.enable`) arrived **before** `NavigationStarting` in every case, so `NavigationStarting` can read the reason and cancel.

| Case | reason | `IsUserInitiated` | `IsRedirected` |
|---|---|---|---|
| Real (DevTools input) anchor click | `anchorClick` | true | false |
| Script `a.click()` | `anchorClick` | true | false |
| Script `location.href = …` | `scriptInitiated` | true | false |
| Form POST button | `formSubmissionPost` | true | false |
| Same-host link that 302s to the other host | the first hop only (`anchorClick` for the same-host URL); none for the redirected one | true | **true** |

- `IsUserInitiated` was true for every case, so it is useless for this. The reason field is the signal.
- `NavigationStartingEventArgs` has **no deferral** in this projection. The decision must be synchronous, which it can be, because the reason is already there.
- Redirects: the cross-host hop arrives as `IsRedirected` with no reason, so it is not previewed. The rule is: preview only when the latest reason for that exact URL is `anchorClick`.

### D. Holding a new window until its first real document: works

- `NewWindowRequested` → deferral → a controller in a **hidden** child window → `e.NewWindow`. The engine loads into it while it stays hidden, and `NavigationStarting` on the new core gives the first real `http(s)` URL.
- `window.open('about:blank')` followed by setting `location` gives `about:blank`, then the real URL. Judge on the second.
- `window.opener` is kept for `window.open(url)` and for the about:blank case (`postMessage` to the opener arrived). A plain `target=_blank` link has **no opener**: Chromium's implicit `noopener`, the same as in any browser. That is expected and not a bug.
