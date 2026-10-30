import Foundation
import WebKit

/// Rendering switches WebKit exposes but `WKPreferences` doesn't declare.
///
/// These reach internal preferences through the same mechanism everyone uses
/// for `developerExtrasEnabled` to open the inspector. Two rules make that
/// safe rather than reckless:
///
///   * Every call is guarded by `responds(to:)` on a real selector. An
///     unrecognised *KVC key* raises an Objective-C exception, which Swift
///     cannot catch and which would take the app down at launch; an
///     unrecognised *selector* is just a `false` from `responds(to:)`. So if a
///     future macOS drops one of these, the feature quietly turns itself off
///     instead of crashing.
///   * Nothing here changes behaviour the user hasn't asked for. Each switch
///     is a preference with a visible control and a stated cost.
enum WebKitTuning {

    /// Turns off GPU-accelerated 2D canvas drawing.
    ///
    /// This is the one mechanism that fits the Google Docs symptom. Docs paints
    /// the document into a `<canvas>` and draws its toolbar and caret as
    /// ordinary DOM. With acceleration on, WebKit rasterises the canvas into a
    /// GPU surface and composites that; if the surface is allocated below the
    /// display's scale, the glyphs are upscaled on the way to the screen while
    /// every DOM layer stays sharp — blurry text, crisp everything else.
    ///
    /// Crucially, JavaScript cannot see this. `canvas.width` keeps reporting
    /// the full 2x backing size it was given, which is why measuring the page
    /// from the outside said everything was fine while the screen disagreed.
    ///
    /// The cost is real: canvas-heavy pages (Figma, Desmos, browser games) draw
    /// on the CPU and get slower. That's why it's a setting.
    @discardableResult
    static func setAcceleratedCanvas(_ enabled: Bool, on preferences: WKPreferences) -> Bool {
        apply(enabled, selector: "_setCanvasUsesAcceleratedDrawing:",
              key: "canvasUsesAcceleratedDrawing", to: preferences)
    }

    /// The broader accelerated drawing path, for the same reason.
    @discardableResult
    static func setAcceleratedDrawing(_ enabled: Bool, on preferences: WKPreferences) -> Bool {
        apply(enabled, selector: "_setAcceleratedDrawingEnabled:",
              key: "acceleratedDrawingEnabled", to: preferences)
    }

    /// Whether the canvas switch is available on this OS at all, so the UI can
    /// say so rather than offering a control that does nothing.
    static var supportsCanvasSwitch: Bool {
        WKPreferences().responds(to: NSSelectorFromString("_setCanvasUsesAcceleratedDrawing:"))
    }

    /// Selector for the existence check, KVC to actually set the value.
    ///
    /// Both halves are needed, and using either alone is broken:
    ///
    ///   * `perform(_:with:)` cannot call a `BOOL` setter. It passes the
    ///     argument as an object pointer, and any non-nil pointer reads as
    ///     `true` — so `perform(sel, with: NSNumber(false))` sets it to *true*.
    ///     This silently did nothing until the value was read back.
    ///   * KVC sets it correctly, but raises `NSUnknownKeyException` on a key
    ///     WebKit no longer has, and Swift can't catch that.
    ///
    /// WebKit generates the accessor and the KVC key from the same preference
    /// definition, so the selector existing is a reliable proof that the key
    /// does too — which makes the pair safe.
    private static func apply(_ value: Bool, selector name: String,
                              key: String, to preferences: WKPreferences) -> Bool {
        guard preferences.responds(to: NSSelectorFromString(name)) else { return false }
        preferences.setValue(value, forKey: key)
        return true
    }
}
