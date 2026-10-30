import AppKit
import WebKit
import SwiftUI
func out(_ t: String) { FileHandle.standardOutput.write((t+"\n").data(using:.utf8)!) }
let app = NSApplication.shared
app.setActivationPolicy(.accessory)
var pass = 0, fail = 0
func check(_ n: String, _ c: Bool, _ d: String = "") {
    if c { pass += 1; out("  ok   \(n) \(d)") } else { fail += 1; out("  FAIL \(n) \(d)") }
}

DispatchQueue.main.async { MainActor.assumeIsolated {
    let model = BrowserModel()               // starts with 1 tab
    model.newTab(url: "https://example.com")
    model.newTab(url: "https://news.ycombinator.com")
    let ids = model.tabs.map(\.id)
    check("three tabs created", model.tabs.count == 3, "count=\(model.tabs.count)")

    // Attach tab 1's web view to a container, as WebViewHost does.
    let containerA = NSView(frame: NSRect(x: 0, y: 0, width: 800, height: 600))
    let win = NSWindow(contentRect: containerA.frame, styleMask: [.titled],
                       backing: .buffered, defer: false)
    win.contentView = containerA
    win.makeKeyAndOrderFront(nil)
    let tab = model.tabs[1]
    tab.webView.frame = containerA.bounds
    containerA.addSubview(tab.webView)

    // Let the page load, then simulate leaving the Browser section entirely.
    DispatchQueue.main.asyncAfter(deadline: .now() + 6) { MainActor.assumeIsolated {
        let urlBefore = tab.webView.url?.absoluteString ?? ""
        let titleBefore = tab.title
        out("before navigating away: url=\(urlBefore) title='\(titleBefore)'")
        check("page loaded", urlBefore.contains("example.com"), urlBefore)

        // Detach — this is what happens when you switch to another section.
        tab.webView.removeFromSuperview()
        check("web view detached", tab.webView.superview == nil)
        check("model still holds tabs", model.tabs.count == 3)
        check("tab identities unchanged", model.tabs.map(\.id) == ids)

        // Re-attach to a *different* container, as coming back does.
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.5) { MainActor.assumeIsolated {
            let containerB = NSView(frame: containerA.bounds)
            win.contentView = containerB
            tab.webView.frame = containerB.bounds
            containerB.addSubview(tab.webView)

            DispatchQueue.main.asyncAfter(deadline: .now() + 2) { MainActor.assumeIsolated {
                let urlAfter = tab.webView.url?.absoluteString ?? ""
                out("after re-attaching:    url=\(urlAfter) title='\(tab.title)'")
                check("same page still loaded", urlAfter == urlBefore, urlAfter)
                check("title preserved", tab.title == titleBefore, "'\(tab.title)'")
                check("no reload occurred", !tab.isLoading)

                // And the DOM is genuinely alive, not a blank re-created view.
                tab.webView.evaluateJavaScript("document.body.innerText.length") { r, _ in
                    let len = (r as? Int) ?? 0
                    check("DOM intact after re-attach", len > 50, "\(len) chars")
                    out(""); out("\(pass) passed, \(fail) failed")
                    exit(fail == 0 ? 0 : 1)
                }
            } }
        } }
    } }
} }
DispatchQueue.main.asyncAfter(deadline: .now() + 60) { out("TIMEOUT"); exit(2) }
app.run()
