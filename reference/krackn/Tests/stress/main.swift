import AppKit
import WebKit

let app = NSApplication.shared
app.setActivationPolicy(.accessory)

// Hammers the scheme handler with cancellations: start loading heavy pages,
// then yank them away mid-flight while dozens of subresource tasks are in
// the air. Any callback into a stopped WKURLSchemeTask throws an ObjC
// exception and kills the process, so surviving this IS the test.
final class Stress: NSObject, WKNavigationDelegate {
    var view: WKWebView!
    var handler = RelaySchemeHandler()
    var iteration = 0
    let maxIterations = 24
    let targets = [
        "https://en.wikipedia.org/wiki/Wi-Fi",
        "https://news.ycombinator.com",
        "https://example.com",
        "https://en.wikipedia.org/wiki/Computer_network",
    ]

    func start() {
        let cfg = WKWebViewConfiguration()
        cfg.setURLSchemeHandler(handler, forURLScheme: RelayURL.scheme)
        cfg.defaultWebpagePreferences.allowsContentJavaScript = true
        view = WKWebView(frame: NSRect(x: 0, y: 0, width: 1000, height: 700), configuration: cfg)
        view.navigationDelegate = self
        churn()
    }

    func churn() {
        iteration += 1
        if iteration > maxIterations {
            print("survived \(maxIterations) interrupted loads")
            // Now tear the whole web view down mid-flight, repeatedly.
            teardownTest(0)
            return
        }
        let t = targets[iteration % targets.count]
        view.load(URLRequest(url: RelayURL.encode(URL(string: t)!)!))
        // Interrupt at an unpredictable point in the load.
        let delay = Double.random(in: 0.05...1.6)
        DispatchQueue.main.asyncAfter(deadline: .now() + delay) { [weak self] in
            guard let self else { return }
            if self.iteration % 4 == 0 { self.view.stopLoading() }
            self.churn()
        }
    }

    func teardownTest(_ n: Int) {
        if n >= 12 {
            print("survived 12 mid-flight web view deallocations")
            print("STRESS OK")
            exit(0)
        }
        let cfg = WKWebViewConfiguration()
        let h = RelaySchemeHandler()
        cfg.setURLSchemeHandler(h, forURLScheme: RelayURL.scheme)
        var v: WKWebView? = WKWebView(frame: .zero, configuration: cfg)
        v?.load(URLRequest(url: RelayURL.encode(URL(string: targets[n % targets.count])!)!))
        DispatchQueue.main.asyncAfter(deadline: .now() + Double.random(in: 0.05...0.7)) {
            v = nil   // deallocate while requests are still outstanding
            _ = v
            self.teardownTest(n + 1)
        }
    }
}

let s = Stress()
s.start()
DispatchQueue.main.asyncAfter(deadline: .now() + 120) {
    print("STRESS TIMEOUT"); exit(2)
}
app.run()
