import SwiftUI
import AppKit

/// All graph interaction in one AppKit view.
///
/// The previous version layered a SwiftUI `DragGesture` over an NSView that
/// returned `nil` from `hitTest` — which made it transparent to *every* event,
/// scroll included, so trackpad zoom silently never fired. Handling pan, scroll,
/// pinch and hover in a single responder is the only way to get all four
/// without them fighting each other.
struct GraphSurface: NSViewRepresentable {
    @Binding var viewport: GraphEngine.Viewport
    /// Cursor position in graph coordinates, or nil when the pointer leaves.
    var onHover: ((CGPoint?) -> Void)?
    /// Reports a click so the caller can select the nearest point of interest.
    var onClick: ((CGPoint) -> Void)?

    final class Surface: NSView {
        var viewport: GraphEngine.Viewport = .default
        var onViewportChange: ((GraphEngine.Viewport) -> Void)?
        var onHover: ((CGPoint?) -> Void)?
        var onClick: ((CGPoint) -> Void)?

        private var tracking: NSTrackingArea?
        private var dragOrigin: (point: NSPoint, viewport: GraphEngine.Viewport)?
        private var didDrag = false

        override var acceptsFirstResponder: Bool { true }
        override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

        override func updateTrackingAreas() {
            super.updateTrackingAreas()
            if let tracking { removeTrackingArea(tracking) }
            let area = NSTrackingArea(
                rect: bounds,
                options: [.mouseEnteredAndExited, .mouseMoved, .activeInKeyWindow, .inVisibleRect],
                owner: self)
            addTrackingArea(area)
            tracking = area
        }

        /// AppKit's origin is bottom-left; the graph maths uses top-left.
        private func flipped(_ point: NSPoint) -> CGPoint {
            CGPoint(x: point.x, y: bounds.height - point.y)
        }

        private func graphPoint(_ point: NSPoint) -> CGPoint {
            let p = flipped(point)
            return CGPoint(
                x: GraphEngine.graphX(p.x, viewport: viewport, size: bounds.size),
                y: GraphEngine.graphY(p.y, viewport: viewport, size: bounds.size))
        }

        // MARK: Pan

        override func mouseDown(with event: NSEvent) {
            dragOrigin = (convert(event.locationInWindow, from: nil), viewport)
            didDrag = false
        }

        override func mouseDragged(with event: NSEvent) {
            guard let origin = dragOrigin else { return }
            let now = convert(event.locationInWindow, from: nil)
            let dxPixels = now.x - origin.point.x
            let dyPixels = now.y - origin.point.y
            guard abs(dxPixels) + abs(dyPixels) > 1 else { return }
            didDrag = true

            // Drag right → content follows the cursor, so the window moves left.
            let dx = -Double(dxPixels) / Double(bounds.width) * origin.viewport.width
            let dy = -Double(dyPixels) / Double(bounds.height) * origin.viewport.height
            onViewportChange?(origin.viewport.panned(dx: dx, dy: dy))
        }

        override func mouseUp(with event: NSEvent) {
            if !didDrag {
                onClick?(graphPoint(convert(event.locationInWindow, from: nil)))
            }
            dragOrigin = nil
            didDrag = false
        }

        // MARK: Scroll and pinch

        override func scrollWheel(with event: NSEvent) {
            let anchorPoint = convert(event.locationInWindow, from: nil)
            let anchor = graphPoint(anchorPoint)

            // A trackpad reports precise deltas; a mouse wheel reports notches.
            // Two-finger scroll pans (the Mac-native behaviour), while a wheel —
            // or ⌘-scroll — zooms, which is what wheel users expect.
            let wantsZoom = !event.hasPreciseScrollingDeltas
                || event.modifierFlags.contains(.command)

            if wantsZoom {
                let magnitude = event.hasPreciseScrollingDeltas
                    ? event.scrollingDeltaY / 100
                    : event.scrollingDeltaY / 3
                let factor = pow(0.92, magnitude)
                onViewportChange?(viewport.zoomed(by: factor,
                                                  around: (x: anchor.x, y: anchor.y)))
            } else {
                let dx = -Double(event.scrollingDeltaX) / Double(bounds.width) * viewport.width
                // Natural scrolling already inverts vertical delta for content,
                // and graph y runs opposite to screen y — so this is *not* negated.
                let dy = Double(event.scrollingDeltaY) / Double(bounds.height) * viewport.height
                onViewportChange?(viewport.panned(dx: dx, dy: dy))
            }
        }

        override func magnify(with event: NSEvent) {
            let anchor = graphPoint(convert(event.locationInWindow, from: nil))
            // magnification is a delta: positive spreads fingers apart.
            let factor = 1 / (1 + event.magnification)
            onViewportChange?(viewport.zoomed(by: factor,
                                              around: (x: anchor.x, y: anchor.y)))
        }

        // MARK: Hover

        override func mouseMoved(with event: NSEvent) {
            onHover?(graphPoint(convert(event.locationInWindow, from: nil)))
        }

        override func mouseExited(with event: NSEvent) {
            onHover?(nil)
        }

        // MARK: Keyboard

        override func keyDown(with event: NSEvent) {
            let nudge = 0.08
            switch event.charactersIgnoringModifiers {
            case "+", "=":
                onViewportChange?(viewport.zoomed(
                    by: 0.8, around: (x: (viewport.xMin + viewport.xMax) / 2,
                                      y: (viewport.yMin + viewport.yMax) / 2)))
            case "-", "_":
                onViewportChange?(viewport.zoomed(
                    by: 1.25, around: (x: (viewport.xMin + viewport.xMax) / 2,
                                       y: (viewport.yMin + viewport.yMax) / 2)))
            default:
                switch event.keyCode {
                case 123: onViewportChange?(viewport.panned(dx: -viewport.width * nudge, dy: 0))
                case 124: onViewportChange?(viewport.panned(dx: viewport.width * nudge, dy: 0))
                case 125: onViewportChange?(viewport.panned(dx: 0, dy: -viewport.height * nudge))
                case 126: onViewportChange?(viewport.panned(dx: 0, dy: viewport.height * nudge))
                default: super.keyDown(with: event)
                }
            }
        }
    }

    func makeNSView(context: Context) -> Surface {
        let view = Surface()
        view.viewport = viewport
        view.onViewportChange = { viewport = $0 }
        view.onHover = onHover
        view.onClick = onClick
        return view
    }

    func updateNSView(_ view: Surface, context: Context) {
        view.viewport = viewport
        view.onViewportChange = { viewport = $0 }
        view.onHover = onHover
        view.onClick = onClick
    }
}
