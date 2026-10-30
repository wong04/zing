import Foundation

/// How serious a finding is. Ordered, so a report's overall level is just the
/// worst thing in it.
enum ThreatLevel: Int, Comparable, CaseIterable {
    case clean = 0
    case notice = 1       // worth knowing, not worth stopping for
    case suspicious = 2   // looks wrong; warn loudly
    case dangerous = 3    // actively hostile pattern; block by default

    static func < (a: ThreatLevel, b: ThreatLevel) -> Bool { a.rawValue < b.rawValue }

    var label: String {
        switch self {
        case .clean: return "Clean"
        case .notice: return "Notice"
        case .suspicious: return "Suspicious"
        case .dangerous: return "Dangerous"
        }
    }

    var icon: String {
        switch self {
        case .clean: return "checkmark.shield.fill"
        case .notice: return "info.circle.fill"
        case .suspicious: return "exclamationmark.triangle.fill"
        case .dangerous: return "xmark.shield.fill"
        }
    }
}

/// One specific thing that was noticed, with the reason spelled out. The
/// reason matters: an antivirus that says "threat detected" and nothing else
/// trains people to click through it.
struct ThreatFinding: Identifiable, Equatable {
    let id = UUID()
    let level: ThreatLevel
    let title: String
    let detail: String

    static func == (a: ThreatFinding, b: ThreatFinding) -> Bool {
        a.level == b.level && a.title == b.title && a.detail == b.detail
    }
}

/// The verdict on a single subject — a URL or a file.
struct ThreatVerdict: Equatable {
    let subject: String
    let findings: [ThreatFinding]

    var level: ThreatLevel { findings.map(\.level).max() ?? .clean }
    var isBlocking: Bool { level >= .dangerous }

    var summary: String {
        switch level {
        case .clean: return "Nothing suspicious found."
        case .notice: return findings.first?.title ?? "Worth a look."
        case .suspicious: return findings.first { $0.level == .suspicious }?.title
            ?? "Looks suspicious."
        case .dangerous: return findings.first { $0.level == .dangerous }?.title
            ?? "Blocked."
        }
    }

    /// The one-line message shown after a download finishes.
    func downloadNotice(for file: URL) -> String {
        switch level {
        case .clean, .notice:
            return "Saved \(file.lastPathComponent) to Downloads. Scanned — clean."
        case .suspicious:
            return "Saved \(file.lastPathComponent), but it looks suspicious: \(summary)"
        case .dangerous:
            return "⚠︎ \(file.lastPathComponent) is dangerous: \(summary)"
        }
    }

    static let clean = ThreatVerdict(subject: "", findings: [])
}

/// Pure pattern-matching over URLs and filenames.
///
/// This is deliberately separate from anything that touches the disk or the
/// network so every rule can be tested against a table of known-bad and
/// known-good inputs. That matters more here than elsewhere in the app: a
/// false positive in a security check is a feature that trains the user to
/// ignore it.
enum ThreatHeuristics {

    // MARK: - URLs

    /// Top-level domains that are either abused overwhelmingly for abuse, or
    /// collide with common file extensions so a filename in text becomes a
    /// clickable link to somebody else's server.
    static let highRiskTLDs: Set<String> = [
        "zip", "mov",                                   // collide with filenames
        "tk", "ml", "ga", "cf", "gq",                   // historically free, abused
        "top", "xyz", "gdn", "work", "click", "link",
        "country", "kim", "science", "party", "review",
    ]

    /// Brands impersonated often enough that a lookalike host is a strong
    /// signal on its own.
    static let commonlyImpersonated: [String] = [
        "apple", "icloud", "paypal", "amazon", "microsoft", "office365",
        "google", "gmail", "facebook", "instagram", "netflix", "coinbase",
        "binance", "metamask", "dhl", "fedex", "usps", "hmrc", "irs",
        "chase", "wellsfargo", "barclays", "santander", "steam", "discord",
    ]

    /// Words that show up in credential-harvest hostnames.
    private static let phishWords: Set<String> = [
        "login", "signin", "verify", "verification", "secure", "security",
        "account", "update", "confirm", "recovery", "unlock", "billing",
        "wallet", "authenticate", "support",
    ]

    static func inspect(url: URL) -> ThreatVerdict {
        var findings: [ThreatFinding] = []
        let host = (url.host ?? "").lowercased()

        // Credentials embedded in the URL are the classic way to make a link
        // read as one site while pointing at another.
        if url.user != nil || url.password != nil {
            findings.append(ThreatFinding(
                level: .dangerous,
                title: "Link disguises its real destination",
                detail: "This URL puts a username before the host, so the part that "
                    + "looks like the site name isn't where it goes. It actually "
                    + "connects to \(host.isEmpty ? "an unnamed host" : host)."))
        }

        if !host.isEmpty {
            findings.append(contentsOf: inspectHost(host, scheme: url.scheme?.lowercased()))
        }

        return ThreatVerdict(subject: url.absoluteString, findings: findings)
    }

    static func inspectHost(_ rawHost: String, scheme: String? = "https") -> [ThreatFinding] {
        var findings: [ThreatFinding] = []
        let host = rawHost.lowercased()
        let labels = host.split(separator: ".").map(String.init)

        // Punycode. Legitimate internationalised domains use it too, which is
        // why this is a warning and not a block — but every homograph attack
        // needs it, so it's always worth surfacing.
        if host.contains("xn--") {
            findings.append(ThreatFinding(
                level: .suspicious,
                title: "Address uses look-alike characters",
                detail: "\(host) is written with non-Latin characters that can be "
                    + "made to look identical to a familiar name. Check it letter "
                    + "by letter before entering anything."))
        }

        // A bare IP over plain HTTP is how a lot of commodity malware fetches
        // its payload. Over HTTPS it's usually just a dev box.
        if isRawIPv4(host) {
            findings.append(ThreatFinding(
                level: scheme == "https" ? .notice : .suspicious,
                title: "Connects to a raw IP address",
                detail: "There's no domain name here, so there's nothing to check a "
                    + "certificate against and no owner to look up."))
        }

        if let tld = labels.last, highRiskTLDs.contains(tld) {
            let collides = tld == "zip" || tld == "mov"
            findings.append(ThreatFinding(
                level: .suspicious,
                title: "High-risk domain ending (.\(tld))",
                detail: collides
                    ? "The .\(tld) ending is also a file extension, so text that looks "
                        + "like a filename becomes a link to somebody's server."
                    : "This ending is used disproportionately for phishing and "
                        + "malware because registrations are free or unverified."))
        }

        // Deeply nested subdomains are used to push the real domain off the
        // end of a narrow address bar.
        if labels.count >= 6 {
            findings.append(ThreatFinding(
                level: .suspicious,
                title: "Unusually deep subdomain",
                detail: "\(labels.count) levels deep. This is often used to bury the "
                    + "real domain past the edge of the address bar."))
        }

        // Brand impersonation: the brand stands alone as a label in the host,
        // but the domain that actually owns the site isn't the brand's.
        //
        // Matching the brand as a bare substring is far too loose, and the two
        // failures it produced are instructive: `appleinsider.com` is a real
        // publication, and `login.microsoftonline.com` is Microsoft's genuine
        // sign-in host. Both merely *contain* a brand name inside a longer
        // word. Requiring the brand to be a whole label — bounded by a dot or
        // a hyphen — keeps `apple.com.secure-login.tk` caught while leaving
        // both of those alone.
        let registrable = registrableDomain(host)
        let hostLabels = Set(host.split(whereSeparator: { $0 == "." || $0 == "-" })
            .map(String.init))
        let ownerLabels = Set(registrable.split(whereSeparator: { $0 == "." || $0 == "-" })
            .map(String.init))

        for brand in commonlyImpersonated where hostLabels.contains(brand) {
            // The brand appearing in the registrable domain means the brand
            // owns the site, whatever the subdomains look like.
            guard !ownerLabels.contains(brand) else { continue }
            findings.append(ThreatFinding(
                level: .dangerous,
                title: "Impersonates \(brand.capitalized)",
                detail: "\"\(brand)\" appears in this address, but the site is actually "
                    + "\(registrable) — not \(brand). This is the standard shape of a "
                    + "credential-stealing page."))
            break
        }

        // Security vocabulary in a hostname, combined with a hyphen-heavy name,
        // is a weak signal on its own but a good one in combination.
        let words = Set(host.split(whereSeparator: { !$0.isLetter }).map(String.init))
        let phishHits = words.intersection(phishWords)
        if phishHits.count >= 2, !findings.contains(where: { $0.level >= .dangerous }) {
            findings.append(ThreatFinding(
                level: .suspicious,
                title: "Hostname reads like a sign-in trap",
                detail: "Contains \(phishHits.sorted().joined(separator: ", ")). Real "
                    + "services put sign-in on their own domain, not in the hostname."))
        }

        return findings
    }

    static func isRawIPv4(_ host: String) -> Bool {
        let parts = host.split(separator: ".")
        guard parts.count == 4 else { return false }
        return parts.allSatisfy { part in
            guard let value = Int(part), part.count <= 3 else { return false }
            return (0...255).contains(value)
        }
    }

    /// The root domain a host belongs to: `docs.google.com` and
    /// `mail.google.com` are both `google.com`. Tab grouping, favicons and the
    /// phishing checks all key on this, so they agree on what "the same site"
    /// means.
    ///
    /// Not the full Public Suffix List — that's a large file to ship and keep
    /// current — but the common two-part country suffixes, so `bbc.co.uk`
    /// doesn't collapse to the meaningless `co.uk`.
    static func registrableDomain(_ host: String) -> String {
        var host = host.lowercased()
        if host.hasSuffix(".") { host.removeLast() }
        // An address isn't a domain. Cutting `192.168.1.10` to its last two
        // labels would group unrelated machines as one "site".
        if isRawIPv4(host) || host.contains(":") { return host }
        let parts = host.split(separator: ".").map(String.init)
        guard parts.count > 2 else { return host }
        let lastTwo = parts.suffix(2).joined(separator: ".")
        if twoPartSuffixes.contains(lastTwo) {
            return parts.suffix(3).joined(separator: ".")
        }
        return lastTwo
    }

    private static let twoPartSuffixes: Set<String> = [
        "co.uk", "ac.uk", "gov.uk", "org.uk", "me.uk", "ltd.uk", "plc.uk", "nhs.uk",
        "co.jp", "ne.jp", "or.jp", "ac.jp", "go.jp",
        "co.nz", "org.nz", "govt.nz", "ac.nz",
        "co.za", "org.za", "gov.za",
        "com.au", "net.au", "org.au", "edu.au", "gov.au",
        "com.br", "net.br", "org.br", "gov.br",
        "com.cn", "net.cn", "org.cn", "gov.cn", "edu.cn",
        "com.mx", "org.mx", "gob.mx",
        "co.in", "net.in", "org.in", "gov.in", "ac.in",
        "co.kr", "or.kr", "go.kr", "ac.kr",
        "com.tr", "org.tr", "gov.tr", "edu.tr",
        "com.sg", "edu.sg", "gov.sg",
        "co.il", "org.il", "ac.il",
        "com.hk", "org.hk", "edu.hk", "gov.hk",
        "com.tw", "org.tw", "edu.tw", "gov.tw",
        "com.ar", "com.co", "com.pe", "com.ve", "com.ec",
        "co.id", "or.id", "ac.id", "go.id",
        "co.th", "or.th", "ac.th", "go.th", "in.th",
        "com.my", "org.my", "edu.my", "gov.my",
        "com.ph", "org.ph", "edu.ph", "gov.ph",
        "com.vn", "edu.vn", "gov.vn",
        "com.pk", "edu.pk", "gov.pk",
        "com.ng", "org.ng", "gov.ng",
        "co.ke", "or.ke", "ac.ke",
        "com.eg", "com.sa", "com.ua", "com.pl", "co.at", "or.at",
    ]

    // MARK: - Files

    /// Types macOS will execute. A download landing as one of these deserves a
    /// second look regardless of what it claims to be.
    static let executableExtensions: Set<String> = [
        "app", "pkg", "dmg", "command", "sh", "bash", "zsh", "scpt", "scptd",
        "workflow", "action", "kext", "osax", "jar", "exe", "scr", "bat",
        "cmd", "com", "msi", "vbs", "vbe", "wsf", "ps1", "hta", "pif",
    ]

    /// Office formats that can carry macros.
    static let macroExtensions: Set<String> = [
        "docm", "xlsm", "pptm", "xlsb", "dotm", "xltm", "potm", "ppsm",
    ]

    /// Extensions people expect to be inert documents. A file with one of these
    /// in the middle and an executable ending is a deliberate disguise.
    private static let documentDecoys: Set<String> = [
        "pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "txt", "rtf",
        "jpg", "jpeg", "png", "gif", "mp3", "mp4", "csv", "zip", "invoice",
    ]

    static func inspect(filename: String) -> [ThreatFinding] {
        var findings: [ThreatFinding] = []
        let name = filename.lowercased()
        let components = name.split(separator: ".").map(String.init)
        let ext = components.count > 1 ? components[components.count - 1] : ""

        // Right-to-left override rewrites how the name is displayed, so
        // "photo\u{202E}gpj.app" shows up in Finder as "photoppa.jpg".
        if filename.unicodeScalars.contains(where: {
            $0.value == 0x202E || $0.value == 0x202B || $0.value == 0x200F
        }) {
            findings.append(ThreatFinding(
                level: .dangerous,
                title: "Filename contains a text-direction override",
                detail: "An invisible character is reversing part of this name so it "
                    + "displays as something other than what it is. There is no "
                    + "legitimate reason for a download to do this."))
        }

        // Double extension: a decoy document type followed by an executable.
        if components.count >= 3, executableExtensions.contains(ext) {
            let inner = components[components.count - 2]
            if documentDecoys.contains(inner) {
                findings.append(ThreatFinding(
                    level: .dangerous,
                    title: "Disguised as a .\(inner) but is a .\(ext)",
                    detail: "The name ends \".\(inner).\(ext)\". macOS will run it as a "
                        + ".\(ext); the \".\(inner)\" is decoration to make it look "
                        + "like a document."))
            }
        }

        if executableExtensions.contains(ext),
           !findings.contains(where: { $0.level >= .dangerous }) {
            findings.append(ThreatFinding(
                level: .suspicious,
                title: "This file can run code",
                detail: "A .\(ext) is a program, not a document. Only open it if you "
                    + "know exactly who made it and why you're getting it."))
        }

        if macroExtensions.contains(ext) {
            findings.append(ThreatFinding(
                level: .suspicious,
                title: "Document can contain macros",
                detail: "A .\(ext) can carry embedded code that runs when opened. "
                    + "Open it with macros disabled unless you trust the sender."))
        }

        return findings
    }

    /// Cross-checks the extension against what the bytes actually are. A file
    /// claiming to be a PNG whose header says otherwise was renamed on purpose.
    static func inspectContent(extension ext: String, sniffed: String?) -> [ThreatFinding] {
        guard let sniffed, !ext.isEmpty else { return [] }
        let claimed = ext.lowercased()
        guard claimed != sniffed else { return [] }
        // Interchangeable pairs that aren't a mismatch in any meaningful sense.
        let equivalent: [Set<String>] = [
            ["jpg", "jpeg"], ["tif", "tiff"], ["htm", "html"], ["mp4", "m4v"],
        ]
        if equivalent.contains(where: { $0.contains(claimed) && $0.contains(sniffed) }) {
            return []
        }
        return [ThreatFinding(
            level: .suspicious,
            title: "Contents don't match the extension",
            detail: "Named .\(claimed) but the file's own header says it's a "
                + ".\(sniffed). Renaming a file to disguise its type is a common "
                + "way to get it past a filter.")]
    }
}
