# Krackn

A macOS browser that keeps nothing, plus a universal file previewer, a converter
for 196 formats, and a toolbox of developer and science utilities.

Built as a SwiftUI app with no third-party dependencies.

**Version 1.1.**

## Build

```bash
./bundle.sh
```

This compiles in release mode, wraps the binary in a proper `.app`, signs it
ad-hoc, and installs it to `/Applications`. If that isn't writable it says so
and falls back to `~/Applications` — but only an app in `/Applications` is
offered as a default-browser candidate, so use `sudo ./bundle.sh` if you want
that. Pass a different destination as the second argument.

Requires the Xcode Command Line Tools — full Xcode is not needed.

## The browser

Tabs with the close button on the left, drag-to-reorder, and pinning
(right-click a tab). Pinned tabs sit left of a divider, are never auto-closed,
and are skipped by whatever ordering strategy is active.

**Tabs can keep themselves in order** — manual, grouped by site, A–Z, last
opened, or newest first — and close themselves after an inactivity period
(1 hour / 1 day / 7 days / custom) or at a set time each day. The active tab,
the last remaining tab and every pinned tab are always spared.

### Privacy, stated precisely

The session runs on a non-persistent `WKWebsiteDataStore`. Cookies, caches,
local storage, IndexedDB and service workers exist in memory only — nothing is
written to disk, closing a tab purges that site's data, and quitting destroys
everything. There is no browsing history because there is nowhere to put one.
The trade is real: you are signed out of everything when Krackn quits, because
staying signed in *is* a stored trace.

Tracking parameters are stripped from every URL before the request goes out, and
third-party requests to 48 known tracker domains are refused by a compiled
content-blocking ruleset.

**What this cannot do** is hide *that* a connection happened from your network.
DNS lookups and the TLS server name travel in the open no matter what an app
does locally. Encrypted DNS covers the lookup; the profile under Network & VPN
covers it for the whole Mac. Nothing client-side covers the rest, and this app
doesn't claim to.

## Security

Not a signature-scanning antivirus, and it doesn't pretend to be. A definitions
database has to be updated continuously, which needs a server and dependencies
this app doesn't have — and macOS already runs XProtect, whose definitions Apple
pushes automatically.

What it does is the part macOS leaves to you:

- **Link inspection** before a page loads — look-alike (punycode) hostnames,
  brand impersonation, credentials hidden in the URL, high-risk TLDs, hostnames
  shaped like sign-in traps.
- **Domain reputation** via filtering DoH resolvers. A host is only refused when
  Quad9 *and* Cloudflare's security resolver both refuse it, so one provider's
  false positive can't take a real site down.
- **Download checks** — signature and notarization via Gatekeeper, a missing
  quarantine flag (how an installer skips that check), disguised executables,
  text-direction overrides in filenames, and headers that disagree with the
  extension.
- **System posture** — Gatekeeper, SIP, FileVault and XProtect status, reported
  rather than duplicated.

The false-positive cases are the ones under test. A checker that flags ordinary
sites gets switched off, at which point it protects nobody.

## What else is in it

### Open a Link

Paste a URL; the app tries four methods in order and shows you which one worked.

| Method | How it works | Beats |
|---|---|---|
| Direct | Ordinary fetch | Nothing — proves the site isn't blocked |
| DoH + IP pinning | Resolves over HTTPS, connects to the address directly | DNS-level blocking |
| Reader relay | A third-party server fetches the page and returns text | DNS, IP, and TLS blocking |
| Wayback archive | Pulls an archived snapshot | Everything, but the content is old |

The DoH + IP pinning path is the interesting one. It resolves the hostname
through Cloudflare or Google over HTTPS, then opens a TLS connection straight to
that address while advertising the real hostname as the SNI — so the server
routes the request correctly and the certificate still validates against the
hostname you asked for. It is not a certificate bypass: a filter that intercepts
the connection fails validation and the fetch is refused.

This required dropping to `Network.framework`. `URLSession` sends the IP address
as the TLS server name when you point it at an IP, which every CDN rejects
outright, so `PinnedHTTPClient` speaks HTTP/1.1 directly — including redirect
following and chunked transfer decoding.

**Subresources go through the relay too.** Fetching just the HTML gets you an
unstyled skeleton: the page's stylesheets, images, fonts and scripts are then
requested by WebKit over the ordinary network, straight back into the block. A
single Wikipedia article pulls in 275 stylesheets and 61 images this way.

So the page is served under a private `aiorelay://` scheme handled by
`RelaySchemeHandler`, which fetches each resource through the same pinned path.
WebKit won't let a handler intercept `https`, so the scheme swap is the way in —
and because the host and path are preserved (`https://a.com/x.css` ↔
`aiorelay://a.com/x.css`) and the *document itself* loads from that scheme, every
relative and protocol-relative reference resolves back into the relay for free.
Only absolute `https://` references need rewriting.

Measured on a Wikipedia article: 17 stylesheets and 748 CSS rules applied, 58 of
60 images loaded, correct fonts and colors.

**What the viewer can't do.** Content sites — news, wikis, docs, forums, blogs —
render properly. Full web *applications* (Instagram, X, Discord, Gmail) do not,
and this is architectural rather than a missing feature. Their JavaScript calls
browser APIs that only work on a real `https://` origin — `navigator.sendBeacon`,
service workers, secure-context crypto — and a custom scheme cannot be a
trustworthy origin. Instagram loads all 97 of its scripts through the relay and
still dies on `TypeError: Beacons can only be sent over HTTP(S)`. Logins don't
carry over either, since cookies belong to the real origin.

For those sites the answer is the DNS profile below, which fixes them in a real
browser. The app detects this case and says so instead of sitting on a splash
screen.

### Diagnose

Tells you *which layer* a block sits at, because that determines what can beat
it:

- Compares your network's DNS answer against a public DoH answer — a mismatch or
  a sinkhole address (`0.0.0.0`, a private range) means DNS tampering.
- Opens a raw TCP connection to port 443 — a failure means IP-level blocking.
- Completes a TLS handshake carrying the real SNI — a failure here while TCP
  works means something is reading the server name and cutting the connection.

Each verdict comes with the methods actually worth trying.

### Network & VPN

Generates a `.mobileconfig` profile that switches **the entire Mac** to encrypted
DNS — Safari, Chrome, everything — not just this app. That is the supported OS
mechanism and it survives reboots. You install it yourself in System Settings;
the app only writes the file.

The VPN section is deliberately honest: this app cannot be a VPN. A VPN is a
server somewhere else that forwards your traffic, and no client-side app can
create that exit point. The section explains how to get one and gives you the
`wg-quick` commands, which you run yourself — an app quietly taking admin rights
to rewrite your routing table is exactly what you shouldn't allow.

### Convert Files

Drag files in; the type is detected automatically.

- **Images** — HEIC, PNG, JPEG, TIFF, GIF, BMP via `sips`. Built into macOS.
- **Documents** — DOCX, RTF, HTML, TXT via `textutil`, and PDF via PDFKit with
  real pagination. Built into macOS.
- **Audio/video** — MP4, MOV, WebM, MKV, MP3, M4A, WAV, FLAC via ffmpeg.
  Needs `brew install ffmpeg`; the app detects its absence and says so.

Output files never overwrite an existing file — collisions get numbered.

### Download

yt-dlp wrapper for video and audio, with live progress. "Save page" routes
through the same relay engine as Open a Link, so it works on blocked sites.

Needs `brew install yt-dlp` for the media modes.

### Utilities

QR codes, hashes (MD5/SHA-1/SHA-256/SHA-512, text or file), Base64 (tolerates
URL-safe and unpadded input), URL encoding, JSON formatting and validation,
password generation via the system CSPRNG, a tracking-parameter link cleaner,
text case conversion, timestamp conversion, and UUIDs.

## Optional dependencies

```bash
brew install ffmpeg yt-dlp
```

Everything else works with a stock macOS install.

## Layout

```
Sources/Krackn/
  App/KracknApp.swift          entry point, navigation, shared UI
  Core/ShellRunner.swift         external process execution
  Features/Unblock/              DoH, pinned HTTP client, relay engine, diagnostics
  Features/Network/              DoH profile generation
  Features/Convert/              conversion backends
  Features/Download/             yt-dlp wrapper
  Features/Utilities/            the toolbox
```

## Tests

```bash
./run-tests.sh          # unit, network, cancellation, encoding
./run-tests.sh --all    # adds the slow stress and render suites
./run-tests.sh unit     # one suite by name
```

671 assertions across sixteen suites. They're standalone programs rather than XCTest
bundles because this is a SwiftPM executable built with Command Line Tools, where
there's no test host to run them in. Each suite compiles the real source files
directly, so it cannot pass against a stale build — an early version of the
runner word-split a path containing a space, silently compiled nothing, and
reported the previous run's results, so the runner now fails loudly if a suite
produces no binary.

What each suite is actually for:

- **unit** — exhausts all 32 input combinations of the block classifier, plus
  hostile input to the URL parser, known-answer hash vectors, and profile XML
  escaping.
- **network** — real fetches against live sites: redirect chains, chunked
  bodies, binary integrity, 404s, and conversion edge cases (unicode filenames,
  zero-byte files, mislabelled extensions, name collisions).
- **cancellation** — proves a cancelled fetch returns in milliseconds instead of
  waiting out a 35-second timeout, and that 15 simultaneous cancels don't
  deadlock.
- **encoding** — renders Latin-1, Windows-1252 and UTF-8 pages through WebKit
  and checks the accented characters survive.
- **stress** — 24 interrupted page loads and 12 mid-flight web view
  deallocations, hunting for the scheme-handler use-after-cancel crash.
- **render** — loads real pages and asserts stylesheets, CSS rules and images
  actually applied.
- **taborg** — tab ordering and the auto-close policy, both pure over a
  snapshot struct so neither needs a `WKWebView` to test.
- **security** — the threat heuristics, led by sixteen real sites that must
  come back clean.
- **theme** — that "Yellow" is still byte-identical to the original palette,
  and that no generated colour family drops below WCAG AA.

The network suite makes real parallel fetches against live sites and can fail
transiently on a slow connection; re-run it before believing it.

### Bugs these caught

Worth recording, because each looked fine until measured:

- **The classifier flagged every CDN as blocked.** It called any disagreement
  between your resolver and a public one "DNS tampering", but anycast CDNs
  answer per-resolver — Instagram legitimately returns different addresses with
  zero overlap. It now ignores the addresses and asks whether the address you
  were given can present a valid certificate for the host, which a sinkhole
  can't fake.
- **Non-UTF-8 pages were corrupted.** `RelayResult` carried a `String`, so a
  Latin-1 page became `caf� na�ve` before it ever reached the renderer. It now
  carries the raw bytes and the declared Content-Type. The same bug was in
  "Save page", which re-encoded to UTF-8 on the way to disk.
- **Cancelling would have hung forever.** `NWConnection`'s `.cancelled` state
  fell into a `default: break`, so the continuation was never resumed. Nothing
  cancelled connections yet, so it was latent — adding the Cancel button would
  have shipped the hang.
- **Drag-and-drop silently ignored some sources.** The drop handler only
  accepted bookmark `Data`; providers that hand back an `NSURL` were dropped
  without a word.
- **The phishing check flagged real sites.** Matching a brand as a bare
  substring made `appleinsider.com` and `login.microsoftonline.com` come back
  as impersonation attempts. The brand now has to be a whole label, bounded by
  a dot or hyphen — which still catches `apple.com.secure-login.tk`.
- **Purple failed WCAG AA invisibly.** HSL lightness isn't perceived lightness,
  so rotating the palette's hue left purple's dark accent at 4.39:1 against its
  own text. The generator now lifts an accent until the pair clears, rather
  than trusting a fixed lightness across every hue.
- **"Never overwrite files" meant overwrite.** `UserDefaults.bool(forKey:)`
  returns `false` for a key that was never written, so the protective default
  inverted itself anywhere `registerDefaults()` hadn't run. The conversion
  collision test caught it the same day it was introduced.

## A note on scope

If the network you're working around belongs to a school or an employer, routing
past its filter probably violates their acceptable-use policy regardless of the
method. That's a policy question, not a technical one, and it's yours to decide.
