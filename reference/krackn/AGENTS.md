# Krackn

macOS super-app: browser, universal file preview, file converter, and a toolbox
of developer/science utilities. SwiftUI, **no third-party dependencies**.

Lives at `~/Krackn/`.

## Commands

```bash
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
- **Canvas-drawn text goes soft when GPU canvas is on.** Google Docs paints the
  document into a `<canvas>` and its toolbar and caret as DOM, so the symptom is
  blurry glyphs inside crisp chrome. WebKit rasterises an accelerated canvas
  into a GPU surface that can sit below the display's scale — and **JavaScript
  cannot see this**: `canvas.width` keeps reporting the correct 2x backing size
  while the pixels are upscaled on composite. `WebKitTuning` turns it off
  (`canvasUsesAcceleratedDrawing`); Settings → Compatibility exposes it.
- **`devicePixelRatio` is *not* the cause of canvas blur here — this was
  measured.** A `WKWebView` built the way `BrowserTab` builds one reports 2 on a
  2x display: windowless, at script-init time, and after attaching. Eight
  theories were tested and disproved before the accelerated-canvas one held —
  dpr, layer `contentsScale`, magnification, the data store, the user agent, the
  tracker rules, the whole SwiftUI/window hierarchy, and display scaling. Don't
  re-run that search; measure the GPU path instead.
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
  `RootView` is keyed on the accent (`.id`) to force the rebuild.
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
- **`Logger.info` is not persisted.** Info-level messages are memory-only and
  vanish unless something is streaming the log at that moment, so `log show`
  finds nothing afterwards. Diagnostics use `.notice`.

- **`.build` caches absolute paths.** If the project folder is ever moved,
  `swift build` fails with a stale module-cache path. Fix: `rm -rf .build`.
- **File lists in shell scripts should stay bash arrays.** The folder no longer
  has a space in its name, but `run-tests.sh` still uses arrays and still fails
  loudly if a suite produces no binary — a plain string word-splits, compiles
  nothing, and would otherwise report the *previous* run's results.
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

- `App/KracknApp.swift` — `@main`, `AppDelegate`, `Section` enum (dock nav),
  `RootView`. **RootView owns `BrowserModel`, `FileViewerModel`, `ConvertModel`,
  `BackgroundRemoverModel`, `PanelStore`** so tabs, recents, queues and torn-off
  panels survive navigating between sections. Never move these into a child
  view's `@StateObject`.
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
  from the app. Max 3.
- `Features/Security/` — `ThreatModel` (pure heuristics), `ThreatScanner`
  (Gatekeeper/XProtect/quarantine/signature checks, filtering-DoH reputation).
- `Features/Browser/TabOrganizer.swift` — pure ordering + auto-close policy over
  `TabSnapshot`, so both are testable without a `WKWebView`.
- `Features/Browser/WebKitTuning.swift` — rendering switches WebKit exposes but
  `WKPreferences` doesn't declare, reached selector-guarded. Currently the
  canvas rasterisation path. Every call is safe on an OS that drops the
  preference: the feature turns itself off rather than crashing.
- `Features/Browser/FaviconStore.swift` — site icons, **memory only**. A favicon
  cache on disk is a dated list of every site visited, i.e. exactly the browsing
  history this browser promises not to keep.
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

## Conventions

- Every visual change gets **verified with a screenshot or hit-test**, not
  eyeballed. Padding bugs are invisible in a full-window capture.
- Logic goes in a pure, testable function; UI wraps it. That's why
  `GraphEngine`, `Expression`, `BlockDiagnostics.classify` are separate.
- Comments explain **why**, especially where the obvious approach fails.
- State: `reset()` clears work but **keeps history** (recents survive).

## Testing

673 assertions, 12 suites (16 with `--all`), standalone programs (no XCTest host
with CommandLineTools).
Suites: `unit network cancellation drop tabs taborg calc graph bgremove security
theme encoding` plus `--all` for `stress render persist download`.

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
