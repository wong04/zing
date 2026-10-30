import Foundation

// Toolbox search ranking. The point is that what people *type* finds the right
// tool first — so each case is a real query and the one answer it must lead
// with.

var pass = 0, fail = 0
func out(_ t: String) { print(t) }
func check(_ name: String, _ condition: Bool, _ detail: @autoclosure () -> String = "") {
    if condition { pass += 1; out("  ok   \(name)") }
    else { fail += 1; out("  FAIL \(name) \(detail())") }
}

// Catalog order, as the Toolbox shows it.
let order = [
    "section:Files", "section:Convert", "section:Download", "tool:Background Remover",
    "section:Open a Link", "section:Diagnose", "section:Network & VPN", "tool:Link Cleaner",
    "section:Security", "tool:Passwords", "tool:Hashes", "tool:HMAC", "tool:JWT Decoder",
    "tool:JSON / YAML", "tool:Base64", "tool:URL Encoding", "tool:Regex Tester", "tool:UUID",
    "tool:Timestamps", "tool:Cron Parser",
    "tool:Scientific Calculator", "tool:Graphing Calculator", "tool:Unit Converter",
    "tool:Number Bases",
    "tool:Text Case", "tool:Text Stats", "tool:Color Converter", "tool:QR Code",
]
let entries = order.map { id in
    ToolboxSearch.Entry(id: id, title: String(id.split(separator: ":", maxSplits: 1)[1]),
                        group: "", keywords: ToolboxSearch.keywords[id] ?? [])
}
func top(_ q: String) -> String { ToolboxSearch.rank(q, entries).first?.title ?? "(nothing)" }

out("── every feature has search words")
check("all \(order.count) catalog items have keywords",
      order.allSatisfy { !(ToolboxSearch.keywords[$0] ?? []).isEmpty },
      "\(order.filter { (ToolboxSearch.keywords[$0] ?? []).isEmpty })")
check("no keywords for things that don't exist",
      Set(ToolboxSearch.keywords.keys).isSubset(of: Set(order)),
      "\(Set(ToolboxSearch.keywords.keys).subtracting(order))")

out("── what people type finds the right thing first")
let cases: [(String, String)] = [
    ("sha256", "Hashes"), ("checksum", "Hashes"), ("md5", "Hashes"),
    ("youtube", "Download"), ("mp4", "Convert"), ("heic", "Convert"),
    ("epoch", "Timestamps"), ("unix time", "Timestamps"),
    ("vpn", "Network & VPN"), ("wireguard", "Network & VPN"),
    ("unblock", "Open a Link"), ("blocked", "Open a Link"),
    ("regex", "Regex Tester"), ("colour", "Color Converter"), ("rgb", "Color Converter"),
    ("remove background", "Background Remover"), ("json", "JSON / YAML"),
    ("yaml", "JSON / YAML"), ("plot", "Graphing Calculator"), ("graph", "Graphing Calculator"),
    ("calc", "Scientific Calculator"), ("celsius", "Unit Converter"),
    ("binary", "Number Bases"), ("camelcase", "Text Case"), ("word count", "Text Stats"),
    ("malware", "Security"), ("phishing", "Security"), ("token", "JWT Decoder"),
    ("guid", "UUID"), ("utm", "Link Cleaner"), ("cron", "Cron Parser"),
    ("password", "Passwords"), ("pdf", "Files"), ("qr", "QR Code"),
]
for (query, want) in cases {
    let got = top(query)
    check("\"\(query)\" → \(want)", got == want, "got \(got)")
}

out("── abbreviations and partial words")
check("\"gc\" finds Graphing Calculator", ToolboxSearch.rank("gc", entries).contains { $0.title == "Graphing Calculator" })
check("\"jwtd\" → JWT Decoder", top("jwtd") == "JWT Decoder", top("jwtd"))
check("\"conv\" → Convert (title beats keyword)", top("conv") == "Convert", top("conv"))
check("\"url enc\" → URL Encoding", top("url enc") == "URL Encoding", top("url enc"))

out("── no false matches")
check("nonsense matches nothing", ToolboxSearch.rank("zzqx", entries).isEmpty)
check("every word must match", ToolboxSearch.rank("json zzqx", entries).isEmpty)
check("empty query returns everything in order", ToolboxSearch.rank("  ", entries) == entries)

out(""); out("\(pass) passed, \(fail) failed")
exit(fail == 0 ? 0 : 1)
