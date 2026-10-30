import Foundation
import os

/// Rendering diagnostics for pages that paint into a canvas.
///
/// Goes to the unified log rather than a file. The log is memory-resident and
/// disappears with the session, so this doesn't undo the browser's promise that
/// nothing about your browsing is written to disk — a debug file listing the
/// sites you visited would be exactly the browsing history this app refuses to
/// keep.
///
/// Read it with:
///
///     log show --last 10m --predicate 'subsystem == "com.krackn.app"' --info
///
enum BrowserDiagnostics {
    private static let logger = Logger(subsystem: "com.krackn.app", category: "rendering")

    static func log(_ message: String) {
        // `notice`, not `info`. Info-level messages are memory-only and are
        // dropped unless something is streaming the log at that moment, so a
        // diagnostic written at `.info` is invisible to `log show` after the
        // fact — which is exactly how you need to read it.
        //
        // `.public` because the whole point is to be readable; the payload is
        // geometry, not page content.
        logger.notice("\(message, privacy: .public)")
    }
}
