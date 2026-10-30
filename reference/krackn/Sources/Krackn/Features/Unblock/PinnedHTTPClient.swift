import Foundation
import Network
import os

/// Speaks HTTP/1.1 over a connection we open ourselves.
///
/// URLSession can't do what this needs. If you point it at an IP address it
/// sends that IP as the TLS server name, and every CDN answers with a handshake
/// failure — the certificate check never even happens. Network.framework lets
/// the dialled address and the advertised server name differ, which is exactly
/// the separation required: connect to an address DNS never told the network
/// about, while still presenting the real hostname so the server routes it and
/// the certificate validates against it.
enum PinnedHTTPClient {

    struct Response {
        let statusCode: Int
        let headers: [String: String]
        let body: Data

        var location: String? {
            headers["location"]
        }

        var isRedirect: Bool {
            (300..<400).contains(statusCode) && location != nil
        }
    }

    enum ClientError: LocalizedError {
        case connectionFailed(String)
        case timedOut
        case malformedResponse
        case tooManyRedirects

        var errorDescription: String? {
            switch self {
            case .connectionFailed(let why): return "Connection failed: \(why)"
            case .timedOut: return "The connection timed out."
            case .malformedResponse: return "The server sent a response we couldn't parse."
            case .tooManyRedirects: return "Too many redirects."
            }
        }
    }

    private static let userAgent =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 "
        + "(KHTML, like Gecko) Version/17.0 Safari/605.1.15"

    /// Resolves `url`'s host over DoH, then fetches it from the resolved
    /// address, following redirects (re-resolving each hop the same way).
    static func fetch(
        url: URL,
        maxRedirects: Int = 5,
        timeout: TimeInterval = 20
    ) async throws -> (response: Response, finalURL: URL) {

        var current = url
        var hops = 0

        while hops <= maxRedirects {
            guard let host = current.host else { throw RelayError.badURL(current.absoluteString) }

            let addresses = try await DoHResolver.resolve(host)
            guard let address = addresses.first else { throw RelayError.nxdomain(host) }

            let response = try await request(
                url: current, address: address, host: host, timeout: timeout)

            guard response.isRedirect, let location = response.location else {
                return (response, current)
            }
            // Location may be relative; resolve it against the URL we just fetched.
            guard let next = URL(string: location, relativeTo: current)?.absoluteURL else {
                return (response, current)
            }
            current = next
            hops += 1
        }
        throw ClientError.tooManyRedirects
    }

    /// One request/response cycle against a specific address.
    private static func request(
        url: URL, address: String, host: String, timeout: TimeInterval
    ) async throws -> Response {

        let isTLS = (url.scheme ?? "https").lowercased() == "https"
        let port = UInt16(url.port ?? (isTLS ? 443 : 80))

        let parameters: NWParameters
        if isTLS {
            let tlsOptions = NWProtocolTLS.Options()
            // The whole trick, in one call: advertise the real hostname even
            // though we're dialling a bare IP. The server routes on it and the
            // certificate is validated against it, so this stays a genuine
            // authenticated TLS session — a MITM still fails.
            sec_protocol_options_set_tls_server_name(
                tlsOptions.securityProtocolOptions, host)
            parameters = NWParameters(tls: tlsOptions)
        } else {
            parameters = .tcp
        }

        let connection = NWConnection(
            host: NWEndpoint.Host(address),
            port: NWEndpoint.Port(rawValue: port)!,
            using: parameters
        )

        var path = url.path.isEmpty ? "/" : url.path
        if let query = url.query { path += "?\(query)" }

        let requestLine = """
        GET \(path) HTTP/1.1\r
        Host: \(host)\r
        User-Agent: \(userAgent)\r
        Accept: text/html,application/xhtml+xml,*/*\r
        Accept-Encoding: identity\r
        Connection: close\r
        \r

        """

        // The cancellation handler tears down the connection, which drives the
        // state machine to `.cancelled` — handled below so the continuation is
        // always resumed. Without both halves, cancelling leaks the task forever.
        return try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { continuation in
            let state = TransferState()

            @Sendable func finish(_ result: Result<Response, Error>) {
                guard state.claimCompletion() else { return }
                connection.cancel()
                continuation.resume(with: result)
            }

            // `Connection: close` means the server signals the end of the body
            // by closing, so "receive until complete" is the natural loop.
            @Sendable func receiveMore() {
                connection.receive(
                    minimumIncompleteLength: 1, maximumLength: 65536
                ) { chunk, _, isComplete, error in
                    if let chunk, !chunk.isEmpty {
                        state.append(chunk)
                    }
                    if let error {
                        finish(.failure(ClientError.connectionFailed(
                            error.localizedDescription)))
                        return
                    }
                    if isComplete {
                        do {
                            finish(.success(try parse(state.data())))
                        } catch {
                            finish(.failure(error))
                        }
                        return
                    }
                    receiveMore()
                }
            }

            connection.stateUpdateHandler = { newState in
                switch newState {
                case .ready:
                    connection.send(
                        content: Data(requestLine.utf8),
                        completion: .contentProcessed { error in
                            if let error {
                                finish(.failure(ClientError.connectionFailed(
                                    error.localizedDescription)))
                            } else {
                                receiveMore()
                            }
                        }
                    )
                case .failed(let error):
                    finish(.failure(ClientError.connectionFailed(error.localizedDescription)))
                case .waiting(let error):
                    // The path isn't viable — typically the block itself.
                    finish(.failure(ClientError.connectionFailed(error.localizedDescription)))
                case .cancelled:
                    // Reached either through our own teardown (already settled,
                    // so this is a no-op) or an external cancel. In the latter
                    // case this is the only thing that resumes the continuation.
                    finish(.failure(CancellationError()))
                default:
                    break
                }
            }

            connection.start(queue: .global(qos: .userInitiated))
            DispatchQueue.global().asyncAfter(deadline: .now() + timeout) {
                finish(.failure(ClientError.timedOut))
            }
            }
        } onCancel: {
            connection.cancel()
        }
    }

    // MARK: - Parsing

    private static func parse(_ raw: Data) throws -> Response {
        // Header/body boundary is a blank line — find it as bytes, since the
        // body may not be valid UTF-8.
        let separator = Data("\r\n\r\n".utf8)
        guard let range = raw.range(of: separator) else {
            throw ClientError.malformedResponse
        }

        let headerText = String(decoding: raw[raw.startIndex..<range.lowerBound], as: UTF8.self)
        var body = raw[range.upperBound...]

        var lines = headerText.components(separatedBy: "\r\n")
        guard !lines.isEmpty else { throw ClientError.malformedResponse }

        let statusLine = lines.removeFirst()
        let statusParts = statusLine.split(separator: " ", maxSplits: 2).map(String.init)
        guard statusParts.count >= 2, let code = Int(statusParts[1]) else {
            throw ClientError.malformedResponse
        }

        var headers: [String: String] = [:]
        for line in lines {
            guard let colon = line.firstIndex(of: ":") else { continue }
            // Lowercase keys so lookups don't depend on the server's casing.
            let key = line[line.startIndex..<colon]
                .trimmingCharacters(in: .whitespaces).lowercased()
            let value = line[line.index(after: colon)...]
                .trimmingCharacters(in: .whitespaces)
            headers[key] = value
        }

        if headers["transfer-encoding"]?.lowercased().contains("chunked") == true {
            body = dechunk(Data(body))[...]
        }

        return Response(statusCode: code, headers: headers, body: Data(body))
    }

    /// Reassembles a chunked body. Each chunk is a hex length line, the bytes,
    /// then CRLF; a zero length ends the stream.
    private static func dechunk(_ input: Data) -> Data {
        var output = Data()
        var cursor = input.startIndex
        let crlf = Data("\r\n".utf8)

        while cursor < input.endIndex {
            guard let lineEnd = input[cursor...].range(of: crlf) else { break }
            let sizeLine = String(decoding: input[cursor..<lineEnd.lowerBound], as: UTF8.self)
            // A chunk-size line may carry extensions after a semicolon.
            let sizeToken = sizeLine.split(separator: ";").first.map(String.init) ?? sizeLine
            guard let size = Int(sizeToken.trimmingCharacters(in: .whitespaces), radix: 16),
                  size > 0
            else { break }

            let chunkStart = lineEnd.upperBound
            let chunkEnd = input.index(chunkStart, offsetBy: size, limitedBy: input.endIndex)
                ?? input.endIndex
            output.append(input[chunkStart..<chunkEnd])
            // Skip the CRLF that terminates the chunk.
            cursor = input.index(chunkEnd, offsetBy: 2, limitedBy: input.endIndex)
                ?? input.endIndex
        }
        return output
    }
}

/// Accumulates bytes arriving on the connection's queue and guards the
/// single-resume contract of the continuation.
private final class TransferState: @unchecked Sendable {
    private let lock = NSLock()
    private var buffer = Data()
    private var completed = false

    func append(_ chunk: Data) {
        lock.lock()
        buffer.append(chunk)
        lock.unlock()
    }

    func data() -> Data {
        lock.lock()
        defer { lock.unlock() }
        return buffer
    }

    /// Returns true exactly once, for whichever caller gets there first.
    func claimCompletion() -> Bool {
        lock.lock()
        defer { lock.unlock() }
        if completed { return false }
        completed = true
        return true
    }
}
