import Foundation
import Network
import os

/// What kind of interference is happening, which determines what can defeat it.
enum BlockKind {
    case notBlocked
    case dnsTampering        // resolver lies; DoH fixes this
    case ipBlocked           // packets to the address are dropped; needs a tunnel
    case tlsInterception     // connection dies at the handshake; needs a tunnel
    case siteDown            // not the network's fault at all
    case inconclusive

    var title: String {
        switch self {
        case .notBlocked: return "Not blocked"
        case .dnsTampering: return "DNS-level block"
        case .ipBlocked: return "IP-level block"
        case .tlsInterception: return "TLS / SNI inspection"
        case .siteDown: return "Site is down"
        case .inconclusive: return "Inconclusive"
        }
    }

    var detail: String {
        switch self {
        case .notBlocked:
            return "This site resolves and connects normally. If it isn't loading, the problem is somewhere else."
        case .dnsTampering:
            return "Your network's DNS server is returning a different answer than the real one. This is the most common and weakest form of blocking — DoH + IP pinning defeats it outright."
        case .ipBlocked:
            return "The name resolves correctly but traffic to the address is being dropped. No amount of DNS trickery fixes this; you need traffic to exit somewhere else, which means a VPN or a proxy you control."
        case .tlsInterception:
            return "The address accepts a connection but the TLS handshake fails. Something is reading the server name and cutting the connection. A reader relay still works here, since you're only ever connecting to the relay."
        case .siteDown:
            return "The hostname doesn't exist even according to public resolvers. It's not your network."
        case .inconclusive:
            return "Results were mixed. Try the relay strategies and see what gets through."
        }
    }

    /// Which strategies are actually worth attempting given this diagnosis.
    var recommended: [RelayStrategy] {
        switch self {
        case .notBlocked: return [.direct]
        case .dnsTampering: return [.dohPinned, .readerRelay]
        case .ipBlocked, .tlsInterception: return [.readerRelay, .wayback]
        case .siteDown: return [.wayback]
        case .inconclusive: return RelayStrategy.allCases
        }
    }
}

struct DiagnosticReport {
    let host: String
    let kind: BlockKind
    let systemDNS: [String]
    let publicDNS: [String]
    let tcpReachable: Bool
    let tlsSucceeded: Bool
    let steps: [String]
}

enum BlockDiagnostics {

    /// Runs the full ladder: compare resolvers, then test TCP, then test TLS.
    /// Each layer that works narrows down where the interference sits.
    static func diagnose(
        host: String,
        progress: (@Sendable (String) -> Void)? = nil
    ) async -> DiagnosticReport {

        var steps: [String] = []

        progress?("Resolving via your network's DNS…")
        let systemAddresses = await DoHResolver.resolveViaSystem(host)
        steps.append(systemAddresses.isEmpty
            ? "Network DNS: no answer (possible NXDOMAIN block)"
            : "Network DNS: \(systemAddresses.joined(separator: ", "))")

        progress?("Resolving via DNS-over-HTTPS…")
        var publicAddresses: [String] = []
        var nameExistsUpstream = true
        do {
            publicAddresses = try await DoHResolver.resolve(host)
            steps.append("Public DoH: \(publicAddresses.joined(separator: ", "))")
        } catch RelayError.nxdomain {
            nameExistsUpstream = false
            steps.append("Public DoH: NXDOMAIN — the name genuinely doesn't exist")
        } catch {
            steps.append("Public DoH: failed (\(error.localizedDescription))")
        }

        if !nameExistsUpstream {
            return DiagnosticReport(
                host: host, kind: .siteDown,
                systemDNS: systemAddresses, publicDNS: [],
                tcpReachable: false, tlsSucceeded: false, steps: steps
            )
        }

        guard let target = publicAddresses.first else {
            return DiagnosticReport(
                host: host, kind: .inconclusive,
                systemDNS: systemAddresses, publicDNS: publicAddresses,
                tcpReachable: false, tlsSucceeded: false, steps: steps
            )
        }

        // A resolver that hands back a private/null address is the textbook
        // signature of a filtering appliance sinkholing the name.
        let sinkholed = systemAddresses.contains { isSinkhole($0) }
        if sinkholed {
            steps.append("Network DNS returned a sinkhole address — that's a deliberate block")
        }

        // Differing addresses prove nothing on their own. Anycast CDNs answer
        // per-resolver, so Meta, Google and Cloudflare routinely hand your
        // resolver a completely different (and completely valid) set than a
        // public one does. Treating that as tampering flags half the internet.
        if !systemAddresses.isEmpty
            && Set(systemAddresses).isDisjoint(with: Set(publicAddresses)) {
            steps.append("Answers differ — normal for large CDNs, so not conclusive by itself")
        }

        // The decisive question isn't which address you got, it's whether the
        // address you got can prove it's the real host. A sinkhole or an
        // intercepting middlebox cannot present a valid certificate.
        var systemTLSOK = false
        if let systemTarget = systemAddresses.first(where: { !isSinkhole($0) }) {
            progress?("Checking the address your network gave you…")
            systemTLSOK = await canConnect(
                host: systemTarget, port: 443, useTLS: true, serverName: host)
            steps.append(systemTLSOK
                ? "Your network's address serves a valid certificate for \(host)"
                : "Your network's address failed to prove it's \(host)")
        }

        progress?("Testing raw TCP to \(target):443…")
        let tcpOK = await canConnect(host: target, port: 443, useTLS: false, serverName: nil)
        steps.append(tcpOK ? "TCP 443: reachable" : "TCP 443: blocked or dropped")

        var tlsOK = false
        if tcpOK {
            progress?("Testing TLS handshake with SNI…")
            tlsOK = await canConnect(host: target, port: 443, useTLS: true, serverName: host)
            steps.append(tlsOK ? "TLS handshake: succeeded" : "TLS handshake: rejected")
        }

        let kind = classify(
            systemTLSOK: systemTLSOK,
            sinkholed: sinkholed,
            systemAnswerEmpty: systemAddresses.isEmpty,
            publicTCPOK: tcpOK,
            publicTLSOK: tlsOK
        )

        return DiagnosticReport(
            host: host, kind: kind,
            systemDNS: systemAddresses, publicDNS: publicAddresses,
            tcpReachable: tcpOK, tlsSucceeded: tlsOK, steps: steps
        )
    }

    /// Turns the observations into a verdict.
    ///
    /// Split out as a pure function so every combination can be tested without
    /// needing a genuinely blocked network to hand.
    ///
    /// - Parameters:
    ///   - systemTLSOK: the address your resolver gave proved it owns the
    ///     hostname via a valid certificate. This dominates everything else.
    ///   - sinkholed: your resolver returned a null or private address.
    ///   - systemAnswerEmpty: your resolver returned nothing at all.
    ///   - publicTCPOK: the DoH-resolved address accepts a connection.
    ///   - publicTLSOK: the DoH-resolved address completes a TLS handshake.
    static func classify(
        systemTLSOK: Bool,
        sinkholed: Bool,
        systemAnswerEmpty: Bool,
        publicTCPOK: Bool,
        publicTLSOK: Bool
    ) -> BlockKind {
        // Proof of identity beats every other signal: if the address you were
        // handed can present a valid certificate for the host, nothing is
        // interfering, whatever the addresses looked like.
        if systemTLSOK { return .notBlocked }

        // A completed handshake is itself proof the address is reachable, so
        // never treat the pair as contradictory.
        let reachable = publicTCPOK || publicTLSOK

        // Your resolver refused to answer, or answered with a dead end.
        if sinkholed || systemAnswerEmpty {
            return reachable ? .dnsTampering : .ipBlocked
        }

        // Your answer can't prove itself but a public one works end to end,
        // so the lie is in the resolver.
        if publicTLSOK { return .dnsTampering }

        // Reachable but the handshake dies — something is reading the server
        // name and cutting the connection.
        if reachable { return .tlsInterception }

        // Nothing gets through to the address at all.
        return .ipBlocked
    }

    /// Addresses a filter hands out when it wants a name to go nowhere.
    private static func isSinkhole(_ ip: String) -> Bool {
        ip == "0.0.0.0"
            || ip == "127.0.0.1"
            || ip.hasPrefix("10.")
            || ip.hasPrefix("192.168.")
            || ip.hasPrefix("172.16.")
    }

    /// Opens a connection and reports whether it reached `.ready`. With
    /// `useTLS` we also complete a handshake carrying the real SNI, which is
    /// what a DPI filter keys on.
    private static func canConnect(
        host: String,
        port: UInt16,
        useTLS: Bool,
        serverName: String?,
        timeout: TimeInterval = 6
    ) async -> Bool {
        await withCheckedContinuation { continuation in
            let parameters: NWParameters
            if useTLS {
                let tlsOptions = NWProtocolTLS.Options()
                if let serverName {
                    sec_protocol_options_set_tls_server_name(
                        tlsOptions.securityProtocolOptions, serverName
                    )
                }
                parameters = NWParameters(tls: tlsOptions)
            } else {
                parameters = .tcp
            }

            let connection = NWConnection(
                host: NWEndpoint.Host(host),
                port: NWEndpoint.Port(rawValue: port)!,
                using: parameters
            )

            // NWConnection has no built-in deadline, and a dropped-packet block
            // manifests as an indefinite hang, so we impose our own.
            let finished = OSAllocatedUnfairLock(initialState: false)
            @Sendable func settle(_ value: Bool) {
                let alreadyDone = finished.withLock { done -> Bool in
                    if done { return true }
                    done = true
                    return false
                }
                guard !alreadyDone else { return }
                connection.cancel()
                continuation.resume(returning: value)
            }

            connection.stateUpdateHandler = { state in
                switch state {
                case .ready:
                    settle(true)
                case .failed, .cancelled:
                    settle(false)
                case .waiting:
                    // "waiting" means the path isn't viable — for our purposes
                    // that's a failure, not something to sit through.
                    settle(false)
                default:
                    break
                }
            }

            connection.start(queue: .global(qos: .userInitiated))
            DispatchQueue.global().asyncAfter(deadline: .now() + timeout) {
                settle(false)
            }
        }
    }
}
