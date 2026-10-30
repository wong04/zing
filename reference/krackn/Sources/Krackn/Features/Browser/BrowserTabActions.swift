import AppKit
import WebKit

/// What the keyboard shortcuts do to a single tab: find, print, text size,
/// the overview thumbnail, and Reader.
extension BrowserTab {

    // MARK: Find (⌘F, ⌘G, ⇧⌘G)

    /// Finds the next (or previous) match and selects it, scrolling it into
    /// view. Case-insensitive and wrapping, like Safari's find bar.
    func find(_ text: String, backwards: Bool = false) async -> Bool {
        guard !text.isEmpty else { return false }
        let config = WKFindConfiguration()
        config.backwards = backwards
        config.caseSensitive = false
        config.wraps = true
        return await withCheckedContinuation { continuation in
            webView.find(text, configuration: config) { result in
                continuation.resume(returning: result.matchFound)
            }
        }
    }

    /// How many times `text` appears in the page's visible text. WebKit's
    /// find API only says whether there's a match, and "3 of 12" is what makes
    /// a find bar useful.
    func countMatches(_ text: String) async -> Int {
        guard !text.isEmpty,
              let data = try? JSONEncoder().encode(text),
              let literal = String(data: data, encoding: .utf8) else { return 0 }
        let script = """
        (() => { const n = \(literal).toLowerCase();
          const t = (document.body ? document.body.innerText : '').toLowerCase();
          if (!n) return 0; let c = 0, i = 0;
          while ((i = t.indexOf(n, i)) !== -1) { c++; i += n.length; }
          return c; })()
        """
        return (try? await webView.evaluateJavaScript(script) as? Int) ?? 0
    }

    // MARK: Print (⌘P)

    func printPage() {
        guard let window = webView.window else { return }
        let info = NSPrintInfo.shared.copy() as! NSPrintInfo
        info.horizontalPagination = .fit
        info.isHorizontallyCentered = false
        info.isVerticallyCentered = false
        let operation = webView.printOperation(with: info)
        operation.showsPrintPanel = true
        operation.showsProgressPanel = true
        // Without a frame WebKit's print view lays out at zero size and the
        // page prints blank.
        operation.view?.frame = webView.bounds
        operation.runModal(for: window, delegate: nil, didRun: nil, contextInfo: nil)
    }

    // MARK: Text size (⌥⌘+, ⌥⌘−)

    /// Text-only zoom — bigger type, same layout and images — which `pageZoom`
    /// can't do. WebKit has it but doesn't declare it, so it's reached the way
    /// `WebKitTuning` reaches the canvas switch: confirm the selectors exist,
    /// then set through KVC (`textZoomFactor` resolves to `_setTextZoomFactor:`).
    /// Measured: setting 1.5 reads back as 1.5. On an OS that drops it, the
    /// menu items simply do nothing rather than crashing.
    static let supportsTextZoom: Bool = {
        WKWebView.instancesRespond(to: NSSelectorFromString("_setTextZoomFactor:"))
            && WKWebView.instancesRespond(to: NSSelectorFromString("_textZoomFactor"))
    }()

    static let textZoomSteps: [Double] = [0.75, 0.85, 1.0, 1.15, 1.3, 1.5, 1.75, 2.0]

    var textZoom: Double {
        guard Self.supportsTextZoom else { return 1 }
        return (webView.value(forKey: "textZoomFactor") as? Double) ?? 1
    }

    func makeTextBigger() {
        setTextZoom(Self.textZoomSteps.first { $0 > textZoom + 0.001 } ?? textZoom)
    }

    func makeTextSmaller() {
        setTextZoom(Self.textZoomSteps.last { $0 < textZoom - 0.001 } ?? textZoom)
    }

    private func setTextZoom(_ value: Double) {
        guard Self.supportsTextZoom else { return }
        webView.setValue(value, forKey: "textZoomFactor")
    }

    // MARK: Thumbnail (tab overview)

    /// Captures what the tab looks like, for the overview. Taken while the tab
    /// is still on screen — as it's switched away from, and when the overview
    /// opens — because a web view that isn't in a window can't be snapshotted.
    func captureThumbnail() {
        guard webView.window != nil, webView.bounds.width > 0 else { return }
        let config = WKSnapshotConfiguration()
        config.snapshotWidth = 520
        webView.takeSnapshot(with: config) { [weak self] image, _ in
            guard let image else { return }
            Task { @MainActor in self?.thumbnail = image }
        }
    }

    // MARK: Reader (⇧⌘R)

    /// Pulls the article out of the page: the element holding the most
    /// paragraph text, stripped of navigation, ads, forms and scripts.
    /// Deliberately small — a heuristic, not Readability — which is enough for
    /// articles and blog posts and says so (returns nil) on anything else.
    func readerArticle() async -> ReaderArticle? {
        let script = """
        (() => {
          const drop = 'script,style,noscript,iframe,form,nav,aside,footer,header,button,input,svg,[role=navigation],[aria-hidden=true],.ad,.ads,.advert,.share,.social,.comments,.related,.newsletter';
          const cands = Array.from(document.querySelectorAll('article,main,[role=main],section,div'));
          let best = null, bestScore = 0;
          for (const el of cands) {
            const ps = el.querySelectorAll(':scope > p, :scope > div > p, :scope > section > p');
            let s = 0; ps.forEach(p => { const n = p.innerText.trim().length; if (n > 40) s += n; });
            if (el.tagName === 'ARTICLE') s *= 1.3;
            if (s > bestScore) { bestScore = s; best = el; }
          }
          if (!best || bestScore < 600) return null;
          const clone = best.cloneNode(true);
          clone.querySelectorAll(drop).forEach(n => n.remove());
          clone.querySelectorAll('*').forEach(n => {
            for (const a of Array.from(n.attributes)) {
              if (!['href','src','alt','srcset'].includes(a.name)) n.removeAttribute(a.name);
            }
          });
          const h1 = document.querySelector('h1');
          const meta = n => (document.querySelector(`meta[name="${n}"],meta[property="${n}"]`) || {}).content;
          return JSON.stringify({
            title: (h1 && h1.innerText.trim()) || document.title,
            byline: meta('author') || meta('article:author') || '',
            site: meta('og:site_name') || location.hostname.replace(/^www\\./, ''),
            html: clone.innerHTML
          });
        })()
        """
        guard let json = try? await webView.evaluateJavaScript(script) as? String,
              let data = json.data(using: .utf8),
              var article = try? JSONDecoder().decode(ReaderArticle.self, from: data)
        else { return nil }
        article.baseURL = webView.url
        return article
    }
}

struct ReaderArticle: Codable, Equatable {
    var title: String
    var byline: String
    var site: String
    var html: String
    var baseURL: URL?

    enum CodingKeys: String, CodingKey { case title, byline, site, html }
}

// MARK: - Site icon

extension BrowserTab {
    /// Reads the icons the page declares and adopts the best one. Icons with
    /// a `media` query that doesn't match are dropped first: GitHub, among
    /// others, declares a white icon for dark mode and a black one for light,
    /// and taking the wrong one leaves the tab with an invisible icon. Run when a
    /// page finishes loading and whenever its title changes, debounced so a
    /// burst of title updates costs one script call.
    func refreshIcon() {
        guard webView.url?.scheme?.hasPrefix("http") == true else { return }
        scheduleIconRead { [weak self] in
            guard let self else { return }
            let probe = """
            JSON.stringify(Array.from(document.querySelectorAll(
              "link[rel~='icon' i], link[rel='apple-touch-icon' i], link[rel='apple-touch-icon-precomposed' i]"
            )).filter(l => !l.media || matchMedia(l.media).matches
            ).slice(0, 12).map(l => ({ href: l.href || '', rel: l.rel || '',
                                       sizes: l.getAttribute('sizes') || '', type: l.type || '' })))
            """
            guard let json = try? await self.webView.evaluateJavaScript(probe) as? String,
                  let data = json.data(using: .utf8),
                  let raw = try? JSONSerialization.jsonObject(with: data) as? [[String: String]]
            else { return }
            let links = raw.map {
                FaviconChoice.Link(href: $0["href"] ?? "", rel: $0["rel"] ?? "",
                                   sizes: $0["sizes"] ?? "", type: $0["type"] ?? "")
            }
            guard let chosen = FaviconChoice.best(links) else { return }
            if chosen != self.iconURL { self.iconURL = chosen }
            FaviconStore.shared.load(iconURL: chosen)
        }
    }
}
