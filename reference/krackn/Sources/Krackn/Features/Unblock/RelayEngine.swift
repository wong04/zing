import Foundation

enum RelayError: LocalizedError {
    case badURL(String)
    case nxdomain(String)
    case resolverUnreachable(String)
    case resolverError(Int)
    case allStrategiesFailed([String])
    case httpStatus(Int)
    case emptyBody

    var errorDescription: String? {
        switch self {
        case .badURL(let s): return "Not a usable URL: \(s)"
        case .nxdomain(let h): return "\(h) doesn't exist upstream either — it's not your network blocking it."
        case .resolverUnreachable(let p): return "Couldn't reach the \(p) DoH resolver."
        case .resolverError(let code): return "DNS resolver returned status \(code)."
        case .allStrategiesFailed(let notes):
            return "Every method failed.\n\n" + notes.joined(separator: "\n")
        case .httpStatus(let code): return "Server returned HTTP \(code)."
        case .emptyBody: return "The response came back empty."
        }
    }
}

/// How the content was obtained. Ordered roughly by fidelity — a direct fetch
/// gives you the real page, a reader relay gives you the text and nothing else.
enum RelayStrategy: String, CaseIterable, Identifiable {
    case direct = "Direct"
    case dohPinned = "DoH + IP pinning"
    case readerRelay = "Reader relay"
    case wayback = "Wayback archive"

    var id: String { rawValue }

    var explanation: String {
        switch self {
        case .direct:
            return "Ordinary fetch. If this works the site was never blocked."
        case .dohPinned:
            return "Resolves the name over HTTPS, then connects straight to the IP while still validating the certificate against the real hostname. Defeats DNS-level blocking."
        case .readerRelay:
            return "A third-party server fetches the page and returns the text. Your network only ever sees a connection to the relay."
        case .wayback:
            return "Pulls an archived snapshot from the Internet Archive. Works even if the site is down, but the content may be old."
        }
    }

    /// Reader and archive strategies return text, not a live document — the UI
    /// renders these differently and warns that scripts/logins won't work.
    var returnsLiveDocument: Bool {
        self == .direct || self == .dohPinned
    }
}

struct RelayResult {
    let strategy: RelayStrategy
    /// Best-effort text, for display and copying. Lossy for pages that aren't
    /// UTF-8 — use `rawBody` whenever the bytes themselves matter.
    let content: String
    /// The bytes exactly as the server sent them. Decoding HTML through a
    /// Swift String assumes UTF-8, which turns every accented character in a
    /// Latin-1 or Windows-1252 page into a replacement character. Handing the
    /// original bytes plus the declared content type to WebKit lets it apply
    /// the charset the page actually declares.
    let rawBody: Data
    /// Content-Type as sent, e.g. "text/html; charset=iso-8859-1".
    let contentType: String?
    let isHTML: Bool
    let finalURL: URL
    /// What we tried before this worked, for the "how did we get here" disclosure.
    let attemptLog: [String]
}

actor RelayEngine {

    /// Generous on purpose. A degraded origin behind a CDN can take ~20s to
    /// answer, and cutting it off early reports a slow server as a blocked one —
    /// which sends you chasing a network problem that isn't there.
    private let timeout: TimeInterval = 35

    /// Tries each enabled strategy in order and returns the first that produces
    /// a body. `preferred` is tried first when supplied.
    func fetch(
        urlString rawInput: String,
        enabled: Set<RelayStrategy>,
        preferred: RelayStrategy? = nil,
        progress: (@Sendable (String) -> Void)? = nil
    ) async throws -> RelayResult {

        let url = try Self.normalize(rawInput)
        guard let host = url.host else { throw RelayError.badURL(rawInput) }

        var order = RelayStrategy.allCases.filter { enabled.contains($0) }
        if let preferred, let idx = order.firstIndex(of: preferred) {
            order.remove(at: idx)
            order.insert(preferred, at: 0)
        }

        var log: [String] = []

        for strategy in order {
            // Trying four strategies at 35s each means a doomed fetch could
            // otherwise hold the UI for two minutes with no way out.
            try Task.checkCancellation()
            progress?("Trying \(strategy.rawValue)…")
            do {
                let result: RelayResult
                switch strategy {
                case .direct:
                    result = try await fetchDirect(url: url, log: log)
                case .dohPinned:
                    result = try await fetchDoHPinned(url: url, host: host, log: log)
                case .readerRelay:
                    result = try await fetchReaderRelay(url: url, log: log)
                case .wayback:
                    result = try await fetchWayback(url: url, log: log)
                }
                return result
            } catch is CancellationError {
                // The user asked us to stop — don't fall through to the next
                // strategy, which would make Cancel look like it did nothing.
                throw CancellationError()
            } catch {
                let note = "✗ \(strategy.rawValue): \(error.localizedDescription)"
                log.append(note)
                progress?(note)
                continue
            }
        }

        throw RelayError.allStrategiesFailed(log)
    }

    // MARK: - Strategies

    private func fetchDirect(url: URL, log: [String]) async throws -> RelayResult {
        var request = URLRequest(url: url)
        request.timeoutInterval = timeout
        request.setValue(Self.userAgent, forHTTPHeaderField: "User-Agent")

        let (data, response) = try await URLSession.shared.data(for: request)
        try Self.validate(response)
        guard !data.isEmpty else { throw RelayError.emptyBody }

        return RelayResult(
            strategy: .direct,
            content: String(decoding: data, as: UTF8.self),
            rawBody: data,
            contentType: (response as? HTTPURLResponse)?
                .value(forHTTPHeaderField: "Content-Type"),
            isHTML: true,
            finalURL: (response as? HTTPURLResponse)?.url ?? url,
            attemptLog: log
        )
    }

    private func fetchDoHPinned(url: URL, host: String, log: [String]) async throws -> RelayResult {
        // Handled by PinnedHTTPClient rather than URLSession — see that file for
        // why URLSession fundamentally cannot perform this fetch.
        let (response, finalURL) = try await PinnedHTTPClient.fetch(url: url, timeout: timeout)

        guard (200..<300).contains(response.statusCode) else {
            throw RelayError.httpStatus(response.statusCode)
        }
        guard !response.body.isEmpty else { throw RelayError.emptyBody }

        return RelayResult(
            strategy: .dohPinned,
            content: String(decoding: response.body, as: UTF8.self),
            rawBody: response.body,
            contentType: response.headers["content-type"],
            isHTML: true,
            finalURL: finalURL,
            attemptLog: log
        )
    }

    private func fetchReaderRelay(url: URL, log: [String]) async throws -> RelayResult {
        // Jina's reader takes the target URL as a path suffix and returns
        // cleaned markdown. It was the only general-purpose relay that survived
        // testing on a filtered network.
        let endpoint = "https://r.jina.ai/\(url.absoluteString)"
        guard let relayURL = URL(string: endpoint) else {
            throw RelayError.badURL(endpoint)
        }

        var request = URLRequest(url: relayURL)
        request.timeoutInterval = 30  // the relay renders the page, so it's slow
        request.setValue("text/plain", forHTTPHeaderField: "Accept")

        let (data, response) = try await URLSession.shared.data(for: request)
        try Self.validate(response)
        guard !data.isEmpty else { throw RelayError.emptyBody }

        return RelayResult(
            strategy: .readerRelay,
            content: String(decoding: data, as: UTF8.self),
            rawBody: data,
            contentType: "text/plain; charset=utf-8",
            isHTML: false,
            finalURL: url,
            attemptLog: log
        )
    }

    private func fetchWayback(url: URL, log: [String]) async throws -> RelayResult {
        struct WaybackResponse: Decodable {
            struct Snapshots: Decodable {
                struct Closest: Decodable {
                    let available: Bool
                    let url: String?
                }
                let closest: Closest?
            }
            let archived_snapshots: Snapshots?
        }

        var components = URLComponents(string: "https://archive.org/wayback/available")!
        components.queryItems = [URLQueryItem(name: "url", value: url.absoluteString)]

        var lookup = URLRequest(url: components.url!)
        lookup.timeoutInterval = timeout
        let (metaData, metaResponse) = try await URLSession.shared.data(for: lookup)
        try Self.validate(metaResponse)

        let meta = try JSONDecoder().decode(WaybackResponse.self, from: metaData)
        guard let snapshot = meta.archived_snapshots?.closest,
              snapshot.available,
              let snapshotURLString = snapshot.url,
              // The API hands back http:// URLs which then redirect; upgrade
              // to https so we don't get downgraded mid-flight.
              let snapshotURL = URL(string: snapshotURLString.replacingOccurrences(
                  of: "http://", with: "https://"))
        else {
            throw RelayError.emptyBody
        }

        var request = URLRequest(url: snapshotURL)
        request.timeoutInterval = 30
        request.setValue(Self.userAgent, forHTTPHeaderField: "User-Agent")

        let (data, response) = try await URLSession.shared.data(for: request)
        try Self.validate(response)
        guard !data.isEmpty else { throw RelayError.emptyBody }

        return RelayResult(
            strategy: .wayback,
            content: String(decoding: data, as: UTF8.self),
            rawBody: data,
            contentType: (response as? HTTPURLResponse)?
                .value(forHTTPHeaderField: "Content-Type"),
            isHTML: true,
            finalURL: snapshotURL,
            attemptLog: log
        )
    }

    // MARK: - Helpers

    private static let userAgent =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 "
        + "(KHTML, like Gecko) Version/17.0 Safari/605.1.15"

    private static func validate(_ response: URLResponse) throws {
        guard let http = response as? HTTPURLResponse else { return }
        guard (200..<400).contains(http.statusCode) else {
            throw RelayError.httpStatus(http.statusCode)
        }
    }

    /// Accepts the sloppy things people actually paste: bare domains, URLs with
    /// stray whitespace, links copied with surrounding angle brackets.
    static func normalize(_ input: String) throws -> URL {
        var text = input.trimmingCharacters(in: .whitespacesAndNewlines)
        text = text.trimmingCharacters(in: CharacterSet(charactersIn: "<>\"'"))
        guard !text.isEmpty else { throw RelayError.badURL(input) }

        if !text.contains("://") {
            text = "https://" + text
        }
        guard let url = URL(string: text), url.host != nil else {
            throw RelayError.badURL(input)
        }
        return url
    }
}
