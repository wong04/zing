import Foundation
import SwiftUI
import WebKit
import AVFoundation

/// A `WKWebView` that refuses to let pinch-magnification stretch the page.
///
/// This is the Google Docs blur. Docs paints the *document* into a `<canvas>`
/// and draws the surrounding toolbar as ordinary DOM — and that split is the
/// whole clue. Under WKWebView's `magnification`, DOM text is re-rendered as
/// vectors at the new scale and stays crisp, but a canvas is a bitmap and can
/// only be stretched. A page that has been magnified therefore shows a sharp
/// UI wrapped around a soft document, which is exactly what was reported. One
/// stray trackpad pinch is enough, and it persists for the life of the tab.
///
/// `pageZoom` is the mechanism that behaves: it re-lays-out and re-rasterises,
/// so text and canvas alike stay sharp at any level. So magnification is pinned
/// at 1 and zoom goes through `pageZoom` instead.
///
/// An earlier version of this class forced `wantsLayer` and overwrote
/// `layer.contentsScale`, on the theory that the canvas was rasterising at 1x.
/// Measuring it killed that theory — a plain `WKWebView` built exactly the way
/// this app builds one reports `devicePixelRatio == 2` on a 2x display — so
/// that code is gone. Hand-managing a layer WebKit owns was risk with no
/// benefit.
final class ScaleAwareWebView: WKWebView {
    /// Edit › AutoFill › Passwords… — macOS sends it to the focused view, and a
    /// plain WKWebView doesn't take it, which is why the menu item was dead on
    /// web pages. Taking it here routes it to `KeychainFillBridge`.
    var onPasswordsCommand: (() -> Void)?

    @objc func _handleInsertFromPasswordsCommand(_ sender: Any?) {
        onPasswordsCommand?()
    }
}

/// One tab. Owns its web view for the lifetime of the tab so switching tabs
/// preserves scroll position, form state and history rather than reloading.
@MainActor
final class BrowserTab: NSObject, ObservableObject, Identifiable {
    let id = UUID()

    @Published var title = "New Tab"
    @Published var urlText = ""
    @Published var isLoading = false
    @Published var progress: Double = 0
    @Published var canGoBack = false
    @Published var canGoForward = false
    @Published var isSecure = false
    /// Set when a load fails in a way that looks like network interference,
    /// which is when offering the relay is actually useful.
    @Published var blockedURL: URL?
    @Published var loadError: String?
    /// Pinned tabs sit at the far left, are never auto-closed, and are skipped
    /// by whatever organisation strategy is active.
    @Published var isPinned = false
    /// Last time the user actually looked at this tab. Drives both the
    /// "last opened" ordering and the inactivity auto-close.
    @Published var lastAccessed = Date()
    /// When the tab was opened, for the "newest first" ordering.
    let createdAt = Date()
    /// Set when the security scanner refuses a navigation, so the UI can say
    /// what was blocked instead of showing a blank page.
    @Published var securityBlock: ThreatVerdict?

    let webView: WKWebView
    private var observations: [NSKeyValueObservation] = []
    /// Where each in-flight download is being written, so the finished handler
    /// knows which file to hand the scanner.
    fileprivate var pendingDownloads: [ObjectIdentifier: URL] = [:]

    /// Called when the page asks for a new window (target="_blank"), so the
    /// browser can open a tab instead of silently dropping the click.
    var onNewTabRequested: ((URL?) -> Void)?
    /// Fires when a download starts, so the UI can report where it went.
    var onDownload: ((String) -> Void)?
    /// Lets the model vet a URL before it loads. Returning a verdict cancels
    /// the navigation.
    var onNavigationCheck: ((URL) -> ThreatVerdict?)?
    /// The new-tab page's "Edit shortcuts" link was clicked.
    var onEditShortcuts: (() -> Void)?
    /// Fires whenever the page changes, so the session file can follow along.
    var onNavigated: (() -> Void)?
    /// A page asked for a popup (`window.open`, `target=_blank`). Returning a
    /// real web view — built from WebKit's configuration — keeps the popup
    /// linked to this page through `window.opener`, which is how "Sign in with
    /// Google" (and Apple, Microsoft, payment pages…) hands the result back.
    var onCreatePopup: ((WKWebViewConfiguration) -> WKWebView?)?
    /// The page called `window.close()`.
    var onCloseRequested: (() -> Void)?
    /// The sign-in field that has focus on the page, if any — where the
    /// Keychain key is drawn. See `KeychainAutofill`.
    @Published var credentialField: KeychainAutofill.Field?
    /// The tab whose page opened this one as a popup, to return to on close.
    var openerID: UUID?
    /// ⌘-click (background) and ⇧⌘-click (frontmost) on a link.
    var onOpenInNewTab: ((URL, _ activate: Bool) -> Void)?
    /// ⇧-click on a link: save it for later instead of following it.
    var onAddToReadingList: ((URL) -> Void)?
    /// Progress for the downloads list.
    var onDownloadEvent: ((DownloadEvent) -> Void)?
    /// What the tab looked like when last on screen, for the tab overview.
    @Published var thumbnail: NSImage?
    /// The icon this page declares for itself. Google Docs, Slides and Drive
    /// share a domain — Docs and Slides even a host — and only this tells
    /// them apart.
    @Published var iconURL: String?
    private var iconTask: Task<Void, Never>?

    /// Runs `work` shortly, replacing any read already waiting.
    func scheduleIconRead(_ work: @escaping @MainActor () async -> Void) {
        iconTask?.cancel()
        iconTask = Task { @MainActor in
            try? await Task.sleep(for: .milliseconds(250))
            guard !Task.isCancelled else { return }
            await work()
        }
    }
    fileprivate var lastIconHost: String?

    /// A snapshot that survives a quit. `interactionState` carries WebKit's
    /// own record of the back/forward list and scroll position, which is what
    /// makes a restored tab land where you left it rather than at the top of
    /// the page.
    var record: TabRecord {
        TabRecord(url: webView.url?.absoluteString ?? urlText,
                  title: title,
                  isPinned: isPinned,
                  isActive: false,
                  interactionState: webView.interactionState as? Data)
    }

    /// Puts a saved tab back, history and scroll included where available.
    func restore(from record: TabRecord) {
        isPinned = record.isPinned
        title = record.title.isEmpty ? (URL(string: record.url)?.host ?? "New Tab")
                                     : record.title
        if let state = record.interactionState {
            webView.interactionState = state
            // A restored interaction state doesn't always trigger a load on
            // its own; nudge it if nothing arrived.
            if webView.url == nil, let url = URL(string: record.url) {
                load(url)
            }
        } else if let url = URL(string: record.url) {
            load(url)
        }
    }

    /// Everything the organiser and auto-closer need, with no web view attached.
    func snapshot(isActive: Bool) -> TabSnapshot {
        let domain = siteDomain
        let group = SiteGroup.of(host: webView.url?.host ?? "", path: webView.url?.path ?? "",
                                 domain: domain)
        return TabSnapshot(id: id, title: title, domain: domain, group: group.key,
                           groupName: group.name, lastAccessed: lastAccessed,
                           createdAt: createdAt, isPinned: isPinned, isActive: isActive)
    }

    /// The registrable-ish domain, used to clump tabs of the same site
    /// together. `docs.google.com` and `mail.google.com` both report
    /// `google.com` so they group as one site rather than three.
    var siteDomain: String {
        guard let host = webView.url?.host?.lowercased() else { return "" }
        return BrowserTab.registrableDomain(of: host)
    }

    /// One definition of "the same site" for the whole app — see
    /// `ThreatHeuristics.registrableDomain`, which `Tests/taborg` covers.
    nonisolated static func registrableDomain(of host: String) -> String {
        ThreatHeuristics.registrableDomain(host)
    }

    init(configuration: WKWebViewConfiguration) {
        webView = ScaleAwareWebView(frame: .zero, configuration: configuration)
        super.init()

        webView.navigationDelegate = self
        webView.uiDelegate = self
        (webView as? ScaleAwareWebView)?.onPasswordsCommand = { [weak self] in
            guard let self else { return }
            if self.credentialField != nil {
                KeychainFillBridge.shared.begin(for: self)
            } else {
                NSSound.beep()
            }
        }
        webView.allowsBackForwardNavigationGestures = true
        // Off deliberately — see `ScaleAwareWebView`. Magnification stretches
        // the rendered surface, which is what softens a canvas-rendered page
        // like Google Docs. ⌘+ / ⌘− drive `pageZoom` instead, which re-renders
        // at full resolution.
        webView.allowsMagnification = false
        // Present as Safari. Sites gate features on the user agent, and an
        // unrecognised one gets you the degraded or "unsupported browser" path.
        // Can be switched off in Settings → Compatibility, because the reverse
        // is also possible: a site can pick a rendering path for "Safari" that
        // this build doesn't handle the way real Safari does.
        webView.customUserAgent = AppSettings.defaultUserAgent ? nil : Self.userAgent

        observations = [
            webView.observe(\.title, options: [.new]) { [weak self] view, _ in
                Task { @MainActor in
                    if view.url?.scheme == StartPage.scheme {
                        self?.title = "New Tab"
                        return
                    }
                    let name = view.title ?? ""
                    self?.title = name.isEmpty ? (view.url?.host ?? "New Tab") : name
                    // A-Z ordering depends on the title, which arrives after
                    // the URL — so a title change is a reason to re-sort too.
                    self?.onNavigated?()
                    // Single-page apps swap their icon along with the title
                    // (Drive → Docs → Slides) without a full navigation.
                    self?.refreshIcon()
                }
            },
            webView.observe(\.url, options: [.new]) { [weak self] view, _ in
                Task { @MainActor in
                    guard let url = view.url else { return }
                    if self?.urlText != url.absoluteString { self?.credentialField = nil }
                    self?.urlText = url.absoluteString
                    self?.isSecure = url.scheme == "https"
                    if url.scheme == StartPage.scheme { self?.isSecure = false }
                    // Start the icon lookup as soon as the URL is known rather
                    // than waiting for `didFinish`. A tab that was already open
                    // when the app updated, or a page that stalls before
                    // finishing, would otherwise never get one — which is
                    // exactly why no icons appeared.
                    if let host = url.host, url.scheme?.hasPrefix("http") == true {
                        FaviconStore.shared.load(host: host)
                        // A different site can't keep the last page's icon.
                        if let old = self?.iconURL, !old.isEmpty,
                           self?.lastIconHost != host { self?.iconURL = nil }
                        self?.lastIconHost = host
                    } else {
                        self?.iconURL = nil
                    }
                    self?.onNavigated?()
                }
            },
            webView.observe(\.estimatedProgress, options: [.new]) { [weak self] view, _ in
                Task { @MainActor in self?.progress = view.estimatedProgress }
            },
            webView.observe(\.isLoading, options: [.new]) { [weak self] view, _ in
                Task { @MainActor in self?.isLoading = view.isLoading }
            },
            webView.observe(\.canGoBack, options: [.new]) { [weak self] view, _ in
                Task { @MainActor in self?.canGoBack = view.canGoBack }
            },
            webView.observe(\.canGoForward, options: [.new]) { [weak self] view, _ in
                Task { @MainActor in self?.canGoForward = view.canGoForward }
            },
        ]
    }

    /// `nonisolated` so background work — the favicon fetch — can read it
    /// without hopping to the main actor for a string constant.
    /// See `UserAgent` — read from the installed Safari, never hard-coded.
    nonisolated static let userAgent: String? = UserAgent.current

    /// The URL bar's canonical text: whatever is genuinely loaded right now.
    /// The internal start-page scheme is an implementation detail, so it shows
    /// as empty rather than `aiohome://start`.
    var displayURL: String {
        guard let url = webView.url else { return "" }
        if url.scheme == StartPage.scheme { return "" }
        if url.scheme == RelayURL.scheme, let real = RelayURL.decode(url) {
            return real.absoluteString
        }
        return url.absoluteString
    }

    // MARK: - Navigation

    /// Accepts either a URL or a search phrase, the way a real address bar does.
    func go(to input: String) {
        let trimmed = input.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return }
        blockedURL = nil
        loadError = nil
        securityBlock = nil
        lastAccessed = Date()

        // One rule for "go there or search" — shared with the new-tab page's
        // search field. See `AddressInput`.
        let target: URL
        switch AddressInput.classify(trimmed) {
        case .url(let url)?:
            target = url
        case .search(let terms)?:
            // Honours the engine chosen in Settings.
            guard let search = AppSettings.searchEngine.queryURL(for: terms) else { return }
            target = search
        case nil:
            return
        }
        load(target)
    }

    /// Single entry point for loading, so tracker stripping and the security
    /// check can't be bypassed by a caller that reaches for `webView.load`.
    func load(_ url: URL) {
        let cleaned = AppSettings.blockTrackers ? Self.stripTrackers(from: url) : url
        if let verdict = onNavigationCheck?(cleaned), verdict.isBlocking {
            securityBlock = verdict
            return
        }
        webView.load(URLRequest(url: cleaned))
    }

    /// Removes tracking parameters before the request leaves the machine.
    /// Doing it at navigation time rather than on the rendered link means it
    /// also covers redirects and pasted URLs.
    nonisolated static func stripTrackers(from url: URL) -> URL {
        guard let scheme = url.scheme?.lowercased(),
              scheme == "http" || scheme == "https",
              let cleaned = UtilityTools.cleanLink(url.absoluteString),
              !cleaned.removed.isEmpty,
              let result = URL(string: cleaned.url)
        else { return url }
        return result
    }

    func reload() {
        blockedURL = nil
        loadError = nil
        securityBlock = nil
        webView.reload()
    }

    /// Goes to the homepage from Settings, or the local start page when that's
    /// left blank — which is the default, and the only homepage that makes no
    /// network request at all.
    func goHome() {
        blockedURL = nil
        loadError = nil
        securityBlock = nil
        let homepage = AppSettings.homepage
        if !homepage.isEmpty, let url = try? RelayEngine.normalize(homepage) {
            load(url)
        } else {
            webView.load(URLRequest(url: StartPage.url))
        }
    }

    func stop() { webView.stopLoading() }
    func goBack() { webView.goBack() }
    func goForward() { webView.goForward() }

    // MARK: - Diagnostics

    /// Reports how the *current page* is actually being rasterised.
    ///
    /// This exists because Google Docs renders soft here and sharp in every
    /// other browser, and every theory tested from outside the app came back
    /// clean: device pixel ratio is 2, the view hierarchy rasterises 1px lines
    /// at native scale, and the browser's configuration renders a canvas
    /// identically to a default web view. The remaining unknown is what Docs
    /// itself decides to do, which can only be measured on the page.
    ///
    /// The canvas ratio is the number that matters. Docs paints the document
    /// into a `<canvas>`; if its backing store is the same size as its CSS box
    /// while `devicePixelRatio` is 2, the document is being drawn at half
    /// resolution and stretched — and that is a Docs-side decision we'd then
    /// know to work around.
    /// Measures every canvas on the page against its CSS box, plus any
    /// transform applied to it or its ancestors. Empty when the page has no
    /// canvas, so ordinary pages generate no noise.
    func canvasReport() async -> String {
        let probe = """
        (function () {
          var list = Array.prototype.slice.call(document.querySelectorAll('canvas'));
          if (!list.length) return '';
          function chain(el) {
            var out = [], node = el;
            while (node && node !== document.documentElement) {
              var t = getComputedStyle(node).transform;
              if (t && t !== 'none') out.push(node.tagName.toLowerCase() + ':' + t);
              node = node.parentElement;
            }
            return out.slice(0, 3).join(' | ') || 'none';
          }
          var rows = list.slice(0, 4).map(function (c) {
            var b = c.getBoundingClientRect();
            return {
              cssBox: Math.round(b.width) + 'x' + Math.round(b.height),
              backing: c.width + 'x' + c.height,
              ratioX: b.width ? +(c.width / b.width).toFixed(3) : 0,
              ratioY: b.height ? +(c.height / b.height).toFixed(3) : 0,
              transforms: chain(c)
            };
          });
          return JSON.stringify({
            dpr: window.devicePixelRatio,
            zoom: (window.outerWidth && window.innerWidth)
                    ? +(window.outerWidth / window.innerWidth).toFixed(3) : null,
            viewport: window.innerWidth + 'x' + window.innerHeight,
            canvasCount: list.length,
            canvases: rows
          });
        })()
        """
        let value = try? await webView.evaluateJavaScript(probe)
        return (value as? String) ?? ""
    }

    func pageDiagnostics() async -> String {
        let probe = """
        (function () {
          var canvases = Array.prototype.slice.call(document.querySelectorAll('canvas'));
          var report = canvases.slice(0, 6).map(function (c) {
            var box = c.getBoundingClientRect();
            return {
              css: Math.round(box.width) + 'x' + Math.round(box.height),
              backing: c.width + 'x' + c.height,
              ratio: box.width ? +(c.width / box.width).toFixed(2) : 0
            };
          });
          return JSON.stringify({
            url: location.hostname,
            devicePixelRatio: window.devicePixelRatio,
            innerSize: window.innerWidth + 'x' + window.innerHeight,
            screen: screen.width + 'x' + screen.height,
            canvasCount: canvases.length,
            canvases: report
          }, null, 2);
        })()
        """

        let result: String
        do {
            let value = try await webView.evaluateJavaScript(probe)
            result = (value as? String) ?? "\(String(describing: value))"
        } catch {
            result = "JavaScript failed: \(error.localizedDescription)"
        }

        return """
        Krackn page diagnostics
        -----------------------
        pageZoom      \(webView.pageZoom)
        magnification \(webView.magnification)  (allowed: \(webView.allowsMagnification))
        window scale  \(webView.window?.backingScaleFactor ?? 0)
        layer scale   \(webView.layer?.contentsScale ?? 0)

        \(result)
        """
    }

    // MARK: - Zoom

    /// Steps a real browser offers, so ⌘+ walks a predictable ladder.
    private static let zoomSteps: [CGFloat] = [
        0.5, 0.67, 0.75, 0.8, 0.9, 1.0, 1.1, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0,
    ]

    @Published var zoom: CGFloat = 1

    func zoomIn() { setZoom(Self.zoomSteps.first { $0 > zoom + 0.001 } ?? zoom) }
    func zoomOut() { setZoom(Self.zoomSteps.last { $0 < zoom - 0.001 } ?? zoom) }
    func resetZoom() { setZoom(1) }

    /// `pageZoom` rather than `magnification`: it re-lays-out the page and
    /// rasterises at the new scale, so text and canvas both stay sharp. The
    /// magnification path scales an already-rendered bitmap and blurs anything
    /// drawn into a canvas.
    private func setZoom(_ value: CGFloat) {
        zoom = value
        webView.pageZoom = value
    }

    /// Tears the tab down so nothing of it survives in memory. The data store
    /// itself is non-persistent, so this is about dropping the in-process
    /// back/forward list and any decoded page rather than touching disk.
    func teardown() {
        webView.stopLoading()
        observations.forEach { $0.invalidate() }
        observations = []
        webView.navigationDelegate = nil
        webView.uiDelegate = nil
        webView.loadHTMLString("", baseURL: nil)
    }
}

// MARK: - Navigation delegate

extension BrowserTab: WKNavigationDelegate {

    nonisolated func webView(
        _ webView: WKWebView, didFailProvisionalNavigation navigation: WKNavigation!,
        withError error: Error
    ) {
        let failingURL = (error as NSError)
            .userInfo[NSURLErrorFailingURLErrorKey] as? URL
        let code = (error as NSError).code
        let description = error.localizedDescription
        Task { @MainActor in
            self.handleFailure(code: code, description: description, url: failingURL)
        }
    }

    /// Logs canvas geometry for diagnostics, and reads the page's declared
    /// icon (see `refreshIcon`).
    nonisolated func webView(
        _ webView: WKWebView, didFinish navigation: WKNavigation!
    ) {
        // Any page that paints into a canvas gets measured and written to the
        // unified log. Seven theories about the Google Docs blur have now been
        // disproved from outside the app; this reports what is actually
        // happening on the page that misbehaves, without writing anything to
        // disk — the log is memory-resident and privacy-preserving, unlike a
        // debug file, which would undo the no-trace promise.
        Task { @MainActor in
            guard let host = webView.url?.host,
                  webView.url?.scheme?.hasPrefix("http") == true else { return }
            let report = await self.canvasReport()
            guard !report.isEmpty else { return }
            BrowserDiagnostics.log("[\(host)] \(report)")
        }

        Task { @MainActor in self.refreshIcon() }
    }

    nonisolated func webView(
        _ webView: WKWebView, didFail navigation: WKNavigation!, withError error: Error
    ) {
        let code = (error as NSError).code
        let description = error.localizedDescription
        Task { @MainActor in
            self.handleFailure(code: code, description: description, url: nil)
        }
    }

    @MainActor
    private func handleFailure(code: Int, description: String, url: URL?) {
        // -999 is "cancelled", which happens constantly during normal
        // navigation when a load is superseded. Reporting it as an error
        // would make the browser look broken while it's working fine.
        guard code != NSURLErrorCancelled else { return }

        loadError = description
        // Only offer the relay for failures that actually look like the
        // network refusing to reach a host.
        let interferenceCodes: Set<Int> = [
            NSURLErrorCannotFindHost,
            NSURLErrorCannotConnectToHost,
            NSURLErrorDNSLookupFailed,
            NSURLErrorTimedOut,
            NSURLErrorNetworkConnectionLost,
            NSURLErrorSecureConnectionFailed,
        ]
        if interferenceCodes.contains(code) {
            blockedURL = url ?? webView.url
        }
    }

    nonisolated func webView(
        _ webView: WKWebView, decidePolicyFor navigationAction: WKNavigationAction,
        decisionHandler: @escaping (WKNavigationActionPolicy) -> Void
    ) {
        // WebKit delivers this on the main thread, so reading the main-actor
        // isolated request here is safe and avoids a pointless hop that would
        // delay the policy decision.
        MainActor.assumeIsolated {
            guard let url = navigationAction.request.url else {
                decisionHandler(.allow)
                return
            }
            // Hand non-web schemes (mailto:, tel:, app links) to the system
            // rather than failing to load them in the web view.
            if let scheme = url.scheme?.lowercased(),
               !["http", "https", "about", "data", "blob",
                 StartPage.scheme, RelayURL.scheme].contains(scheme) {
                decisionHandler(.cancel)
                NSWorkspace.shared.open(url)
                return
            }

            // Modifier-clicks on links, as Safari handles them. Only for real
            // link activations: a shift-click that extends a text selection
            // is not a navigation and never arrives here.
            if navigationAction.navigationType == .linkActivated,
               navigationAction.targetFrame?.isMainFrame != false {
                let flags = navigationAction.modifierFlags
                if flags.contains(.command) || navigationAction.buttonNumber == 2 {
                    decisionHandler(.cancel)
                    self.onOpenInNewTab?(url, flags.contains(.shift))
                    return
                }
                if flags.contains(.option) {
                    decisionHandler(.download)
                    return
                }
                if flags.contains(.shift) {
                    decisionHandler(.cancel)
                    self.onAddToReadingList?(url)
                    return
                }
            }

            // Refuse a host the scanner flags, before a single byte is
            // requested from it.
            if let verdict = onNavigationCheck?(url), verdict.isBlocking {
                self.securityBlock = verdict
                decisionHandler(.cancel)
                return
            }

            // "Edit shortcuts" on the new-tab page opens Settings rather than
            // loading anything — and only from that page, so no website can
            // pop Settings open by linking here.
            if url.scheme == StartPage.scheme, url.host == StartPage.editHost {
                decisionHandler(.cancel)
                if navigationAction.sourceFrame.request.url?.scheme == StartPage.scheme {
                    onEditShortcuts?()
                }
                return
            }

            // The new-tab page's search field submits to the search engine.
            // Catch it first: an address typed there should be *opened*, not
            // searched for — `apple.com` used to search for "apple.com". Same
            // rule as the address bar (`AddressInput`), so the two can't drift.
            if navigationAction.navigationType == .formSubmitted,
               navigationAction.sourceFrame.request.url?.scheme == StartPage.scheme,
               let query = URLComponents(url: url, resolvingAgainstBaseURL: false)?
                   .queryItems?.first(where: { $0.name == "q" })?.value {
                decisionHandler(.cancel)
                self.go(to: query)
                return
            }

            // `<a download>` — including blob: and data: links that pages build
            // on the fly — means "save this", not "go there". Checked after
            // the scanner, so a flagged host can't push a file either.
            if navigationAction.shouldPerformDownload {
                decisionHandler(.download)
                return
            }

            // Re-issue main-frame navigations with tracking parameters removed.
            // Only when something was actually removed, or this would loop.
            if AppSettings.blockTrackers, navigationAction.targetFrame?.isMainFrame == true {
                let cleaned = Self.stripTrackers(from: url)
                if cleaned != url {
                    decisionHandler(.cancel)
                    webView.load(URLRequest(url: cleaned))
                    return
                }
            }

            decisionHandler(.allow)
        }
    }

    /// Download what can't be shown, or what the server marks as a file.
    /// See `DownloadPolicy`.
    nonisolated func webView(
        _ webView: WKWebView, decidePolicyFor navigationResponse: WKNavigationResponse,
        decisionHandler: @escaping (WKNavigationResponsePolicy) -> Void
    ) {
        // Delivered on the main thread, like the action policy above.
        MainActor.assumeIsolated {
            let disposition = (navigationResponse.response as? HTTPURLResponse)?
                .value(forHTTPHeaderField: "Content-Disposition")
            let download = DownloadPolicy.shouldDownload(
                canShow: navigationResponse.canShowMIMEType,
                contentDisposition: disposition,
                isMainFrame: navigationResponse.isForMainFrame)
            decisionHandler(download ? .download : .allow)
        }
    }

    nonisolated func webView(
        _ webView: WKWebView, navigationAction: WKNavigationAction,
        didBecome download: WKDownload
    ) {
        Task { @MainActor in download.delegate = self }
    }

    nonisolated func webView(
        _ webView: WKWebView, navigationResponse: WKNavigationResponse,
        didBecome download: WKDownload
    ) {
        Task { @MainActor in download.delegate = self }
    }
}

// MARK: - UI delegate

extension BrowserTab: WKUIDelegate {

    /// target="_blank" and window.open().
    ///
    /// This used to return nil and open the URL in a fresh, unrelated tab.
    /// That loses `window.opener`: the page's `window.open` got back null, so
    /// sign-in popups — Google's included — finished signing you in with
    /// nowhere to send the result, and couldn't close themselves either.
    /// WebKit needs the returned view built from *its* configuration, and it
    /// loads the request into it itself.
    nonisolated func webView(
        _ webView: WKWebView, createWebViewWith configuration: WKWebViewConfiguration,
        for navigationAction: WKNavigationAction, windowFeatures: WKWindowFeatures
    ) -> WKWebView? {
        MainActor.assumeIsolated {
            if let popup = self.onCreatePopup?(configuration) { return popup }
            self.onNewTabRequested?(navigationAction.request.url)
            return nil
        }
    }

    /// `window.close()` from the page — a sign-in popup finishing, typically.
    nonisolated func webViewDidClose(_ webView: WKWebView) {
        MainActor.assumeIsolated { self.onCloseRequested?() }
    }

    /// Camera and microphone. Without this delegate method WebKit denies every
    /// `getUserMedia` call outright, which is why video calls, voice input and
    /// anything using a webcam simply failed with no prompt and no explanation.
    ///
    /// Two gates have to pass: macOS has to have granted *the app* access to
    /// the device (a TCC prompt, handled here), and then the user has to allow
    /// *this site* to use it. Asking the OS first means the site prompt is only
    /// shown when granting it can actually work.
    nonisolated func webView(
        _ webView: WKWebView,
        requestMediaCapturePermissionFor origin: WKSecurityOrigin,
        initiatedByFrame frame: WKFrameInfo,
        type: WKMediaCaptureType,
        decisionHandler: @escaping (WKPermissionDecision) -> Void
    ) {
        Task { @MainActor in
            let host = origin.host.isEmpty ? "This page" : origin.host
            let granted = await BrowserTab.requestSystemCapture(for: type)
            guard granted else {
                let what = type == .camera ? "camera" : "microphone"
                let alert = NSAlert()
                alert.messageText = "Krackn doesn't have \(what) access"
                alert.informativeText =
                    "macOS is blocking it at the system level. Open System Settings › "
                    + "Privacy & Security › \(type == .camera ? "Camera" : "Microphone") "
                    + "and enable Krackn, then reload the page."
                alert.addButton(withTitle: "Open Settings")
                alert.addButton(withTitle: "Cancel")
                if alert.runModal() == .alertFirstButtonReturn {
                    let pane = type == .camera ? "Privacy_Camera" : "Privacy_Microphone"
                    if let url = URL(string:
                        "x-apple.systempreferences:com.apple.preference.security?\(pane)") {
                        NSWorkspace.shared.open(url)
                    }
                }
                decisionHandler(.deny)
                return
            }

            let alert = NSAlert()
            alert.messageText = "\(host) wants to use your \(Self.label(for: type))"
            alert.informativeText = "Only allow this if you were expecting it."
            alert.addButton(withTitle: "Allow")
            alert.addButton(withTitle: "Don't Allow")
            decisionHandler(alert.runModal() == .alertFirstButtonReturn ? .grant : .deny)
        }
    }

    private static func label(for type: WKMediaCaptureType) -> String {
        switch type {
        case .camera: return "camera"
        case .microphone: return "microphone"
        case .cameraAndMicrophone: return "camera and microphone"
        @unknown default: return "camera and microphone"
        }
    }

    /// Asks macOS for the underlying device permission, covering both halves of
    /// a combined request.
    private static func requestSystemCapture(for type: WKMediaCaptureType) async -> Bool {
        func request(_ media: AVMediaType) async -> Bool {
            switch AVCaptureDevice.authorizationStatus(for: media) {
            case .authorized: return true
            case .notDetermined: return await AVCaptureDevice.requestAccess(for: media)
            default: return false
            }
        }
        switch type {
        case .camera:
            return await request(.video)
        case .microphone:
            return await request(.audio)
        default:
            // Both halves must be granted, and both must be *asked for* — so
            // these can't be folded into one `&&`, which would short-circuit
            // and leave the microphone prompt unshown.
            let video = await request(.video)
            let audio = await request(.audio)
            return video && audio
        }
    }

    /// Screen sharing. Declining explicitly is better than the silent failure
    /// that comes from not implementing it.
    nonisolated func webView(
        _ webView: WKWebView,
        requestDisplayCapturePermissionFor origin: WKSecurityOrigin,
        initiatedByFrame frame: WKFrameInfo,
        decisionHandler: @escaping (WKPermissionDecision) -> Void
    ) {
        Task { @MainActor in
            let host = origin.host.isEmpty ? "This page" : origin.host
            let alert = NSAlert()
            alert.messageText = "\(host) wants to share your screen"
            alert.informativeText = "This lets the page see everything on your display."
            alert.addButton(withTitle: "Allow")
            alert.addButton(withTitle: "Don't Allow")
            decisionHandler(alert.runModal() == .alertFirstButtonReturn ? .grant : .deny)
        }
    }

    // Without these three, JavaScript dialogs are silently ignored and pages
    // that gate behaviour on a confirm() appear frozen.
    nonisolated func webView(
        _ webView: WKWebView, runJavaScriptAlertPanelWithMessage message: String,
        initiatedByFrame frame: WKFrameInfo, completionHandler: @escaping () -> Void
    ) {
        Task { @MainActor in
            let alert = NSAlert()
            alert.messageText = message
            alert.addButton(withTitle: "OK")
            alert.runModal()
            completionHandler()
        }
    }

    nonisolated func webView(
        _ webView: WKWebView, runJavaScriptConfirmPanelWithMessage message: String,
        initiatedByFrame frame: WKFrameInfo, completionHandler: @escaping (Bool) -> Void
    ) {
        Task { @MainActor in
            let alert = NSAlert()
            alert.messageText = message
            alert.addButton(withTitle: "OK")
            alert.addButton(withTitle: "Cancel")
            completionHandler(alert.runModal() == .alertFirstButtonReturn)
        }
    }

    nonisolated func webView(
        _ webView: WKWebView, runJavaScriptTextInputPanelWithPrompt prompt: String,
        defaultText: String?, initiatedByFrame frame: WKFrameInfo,
        completionHandler: @escaping (String?) -> Void
    ) {
        Task { @MainActor in
            let alert = NSAlert()
            alert.messageText = prompt
            let field = NSTextField(frame: NSRect(x: 0, y: 0, width: 260, height: 24))
            field.stringValue = defaultText ?? ""
            alert.accessoryView = field
            alert.addButton(withTitle: "OK")
            alert.addButton(withTitle: "Cancel")
            let confirmed = alert.runModal() == .alertFirstButtonReturn
            completionHandler(confirmed ? field.stringValue : nil)
        }
    }

    /// File pickers for <input type="file">.
    nonisolated func webView(
        _ webView: WKWebView, runOpenPanelWith parameters: WKOpenPanelParameters,
        initiatedByFrame frame: WKFrameInfo,
        completionHandler: @escaping ([URL]?) -> Void
    ) {
        Task { @MainActor in
            let panel = NSOpenPanel()
            panel.allowsMultipleSelection = parameters.allowsMultipleSelection
            panel.canChooseDirectories = parameters.allowsDirectories
            completionHandler(panel.runModal() == .OK ? panel.urls : nil)
        }
    }
}

// MARK: - Downloads

extension BrowserTab: WKDownloadDelegate {

    nonisolated func download(
        _ download: WKDownload,
        decideDestinationUsing response: URLResponse,
        suggestedFilename: String,
        completionHandler: @escaping (URL?) -> Void
    ) {
        Task { @MainActor in
            let downloads = AppSettings.downloadsDirectory
            var destination = downloads.appendingPathComponent(suggestedFilename)
            // Never clobber an existing download.
            var counter = 2
            let base = destination.deletingPathExtension().lastPathComponent
            let ext = destination.pathExtension
            while FileManager.default.fileExists(atPath: destination.path) {
                let name = ext.isEmpty ? "\(base) \(counter)" : "\(base) \(counter).\(ext)"
                destination = downloads.appendingPathComponent(name)
                counter += 1
            }
            self.pendingDownloads[ObjectIdentifier(download)] = destination
            self.onDownload?("Downloading \(destination.lastPathComponent)…")
            self.onDownloadEvent?(.started(ObjectIdentifier(download), destination))
            completionHandler(destination)
        }
    }

    nonisolated func downloadDidFinish(_ download: WKDownload) {
        Task { @MainActor in
            guard let file = self.pendingDownloads
                .removeValue(forKey: ObjectIdentifier(download)) else {
                self.onDownload?("Saved to Downloads.")
                return
            }
            // Every download gets inspected before the user is told it's ready.
            let report = await ThreatScanner.shared.scanDownload(at: file)
            let notice = report.downloadNotice(for: file)
            self.onDownload?(notice)
            self.onDownloadEvent?(.finished(ObjectIdentifier(download), notice))
        }
    }

    nonisolated func download(_ download: WKDownload, didFailWithError error: Error,
                              resumeData: Data?) {
        let message = error.localizedDescription
        Task { @MainActor in
            self.pendingDownloads.removeValue(forKey: ObjectIdentifier(download))
            self.onDownload?("Download failed: \(message)")
            self.onDownloadEvent?(.failed(ObjectIdentifier(download), message))
        }
    }
}

