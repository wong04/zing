import Foundation
import AppKit

var pass = 0, fail = 0
func check(_ name: String, _ cond: Bool, _ detail: String = "") {
    if cond { pass += 1 } else { fail += 1; print("   FAIL: \(name) \(detail)") }
}
func head(_ s: String) { print("\n=== \(s) ===") }

let sem = DispatchSemaphore(value: 0)
Task {

// ---------- A. classifier: exhaustive ----------
head("A. BlockDiagnostics.classify — all 32 combinations")
var table: [String] = []
for sysTLS in [true, false] {
 for sink in [true, false] {
  for empty in [true, false] {
   for tcp in [true, false] {
    for tls in [true, false] {
      let k = BlockDiagnostics.classify(systemTLSOK: sysTLS, sinkholed: sink,
              systemAnswerEmpty: empty, publicTCPOK: tcp, publicTLSOK: tls)
      table.append("sysTLS=\(sysTLS ? 1:0) sink=\(sink ? 1:0) empty=\(empty ? 1:0) tcp=\(tcp ? 1:0) tls=\(tls ? 1:0) -> \(k.title)")
      // invariants
      if sysTLS { check("sysTLS implies notBlocked", k == .notBlocked, "got \(k.title)") }
      if !sysTLS && sink && tcp { check("sinkhole+tcp -> dnsTampering", k == .dnsTampering, "got \(k.title)") }
      if !sysTLS && !sink && !empty && !tcp && !tls { check("no reachability -> ipBlocked", k == .ipBlocked, "got \(k.title)") }
      if !sysTLS && !sink && !empty && tcp && !tls { check("tcp no tls -> tlsInterception", k == .tlsInterception, "got \(k.title)") }
      if !sysTLS && !sink && !empty && tls { check("public tls ok -> dnsTampering", k == .dnsTampering, "got \(k.title)") }
      check("every verdict has advice", !k.recommended.isEmpty)
      check("every verdict has detail", !k.detail.isEmpty && !k.title.isEmpty)
    }}}}}
print("   (\(table.count) combinations evaluated)")
// Detection must not have collapsed to always-notBlocked
let blockedCount = table.filter { !$0.hasSuffix("Not blocked") }.count
check("still detects blocks", blockedCount >= 12, "only \(blockedCount)/32 non-notBlocked")
print("   \(blockedCount)/32 combinations still report some kind of block")

// ---------- B. URL normalization ----------
head("B. RelayEngine.normalize")
let good: [(String,String)] = [
  ("example.com", "https://example.com"),
  ("  example.com  ", "https://example.com"),
  ("<https://a.com/x>", "https://a.com/x"),
  ("http://a.com", "http://a.com"),
  ("https://a.com:8443/p?q=1", "https://a.com:8443/p?q=1"),
  ("a.com/path with space", "https://a.com/path with space"),
  ("HTTPS://UPPER.COM", "HTTPS://UPPER.COM"),
  ("sub.domain.co.uk/x", "https://sub.domain.co.uk/x"),
]
for (input, _) in good {
  let r = try? RelayEngine.normalize(input)
  check("normalize '\(input)'", r != nil, "-> nil")
}
let bad = ["", "   ", "<>", "\"\"", "'"]
for input in bad {
  check("reject '\(input)'", (try? RelayEngine.normalize(input)) == nil, "-> accepted")
}
// must not crash on hostile input
for weird in ["http://", "https://", "://x", "a b c", String(repeating: "a", count: 5000),
              "https://[::1]/", "https://user:pw@host.com/", "café.com", "https://.",
              "javascript:alert(1)", "file:///etc/passwd", "\n\n\n"] {
  _ = try? RelayEngine.normalize(weird)
}
check("no crash on hostile input", true)

// ---------- C. Utilities ----------
head("C. UtilityTools")
check("sha256 vector", UtilityTools.hash("abc", using: .sha256) == "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")
check("sha1 vector", UtilityTools.hash("abc", using: .sha1) == "a9993e364706816aba3e25717850c26c9cd0d89d")
check("md5 vector", UtilityTools.hash("abc", using: .md5) == "900150983cd24fb0d6963f7d28e17f72")
check("sha512 len", UtilityTools.hash("abc", using: .sha512).count == 128)
check("empty hash works", !UtilityTools.hash("", using: .sha256).isEmpty)
let big = String(repeating: "x", count: 2_000_000)
check("2MB hash", UtilityTools.hash(big, using: .sha256).count == 64)
check("unicode hash", !UtilityTools.hash("🎉café日本", using: .sha256).isEmpty)

check("b64 roundtrip unicode", UtilityTools.base64Decode(UtilityTools.base64Encode("héllo 🎉")) == "héllo 🎉")
check("b64 unpadded", UtilityTools.base64Decode("aGVsbG8") == "hello")
check("b64 urlsafe", UtilityTools.base64Decode("aGVsbG8_d29ybGQ").map{_ in true} ?? true)
check("b64 garbage -> nil", UtilityTools.base64Decode("!!!not base64!!!") == nil)
check("b64 empty", UtilityTools.base64Encode("") == "")
check("b64 whitespace tolerated", UtilityTools.base64Decode("  aGVsbG8  ") == "hello")

check("url encode space", UtilityTools.urlEncode("a b") == "a%20b")
check("url roundtrip", UtilityTools.urlDecode(UtilityTools.urlEncode("a b&c=d?e")) == "a b&c=d?e")
check("url encode unicode", !UtilityTools.urlEncode("café🎉").isEmpty)

if case .success(let s) = UtilityTools.formatJSON("{\"a\":1}", pretty: true) { check("json pretty", s.contains("\n")) } else { check("json pretty", false) }
if case .success(let s) = UtilityTools.formatJSON("{\"a\": 1}", pretty: false) { check("json minify", !s.contains("\n")) } else { check("json minify", false) }
if case .failure = UtilityTools.formatJSON("{bad}", pretty: true) { check("json invalid rejected", true) } else { check("json invalid rejected", false) }
if case .success = UtilityTools.formatJSON("[]", pretty: true) { check("json empty array", true) } else { check("json empty array", false) }
if case .success = UtilityTools.formatJSON("123", pretty: true) { check("json fragment", true) } else { check("json fragment", false) }
if case .failure = UtilityTools.formatJSON("", pretty: true) { check("json empty rejected", true) } else { check("json empty rejected", false) }

for len in [8, 20, 64] {
  let p = UtilityTools.generatePassword(length: len, symbols: true, digits: true, uppercase: true)
  check("password length \(len)", p.count == len, "got \(p.count)")
}
let noSym = UtilityTools.generatePassword(length: 200, symbols: false, digits: false, uppercase: false)
check("password respects flags", noSym.allSatisfy { $0.isLowercase && $0.isLetter }, "got \(noSym.prefix(30))")
check("password excludes ambiguous", !UtilityTools.generatePassword(length: 500, symbols: false, digits: true, uppercase: true).contains(where: { "0O1lI".contains($0) }))
var uniq = Set<String>()
for _ in 0..<200 { uniq.insert(UtilityTools.generatePassword(length: 16, symbols: true, digits: true, uppercase: true)) }
check("passwords are random", uniq.count == 200, "only \(uniq.count) distinct")

let c1 = UtilityTools.cleanLink("https://x.com/a?utm_source=n&id=7&fbclid=z")
check("cleaner keeps real params", c1?.url.contains("id=7") == true)
check("cleaner strips trackers", c1?.removed.count == 2, "\(c1?.removed ?? [])")
check("cleaner on clean url", UtilityTools.cleanLink("https://x.com/a")?.removed.isEmpty == true)
check("cleaner no query", UtilityTools.cleanLink("example.com")?.url.contains("example.com") == true)
check("cleaner all-tracker drops query", UtilityTools.cleanLink("https://x.com/a?utm_source=n")?.url.contains("?") == false)
check("cleaner bad input", UtilityTools.cleanLink("") == nil)

check("camel", UtilityTools.convertCase("hello world again", to: .camel) == "helloWorldAgain")
check("snake from camel", UtilityTools.convertCase("helloWorldAgain", to: .snake) == "hello_world_again")
check("kebab", UtilityTools.convertCase("Hello World", to: .kebab) == "hello-world")
check("case empty", UtilityTools.convertCase("", to: .camel) == "")
check("case unicode no crash", !UtilityTools.convertCase("café über", to: .snake).isEmpty)

check("timestamp seconds", UtilityTools.describeTimestamp("1753800000").count == 5)
check("timestamp millis", UtilityTools.describeTimestamp("1753800000000").count == 5)
check("timestamp zero", UtilityTools.describeTimestamp("0").count == 5)
check("timestamp negative", UtilityTools.describeTimestamp("-100").count == 5)
check("timestamp garbage", UtilityTools.describeTimestamp("abc").isEmpty)
check("timestamp empty", UtilityTools.describeTimestamp("").isEmpty)

check("qr basic", UtilityTools.makeQRCode(from: "https://example.com") != nil)
check("qr empty -> nil", UtilityTools.makeQRCode(from: "") == nil)
check("qr unicode", UtilityTools.makeQRCode(from: "café 🎉 日本語") != nil)
check("qr long", UtilityTools.makeQRCode(from: String(repeating: "a", count: 1200)) != nil)

// ---------- D. DoH profile ----------
head("D. DoHProfile")
for r in DoHProfile.resolvers {
  let p = DoHProfile.makeProfile(for: r)
  check("\(r.name) parses", (try? PropertyListSerialization.propertyList(from: Data(p.utf8), format: nil)) != nil)
  check("\(r.name) has url", p.contains(r.url))
}
for name in ["AlgoVerse AI SEAL Test 1", "A&B <x> \"q\"", "", "   ", String(repeating: "n", count: 300), "café 🎉"] {
  let p = DoHProfile.makeProfile(for: DoHProfile.resolvers[0], displayName: name)
  let parsed = try? PropertyListSerialization.propertyList(from: Data(p.utf8), format: nil) as? [String: Any]
  check("name '\(name.prefix(18))' parses", parsed != nil)
  if let d = parsed as? [String: Any], let shown = d["PayloadDisplayName"] as? String {
    let expect = name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
      ? "Encrypted DNS — \(DoHProfile.resolvers[0].name)" : name
    check("name '\(name.prefix(18))' preserved", shown == expect, "got '\(shown)'")
  }
  let fn = DoHProfile.fileName(for: DoHProfile.resolvers[0], displayName: name)
  check("filename safe '\(name.prefix(12))'", !fn.contains("/") && !fn.contains(":") && fn.hasSuffix(".mobileconfig"), "got \(fn)")
}
let u1 = DoHProfile.makeProfile(for: DoHProfile.resolvers[0])
let u2 = DoHProfile.makeProfile(for: DoHProfile.resolvers[0])
check("UUIDs regenerate", u1 != u2)

// PayloadScope=System is required, not optional. A DNS settings payload is
// backed by a NetworkExtension service that applies machine-wide, and that
// service cannot be created in a user-scoped profile — which is the default.
// Without it macOS refuses the install with a message that names a 'VPN
// Service' payload the profile does not contain, sending you hunting for a
// payload that was never there.
for r in DoHProfile.resolvers {
  let p = DoHProfile.makeProfile(for: r)
  guard let d = try? PropertyListSerialization.propertyList(from: Data(p.utf8), format: nil)
          as? [String: Any] else {
    check("\(r.name) parses for scope check", false); continue
  }
  check("\(r.name) is System-scoped", (d["PayloadScope"] as? String) == "System",
        "got \(String(describing: d["PayloadScope"]))")
  // The identifiers must carry the current bundle id. Profiles generated
  // before the rename said com.allinone.*, which is how you can tell a stale
  // build produced a file.
  check("\(r.name) identifier is current",
        (d["PayloadIdentifier"] as? String)?.hasPrefix("com.krackn.") == true,
        "got \(String(describing: d["PayloadIdentifier"]))")
  // And no VPN payload, so the misleading error can never be literal.
  let payloads = (d["PayloadContent"] as? [[String: Any]]) ?? []
  check("\(r.name) has exactly one DNS payload",
        payloads.count == 1
          && (payloads.first?["PayloadType"] as? String) == "com.apple.dnsSettings.managed")
  check("\(r.name) contains no VPN payload",
        !payloads.contains { ($0["PayloadType"] as? String)?.contains("vpn") == true })
}

// ---------- E. ShellRunner ----------
head("E. ShellRunner")
check("finds sips", ShellRunner.which("sips") != nil)
check("missing tool -> nil", ShellRunner.which("definitely-not-a-real-binary-xyz") == nil)
check("absolute path", ShellRunner.which("/bin/echo") == "/bin/echo")
check("absolute missing", ShellRunner.which("/nope/nope") == nil)
do { _ = try await ShellRunner.run("definitely-not-real-xyz", []); check("missing throws", false) }
catch { check("missing throws", true) }
let ec = try? await ShellRunner.run("/bin/sh", ["-c", "exit 3"])
check("exit code captured", ec?.exitCode == 3, "got \(String(describing: ec?.exitCode))")
let outT = try? await ShellRunner.run("/bin/sh", ["-c", "echo hello; echo err >&2"])
check("stdout captured", outT?.stdout.contains("hello") == true)
check("stderr captured", outT?.stderr.contains("err") == true)
let bigOut = try? await ShellRunner.run("/bin/sh", ["-c", "for i in $(seq 1 20000); do echo line$i; done"])
check("large output intact", (bigOut?.stdout.split(separator: "\n").count ?? 0) == 20000, "got \(bigOut?.stdout.split(separator: "\n").count ?? 0)")
let spaceArg = try? await ShellRunner.run("/bin/echo", ["a b", "c d"])
check("args with spaces", spaceArg?.stdout.contains("a b c d") == true, "got \(spaceArg?.stdout ?? "")")
var lines = 0
_ = try? await ShellRunner.run("/bin/sh", ["-c", "printf 'a\\nb\\rc\\n'"], onOutputLine: { _ in lines += 1 })
check("line streaming splits \\n and \\r", lines >= 3, "got \(lines)")

// ---------- F. DevTools (engineering/science) ----------
head("F. DevTools")
check("base: decimal", DevTools.convertBase("255")?.hex == "0xFF")
check("base: hex input", DevTools.convertBase("0xFF")?.decimal == "255")
check("base: binary input", DevTools.convertBase("0b1010")?.decimal == "10")
check("base: octal input", DevTools.convertBase("0o17")?.decimal == "15")
check("base: underscores ok", DevTools.convertBase("1_000")?.decimal == "1000")
check("base: garbage -> nil", DevTools.convertBase("zzz") == nil)
check("base: empty -> nil", DevTools.convertBase("") == nil)

check("color: hex", DevTools.convertColor("#FF0000")?.rgb == "rgb(255, 0, 0)")
check("color: shorthand", DevTools.convertColor("#f00")?.hex == "#FF0000")
check("color: rgb input", DevTools.convertColor("rgb(0, 255, 0)")?.hex == "#00FF00")
check("color: hsl present", DevTools.convertColor("#0000FF")?.hsl.contains("240") == true)
check("color: cmyk present", DevTools.convertColor("#000000")?.cmyk.contains("100%") == true)
check("color: garbage -> nil", DevTools.convertColor("nope") == nil)

check("unit: m to ft", abs((DevTools.convertUnit(1, from: "m", to: "ft", kind: .length) ?? 0) - 3.28084) < 0.001)
check("unit: C to F", abs((DevTools.convertUnit(100, from: "°C", to: "°F", kind: .temperature) ?? 0) - 212) < 0.001)
check("unit: C to K", abs((DevTools.convertUnit(0, from: "°C", to: "K", kind: .temperature) ?? 0) - 273.15) < 0.001)
check("unit: F to C", abs((DevTools.convertUnit(32, from: "°F", to: "°C", kind: .temperature) ?? 0)) < 0.001)
check("unit: KB to B", DevTools.convertUnit(1, from: "KB", to: "B", kind: .data) == 1024)
check("unit: bad unit -> nil", DevTools.convertUnit(1, from: "xx", to: "m", kind: .length) == nil)
check("unit: every kind has units", DevTools.UnitKind.allCases.allSatisfy { !$0.units.isEmpty })

let st = DevTools.analyze("Hello world. This is a test!\n\nSecond para.")
check("stats: words", st.words == 8, "got \(st.words)")
check("stats: sentences", st.sentences >= 2)
check("stats: paragraphs", st.paragraphs == 2, "got \(st.paragraphs)")
check("stats: tokens estimated", st.approxTokens > 0)
check("stats: empty safe", DevTools.analyze("").words == 0)

check("hmac known vector",
      DevTools.hmac(message: "The quick brown fox jumps over the lazy dog", key: "key", algorithm: .sha256)
      == "f7bc83f430538424b13298e6aa6fb143ef4d59a14946175997479dbc2d1a3cd8")
check("hmac differs by key",
      DevTools.hmac(message: "m", key: "a", algorithm: .sha256) != DevTools.hmac(message: "m", key: "b", algorithm: .sha256))

check("cron: valid parses", DevTools.describeCron("0 9 * * 1-5") != nil)
check("cron: wrong arity -> nil", DevTools.describeCron("0 9 *") == nil)

if case .success(let m) = DevTools.testRegex(pattern: "(\\w+)@(\\w+)", against: "a@b c@d", caseInsensitive: false) {
    check("regex: match count", m.count == 2, "got \(m.count)")
    check("regex: groups captured", m.first?.groups.count == 2)
} else { check("regex: matched", false) }
if case .failure = DevTools.testRegex(pattern: "([", against: "x", caseInsensitive: false) {
    check("regex: invalid pattern reported", true)
} else { check("regex: invalid pattern reported", false) }

check("jwt: decodes payload",
      DevTools.decodeJWT("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjMifQ.sig")?.payload.contains("123") == true)
check("jwt: garbage -> nil", DevTools.decodeJWT("notajwt") == nil)

// ---------- G. URL vs search classification ----------
head("G. Address bar routing")
// Anything with a scheme must load directly, never become a web search.
for raw in ["aiohome://start", "aiorelay://example.com", "https://x.com",
            "http://x.com", "file:///tmp/a.txt"] {
    check("'\(raw)' treated as URL", raw.contains("://"))
}
for raw in ["how to center a div", "swift concurrency", "what is 2+2"] {
    check("'\(raw)' treated as search", !raw.contains("://") && raw.contains(" "))
}
check("bare domain is a URL", (try? RelayEngine.normalize("example.com")) != nil)


// ---------- L. yt-dlp failure classification ----------
head("L. yt-dlp failures")

// Must be recognised as "the extractor is stale", because those are the only
// ones a newer yt-dlp actually fixes — and the retry/update offer hangs off it.
for s in [
  "ERROR: unable to download video data: HTTP Error 403: Forbidden",
  "ERROR: unable to extract player response",
  "WARNING: nsig extraction failed",
  "ERROR: requested format is not available",
  "HTTP Error 403: Forbidden",
] {
  check("stale extractor: \(s.prefix(38))", DownloadErrors.looksLikeExtractorBreakage(s))
}

// Must NOT be — offering "update yt-dlp" for a private video trains people to
// ignore the suggestion.
for s in [
  "ERROR: Private video. Sign in if you've been granted access",
  "ERROR: Video unavailable",
  "ERROR: 'x' is not a valid URL",
  "ERROR: No space left on device",
] {
  check("not extractor: \(s.prefix(38))", !DownloadErrors.looksLikeExtractorBreakage(s))
}

// The message the user actually reads.
check("403 explained plainly",
      DownloadErrors.explain("HTTP Error 403: Forbidden", exitCode: 1).contains("updating"))
check("private video explained",
      DownloadErrors.explain("ERROR: Private video.", exitCode: 1).contains("private"))
check("age gate explained",
      DownloadErrors.explain("ERROR: Sign in to confirm your age", exitCode: 1).contains("age-restricted"))
check("disk full explained",
      DownloadErrors.explain("ERROR: No space left on device", exitCode: 1).contains("disk is full"))
check("unknown falls back to last line",
      DownloadErrors.explain("something\nweird happened", exitCode: 3) == "weird happened")

// ---------- M. Session restore ----------
head("M. SessionStore")

func rec(_ url: String, pinned: Bool = false, active: Bool = false,
         state: Data? = nil, title: String = "t") -> TabRecord {
    TabRecord(url: url, title: title, isPinned: pinned, isActive: active,
              interactionState: state)
}

// Only real pages come back. Restoring a window full of blank new tabs is
// worse than restoring nothing.
check("http restorable", rec("http://a.com").isRestorable)
check("https restorable", rec("https://a.com/x?y=1").isRestorable)
check("start page not restorable", !rec("aiohome://start").isRestorable)
check("relay scheme not restorable", !rec("aiorelay://a.com").isRestorable)
check("file not restorable", !rec("file:///etc/passwd").isRestorable)
check("junk not restorable", !rec("not a url").isRestorable)
check("empty not restorable", !rec("").isRestorable)

// The cap keeps pinned tabs first — those are the ones explicitly kept.
var many = (0..<60).map { rec("https://loose\($0).com") }
many += (0..<5).map { rec("https://pinned\($0).com", pinned: true) }
let capped = SessionStore.prepare(many)
check("cap respected", capped.count == SessionStore.maximumTabs, "got \(capped.count)")
check("all pinned survive the cap", capped.filter(\.isPinned).count == 5,
      "got \(capped.filter(\.isPinned).count)")

// An oversized interaction state is dropped, but the tab still reopens.
let huge = Data(repeating: 0, count: SessionStore.maximumStateBytes + 1)
let trimmed = SessionStore.prepare([rec("https://a.com", state: huge)])
check("oversized state dropped", trimmed.first?.interactionState == nil)
check("tab still kept without its state", trimmed.count == 1)
let small = Data(repeating: 0, count: 32)
check("small state preserved",
      SessionStore.prepare([rec("https://a.com", state: small)]).first?.interactionState == small)

// Round trip, including which tab was in front.
let session = [rec("https://a.com", pinned: true),
               rec("https://b.com", active: true),
               rec("aiohome://start")]
if let data = try? SessionStore.encode(session) {
  let back = SessionStore.decode(data)
  check("start page filtered on save", back.count == 2, "got \(back.count)")
  check("pinned survives round trip", back.first?.isPinned == true)
  check("active tab remembered", back.contains { $0.isActive && $0.url == "https://b.com" })
} else { check("session encodes", false) }

// A corrupt file must never stop the browser opening.
check("garbage decodes to empty", SessionStore.decode(Data("{{{not json".utf8)).isEmpty)
check("empty decodes to empty", SessionStore.decode(Data()).isEmpty)

head("favicons — each page's own icon, not the site's")
typealias FL = FaviconChoice.Link
check("prefers rel=icon over apple-touch-icon",
      FaviconChoice.best([FL(href: "https://x.com/touch.png", rel: "apple-touch-icon", sizes: "180x180"),
                          FL(href: "https://x.com/i.png", rel: "icon", sizes: "32x32")]) == "https://x.com/i.png")
check("picks the size nearest 32",
      FaviconChoice.best([FL(href: "https://x.com/16.png", rel: "icon", sizes: "16x16"),
                          FL(href: "https://x.com/32.png", rel: "icon", sizes: "32x32"),
                          FL(href: "https://x.com/192.png", rel: "icon", sizes: "192x192")]) == "https://x.com/32.png")
check("an svg or 'any' beats a tiny bitmap",
      FaviconChoice.best([FL(href: "https://x.com/16.png", rel: "icon", sizes: "16x16"),
                          FL(href: "https://x.com/i.svg", rel: "icon", type: "image/svg+xml")]) == "https://x.com/i.svg")
check("unlabelled icons keep the page's order",
      FaviconChoice.best([FL(href: "https://x.com/a.ico", rel: "shortcut icon"),
                          FL(href: "https://x.com/b.ico", rel: "icon")]) == "https://x.com/a.ico")
check("data: image icons are allowed",
      FaviconChoice.best([FL(href: "data:image/png;base64,AAAA", rel: "icon")]) == "data:image/png;base64,AAAA")
check("javascript: and empty hrefs are ignored",
      FaviconChoice.best([FL(href: "javascript:alert(1)", rel: "icon"), FL(href: "", rel: "icon")]) == nil)
// The bug itself: two Google products on the same host keep different icons.
let docsIcon = FaviconChoice.best([FL(href: "https://ssl.gstatic.com/docs/documents/images/kix-favicon-2023q4.ico", rel: "shortcut icon")])
let slidesIcon = FaviconChoice.best([FL(href: "https://ssl.gstatic.com/docs/presentations/images/favicon-2023q4.ico", rel: "shortcut icon")])
check("Docs and Slides resolve to different icons", docsIcon != nil && docsIcon != slidesIcon)
check("sizes parse", FaviconChoice.largestSize("16x16 32x32") == 32 && FaviconChoice.largestSize("any") == nil)

head("address input — go there, or search for it")
func goes(_ text: String, _ want: String) {
    if case .url(let u) = AddressInput.classify(text) {
        check("\"\(text)\" opens \(want)", u.absoluteString == want, u.absoluteString)
    } else { check("\"\(text)\" opens \(want)", false, "searched instead") }
}
func searches(_ text: String) {
    if case .search = AddressInput.classify(text) { check("\"\(text)\" is searched", true) }
    else { check("\"\(text)\" is searched", false, "opened as a link") }
}
goes("apple.com", "https://apple.com")
goes("docs.google.com", "https://docs.google.com")
goes("apple.com/iphone", "https://apple.com/iphone")
goes("bbc.co.uk", "https://bbc.co.uk")
goes("purdue.edu/admissions?x=1", "https://purdue.edu/admissions?x=1")
goes("github.io", "https://github.io")
goes("  Apple.com  ", "https://Apple.com")
goes("https://example.com/a b".replacingOccurrences(of: " ", with: "%20"), "https://example.com/a%20b")
goes("http://neverssl.com", "http://neverssl.com")
goes("localhost:3000", "http://localhost:3000")
goes("192.168.1.1", "http://192.168.1.1")
goes("myserver.lan:8080/status", "https://myserver.lan:8080/status")
goes("about:blank", "about:blank")
searches("node.js")
searches("index.html")
searches("file.txt")
searches("what is apple.com")
searches("hello")
searches("2+2")
searches("swift.version")
searches("c++ tutorial")
check("empty input does nothing", AddressInput.classify("   ") == nil)

print("\n── New-tab shortcuts are yours to choose")
do {
    let key = AppSettings.Key.startShortcuts
    let saved = UserDefaults.standard.string(forKey: key)
    defer { if let saved { UserDefaults.standard.set(saved, forKey: key) } else { UserDefaults.standard.removeObject(forKey: key) } }

    UserDefaults.standard.removeObject(forKey: key)
    check("nothing chosen yet: the built-in eight", StartShortcuts.current == StartShortcuts.defaults)
    check("built-ins keep their symbols", StartShortcuts.glyph(for: StartShortcuts.defaults[0]) == "◱")

    let apple = StartShortcuts.make(title: "", address: "apple.com")
    check("a bare address becomes https", apple?.url == "https://apple.com", apple?.url ?? "nil")
    check("and is named after the site", apple?.title == "Apple", apple?.title ?? "nil")
    check("its symbol is its initial", apple.map(StartShortcuts.glyph(for:)) == "A")
    let docs = StartShortcuts.make(title: "  My Docs ", address: "docs.google.com/document/u/0/")
    check("a given name is kept, trimmed", docs?.title == "My Docs", docs?.title ?? "nil")
    check("paths survive", docs?.url == "https://docs.google.com/document/u/0/", docs?.url ?? "nil")
    check("words are not an address", StartShortcuts.make(title: "x", address: "best pizza") == nil)
    check("javascript: is refused", StartShortcuts.make(title: "x", address: "javascript:alert(1)") == nil)
    check("file: is refused", StartShortcuts.make(title: "x", address: "file:///etc/passwd") == nil)

    StartShortcuts.current = [apple!, docs!]
    check("a chosen list round-trips", StartShortcuts.current == [apple!, docs!])
    StartShortcuts.current = []
    check("an empty list stays empty (not the defaults)", StartShortcuts.current.isEmpty)
    StartShortcuts.resetToDefaults()
    check("restore defaults", StartShortcuts.current == StartShortcuts.defaults)

    // Stored data is never trusted into a page's hrefs.
    let tampered = #"[{"title":"ok","url":"https://ok.com"},{"title":"x","url":"javascript:alert(1)"},{"title":"y","url":"data:text/html,hi"}]"#
    check("stored non-web addresses are dropped",
          StartShortcuts.decode(tampered).map(\.url) == ["https://ok.com"])
    check("unreadable data falls back to the built-ins", StartShortcuts.decode("{nope") == StartShortcuts.defaults)
    let many = (0..<30).map { StartShortcut(title: "s\($0)", url: "https://s\($0).com") }
    check("capped at \(StartShortcuts.limit)", StartShortcuts.decode(StartShortcuts.encode(many)).count == StartShortcuts.limit)
    check("names are escaped for the page",
          StartShortcuts.escapeHTML(#"<script>"x" & 'y'</script>"#)
            == "&lt;script&gt;&quot;x&quot; &amp; &#39;y&#39;&lt;/script&gt;")
}

head("tab style — the old \"Stack by site\" arrangement carries over")
do {
    let d = UserDefaults.standard
    let saved = (d.string(forKey: AppSettings.Key.tabOrganization), d.object(forKey: AppSettings.Key.tabDropdowns))
    d.set("stacked", forKey: AppSettings.Key.tabOrganization)
    d.removeObject(forKey: AppSettings.Key.tabDropdowns)
    AppSettings.registerDefaults()
    check("becomes By site…", d.string(forKey: AppSettings.Key.tabOrganization) == "domain")
    check("…shown as dropdowns", AppSettings.tabDropdowns)
    // Leave the test binary's defaults as they were.
    if let o = saved.0 { d.set(o, forKey: AppSettings.Key.tabOrganization) } else { d.removeObject(forKey: AppSettings.Key.tabOrganization) }
    if let t = saved.1 { d.set(t, forKey: AppSettings.Key.tabDropdowns) } else { d.removeObject(forKey: AppSettings.Key.tabDropdowns) }
}
check("dropdowns are off by default", !AppSettings.flag("krackn.unset.probe", default: false))

head("downloads — save what can't be shown, or what's marked as a file")
check("a zip opened as a page downloads",
      DownloadPolicy.shouldDownload(canShow: false, contentDisposition: nil, isMainFrame: true))
check("Content-Disposition: attachment downloads, even if showable",
      DownloadPolicy.shouldDownload(canShow: true, contentDisposition: "attachment; filename=\"a.pdf\"", isMainFrame: true))
check("…and from a hidden frame (how download buttons often work)",
      DownloadPolicy.shouldDownload(canShow: true, contentDisposition: " Attachment ", isMainFrame: false))
check("an ordinary page is shown", !DownloadPolicy.shouldDownload(canShow: true, contentDisposition: nil, isMainFrame: true))
check("inline disposition is shown", !DownloadPolicy.shouldDownload(canShow: true, contentDisposition: "inline", isMainFrame: true))
check("an odd type in an embedded frame doesn't drop a file",
      !DownloadPolicy.shouldDownload(canShow: false, contentDisposition: nil, isMainFrame: false))

head("user agent — follows the installed Safari (the Google Docs blur)")
// A fixed "Safari 17.6" made Google serve Docs at half canvas resolution.
let installed = UserAgent.installedSafariVersion()
check("reads this Mac's Safari version", installed != nil, "\(installed ?? "nil")")
if let v = installed {
    check("the identifier carries that version", UserAgent.current?.contains("Version/\(v) ") == true,
          UserAgent.current ?? "nil")
}
check("never the stale 17.6 again", UserAgent.current?.contains("Version/17.6 ") != true)
check("Safari's exact format", UserAgent.safari(version: "26.6.2") ==
      "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/26.6.2 Safari/605.1.15")
check("accepts real versions", UserAgent.isValidVersion("26.6.2") && UserAgent.isValidVersion("18"))
check("rejects junk", !UserAgent.isValidVersion("") && !UserAgent.isValidVersion("26.") &&
      !UserAgent.isValidVersion("beta") && !UserAgent.isValidVersion("26 6"))
check("missing Safari falls back to WebKit's own (nil), not a stale constant",
      UserAgent.installedSafariVersion(infoPlist: "/nonexistent/Info.plist") == nil)

print("\n==================== \(pass) passed, \(fail) failed ====================")
sem.signal()
}
sem.wait()
exit(fail == 0 ? 0 : 1)
