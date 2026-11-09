# Link previews (Arc's "Peek"): design

A new feature with no Krackn source, so it gets a `new` PARITY row. Written 2026-11-08 from a conversation with the owner. The research behind it covers how Arc behaves (each point marked confirmed or not) and how the feature fits Zing's code. It is outside the repo, at `zing-notes/arc-peek-research.md`. This document repeats the parts a builder needs.

## Intent

The owner used this in Arc and loved it. They click a link, for example a Google Doc in Gmail, and the page opens as a near-fullscreen **preview** over the current tab instead of as a new tab. They read it, click around in it, and then either close it, which drops them back where they were with no stray tab, or expand it into a real tab if they want to keep it. The aim is fewer throwaway tabs without losing the page they were on.

## Goals

- A link from **any tab** to a **different host** opens as a preview (decisions 1 and 2).
- The preview sits over the page area, near full size. The page behind is dimmed but stays live (a video keeps playing). The tab strip, toolbar and rail are not dimmed and stay usable.
- Two buttons at the top right: **close** and **open as tab**.
- **Expand keeps the same loaded page**: no reload. Scroll position, typed text, sign-in state and playing media all carry over into the new tab.
- A preview is a full browsing context. Links inside it navigate within it, Back and Forward work, and a sign-in popup opened from it keeps `window.opener`.

## Non-goals (v1)

- A split-view button. Later, as expand followed by `SplitWith(source, new)` (decision 9).
- Find (Ctrl+F) inside a preview. `FindController` targets the active tab. Follow-up.
- Previews that survive a tab switch, more than one live preview per window, or previews saved across restarts.
- Reopening a closed preview *as a preview*.
- Rounded corners.
- Little Arc (links from other apps in a small window).
- Blur behind the dim.

## Anti-goals: a failure even if it works

- Expand reloads the page, or loses form input or sign-in state.
- A sign-in or payment popup from inside a preview loses `window.opener`, or a sign-in flow ends up stuck inside a preview instead of the tab that started it.
- The page behind takes clicks or keys while a preview is open.
- A preview's views leak: after it closes, `live-counts` is higher than before it opened.
- Optional preview code makes startup fail. Previews are an optional feature, and the "optional features never fail startup" rule applies.
- The dim or the preview lands in the wrong place after a resize, a DPI change or a tab-bar move. Position comes from `ChromeApi.Insets()` only.
- Visual hosting is reopened for this. It was rejected on 2026-11-01 (`spikes/visual-hosting/README.md`).

## Constraints

- House rules: tabs, early returns, specific exceptions, no silent fallbacks, no scope creep.
- Logic goes in `host/Zing.Core` (no UI, xUnit-tested). `host/Zing.Host` stays a thin layer.
- New overlays get a new `OverlayLevel`. They never raise themselves (AGENTS.md).
- Every `await` resumes on the UI thread. After any `await` on a view or page, check it is still there (`ViewGoneException`). Delayed work uses `IWindowHost.Scheduler`.
- e2e and spike runs: `ZING_E2E=1`, a scratch `ZING_USER_DATA`, no clipboard. Tests never read the real keyboard. Shift and other modifiers go through a test seam, and real input is a hand-run `real-input.spec.ts` check.
- Privacy: previews store nothing. A closed preview goes onto the in-memory reopen-closed stack, as closed tabs already do. No privacy-banner change.

## Decisions (the owner's)

| # | Question | Decision |
|---|---|---|
| 1 | Which tabs preview links | **Every tab**, not only Pinned and Favourites as in Arc. |
| 2 | What counts as "another site" | **A different host**, ignoring a leading `www.`. Gmail (`mail.google.com`) → Docs (`docs.google.com`) previews. A GitHub page → another GitHub page does not. Not the registrable domain, because that would not preview Gmail → Docs. |
| 3 | Shift-click | **As in Arc: Shift-click previews any link**, same-host links included. A setting turns this off. (Checked against Arc's release notes: both macOS and Windows behave this way, and Windows has the off switch.) |
| 4 | Sign-in pages | **Never preview.** They load as they would without the feature, so the sign-in finishes in the tab that started it. Uses a short built-in list. |
| 5 | Keys | **Ctrl+O** expands (Arc's Cmd-O; chosen over Ctrl+Enter, which is "send" in Gmail and Slack). **Esc** and **Ctrl+W** close. Esc closes only when the page did not use it itself (closing its own dialog, leaving fullscreen). |
| 6 | Switching tabs | **Closes the preview.** |
| 7 | Reopen closed (Ctrl+Shift+T) | **Brings it back as a tab.** |
| 8 | Corners | **Square**, with a thin border and a soft shadow so the preview stands out from the dim. |
| 9 | Split-view button | **Later**, not v1. |
| 10 | Links from search pages | **No preview.** A link clicked on a search-engine results page loads in the tab as it does today. Every other site that links out previews. |

## Calls made in writing this (not the owner's; check them)

- **Redirects and blank windows.** Gmail often opens `www.google.com/url?q=…` or `window.open('about:blank')` and sets the location afterwards. A new-window request is judged on its first real `http(s)` document, not on `about:blank`. Known redirectors (`google.com/url`) are judged on their `q`/`url` target. Until that judgement, the page exists but is not shown.
- **Sign-in inside a preview.** If a preview's page sends itself to a sign-in host (a signed-out Docs bouncing to `accounts.google.com`), it stays in the preview and finishes there. Cookies are shared, so the tabs are signed in too. Decision 4 covers only the link that starts a preview.
- **Modifiers for new-window requests.** WebView2 gives no modifier or button data, so Zing reads Shift, Ctrl and the middle button with `Native.IsDown` when the event fires. Tests go through a seam that sets these.
- **Split view.** The source is the pane whose tab is active. The scrim covers the whole split area, so the other pane cannot be clicked while a preview is open. Expand adds the new tab after the source tab and shows it on its own. The split group is left as it was, to return to.
- **Excluded hosts apply to the link's target**, except the search-page rule, which applies to the source. Navigations inside a preview are never checked against the lists.
- The meeting-host exclusion, the `target=_blank` rule, closing on a click on the dim, and the feature-off setting were also my calls.

## Behaviour

### When a link opens as a preview

Two engine events carry link clicks:

- **A plain click** that stays in the window raises `NavigationStarting`. It previews when *all* of these hold: the latest `Page.frameRequestedNavigation` reason for that exact URL is `anchorClick` (so not a redirect, form post, reload, script navigation, address-bar entry or bookmark; see Spike results), and the target host differs from the current host (decision 2). The navigation in the tab is cancelled and the URL opens in a preview instead.
- **A new-window request** raises `NewWindowRequested`. That covers `target=_blank`, `window.open` without popup features, and Shift-, Ctrl- and middle-click.
  - **Shift held:** preview, whatever the host (decision 3), unless the Shift setting is off.
  - **Ctrl held or middle button:** unchanged. A tab, as today.
  - **`window.open` with popup features** (`width`, `height`, `popup`): unchanged. Adopted as a tab with `window.opener`, as `TabService.AdoptPopup` does today. WebView2's `WindowFeatures` has no `popup` flag: a size or position counts, and so does `ShouldDisplayToolbar` being false, which the e2e shows for `popup` while a plain `window.open`, a `target=_blank` link and `noopener` alone keep the toolbar.
  - **Otherwise** (`target=_blank`, plain `window.open`): preview if the host differs, a tab as today if it is the same. This matters for the owner's own case, because Gmail opens links in a new window.

**Never preview** (the link behaves as it does today):
- Sign-in hosts (decision 4). The v1 list: `accounts.google.com`, `login.microsoftonline.com`, `login.live.com`, `appleid.apple.com`, `github.com/login` and `github.com/login/oauth`. It lives in one place in `Zing.Core`.
- Links clicked on a search-engine results page (decision 10). The source is the results page of a search engine in `SEARCH_ENGINES` (Google, DuckDuckGo, Bing, Brave, Kagi, Startpage), matched by host plus results path (for example `google.*/search`).
- Meeting hosts: `meet.google.com`, `*.zoom.us`, `teams.microsoft.com`, `teams.live.com`. A preview is easy to close by accident. Arc excludes these too.
- Schemes other than `http`/`https` (`mailto:`, `javascript:`, `blob:`, `data:`, external protocols), and Zing's own pages.
- The URL of the current page (same URL without the fragment).
- Downloads. A link that turns into a download inside a preview goes to the downloads list as usual, and the preview closes if it never showed a document.
- Clicks from a tab while a preview is already open. The scrim blocks input, so this should not happen. If a request still arrives, it is handled as it would be with no preview open.

The rule is a pure function in `Zing.Core` (`PeekRules`): source URL, target URL, which event, modifiers and settings in, `Preview` or `Normal` out. It is tested from a table.

### Inside a preview

- Link clicks navigate the preview itself, with no nested previews, whatever the host. Back and Forward are the preview's own history.
- A **popup** from the preview (`window.open`, including OAuth and payment popups) is stacked as a **layer** over the preview, the same size and inset about 16 DIP so the stack shows. It is the page the engine asked for, so `window.opener` works. When it closes itself, the layer below shows again. Layers are capped at 3, but a popup always opens even above the cap, because refusing it would break the flow.
- Expand is disabled while a popup layer is on top.
- Downloads, permission prompts and zoom work as on a tab. Prompts draw in the page-UI layer, which sits above the preview.

### How it looks

- **Area:** the page area (`PageRect()` from `ChromeApi.Insets()`). With split view, the whole split area.
- **Preview rectangle:** the page area inset by a horizontal margin of 6% (at least 48 DIP on the right so the buttons fit) and 24 DIP top and bottom, centred, at most 1400 DIP wide. These are starting values. The owner judges them in the running app.
- **Dim:** black at about 45% over the page area, except the preview rectangle. Stronger, with a visible border, under high contrast. It does not depend on the theme.
- **Corners:** square, with a 1 px border and a soft shadow (decision 8).
- **Buttons:** two 32 DIP round buttons stacked in the right-hand margin, top-aligned with the preview. Close (X) is above open-as-tab (double arrow). Each has a tooltip that names its shortcut and follows the theme and accent.
- **Motion:** a short fade-and-scale on open and close, skipped under `prefers-reduced-motion`.

### Closing

The X button, Esc (decision 5), Ctrl+W, a click on the dim, switching tabs (decision 6), opening a new tab, navigating the source tab, or closing the window.

Close removes the preview's page, scrim and buttons, and gives focus back to the source tab's page. The preview's current URL and title go onto the reopen-closed stack as an ordinary, unpinned entry, so Ctrl+Shift+T brings it back as a tab (decision 7).

With popup layers open, the X button and Esc close only the top layer. Ctrl+W and a click on the dim close the whole preview.

### Expanding

The open-as-tab button or Ctrl+O.

1. A tab record is added right after the source tab, with the preview's current URL and title.
2. The preview's **existing page** becomes that tab's page. It is the same `WebView2Page`, the same `CoreWebView2`, the same window. Nothing reloads.
3. The new tab is activated and laid out in the normal page slot. The scrim and buttons go.
4. The tab remembers the source tab as its opener, so closing it returns to the source tab, as popups do today.

### Keyboard, focus and accessibility

- Every key goes through the existing route (`OnKey` → `ChromeApi.HandleKey` → `Commands`), so the keys work whichever view has the keyboard. While a preview is open, Ctrl+W means "close the preview", not "close the tab".
- When a preview opens, the keyboard moves into its page. When it closes, the keyboard goes back to the source tab's page.
- Tab order: the preview's page, then close, then open-as-tab. The page behind cannot get the keyboard.
- The buttons are real `<button>`s with accessible names ("Close preview", "Open as tab") and visible focus rings. On open, a live region announces "Preview of <host>. Press Escape to close."
- The scrim never takes activation (`WS_EX_NOACTIVATE`).

### Settings

Three keys in `src/shared/settings/schema.ts`, generated into `Settings.cs` like the others, with rows on the settings page:

- `linkPreviews`: flag, default **on**. The whole feature.
- `linkPreviewsShiftClick`: flag, default **on**. Shift-click previews any link (decision 3).
- `linkPreviewsFromNewWindows`: flag, default **on**. `target=_blank` and plain `window.open` to another host open as previews. Off means they become tabs, as today.

The host rule (decision 2) and the excluded-host lists are fixed in code, not settings.

## Architecture (from the research, design A)

Four pieces:

1. **The preview page.** An ordinary `WebView2Page` in a child window, created through `IPageHost.Create` like a tab page, but owned by a new **`PeekSession`** (`Zing.Core`) instead of a `TabRecord`. It is placed at the preview rectangle and raised above the tab pages. Its `IPageEvents` is a `PeekPageEvents`. Expand swaps that for the new tab's `TabPageEvents`, so the page's events must be replaceable.
2. **The scrim.** A plain Win32 owned popup with **no WebView2**: `WS_EX_LAYERED` with constant alpha, `WS_EX_NOACTIVATE`, covering the page area. `SetWindowRgn` cuts out the preview rectangle, so the preview page shows and takes clicks through the hole. A click on the scrim closes the preview. It avoids WinUI 3's "transparent WebView2 over WebView2 shows black" limit because it is not a WebView2.
3. **The buttons.** A small renderer popup, `PeekLayer` (hash `#peek`), in the same family as `PageUiLayer`. It is transparent and placed in the margin at the top right of the preview. It also draws the border and shadow around the hole.
4. **Z-order.** New `OverlayLevel` values `PeekScrim` and `PeekControls`, **below `PageUi`**, so find, downloads and permission prompts still draw over a preview, and the overview, settings and notices sit above it.

`PeekSession` owns the layer stack (page, popup layers), the cap, close order and the expand intent, with no UI. `TabService` gains a way to adopt an existing page for a new record (`AdoptPage`). That path needs tests for sleeping, ordering, the opener link and session save. The source tab must not be put to sleep while its preview is open.

Fallback if the scrim hole fails (design B): the preview page lives in an opaque overlay popup with a full scrim below it. Expand then has to re-parent the controller into a child window, which needs a new `HostedView` rehost operation. Use B only if the spike rules A out.

Rejected: CSS dim injected into the page behind (fragile, visible to the site), a second top-level window (taskbar, Alt+Tab, focus), and visual hosting (see Anti-goals).

## Spike results

The spike, done 2026-11-08, is in `spikes/link-preview/` (throwaway, not part of the product). Its README has the details.

- **The scrim hole works.** A layered owned popup with a `SetWindowRgn` hole dims the live page, the preview takes clicks through the hole, and the region follows a resize. Design A is a go.
- **Expand keeps the page.** Moving the preview's child window into the tab slot keeps the marker, the input, the scroll position and `timeOrigin`, with no navigation. Re-parenting the controller works too, so design B stays a usable fallback.
- **Link clicks can be told apart.** `Page.frameRequestedNavigation` (after `Page.enable`) arrives **before** `NavigationStarting` every time, with reason `anchorClick`, `scriptInitiated` or `formSubmissionPost`. Plain-click previews are in v1.
  - `IsUserInitiated` was true even for script navigations, so the rule keys on the reason: preview only when the latest reason for that exact URL is `anchorClick`.
  - `NavigationStarting` has no deferral in this SDK, so the decision is synchronous.
  - Redirected hops carry no reason, so they never preview.
- **Held new windows work.** A new-window request can load in a hidden child window until its first real `http(s)` document, then be shown as a preview or a tab, and `window.open` keeps `window.opener`. Plain `target=_blank` links have no opener (Chromium's implicit `noopener`), as in any browser.

## Risks, most serious first

1. **Previews on every tab change everyday browsing.** Search pages are excluded (decision 10), but any other site that links out now previews. The owner should try it for a day.
2. **The owned-popup focus model** (the "activation dance", `WM_NCACTIVATE`). Two more owned popups bring bugs of that kind. The spike did not cover focus, a DPI change between monitors, or flicker during a live drag-resize. These are hand checks.
3. **Expand into `TabService`.** Today "page exists" means "tab is awake". Injecting a page must not confuse sleep, restore or ordering.
4. **Keys change meaning** while a preview is open (Ctrl+W, Esc). They are routed in one place and tested.
5. **`Page.enable` on every page.** It opens a DevTools event stream per page. Check that it adds no visible cost and does not clash with any other DevTools use in the host. With `linkPreviews` off, `Page.enable` still runs on every page and the raw-input sink for the middle button stays registered: the setting switches the behaviour off, not its cost.

## Tests (what "done" means)

- **xUnit:** `PeekRules` table (every row of "When a link opens as a preview", including `www.`, Gmail → Docs, each excluded host, Shift on and off, settings off). `PeekSession` (layers, cap, a popup above the cap, close order, expand disabled under a popup). `TabService.AdoptPage`.
- **vitest:** the three settings keys and their defaults.
- **e2e**, on local fixtures with no live sites:
  - A cross-host link opens a preview. No new tab appears, and `layers()` shows the preview page, scrim and buttons.
  - A same-host link navigates in place.
  - Close and Ctrl+W return `live-counts` to its earlier value and focus to the source page.
  - Expand gives one new tab with the same page: the marker survives and `timeOrigin` is unchanged.
  - A popup from a preview keeps `window.opener` and shows the preview again when it closes. This mirrors `core.spec.ts`'s popup test.
  - With the setting off, no preview opens.
  - The Shift seam works.
  - Teardown during preview start leaks nothing.
- **Hand checks:**
  - The dim and the hole at 100% and 150% DPI.
  - Window drag and resize.
  - Two monitors.
  - Narrator reads the buttons.
  - A real Shift-click and Esc (`real-input.spec.ts`).
  - Light and dark screenshots for the PARITY row.

## Left to the builder

- The exact file and class names beyond `PeekSession`, `PeekRules`, `PeekLayer` and the two `OverlayLevel` values.
- How the page's events become replaceable (a settable property or an indirection).
- Animation timing, shadow size and the exact dim alpha, within the values above.
- How to tell that Esc was "used by the page". If it cannot be told reliably, ship Ctrl+W and the buttons, and say so.
- The PARITY row ID and the GitHub issue.

## Hand checks (owner)

- [x] H1: the dim and the hole at 100% and 150% DPI.
- [x] H2: dragging the window to a monitor with different scaling while a preview is open.
- [x] H3: live drag-resize, with no flicker that exposes the dim over the preview.
- [x] H4: Narrator reads "Close preview" and "Open as tab" and the live region.
- [x] H5: Alt+Tab away and back, and focus returns to the preview page.
- [x] H6: high contrast.
- [x] H7: Gmail → a Docs link, and a Google search result loads in place.
- [x] H8: `Page.enable` cost. Open 20 tabs and compare memory and CPU with the previous build, and note it.
- [x] H9: run the three link-preview @real-input tests (npm run test:e2e:real-input) on an idle PC; they have not run yet.

Owner hand check, 2026-11-08: H1–H8 passed on a scratch profile (no memory or CPU figures were recorded for H8). On 2026-11-09 the dark screenshot was taken. H9 ran from this branch, but 7 of 8 real-input tests failed, main's own included, because the real input never reached Zing on that PC. The tier has to be fixed before H9 can be judged; the owner checked Shift-click, Esc and middle-click by hand.

On 2026-11-09, after the tier's high-DPI fix (#164), H9 found that Esc over a page's own dialog closed the preview; the owner confirmed it by hand. Zing now leaves Esc to an open dialog (it follows Page.javascriptDialogOpening/Closed), and the owner checked the fix by hand. The whole real-input tier then passed, 8 of 8.
