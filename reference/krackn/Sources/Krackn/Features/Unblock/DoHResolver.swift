import Foundation

/// Resolves hostnames over HTTPS, bypassing whatever resolver the network handed
/// us over DHCP. This is the single most useful trick against consumer-grade
/// filtering, which is overwhelmingly implemented at the DNS layer.
enum DoHResolver {

    /// The raw values match `DoHProfile.resolvers` exactly, so the resolver
    /// chosen in Settings is the resolver the app's own lookups use. They were
    /// two disjoint lists before: Settings offered four providers and this enum
    /// knew two, so even once the preference was read half the choices had
    /// nothing to map onto.
    enum Provider: String, CaseIterable, Identifiable {
        case cloudflare = "Cloudflare"
        case cloudflareSecurity = "Cloudflare (malware blocking)"
        case google = "Google"
        case quad9 = "Quad9"

        var id: String { rawValue }

        /// All four speak the JSON DoH dialect, but the Cloudflare endpoints
        /// require the dns-json Accept header — without it you get a 400.
        var endpoint: String {
            switch self {
            case .cloudflare: return "https://cloudflare-dns.com/dns-query"
            case .cloudflareSecurity: return "https://security.cloudflare-dns.com/dns-query"
            case .google: return "https://dns.google/resolve"
            // Quad9 serves its JSON API on 5053; the standard port speaks
            // wire-format only.
            case .quad9: return "https://dns.quad9.net:5053/dns-query"
            }
        }

        var requiresJSONAcceptHeader: Bool {
            self == .cloudflare || self == .cloudflareSecurity
        }

        /// Whether this resolver refuses known-malicious domains. The security
        /// section uses these as a reputation signal.
        var filtersMalware: Bool {
            self == .cloudflareSecurity || self == .quad9
        }
    }

    private struct DNSResponse: Decodable {
        let Status: Int
        let Answer: [Answer]?

        struct Answer: Decodable {
            let name: String
            let type: Int
            let data: String
        }
    }

    /// Returns the A/AAAA records for `host`. An empty array means the name
    /// resolved but has no address records; a thrown error means the lookup
    /// itself failed.
    /// `provider` defaults to whichever resolver the user picked in Settings,
    /// so that preference actually governs the app's lookups instead of being
    /// written and ignored.
    static func resolve(
        _ host: String,
        using provider: Provider? = nil,
        timeout: TimeInterval = 8
    ) async throws -> [String] {
        let provider = provider ?? AppSettings.dohProvider
        var components = URLComponents(string: provider.endpoint)!
        components.queryItems = [
            URLQueryItem(name: "name", value: host),
            URLQueryItem(name: "type", value: "A"),
        ]
        guard let url = components.url else {
            throw RelayError.badURL(host)
        }

        var request = URLRequest(url: url)
        request.timeoutInterval = timeout
        if provider.requiresJSONAcceptHeader {
            request.setValue("application/dns-json", forHTTPHeaderField: "Accept")
        }

        let (data, response) = try await URLSession.shared.data(for: request)
        guard let http = response as? HTTPURLResponse, http.statusCode == 200 else {
            throw RelayError.resolverUnreachable(provider.rawValue)
        }

        let decoded = try JSONDecoder().decode(DNSResponse.self, from: data)
        // Status 3 is NXDOMAIN — the name genuinely doesn't exist upstream,
        // which is different from our local resolver lying about it.
        guard decoded.Status == 0 else {
            if decoded.Status == 3 { throw RelayError.nxdomain(host) }
            throw RelayError.resolverError(decoded.Status)
        }
        // type 1 = A record. CNAMEs (type 5) come back in the same array and
        // must be filtered out or we'd try to connect to a hostname.
        return (decoded.Answer ?? [])
            .filter { $0.type == 1 }
            .map(\.data)
    }

    /// Resolves through the OS resolver — i.e. whatever DNS server the WiFi
    /// gave us. Comparing this against `resolve` is how we detect DNS tampering.
    static func resolveViaSystem(_ host: String) async -> [String] {
        await withCheckedContinuation { continuation in
            DispatchQueue.global(qos: .userInitiated).async {
                var hints = addrinfo(
                    ai_flags: 0,
                    ai_family: AF_INET,
                    ai_socktype: SOCK_STREAM,
                    ai_protocol: 0,
                    ai_addrlen: 0,
                    ai_canonname: nil,
                    ai_addr: nil,
                    ai_next: nil
                )
                var info: UnsafeMutablePointer<addrinfo>?
                defer { if info != nil { freeaddrinfo(info) } }

                guard getaddrinfo(host, nil, &hints, &info) == 0 else {
                    continuation.resume(returning: [])
                    return
                }

                var addresses: [String] = []
                var cursor = info
                while let node = cursor {
                    if let addr = node.pointee.ai_addr {
                        var buffer = [CChar](repeating: 0, count: Int(NI_MAXHOST))
                        if getnameinfo(
                            addr, node.pointee.ai_addrlen,
                            &buffer, socklen_t(buffer.count),
                            nil, 0, NI_NUMERICHOST
                        ) == 0 {
                            let ip = String(cString: buffer)
                            if !addresses.contains(ip) { addresses.append(ip) }
                        }
                    }
                    cursor = node.pointee.ai_next
                }
                continuation.resume(returning: addresses)
            }
        }
    }
}
