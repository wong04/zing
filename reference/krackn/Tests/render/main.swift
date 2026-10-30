import AppKit
import WebKit

let app = NSApplication.shared
app.setActivationPolicy(.accessory)

final class Driver: NSObject, WKNavigationDelegate {
    let handler = RelaySchemeHandler()
    var view: WKWebView!
    var targets: [String]
    var results: [String] = []

    init(targets: [String]) { self.targets = targets }

    func next() {
        guard !targets.isEmpty else {
            print("\n===== SUMMARY =====")
            results.forEach { print($0) }
            exit(0)
        }
        let site = targets.removeFirst()
        let cfg = WKWebViewConfiguration()
        cfg.setURLSchemeHandler(handler, forURLScheme: RelayURL.scheme)
        cfg.defaultWebpagePreferences.allowsContentJavaScript = true
        view = WKWebView(frame: NSRect(x: 0, y: 0, width: 1200, height: 900), configuration: cfg)
        view.navigationDelegate = self
        let real = URL(string: site)!
        print("\n--- loading \(site) ---")
        view.load(URLRequest(url: RelayURL.encode(real)!))

        // Don't wait forever if a page never settles.
        DispatchQueue.main.asyncAfter(deadline: .now() + 45) { [weak self] in
            guard let self, self.view != nil else { return }
            self.report(site, note: "(timeout)")
        }
    }

    func webView(_ w: WKWebView, didFinish n: WKNavigation!) { 
        // let subresources settle
        DispatchQueue.main.asyncAfter(deadline: .now() + 6) { self.report(w.url?.absoluteString ?? "?") }
    }
    func webView(_ w: WKWebView, didFail n: WKNavigation!, withError e: Error) {
        results.append("FAIL \(e.localizedDescription)"); view = nil; next()
    }
    func webView(_ w: WKWebView, didFailProvisionalNavigation n: WKNavigation!, withError e: Error) {
        results.append("FAIL provisional \(e.localizedDescription)"); view = nil; next()
    }

    func report(_ site: String, note: String = "") {
        guard let v = view else { return }
        let js = """
        (function(){
          var imgs = Array.from(document.images);
          var ok = imgs.filter(function(i){return i.complete && i.naturalWidth>0;}).length;
          var sheets = 0, rules = 0;
          for (var s of document.styleSheets) { sheets++; try { rules += s.cssRules.length; } catch(e){} }
          var bg = getComputedStyle(document.body).backgroundColor;
          return JSON.stringify({
            title: document.title,
            textLen: (document.body.innerText||'').length,
            images: imgs.length, imagesLoaded: ok,
            styleSheets: sheets, cssRules: rules,
            bodyBg: bg,
            relayRefs: document.documentElement.innerHTML.split('aiorelay://').length - 1,
            httpsRefs: document.documentElement.innerHTML.split('https://').length - 1
          });
        })()
        """
        v.evaluateJavaScript(js) { res, err in
            let line = "\(site) \(note)\n    \(res as? String ?? "ERR \(String(describing: err))")"
            print(line); self.results.append(line)
            self.view = nil
            self.next()
        }
    }
}

let d = Driver(targets: ["https://news.ycombinator.com", "https://en.wikipedia.org/wiki/Wi-Fi"])
d.next()
app.run()
