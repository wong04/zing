# Krackn

macOS super-app: browser, universal file preview, file converter, and a toolbox
of developer/science utilities. SwiftUI, **no third-party dependencies**.

Lives at `~/Krackn/`.

## Commands

```bash
./Tools/make-icon.sh   # regenerate Resources/AppIcon.icns from the logo's vector paths
./bundle.sh            # release build → /Applications/Krackn.app (do this to run it)
sudo ./bundle.sh       # if /Applications isn't writable; falls back to ~/Applications
./run-tests.sh         # fast suites
./run-tests.sh --all   # + stress, render, persist, download (slow, drives real WKWebViews)
./run-tests.sh unit    # one suite by name
swift build            # debug compile only
```

Installs to **/Applications**, not `~/Applications` — Launch Services only offers
an app as a default-browser candidate from a scanned location, and a per-user
Applications folder isn't one.

Always `./bundle.sh` and relaunch to see changes — a bare `swift build` binary has
no bundle identity and won't behave like an app.

## Hard-won gotchas — read before touching these

- **Check *which* Krackn.app is running before debugging anything.** The install
  destination moved from `~/Applications` to `/Applications`, a stale v1.0 copy
  stayed behind, and the Dock kept launching that one. Every fix appeared to
  change nothing — because the running binary predated all of them. An entire
  session went into chasing phantom bugs (missing favicons, pinning "broken",
  popups "not working") that were simply features absent from the old build.
  `bundle.sh` now warns when a second copy exists. First diagnostic, always:

  ```bash
  ps -eo pid,comm | grep -i krackn      # which path is actually running
  mdfind -name "Krackn.app"             # is there more than one
  ```

- **`UserDefaults.bool(forKey:)` returns `false` for an unset key.** Any
  protective setting read through it defaults to *off* wherever
  `AppSettings.registerDefaults()` hasn't run. Use `AppSettings.flag(_:default:)`,
  which checks `object(forKey:)` first. This turned "never overwrite files" into
  "always overwrite" and was caught only by the conversion collision test.
- **A drop zone drawn as a dashed outline around `.clear` has no hit area.**
  SwiftUI won't route a drop to a clear fill, so only the icon and label inside
  it were targets. Use `.dropsFiles(isTargeted:onDrop:)`, which sets
  `contentShape` first.
- **A view that observes the model does not observe the tabs.** `BrowserView`
  is `@ObservedObject` on `BrowserModel`; `canGoBack` lives on `BrowserTab`, so
  the back/forward arrows sat stale until an unrelated redraw. Anything reading
  per-tab state needs its own child view observing that tab (`ToolbarRow`).
- **Google Docs was blurry because of the user agent — measured, finally.**
  Krackn identified as a hard-coded "Safari 17.6". Google treats that as an
  unsupported browser (it shows a "no longer supported" banner) and serves a
  degraded document view whose canvas has **0.5** backing pixels per CSS pixel
  instead of 2 — text at a quarter of the Retina pixel count, while the DOM
  toolbar stays sharp. With the installed Safari's version (26.6.2) the ratio
  is 2.0 and the text is sharp, on load and reload. GPU canvas on/off,
  GPU-process canvas/DOM rendering on/off: no difference at all. `UserAgent`
  now reads the version from the installed Safari at launch; never hard-code
  it again. (`Tests/unit` asserts it isn't 17.6.)
- **The earlier canvas theories were wrong — don't revive them.** GPU canvas
  rasterisation, devicePixelRatio, contentsScale, magnification, data store,
  tracker rules, the view hierarchy and display scaling were all ruled out, and
  the "accelerated canvas" switch was turned off by default on a theory that
  measurement later disproved; it's back on (WebKit's default, faster).
- **How to measure web rendering offscreen.** An offscreen window counts as
  hidden, so WebKit stops painting it — disable `_setWindowOcclusionDetectionEnabled:`
  on the web view. Capture what the window server composited with
  `CGWindowListCreateImage` (unavailable to Swift; call it via `dlsym`), not
  `takeSnapshot`, which takes a different rendering path. Then read the page's
  own numbers (canvas `width / clientWidth`) alongside the pixels.
- **`perform(_:with:)` cannot call a `BOOL` setter.** It passes the argument as
  an object pointer, and any non-nil pointer reads as `true` — so
  `perform(sel, with: NSNumber(false))` sets the value to *true* and reports
  success. Use `responds(to:)` as the existence guard and KVC to set. That
  combination is also the only *safe* one: an unknown KVC key raises an ObjC
  exception Swift can't catch, while an unknown selector is just `false`.
- **Camera and mic need three things, not one.** The `Info.plist` usage strings
  (missing ones kill the process), `AVCaptureDevice.requestAccess`, *and*
  `requestMediaCapturePermissionFor` on the UI delegate. Without the delegate
  method WebKit denies every `getUserMedia` silently.
- **`Palette` is computed, not stored.** It resolves through
  `AccentTheme.current` on every read, so the accent can change at runtime.
  `WindowRoot` keys its content on the accent (`.id`) to force the rebuild.
  That `.id` used to sit above `RootView`, so changing the accent destroyed
  every `@StateObject` in it — the browser model with all its tabs (each
  replaced and reloaded), and Settings closed mid-change. State now lives in
  `WindowSession`, above the `.id`; window-close work (⇧⌘T record, edge
  monitor, `BrowserModel.primary`) is in `WindowRoot.onDisappear`, because a
  rebuild makes `RootView` disappear too. New-tab pages are recoloured in place
  (`restyleStartPages` swaps `<style id="krackn-theme">`), not reloaded.
- **Fullscreen video needs two things.** `isElementFullscreenEnabled` is
  off by default in WKWebView — `document.fullscreenEnabled` is *undefined*,
  so YouTube's fullscreen button did nothing (measured: undefined → true).
  And in fullscreen WebKit moves the web view into its own window, leaving a
  placeholder; `WebViewHost.Container.adopt` must not reclaim it then (any
  redraw would, ending fullscreen mid-video). It waits on `fullscreenState`
  via KVO and takes the web view back after. `Tests/tabs` pins it.
- **Two containers can want the same web view.** On a rebuild the old
  `WebViewHost.Container` claimed the web view back after the new one took it,
  then left the window with it — a blank page (measured). Containers hold the
  web view only while in a window and hand it off on leaving
  (`Tests/tabs` pins the bad order).
- **Never put `UTType.item` in an `onDrop` type list.** `.item` is the root every
  type conforms to, so it makes the view a universal drop target — and the
  tracking AppKit installs for that **swallows right-clicks**. Adding it to make
  exotic files draggable silently killed every context menu underneath a drop
  target: the tab strip's (and with it the only way to pin a tab) and the
  recents lists'. `Tests/drop` asserts it stays out.
- **`NSItemProvider(contentsOf:)` does not advertise `public.file-url`.** It
  registers only the file's own type — an `.stl` says `public.3d-content`, a
  `.msh` gets a dynamic `dyn.…` — so drop targets refused them outright and only
  images and PDFs could be dragged between tools. Use `NSItemProvider.file(_:)`,
  which registers the URL as a URL. That, not `.item`, is what makes every file
  type draggable.
- **`.gesture` loses to an enclosing `ScrollView`; `.simultaneousGesture` does
  not.** The dock rows live in a scroll view, so an exclusive gesture never
  received the press and tearing off a floating panel stopped working entirely.
  The tear-off modifier also owns the *tap*, because leaving a `Button`
  underneath meant a completed tear-off fired both — opening a panel *and*
  navigating.
- **The web view must be pixel-aligned.** SwiftUI can hand the host a fractional
  frame, and a web view laid out across half a device pixel has its whole
  composited layer resampled. `WebViewHost.Container` lays it out manually and
  rounds with `backingAlignedRect`; the browser chrome above it has explicit
  whole-number heights for the same reason.
- **An `NSTextField` is not its own first responder while editing.** AppKit
  installs a *field editor* that holds first-responder status, so the field's
  own `resignFirstResponder` frequently never fires — report the end of editing
  from `controlTextDidEndEditing`, and self-correct against
  `field.currentEditor() != nil` on every `updateNSView`.
- **Session restore is a deliberate privacy exception.** Remembering open tabs
  means writing visited URLs to disk — the one record this browser otherwise
  refuses to keep. It's off by default, and `SettingsView`'s privacy banner
  flips from green to a warning whenever `restoreTabs` or `persistentProfile`
  is on. Don't "tidy" that banner back to always-green.
- **Keychain AutoFill is a bridge, because WKWebView can't take it.** macOS's
  Edit › AutoFill › Passwords… (`_handleInsertFromPasswordsCommand:`) works on
  NSTextView, not WKWebView — measured. `KeychainAutofill` detects sign-in
  fields (main frame only, in an isolated `WKContentWorld` the page can't
  see), draws a key in the field; clicking it lays a transparent NSTextView
  over the field, opens Apple's picker on that, and moves the inserted value
  into the page through the native setter + input events. Nothing is stored
  or logged. The harness drives it with `presentPicker: false` +
  `simulatePickerInsert`; Apple's picker itself can only be tested by a person.
  Detection *asks* what's focused (load, clicks, keys, focus changes, a light
  poll) rather than waiting for `focusin` alone, which missed autofocused
  fields (Google's sign-in), shadow-DOM inputs, same-origin frames (events
  there never reach the main document — listeners are attached inside each
  frame as it loads) and re-clicks. Cross-origin frames are unreadable by
  design, so they're never filled.
- **Links from other apps must be claimed by a window, or SwiftUI makes a new
  one each time.** With only `AppDelegate.application(_:open:)`, every link from
  another app (Krackn as default browser) opened a fresh window — measured with
  a probe: two links, three windows. `AppGate` now has
  `.handlesExternalEvents(preferring: ["*"], allowing: ["*"])` + `.onOpenURL`,
  queueing into a per-window `LinkInbox` that `RootView` drains (links can
  arrive before sign-in). Don't add `application(_:open:)` back: it still fires
  alongside, and double-handles.
- **Popups must be real web views, or "Sign in with Google" breaks.**
  `createWebViewWith` used to return nil and open the URL in a new, unrelated
  tab. The page's `window.open` then got `null` — no `window.opener` — so OAuth
  popups (Google, Apple, Microsoft, payment pages) finished with nowhere to send
  the result and couldn't close themselves. Return a `BrowserTab` built from
  *WebKit's* configuration (WebKit loads the request itself), and handle
  `webViewDidClose` by closing that tab and returning to its opener. Measured
  with a popup that posts to its opener and closes: null before, delivered after.
- **A web view only downloads when told to.** Without
  `decidePolicyFor navigationResponse` answering `.download`, WebKit tries to
  *display* everything — a .zip or installer just failed silently, and nothing
  on any site could be downloaded. `DownloadPolicy` decides (can't be shown, or
  `Content-Disposition: attachment`), and `navigationAction.shouldPerformDownload`
  covers `<a download>`. `KRACKN_DOWNLOADS_DIR` redirects downloads for harnesses.
- **`Logger.info` is not persisted.** Info-level messages are memory-only and
  vanish unless something is streaming the log at that moment, so `log show`
  finds nothing afterwards. Diagnostics use `.notice`.

- **`Text(someString)` does not render Markdown.** Only a string *literal*
  becomes a `LocalizedStringKey`; a concatenated or stored `String` prints its
  asterisks verbatim (the Security explainer showed `**not**`). Use
  `Text(.init(string))` — and hoist long concatenations into a typed `let`
  first, or the type checker times out.
- **`ImageRenderer` can't draw a `ScrollView`.** It renders the page blank. To
  verify a whole screen offscreen, host it in an `NSHostingView` in a borderless
  offscreen window and `cacheDisplay(in:to:)` — that also draws real AppKit
  controls (menus, text fields), which `ImageRenderer` shows as placeholders.

- **Tab order has to be re-sorted when a page *loads*, not just when a tab
  opens.** A new tab has no site until its URL arrives, so sorting on
  creation put every tab in the "no domain" bucket and "By site" looked
  broken. `BrowserModel.scheduleReorganize` runs on URL and title changes.
- **SwiftUI drawn over the page doesn't get the page's clicks.** A web view
  is a real AppKit subview, and AppKit hit-tests it before any SwiftUI
  overlay above it — measured: clicks and right-clicks on the tab dropdown's
  rows went to the web page underneath (its own "Reload" menu). While the
  dropdown is open `WebViewHost(shielded:)` makes the container return nil
  from `hitTest` *and* unregisters the web view's 17 drag types (restored
  after) — AppKit can route a drag by registered types rather than hit-test,
  and the dropdown is spring-loaded: a file held over a stack opens it, held
  over a row switches to that tab and closes it so the drop lands on the
  page, and leaving both closes it. Anything new floating over the page
  needs the same shield.
  (`Tests/tabs` pins it.) The dropdown is Krackn's own (`TabStackDropdown`,
  drawn in `BrowserView`'s overlay, positioned from `StackFrameKey`), not a
  system popover, and every row has the same right-click menu as a tab
  (`TabMenuItems`).
- **Offscreen click harnesses: keep the window non-key for left clicks.** A
  key-capable window in an inactive app spends the first click activating
  itself, so SwiftUI taps never fire; but context menus only open in a key
  window. Click first, then make it key to probe menus (record via
  `NSMenu.didBeginTrackingNotification`, then `cancelTracking`).
- **A dragged tab is drawn at start + translation, and its slot comes from
  that drawn position.** Re-anchoring on `tabFrames` right after a swap read
  frames one layout pass stale and threw the chip a whole slot from the
  cursor. `TabReorder.centerAfterMove` predicts the new slot instead.
- **Google is one domain but many products.** Dropdown stacks group by
  `SiteGroup` (in `TabOrganizer.swift`): the registrable domain, except that
  Google's products each get their own named dropdown — Gmail, Gemini, Drive,
  Google (search) — and Docs, Sheets and Slides, which share the host
  `docs.google.com`, are split by the path's first segment. Only google.com and
  its country domains count (`google.xyz` doesn't). "By site" ordering and
  data purging still use the plain domain. `Tests/taborg` pins 24 cases.
- **Tab style is separate from arrangement.** `AppSettings.tabDropdowns`
  (Separate | Dropdown) works with every `TabOrganization`: the arrangement
  orders the tabs, then `TabOrganizer.stacks` groups them by root domain in
  that order (A–Z sorts sites by name instead). One-page sites stay plain
  `TabChip`s; more pages make a `TabStackChip` → `TabStackDropdown`. The old
  "stacked" arrangement value migrates to By site + Dropdown. Dropdown mode
  drags too (`slotDrag`): pinned tabs among themselves, a stack along the
  strip with all its pages (`TabOrganizer.movingStack`), and pages within a
  dropdown (`TabOrganizer.moving`, swapped among the slots the stack already
  holds). In a sorted arrangement the order on screen is frozen into the
  manual order first (`freezeDisplayedOrder`), so switching doesn't jump.
  The row drag measures in `.global` space: in the row's own space the
  distance lost a row at every swap, because the row moves as it's reordered.
- **Tab contents are positioned by offsets, not stacks and truncation.** An
  HStack with a `…`-truncated title re-truncates to the *final* width the
  moment tabs open, close or move, while the frame animates — so titles and
  icons lurched as the plates glided. `TabChip` draws the title at its natural
  width, fades it at the edge, and places it with an offset computed from the
  width, which animates in lockstep with the frame. Icon-only tabs are 40pt so
  the close button can sit at the left beside the icon, as on every tab.
- **The traffic lights are moved, on purpose** (`WindowChrome`), to sit inside
  the inset sidebar. AppKit resets them on resize, key changes and full
  screen, so they're re-placed on each. Measured defaults: 7pt left, 6pt top,
  14×16 buttons; placing them below the 28pt band does not clip them.
- **The edge-reveal sidebar uses an event monitor, not a hover zone.** An
  invisible strip along the left edge would sit over the web view and eat its
  clicks. The monitor needs `window.acceptsMouseMovedEvents = true`
  (`WindowChrome`) — AppKit sends no mouse-moved events otherwise.
- **A panel's dragged position is local view state until the drop.** Writing
  it to the store each frame put the dragged panel and the panels it pushes in
  one animated transaction, so the panel under the cursor lagged behind it.

- **`.build` caches absolute paths.** If the project folder is ever moved,
  `swift build` fails with a stale module-cache path. Fix: `rm -rf .build`.
- **File lists in shell scripts should stay bash arrays.** The folder no longer
  has a space in its name, but `run-tests.sh` still uses arrays and still fails
  loudly if a suite produces no binary — a plain string word-splits, compiles
  nothing, and would otherwise report the *previous* run's results.
- **`controlTextDidBeginEditing` only fires on the first keystroke**, not when
  a field is clicked. Reporting the address bar's focus from it meant a click
  that just selected the URL left the app thinking the bar was idle, so the
  short domain never came back on clicking out. Focus is reported from
  `becomeFirstResponder`; end of editing from `controlTextDidEndEditing`
  (which does fire without typing). And the *first* click doesn't reach AppKit's
  text tracking at all (`Field.mouseDown` takes focus and selects all, no
  `super`): selecting before the tracking left a caret, selecting after it
  left a one-character "drag" selection. Clicks while editing go to `super`.
  Underneath both: **AppKit auto-focuses the first text field** when a window
  opens or becomes key, so the address bar was often already "editing" before
  the user clicked — making their click a click *inside* an active field.
  `Field.becomeFirstResponder` now refuses focus unless it came through the
  field's own `mouseDown`, ⌘L or Tab — *not* merely "a mouse-down inside the
  bar", which still let in the auto-focus during the click that activates a
  background window (nothing selected, then every click went to the field
  editor: caret or one-letter drag). `acceptsFirstMouse` is true so that
  activating click reaches `mouseDown`. Return hands focus back to the page.
- **`.background(WindowDragArea())` silently never receives clicks.** SwiftUI
  only routes hits to a representable that occupies real layout space, so drag
  strips must *be* the padding, not sit behind it. An earlier version of this
  also made the address bar unclickable.
- **`NSViewRepresentable` has no intrinsic size.** Unbounded, it expands
  vertically and swallows clicks meant for views below. Pin heights.
- **The invisible titlebar owns the top ~28pt for dragging.** That's why
  `window.isMovable = false` and `WindowDragArea` moves the window manually —
  otherwise tab drags move the window instead of reordering.
- **Keychain items are bound to the creating app's code signature.** Ad-hoc
  signing changes per build, so items can't always be re-read. See
  `Migration.swift` and `AccountStore.needsPasswordReset`.
- **`RelayResult` carries `rawBody` + `contentType`, not just a String.**
  Round-tripping HTML through a Swift String forces UTF-8 and corrupts Latin-1 /
  Windows-1252 pages.
- **Don't put SwiftUI views in `DevTools.swift`** — that file is pure logic with
  no SwiftUI import. Views go in `DevToolViews.swift`.

## Architecture

- `App/KracknApp.swift` — `@main`, `AppDelegate`, `Section`, `WindowRoot`,
  `WindowSession`, `RootView`, `FeatureHost` (the top bar every feature page
  gets: back, title, pop out). **`WindowSession` owns `BrowserModel`,
  `FileViewerModel`, `ConvertModel`, `BackgroundRemoverModel`, `PanelStore`,
  `RecentFeatures`** (plus the route and whether Settings is open), created
  once per window in `WindowRoot` *above* the `.id(accent)` rebuild, so tabs,
  recents, queues and popped-out windows survive navigation and accent
  changes. Never move these into a `@StateObject` below that `.id`.
- `App/Navigation.swift` — `Route` (browser / toolbox / feature), the Toolbox
  catalog (`ToolboxGroup`), `RecentFeatures`, the `Motion` curves, `.glass()`.
  Files, Convert, Download, Open a Link, Diagnose, Network and Security are
  Toolbox items now, not sidebar sections.
- `App/Commands.swift` — every Safari shortcut as a real menu item
  (`KracknCommands`), routed to the frontmost window through a focused
  `WindowActions`; `RootView.handle(_:)` does the work. Second keys (⇧⌘],
  ⌘1–9, ⌘=) are `AlternateShortcuts`. Replacing `.saveItem` removes SwiftUI's
  own Close — measured — which is what frees ⌘W for Close Tab.
- `Features/Browser/BrowserChrome.swift` — find bar, Favourites bar,
  bookmarks/Reading List sidebar, tab overview, Reader, downloads list,
  press-and-hold history menus. `BrowserTabActions.swift` is the per-tab side
  (find, print, text zoom via the guarded `textZoomFactor` KVC key, Reader
  extraction, thumbnails).
- `Features/Browser/BrowserLibrary.swift` + `LibraryStore.swift` — bookmarks
  and Reading List, on disk at `library.json`: the one store the browser writes
  on purpose, because every entry was saved deliberately. `KRACKN_LIBRARY_FILE`
  redirects it for harnesses so they never touch the real file.
- Multiple windows: each has its own `BrowserModel`; only `BrowserModel.primary`
  restores and saves the session file, so windows don't overwrite each other.
- `App/ToolboxSearch.swift` — pure ranking for Toolbox search, plus the
  keyword table ("sha256" → Hashes, "youtube" → Download). Every catalog item
  must have keywords; `Tests/toolbox` checks that and 34 real queries. Add
  keywords when adding a tool.
- `App/Sidebar.swift` — Browser, Toolbox, the 3 most recent features,
  minimized windows, Settings at the foot. Pinned it's an inset glass pane;
  unpinned (⌃⌘S) `EdgeRevealMonitor` slides it out when the cursor reaches the
  left edge.
- `App/PanelLayout.swift` — pure collision resolution for popped-out windows
  (branch-and-bound over free spots; greedy failed in tight windows) and the
  recents list. `Tests/panels`.
- `Features/Settings/SettingsView.swift` — `SettingsSheet`: a blurred-backdrop
  sheet (⌘,) with categories and flat rows, not a page.
- `App/AppSettings.swift` — **every** persisted preference, with typed
  accessors. Both the settings UI and the consuming code go through here; six
  settings were previously declared only in the UI and read by nothing.
  Adding a key without a reader is the bug this file exists to prevent.
- `App/Accent.swift` — `AccentTheme` (7 colour families) and `PaletteTokens`.
  Yellow is the original palette verbatim; the rest are generated by rotating
  its hue while holding the lightness profile, with a contrast lift so every
  accent still carries its own text.
- `App/Theme.swift` — `Palette` (computed through the current accent),
  `AppFont` (bundled Inter). `lightTokens`/`darkTokens` exported as CSS for the
  browser start page.
- `App/ScrollFade.swift` — `FadingScrollView` and `.scrollEdgeFade`, the frosted
  top/bottom edges used on every non-browser page.
- `App/FileComponents.swift` — `FileIcon`, `FileRow`, `RecentFilesList`,
  `ToolEmptyState`. The viewer, converter and background remover share these so
  they read as one set of tools.
- `App/FloatingPanels.swift` — `PanelStore` and the tear-off gesture. Panels are
  drawn *inside* the main window, not as `NSWindow`s, so they can't drift away
  from the app. Max 3 visible; minimized ones stay in the hierarchy (hidden) so
  their state survives, and appear in the sidebar. Other panels make room
  only when the dragged one is *released* — live pushing mid-drag was
  rejected as distracting.
- `Features/Security/` — `ThreatModel` (pure heuristics), `ThreatScanner`
  (Gatekeeper/XProtect/quarantine/signature checks, filtering-DoH reputation).
- `Features/Browser/WebViewHost.swift` — puts a tab's `WKWebView` on screen,
  pixel-aligned, and shields it from clicks while a menu floats over it.
- `Features/Browser/TabOrganizer.swift` — pure ordering + auto-close policy over
  `TabSnapshot`, so both are testable without a `WKWebView`.
- `Features/Browser/WebKitTuning.swift` — rendering switches WebKit exposes but
  `WKPreferences` doesn't declare, reached selector-guarded. Every call is safe
  on an OS that drops the preference: the feature turns itself off rather than
  crashing.
- `Features/Browser/AddressInput.swift` — the one "go there or search" rule,
  shared by the address bar and the new-tab page's search field (whose form
  submission `BrowserTab` intercepts). Real TLD lists, not "has a dot" —
  `node.js` and `index.html` are searches. `Tests/unit` pins 22 inputs.
- `Features/Browser/StartShortcuts.swift` — the new-tab page's tiles, chosen in
  Settings › New Tab (or the page's own "Edit shortcuts" link, which
  `BrowserTab` catches only when clicked *on* the start page, so no site can
  open Settings by linking there). Unset = the built-in eight. Only http(s)
  addresses are accepted or rendered, and names/addresses are HTML-escaped —
  they're the one user-typed text that goes into the page. `Tests/unit`.
- `Features/Browser/UserAgent.swift` — the identifier sent to sites, read from
  the installed Safari. A stale one is what blurred Google Docs.
- `Features/Browser/FaviconStore.swift` — site icons, **memory only**. A favicon
  cache on disk is a dated list of every site visited, i.e. exactly the browsing
  history this browser promises not to keep. Keyed by the page's *declared* icon
  (`BrowserTab.iconURL`, chosen by `FaviconChoice`), falling back to the full
  host — never the registrable domain, which gave every Google tab Gmail's icon
  (Docs, Sheets and Slides even share a host). Icons whose `media` query doesn't
  match the current scheme are skipped, or dark-mode tabs get black icons.
- `Features/Browser/BrowserDiagnostics.swift` — canvas geometry for any page
  that paints into one, to the unified log (not a file, for the same reason).
  Read with `log show --last 10m --predicate 'subsystem == "com.krackn.app"'`.
- `App/Components.swift` — `PillButton`, `IconButton`, `SegmentedControl`,
  `PillField`, `SelectAllField`, `PillSlider`, pill `Toggle`, `WindowDragArea`,
  `WindowChrome`. **Use these, not stock AppKit controls** — the whole point is
  that nothing looks like a default SwiftUI form.
- `App/Migration.swift` — one-time carry-over from the "All in One" rename.
- `Features/Unblock/` — DoH resolver, `PinnedHTTPClient` (hand-rolled HTTP/1.1
  over Network.framework), relay engine, block diagnostics.
- `Features/Browser/` — tabs, `StartPage` (local `aiohome://` new-tab page with
  clock), `TabReorder` (live drag geometry).
- `Features/Viewer/` — multi-backend preview: SceneKit for 3D, AVKit for media,
  NSImage, text, QuickLook fallback.
- `Features/Utilities/` — `Expression` (recursive-descent parser),
  `GraphEngine` + `GraphSurface` (graphing), `BackgroundRemover` (Vision).

## Design system

macOS idiom, not marketing-site: hairlines (`Palette.separator`), soft
elevation (`softElevation`), continuous rounded rects (`Metrics.cardRadius` /
`controlRadius`), one type family (SF Pro) via `.app(size, weight)` — never
`.callout`/`.caption`/`.title2`. Accent is a *highlight*: filled primary
buttons, switch tracks, focus rings. Selection is shown by elevation
(`Palette.raised` plate on `controlFill`) or a quiet `Palette.selection`
plate, never an accent flood. Accent as text or glyph uses
`Palette.accentText` — raw accent is under 2:1 on light surfaces. `control`,
`raised` and `accentInk` are derived in `Accent.swift` and contrast-checked by
`Tests/theme`. `SectionCard` puts its title *above* the card, the way
System Settings does. No `Palette.outline.opacity(…)` strokes; that heavy
outline was the "childish" look.

## Conventions

- Every visual change gets **verified with a screenshot or hit-test**, not
  eyeballed. Padding bugs are invisible in a full-window capture.
- Logic goes in a pure, testable function; UI wraps it. That's why
  `GraphEngine`, `Expression`, `BlockDiagnostics.classify` are separate.
- Comments explain **why**, especially where the obvious approach fails.
- State: `reset()` clears work but **keeps history** (recents survive).

## Testing

969 offline assertions across 14 suites (plus `network`/`cancellation`, which need DoH; 17 with `--all`). Standalone programs (no XCTest host
with CommandLineTools).
Suites: `unit network cancellation drop tabs taborg calc graph bgremove security
theme encoding logo panels toolbox library` plus `--all` for `stress render persist download`.

`AppSettings.swift` + `Accent.swift` are in the `FOUNDATION` array that every
suite's file list starts with — most of the app reaches one of them now.

Tests have caught real bugs repeatedly — CDN false positives in the block
classifier, `-2^2` precedence, poles reported as roots, duplicate roots,
non-UTF-8 corruption, a cancellation deadlock, `appleinsider.com` and
`login.microsoftonline.com` flagged as phishing by a too-loose brand match,
purple's dark accent silently failing WCAG AA, "never overwrite" defaulting
to overwrite, and a `.item` drop type that killed every context menu in the app.
Add a test when fixing anything.

**Verify a fix took effect, don't assume it did.** Two fixes in a row here
reported success while changing nothing: `perform(_:with:)` "setting" a BOOL
preference that stayed at its old value, and a favicon lookup wired only to
`didFinish` so tabs open before the update never fired it. Both looked correct
in the diff. Read the value back, or drive the path, before believing it.

For the security heuristics the **false-positive cases matter more than the
detections**. A checker that flags ordinary sites gets switched off, at which
point it protects nobody. `Tests/security` leads with sixteen real sites that
must stay clean.

## Known limits (do not "fix" by pretending)

- **Private browsing is local-only.** The data store is non-persistent, so
  nothing is written to disk and a closed tab leaves no trace on the machine.
  It cannot hide *that* a connection happened from the network: DNS lookups and
  the TLS SNI hostname are on the wire regardless. Say so; don't claim
  "untraceable".
- **The security section is not an antivirus.** No signature database, no
  definition feed — those can't ship with no dependencies and no server. It does
  link inspection, signature/quarantine/Gatekeeper verification, and
  filtering-DoH reputation. XProtect is the signature scanner; surface its
  status rather than duplicating it.
- Relay viewer can't run web *apps* (Instagram etc.) — custom scheme isn't a
  trustworthy origin. The DNS profile is the real answer.
- Page load is ~3.3s cold on this network; measured that preconnect,
  process prewarming and scheme choice make **no** difference. Don't re-litigate.
- Graphing: no implicit relations, inequalities, sliders, or regressions.
  Tangential intersections without a sign change are missed.
