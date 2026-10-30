import AppKit
import WebKit

let app = NSApplication.shared
app.setActivationPolicy(.accessory)

var pass = 0, fail = 0
func check(_ n: String, _ c: Bool, _ d: String = "") { if c { pass += 1; print("   ok   \(n)") } else { fail += 1; print("   FAIL \(n) \(d)") } }

// Each case seeds bytes in a specific charset and checks what WebKit renders.
struct Case { let name: String; let seedURL: String; let loadURL: String; let enc: String.Encoding; let charset: String }
let text = "café naïve £ ÿ Ünter"
let cases = [
  Case(name: "latin-1, no trailing slash", seedURL: "https://enc1.test", loadURL: "https://enc1.test", enc: .isoLatin1, charset: "iso-8859-1"),
  Case(name: "latin-1, trailing slash",    seedURL: "https://enc2.test/", loadURL: "https://enc2.test/", enc: .isoLatin1, charset: "iso-8859-1"),
  Case(name: "windows-1252",               seedURL: "https://enc3.test/", loadURL: "https://enc3.test/", enc: .windowsCP1252, charset: "windows-1252"),
  Case(name: "utf-8",                      seedURL: "https://enc4.test/", loadURL: "https://enc4.test/", enc: .utf8, charset: "utf-8"),
]

final class D: NSObject, WKNavigationDelegate {
    var idx = 0
    var view: WKWebView!
    var handler = RelaySchemeHandler()

    func next() {
        guard idx < cases.count else {
            print("\n\(pass) passed, \(fail) failed")
            exit(fail == 0 ? 0 : 1)
        }
        let c = cases[idx]
        let html = "<html><head><meta charset=\"\(c.charset)\"></head><body><p id=t>\(text)</p></body></html>"
        guard let body = html.data(using: c.enc) else { print("   skip \(c.name) (cannot encode)"); idx += 1; next(); return }

        let cfg = WKWebViewConfiguration()
        handler = RelaySchemeHandler()
        handler.seed(url: URL(string: c.seedURL)!, body: body, contentType: "text/html; charset=\(c.charset)")
        cfg.setURLSchemeHandler(handler, forURLScheme: RelayURL.scheme)
        view = WKWebView(frame: NSRect(x: 0, y: 0, width: 600, height: 400), configuration: cfg)
        view.navigationDelegate = self
        view.load(URLRequest(url: RelayURL.encode(URL(string: c.loadURL)!)!))
    }

    func webView(_ w: WKWebView, didFinish n: WKNavigation!) {
        let c = cases[idx]
        w.evaluateJavaScript("document.getElementById('t') ? document.getElementById('t').innerText : '(no element)'") { r, _ in
            let got = (r as? String) ?? "(nil)"
            check("\(c.name) renders correctly", got == text, "expected '\(text)' got '\(got)'")
            self.idx += 1
            self.next()
        }
    }
    func webView(_ w: WKWebView, didFailProvisionalNavigation n: WKNavigation!, withError e: Error) {
        check("\(cases[idx].name) loaded", false, "seed miss -> \(e.localizedDescription)")
        idx += 1; next()
    }
}
let d = D(); d.next()
DispatchQueue.main.asyncAfter(deadline: .now() + 60) { print("TIMEOUT"); exit(2) }
app.run()
