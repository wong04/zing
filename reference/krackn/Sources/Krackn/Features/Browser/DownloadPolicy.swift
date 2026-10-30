import Foundation

/// Whether a response should be saved as a file rather than shown.
///
/// Missing entirely until now, which is why nothing downloaded: a web view
/// only downloads when the app answers "download" for a response, and without
/// that answer WebKit tries to display everything. A .zip or an installer it
/// can't display just failed silently ("frame load interrupted").
enum DownloadPolicy {
    /// - Parameters:
    ///   - canShow: WebKit's own `canShowMIMEType` for the response.
    ///   - contentDisposition: the response's `Content-Disposition` header.
    ///   - isMainFrame: whether this is the page itself, not an embedded frame.
    static func shouldDownload(canShow: Bool, contentDisposition: String?,
                               isMainFrame: Bool) -> Bool {
        // The server saying "this is a file" — how most download buttons work,
        // often from a hidden frame, so frames count here too.
        if let disposition = contentDisposition?
            .trimmingCharacters(in: .whitespaces).lowercased(),
           disposition.hasPrefix("attachment") {
            return true
        }
        // Something the browser can't display, opened as a page: the only
        // useful thing to do with it is save it. Not for embedded frames,
        // where an odd content type from an ad shouldn't drop a file in
        // Downloads.
        return isMainFrame && !canShow
    }
}
