import Foundation

var pass = 0, fail = 0
func out(_ t: String) { print(t) }
func ok(_ n: String) { pass += 1; out("  ok   \(n)") }
func bad(_ n: String, _ d: String) { fail += 1; out("  FAIL \(n): \(d)") }

/// A URL that must be refused outright.
func expectBlocked(_ raw: String) {
    guard let url = URL(string: raw) else { bad(raw, "unparseable"); return }
    let verdict = ThreatHeuristics.inspect(url: url)
    if verdict.isBlocking { ok("blocks \(raw) — \(verdict.summary)") }
    else { bad(raw, "expected block, got \(verdict.level.label)") }
}

/// A URL that must be warned about but not blocked.
func expectSuspicious(_ raw: String) {
    guard let url = URL(string: raw) else { bad(raw, "unparseable"); return }
    let verdict = ThreatHeuristics.inspect(url: url)
    if verdict.level == .suspicious { ok("warns \(raw) — \(verdict.summary)") }
    else { bad(raw, "expected suspicious, got \(verdict.level.label)") }
}

/// A URL that must produce no warning at all. These matter more than the
/// positives: a checker that flags ordinary sites gets switched off.
func expectClean(_ raw: String) {
    guard let url = URL(string: raw) else { bad(raw, "unparseable"); return }
    let verdict = ThreatHeuristics.inspect(url: url)
    if verdict.level <= .notice { ok("allows \(raw)") }
    else { bad(raw, "false positive: \(verdict.level.label) — \(verdict.summary)") }
}

out("── real sites must not be flagged")

// Every one of these is an ordinary site. A false positive here is worse than
// a missed detection, because it teaches the user to ignore the warning.
for site in [
    "https://www.google.com/search?q=hello",
    "https://github.com/apple/swift",
    "https://en.wikipedia.org/wiki/Main_Page",
    "https://news.bbc.co.uk/news",
    "https://docs.google.com/document/d/abc123/edit",
    "https://mail.google.com/mail/u/0",
    "https://www.apple.com/uk/mac/",
    "https://appleinsider.com/articles/latest",
    "https://stackoverflow.com/questions/12345",
    "https://accounts.google.com/signin",
    "https://login.microsoftonline.com/common/oauth2",
    "https://secure.paypal.com/myaccount",
    "https://www.amazon.co.uk/gp/cart",
    "https://open.spotify.com/playlist/xyz",
    "https://developer.mozilla.org/en-US/docs/Web",
    "https://arxiv.org/abs/2401.00001",
] { expectClean(site) }

out("── impersonation must be blocked")

// The brand appears in the host but isn't the registrable domain.
expectBlocked("https://apple.com.secure-login.tk/verify")
expectBlocked("https://paypal.account-update.xyz/signin")
expectBlocked("https://signin-netflix.billing-verify.top/")
expectBlocked("https://icloud-find-my.support-recovery.ga/")
expectBlocked("https://microsoft-office365.login-verify.cf/")

// Credentials in the URL disguise the real destination.
expectBlocked("https://www.google.com@evil.example.net/")

out("── suspicious but not blocked")

// Punycode is legitimate for real internationalised domains, so it warns.
expectSuspicious("https://xn--80ak6aa92e.com/")
// Raw IP over plain HTTP.
expectSuspicious("http://185.243.115.24/payload")
// High-risk TLD that collides with a filename.
expectSuspicious("https://invoice-march.zip/")
// Deep subdomain nesting pushes the real domain off the address bar.
expectSuspicious("https://a.b.c.d.e.f.example.org/")

out("── host inspection details")

func hasFinding(_ host: String, containing text: String) -> Bool {
    ThreatHeuristics.inspectHost(host).contains { $0.title.contains(text) }
}
if hasFinding("apple.com.evil.tk", containing: "Impersonates") {
    ok("names the impersonated brand")
} else { bad("impersonation title", "no Impersonates finding") }

if !hasFinding("apple.com", containing: "Impersonates") {
    ok("the real apple.com is not flagged as impersonating itself")
} else { bad("apple.com", "flagged its own brand") }

if !hasFinding("support.apple.com", containing: "Impersonates") {
    ok("apple's own subdomain is not flagged")
} else { bad("support.apple.com", "flagged a legitimate subdomain") }

out("── IPv4 detection")

func expectIP(_ host: String, _ want: Bool) {
    let got = ThreatHeuristics.isRawIPv4(host)
    if got == want { ok("\(host) isIP=\(got)") } else { bad(host, "got \(got) want \(want)") }
}
expectIP("192.168.1.1", true)
expectIP("8.8.8.8", true)
expectIP("255.255.255.255", true)
expectIP("256.1.1.1", false)      // out of range
expectIP("1.2.3", false)          // too few parts
expectIP("example.com", false)
expectIP("1.2.3.4.5", false)

out("── filenames")

func fileLevel(_ name: String) -> ThreatLevel {
    ThreatHeuristics.inspect(filename: name).map(\.level).max() ?? .clean
}
func expectFile(_ name: String, _ want: ThreatLevel) {
    let got = fileLevel(name)
    if got == want { ok("\(name) -> \(got.label)") }
    else { bad(name, "got \(got.label) want \(want.label)") }
}

// Ordinary documents.
expectFile("report.pdf", .clean)
expectFile("holiday.jpg", .clean)
expectFile("archive.zip", .clean)
expectFile("notes.txt", .clean)
expectFile("presentation.pptx", .clean)

// Programs are worth a warning but aren't malicious by themselves.
expectFile("Installer.dmg", .suspicious)
expectFile("setup.pkg", .suspicious)
expectFile("build.sh", .suspicious)

// Macro-bearing documents.
expectFile("invoice.docm", .suspicious)
expectFile("budget.xlsm", .suspicious)

// A document extension followed by an executable one is a deliberate disguise.
expectFile("invoice.pdf.app", .dangerous)
expectFile("photo.jpg.command", .dangerous)
expectFile("statement.docx.exe", .dangerous)

// Right-to-left override reverses how the name displays in Finder.
expectFile("photo\u{202E}gpj.app", .dangerous)

out("── content vs extension")

func expectMismatch(_ ext: String, _ sniffed: String?, _ shouldFlag: Bool) {
    let findings = ThreatHeuristics.inspectContent(extension: ext, sniffed: sniffed)
    let flagged = !findings.isEmpty
    if flagged == shouldFlag { ok(".\(ext) vs \(sniffed ?? "nil") flagged=\(flagged)") }
    else { bad("\(ext)/\(sniffed ?? "nil")", "got \(flagged) want \(shouldFlag)") }
}
expectMismatch("png", "png", false)
expectMismatch("jpg", "jpeg", false)     // interchangeable, not a mismatch
expectMismatch("jpeg", "jpg", false)
expectMismatch("tif", "tiff", false)
expectMismatch("pdf", "macho", true)     // a "PDF" that's actually a binary
expectMismatch("png", "exe", true)
expectMismatch("txt", nil, false)        // unknown header proves nothing

out("── magic numbers")

func expectSniff(_ bytes: [UInt8], _ want: String?) {
    let got = ThreatScanner.sniffType(Data(bytes))
    if got == want { ok("\(want ?? "nil") header") }
    else { bad("sniff", "got \(got ?? "nil") want \(want ?? "nil")") }
}
expectSniff([0x89, 0x50, 0x4E, 0x47, 0, 0, 0, 0], "png")
expectSniff([0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0], "jpg")
expectSniff([0x25, 0x50, 0x44, 0x46, 0, 0, 0, 0], "pdf")
expectSniff([0x50, 0x4B, 0x03, 0x04, 0, 0, 0, 0], "zip")
expectSniff([0xCF, 0xFA, 0xED, 0xFE, 0, 0, 0, 0], "macho")
expectSniff([0x4D, 0x5A, 0x90, 0x00, 0, 0, 0, 0], "exe")
expectSniff([0x23, 0x21, 0x2F, 0x62, 0, 0, 0, 0], "sh")
expectSniff([0x00, 0x01, 0x02, 0x03, 0, 0, 0, 0], nil)

out("── verdict aggregation")

let mixedFindings = ThreatVerdict(subject: "x", findings: [
    ThreatFinding(level: .notice, title: "a", detail: ""),
    ThreatFinding(level: .dangerous, title: "b", detail: ""),
    ThreatFinding(level: .suspicious, title: "c", detail: ""),
])
if mixedFindings.level == .dangerous { ok("verdict takes the worst finding") }
else { bad("aggregation", "got \(mixedFindings.level.label)") }
if mixedFindings.isBlocking { ok("dangerous blocks") } else { bad("isBlocking", "false") }

let cleanVerdict = ThreatVerdict(subject: "x", findings: [])
if cleanVerdict.level == .clean && !cleanVerdict.isBlocking { ok("empty verdict is clean") }
else { bad("empty verdict", "\(cleanVerdict.level.label)") }

let noticeOnly = ThreatVerdict(subject: "x", findings: [
    ThreatFinding(level: .notice, title: "signed", detail: ""),
])
if !noticeOnly.isBlocking { ok("a notice never blocks") } else { bad("notice", "blocked") }

out("")
out("\(pass) passed, \(fail) failed")
exit(fail == 0 ? 0 : 1)
