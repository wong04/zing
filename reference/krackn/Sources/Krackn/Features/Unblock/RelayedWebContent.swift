import Foundation
import SwiftUI
import WebKit
import UniformTypeIdentifiers

/// Maps real URLs onto a private scheme that WKWebView will hand to us.
///
/// WebKit refuses to let a scheme handler intercept `http` or `https` — those
/// are reserved. So the page is served under our own scheme instead, keeping
/// the host and path intact:
///
///     https://example.com/a/style.css  <->  aiorelay://example.com/a/style.css
///
/// Preserving the structure is what makes this work. Because the *document
/// itself* is loaded from `aiorelay://`, every relative reference (`/w/load.php`)
/// and every protocol-relative one (`//upload.example.org/x.png`) resolves back
/// into our scheme automatically, with no HTML rewriting at all. Only absolute
/// `https://` links need patching.
enum RelayURL {
    static let scheme = "aiorelay"

    static func encode(_ url: URL) -> URL? {
        guard var components = URLComponents(url: url, resolvingAgainstBaseURL: false) else {
            return nil
        }
        components.scheme = scheme
        return components.url
    }

    static func decode(_ url: URL) -> URL? {
        guard url.scheme == scheme,
              var components = URLComponents(url: url, resolvingAgainstBaseURL: false)
        else { return nil }
        components.scheme = "https"
        return components.url
    }
}

/// Fetches every resource a page needs through the relay instead of letting
/// WebKit request them over the ordinary network, where the block still applies.
final class RelaySchemeHandler: NSObject, WKURLSchemeHandler {

    /// Lets the caller hand us the document it already downloaded, so opening a
    /// page doesn't fetch the main HTML a second time.
    private var seeded: [String: (data: Data, contentType: String)] = [:]
    /// Tasks WebKit has since cancelled. Calling back into a stopped task raises
    /// an Objective-C exception that takes the whole app down, so this is not
    /// optional bookkeeping.
    private var live = Set<ObjectIdentifier>()

    /// Seed with the original bytes and the server's own Content-Type. Passing
    /// a Swift String here instead would force UTF-8 and corrupt any page that
    /// declares a different charset.
    func seed(url: URL, body: Data, contentType: String?) {
        seeded[url.absoluteString] = (body, contentType ?? "text/html")
    }

    func webView(_ webView: WKWebView, start task: WKURLSchemeTask) {
        let id = ObjectIdentifier(task)
        live.insert(id)

        guard let requested = task.request.url,
              let original = RelayURL.decode(requested)
        else {
            finish(task, id: id, with: .failure(RelayError.badURL(
                task.request.url?.absoluteString ?? "?")))
            return
        }

        if let cached = seeded[original.absoluteString] {
            seeded.removeValue(forKey: original.absoluteString)
            let body = Self.patch(cached.data, contentType: cached.contentType)
            finish(task, id: id, with: .success((body, cached.contentType, original)))
            return
        }

        Task { [weak self] in
            let outcome = await Self.load(original)
            await MainActor.run {
                self?.finish(task, id: id, with: outcome)
            }
        }
    }

    func webView(_ webView: WKWebView, stop task: WKURLSchemeTask) {
        live.remove(ObjectIdentifier(task))
    }

    // MARK: - Fetching

    /// `contentType` is kept whole, charset and all — splitting it early was
    /// how the declared encoding used to get lost on the way to WebKit.
    private typealias Payload = (data: Data, contentType: String, url: URL)

    private static func load(_ url: URL) async -> Result<Payload, Error> {
        // The pinned client is the whole point: it resolves over DoH and dials
        // the real address, so a DNS-blocked host still answers.
        do {
            let (response, finalURL) = try await PinnedHTTPClient.fetch(url: url, timeout: 25)
            guard (200..<300).contains(response.statusCode) else {
                throw RelayError.httpStatus(response.statusCode)
            }
            let contentType = response.headers["content-type"] ?? guessMIME(for: url)
            return .success((patch(response.body, contentType: contentType), contentType, finalURL))
        } catch {
            // Some hosts reject our hand-rolled request but are reachable
            // normally — fall back rather than dropping the resource.
            do {
                var request = URLRequest(url: url)
                request.timeoutInterval = 20
                let (data, response) = try await URLSession.shared.data(for: request)
                var contentType = response.mimeType ?? guessMIME(for: url)
                if let encodingName = response.textEncodingName,
                   !contentType.lowercased().contains("charset") {
                    contentType += "; charset=\(encodingName)"
                }
                return .success((patch(data, contentType: contentType), contentType, url))
            } catch {
                return .failure(error)
            }
        }
    }

    private func finish(
        _ task: WKURLSchemeTask, id: ObjectIdentifier, with outcome: Result<Payload, Error>
    ) {
        // WebKit may have torn the task down while we were on the network.
        guard live.contains(id) else { return }
        live.remove(id)

        switch outcome {
        case .success(let payload):
            // URLResponse wants the bare type and the encoding separately.
            let mimeType = payload.contentType
                .split(separator: ";").first
                .map { String($0).trimmingCharacters(in: .whitespaces) }
                ?? "application/octet-stream"
            let response = URLResponse(
                url: RelayURL.encode(payload.url) ?? task.request.url!,
                mimeType: mimeType,
                expectedContentLength: payload.data.count,
                // Let the declared charset win; only assume UTF-8 when the
                // server said nothing about encoding.
                textEncodingName: Self.charset(from: payload.contentType)
            )
            task.didReceive(response)
            task.didReceive(payload.data)
            task.didFinish()
        case .failure(let error):
            task.didFailWithError(error)
        }
    }

    // MARK: - Rewriting

    /// Relative and protocol-relative URLs come along for free, but absolute
    /// `https://` references would escape back onto the blocked network — so
    /// those get pointed at our scheme too.
    ///
    /// Decoding must honour the page's own charset. Assuming UTF-8 here would
    /// re-corrupt exactly the Latin-1 and Windows-1252 pages that carrying raw
    /// bytes was meant to preserve. If the text can't be decoded or re-encoded
    /// losslessly we return the original bytes untouched — a few absolute URLs
    /// escaping the relay is a far smaller failure than a garbled page.
    private static func patch(_ data: Data, contentType: String) -> Data {
        let kind = contentType.lowercased()
        guard kind.contains("html") || kind.contains("css") else { return data }

        let encoding = stringEncoding(from: contentType)
        guard var text = String(data: data, encoding: encoding)
                ?? String(data: data, encoding: .utf8)
        else { return data }

        for pattern in [
            // Attributes carrying a single URL.
            #"((?:src|href|poster|data-src|srcset|action)\s*=\s*["'])https://"#,
            // CSS url(...) with or without quotes.
            #"(url\(\s*["']?)https://"#,
            // Anything left inside a srcset candidate list.
            #"(,\s*)https://"#,
        ] {
            guard let regex = try? NSRegularExpression(
                pattern: pattern, options: [.caseInsensitive]) else { continue }
            text = regex.stringByReplacingMatches(
                in: text,
                range: NSRange(text.startIndex..., in: text),
                withTemplate: "$1\(RelayURL.scheme)://"
            )
        }

        // A <base> tag would re-anchor every relative URL back at https and
        // undo the whole arrangement.
        if let baseTag = try? NSRegularExpression(
            pattern: #"<base[^>]*>"#, options: [.caseInsensitive]) {
            text = baseTag.stringByReplacingMatches(
                in: text, range: NSRange(text.startIndex..., in: text), withTemplate: "")
        }

        // Re-encode in the charset we read it as, so the bytes stay consistent
        // with what the response header claims.
        return text.data(using: encoding) ?? data
    }

    /// Pulls `charset=` out of a Content-Type, e.g. "text/html; charset=EUC-JP".
    /// Returning nil for non-text lets WebKit treat the payload as binary.
    private static func charset(from contentType: String) -> String? {
        if let range = contentType.range(of: "charset=", options: .caseInsensitive) {
            let raw = contentType[range.upperBound...]
                .trimmingCharacters(in: CharacterSet(charactersIn: " \"';"))
            if !raw.isEmpty { return raw }
        }
        return contentType.lowercased().hasPrefix("text/") ? "utf-8" : nil
    }

    /// Resolves a charset name to a Swift encoding via the IANA registry, which
    /// covers the long tail (EUC-JP, Shift_JIS, KOI8-R) that a hand-written
    /// switch would miss. Defaults to UTF-8 when unlabelled or unrecognised.
    private static func stringEncoding(from contentType: String) -> String.Encoding {
        guard let name = charset(from: contentType) else { return .utf8 }
        let cfEncoding = CFStringConvertIANACharSetNameToEncoding(name as CFString)
        guard cfEncoding != kCFStringEncodingInvalidId else { return .utf8 }
        return String.Encoding(rawValue: CFStringConvertEncodingToNSStringEncoding(cfEncoding))
    }

    private static func guessMIME(for url: URL) -> String {
        let ext = url.pathExtension.lowercased()
        if let type = UTType(filenameExtension: ext)?.preferredMIMEType {
            return type
        }
        switch ext {
        case "js", "mjs": return "text/javascript"
        case "css": return "text/css"
        case "woff2": return "font/woff2"
        case "": return "text/html"
        default: return "application/octet-stream"
        }
    }
}

/// Renders a relayed page. Every request the page makes is served through
/// `RelaySchemeHandler`, so styling, images, fonts and scripts all arrive over
/// the same path that got the HTML.
struct RelayWebView: NSViewRepresentable {
    let url: URL
    let seedBody: Data?
    let seedContentType: String?
    /// Fires when the page turns out to be an app that won't boot under our
    /// scheme, so the UI can explain that instead of showing a dead splash.
    var onAppLikeFailure: (() -> Void)?

    func makeCoordinator() -> Coordinator { Coordinator(onAppLikeFailure: onAppLikeFailure) }

    final class Coordinator: NSObject, WKNavigationDelegate {
        let handler = RelaySchemeHandler()
        var loaded: String?
        let onAppLikeFailure: (() -> Void)?

        init(onAppLikeFailure: (() -> Void)?) {
            self.onAppLikeFailure = onAppLikeFailure
        }

        func webView(_ webView: WKWebView, didFinish navigation: WKNavigation!) {
            // Give the page a fair chance to render before judging it.
            DispatchQueue.main.asyncAfter(deadline: .now() + 5) { [weak self, weak webView] in
                guard let self, let webView else { return }
                let probe = """
                JSON.stringify({
                  text: (document.body ? (document.body.innerText || '').trim().length : 0),
                  scripts: document.scripts.length
                })
                """
                webView.evaluateJavaScript(probe) { result, _ in
                    guard let json = (result as? String)?.data(using: .utf8),
                          let parsed = try? JSONSerialization.jsonObject(with: json)
                            as? [String: Int],
                          let text = parsed["text"], let scripts = parsed["scripts"]
                    else { return }
                    // Plenty of script, no visible text: a client-rendered app
                    // that died on a browser API our scheme can't provide.
                    if text < 200 && scripts > 20 {
                        self.onAppLikeFailure?()
                    }
                }
            }
        }
    }

    func makeNSView(context: Context) -> WKWebView {
        let configuration = WKWebViewConfiguration()
        // Scheme handlers must be registered before the web view exists.
        configuration.setURLSchemeHandler(context.coordinator.handler,
                                          forURLScheme: RelayURL.scheme)
        // Most modern pages render client-side; with this off they look broken
        // even when every byte arrived intact.
        configuration.defaultWebpagePreferences.allowsContentJavaScript = true

        let view = WKWebView(frame: .zero, configuration: configuration)
        view.navigationDelegate = context.coordinator
        return view
    }

    func updateNSView(_ view: WKWebView, context: Context) {
        guard context.coordinator.loaded != url.absoluteString,
              let relayed = RelayURL.encode(url)
        else { return }
        context.coordinator.loaded = url.absoluteString

        if let seedBody {
            context.coordinator.handler.seed(
                url: url, body: seedBody, contentType: seedContentType)
        }
        view.load(URLRequest(url: relayed))
    }
}
