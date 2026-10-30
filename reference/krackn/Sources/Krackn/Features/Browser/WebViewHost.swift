import SwiftUI
import WebKit

/// Hosts a tab's web view. Each tab keeps its own `WKWebView` alive, so this
/// only ever swaps which one is on screen — recreating them would throw away
/// scroll position, form contents and history on every tab switch.
struct WebViewHost: NSViewRepresentable {
    let webView: WKWebView
    /// True while something SwiftUI draws over the page should get the
    /// clicks — the tab dropdown. A web view is a real AppKit subview, so it
    /// wins hit-testing over any SwiftUI overlay above it: measured, a click
    /// or right-click on the dropdown's rows went to the page underneath.
    var shielded = false

    /// Keeps the web view snapped to whole device pixels.
    ///
    /// SwiftUI sizes this container from a stack whose height comes partly from
    /// text metrics, so its bounds can land on a fractional point — and a web
    /// view laid out across half a device pixel has its whole composited layer
    /// resampled on the way to the screen. That softens everything, and it
    /// shows up worst on thin body text while bold UI chrome survives it, which
    /// is the shape of the Google Docs complaint.
    ///
    /// Autoresizing can't do this: it propagates the fractional value straight
    /// through. Laying out manually and rounding to the backing grid is the
    /// only way to guarantee alignment across every resize.
    final class Container: NSView {
        /// The web view this container should be showing.
        ///
        /// Two containers can want the same web view at once: rebuilding the
        /// view tree (an accent change) makes a new container while the old
        /// one is still being torn down, and the old one could take the web
        /// view back last and carry it out of the window — measured: a blank
        /// page, with the web view inside a detached container. So a container
        /// only holds the web view while it's in a window, gives it up when it
        /// leaves, and says so, and whichever container is on screen takes it.
        weak var wanted: WKWebView? {
            didSet {
                guard wanted !== oldValue else { return }
                watchFullscreen()
                adopt()
            }
        }
        private static let released = Notification.Name("KracknWebViewReleased")
        private var observer: NSObjectProtocol?
        private var fullscreenWatch: NSKeyValueObservation?

        override init(frame: NSRect) {
            super.init(frame: frame)
            observer = NotificationCenter.default.addObserver(
                forName: Self.released, object: nil, queue: .main
            ) { [weak self] note in
                MainActor.assumeIsolated {
                    guard let self, note.object as AnyObject? === self.wanted else { return }
                    self.adopt()
                }
            }
        }

        required init?(coder: NSCoder) { fatalError("not used") }

        deinit {
            if let observer { NotificationCenter.default.removeObserver(observer) }
        }

        override func viewDidMoveToWindow() {
            super.viewDidMoveToWindow()
            if window != nil { adopt() } else { release() }
        }

        /// A page in fullscreen (a YouTube video, a slideshow) has its web
        /// view moved into WebKit's own fullscreen window, leaving a
        /// placeholder here. Reclaiming it then — any redraw would — pulls the
        /// page out of fullscreen mid-video. So the container keeps its hands
        /// off until WebKit is done, then takes the web view back, or hands it
        /// on if this container left the screen in the meantime.
        private func watchFullscreen() {
            fullscreenWatch = wanted?.observe(\.fullscreenState, options: [.new]) { [weak self] web, _ in
                guard web.fullscreenState == .notInFullscreen else { return }
                DispatchQueue.main.async {
                    MainActor.assumeIsolated {
                        guard let self else { return }
                        if self.window != nil { self.adopt() } else { self.release() }
                    }
                }
            }
        }

        /// Shows `wanted`, if this container is on screen.
        func adopt() {
            guard window != nil, let web = wanted, web.superview !== self,
                  web.fullscreenState == .notInFullscreen else { return }
            for view in subviews { detach(view) }
            // No autoresizing mask: `layout()` owns the frame so it can round
            // it. An autoresizing web view would drift back off the pixel grid
            // on the next resize.
            web.autoresizingMask = []
            addSubview(web)
            if shielded { shield(web) }
            needsLayout = true
        }

        private func release() {
            guard let web = wanted, web.superview === self else { return }
            detach(web)
            NotificationCenter.default.post(name: Self.released, object: web)
        }

        /// Takes a view out, leaving it as it was found.
        private func detach(_ view: NSView) {
            if shielded { unshield(view) }
            view.removeFromSuperview()
        }

        /// Clicks *and* drags pass over the page. Drags need their own
        /// handling: AppKit can pick a drag's destination by registered types,
        /// and the web view registers for files — so while shielded it's taken
        /// off that list (and put back exactly as it was afterwards), or a file
        /// held over the tab dropdown would go to the page underneath.
        var shielded = false {
            didSet {
                guard shielded != oldValue, let web = subviews.first else { return }
                if shielded { shield(web) } else { unshield(web) }
            }
        }
        private var savedDragTypes: [NSPasteboard.PasteboardType] = []

        private func shield(_ web: NSView) {
            guard savedDragTypes.isEmpty else { return }
            savedDragTypes = web.registeredDraggedTypes
            web.unregisterDraggedTypes()
        }

        private func unshield(_ web: NSView) {
            guard !savedDragTypes.isEmpty else { return }
            web.registerForDraggedTypes(savedDragTypes)
            savedDragTypes = []
        }

        override func hitTest(_ point: NSPoint) -> NSView? {
            shielded ? nil : super.hitTest(point)
        }

        override func layout() {
            super.layout()
            guard let web = subviews.first else { return }
            let aligned = backingAlignedRect(bounds, options: .alignAllEdgesNearest)
            if web.frame != aligned { web.frame = aligned }
        }

        override func viewDidChangeBackingProperties() {
            super.viewDidChangeBackingProperties()
            // The backing grid itself changed — moving between a Retina and a
            // non-Retina display — so the alignment has to be recomputed.
            needsLayout = true
        }
    }

    func makeNSView(context: Context) -> NSView {
        let container = Container()
        container.autoresizingMask = [.width, .height]
        return container
    }

    func updateNSView(_ container: NSView, context: Context) {
        guard let container = container as? Container else { return }
        container.shielded = shielded
        container.wanted = webView
        container.adopt()
        container.needsLayout = true
    }
}
