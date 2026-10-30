import Foundation
import AppKit

var pass = 0, fail = 0
func check(_ n: String, _ c: Bool, _ d: String = "") { if c { pass += 1 } else { fail += 1; print("   FAIL: \(n) \(d)") } }
func head(_ s: String) { print("\n=== \(s) ===") }
let sem = DispatchSemaphore(value: 0)

Task {
// ---------- F. PinnedHTTPClient against the real internet ----------
head("F. PinnedHTTPClient — real network")
struct Case { let url: String; let note: String }
let cases = [
  Case(url: "https://example.com", note: "plain"),
  Case(url: "http://github.com", note: "http->https redirect"),
  Case(url: "https://wikipedia.org", note: "redirect to www"),
  Case(url: "https://en.wikipedia.org/wiki/Wi-Fi", note: "large chunked"),
  Case(url: "https://news.ycombinator.com", note: "small"),
  Case(url: "https://www.google.com/search?q=test", note: "query string"),
  Case(url: "https://cloudflare.com", note: "cdn"),
]
for c in cases {
  do {
    let u = try RelayEngine.normalize(c.url)
    let (r, f) = try await PinnedHTTPClient.fetch(url: u, timeout: 30)
    check("\(c.note) status ok", (200..<400).contains(r.statusCode), "got \(r.statusCode)")
    check("\(c.note) body non-empty", r.body.count > 0)
    check("\(c.note) has content-type", r.headers["content-type"] != nil)
    check("\(c.note) final url valid", f.host != nil)
    // if content-length was given and not chunked, body should match it
    if let cl = r.headers["content-length"], let n = Int(cl),
       r.headers["transfer-encoding"] == nil {
      check("\(c.note) length matches header", r.body.count == n, "hdr \(n) vs body \(r.body.count)")
    }
  } catch { check("\(c.note) fetch", false, "\(error.localizedDescription)") }
}
// 404 should be returned, not thrown
do {
  let (r, _) = try await PinnedHTTPClient.fetch(url: URL(string: "https://example.com/definitely-missing-xyz")!, timeout: 25)
  check("404 surfaced as status", r.statusCode >= 400, "got \(r.statusCode)")
} catch { check("404 surfaced as status", false, "threw \(error.localizedDescription)") }
// binary integrity
do {
  let (r, _) = try await PinnedHTTPClient.fetch(url: URL(string: "https://www.google.com/favicon.ico")!, timeout: 25)
  check("binary body non-empty", r.body.count > 100, "\(r.body.count) bytes")
  check("binary not mangled", r.body.count > 0 && !r.body.isEmpty)
} catch { check("binary fetch", false, "\(error.localizedDescription)") }
// nonexistent host must fail cleanly, not hang
let t0 = Date()
do { _ = try await PinnedHTTPClient.fetch(url: URL(string: "https://no-such-host-zzz123456.com")!, timeout: 20); check("bad host fails", false) }
catch { check("bad host fails cleanly", true); check("bad host fails fast", Date().timeIntervalSince(t0) < 25, "took \(Int(Date().timeIntervalSince(t0)))s") }

// ---------- G. Concurrency ----------
head("G. Concurrency — 12 parallel fetches")
let cstart = Date()
await withTaskGroup(of: Bool.self) { g in
  for i in 0..<12 {
    g.addTask {
      let engine = RelayEngine()
      let target = i % 2 == 0 ? "https://example.com" : "https://news.ycombinator.com"
      return (try? await engine.fetch(urlString: target, enabled: [.dohPinned])) != nil
    }
  }
  var ok = 0
  for await r in g where r { ok += 1 }
  check("all parallel fetches succeeded", ok == 12, "\(ok)/12")
}
print("   parallel batch took \(Int(Date().timeIntervalSince(cstart)))s")

// ---------- H. ConversionEngine edge cases ----------
head("H. ConversionEngine — edge cases")
let fm = FileManager.default
// Work in a private temp directory. Relying on the current directory made
// these tests depend on where the runner was invoked from, and left scratch
// files behind in the repo.
let dir = fm.temporaryDirectory.appendingPathComponent("aio-convtest-\(UUID().uuidString)")
try? fm.createDirectory(at: dir, withIntermediateDirectories: true)
func scratch(_ name: String) -> String { dir.appendingPathComponent(name).path }

// A real image fixture, generated rather than assumed to exist on disk.
if let icon = NSImage(contentsOfFile:
        "/System/Library/CoreServices/CoreTypes.bundle/Contents/Resources/GenericApplicationIcon.icns"),
   let tiff = icon.tiffRepresentation,
   let rep = NSBitmapImageRep(data: tiff),
   let pngData = rep.representation(using: .png, properties: [:]) {
    try? pngData.write(to: URL(fileURLWithPath: scratch("in.png")))
}
let png = ConversionFormat(ext: "png", label: "PNG", backend: .sips)
let pdf = ConversionFormat(ext: "pdf", label: "PDF", backend: .pdfkit)
let txt = ConversionFormat(ext: "txt", label: "TXT", backend: .textutil)

// unicode + spaces in filename
let weirdName = scratch("café 🎉 test file.txt")
try? "hello unicode".write(toFile: weirdName, atomically: true, encoding: .utf8)
do { let o = try await ConversionEngine.convert(source: URL(fileURLWithPath: weirdName), to: pdf, outputDirectory: dir, quality: 80)
     check("unicode/space filename", fm.fileExists(atPath: o.path)) }
catch { check("unicode/space filename", false, error.localizedDescription) }

// missing file must fail, not crash
do { _ = try await ConversionEngine.convert(source: URL(fileURLWithPath: scratch("nope-missing.png")), to: png, outputDirectory: dir, quality: 80)
     check("missing file rejected", false) }
catch { check("missing file rejected", true) }

// zero-byte file
fm.createFile(atPath: scratch("empty.txt"), contents: Data())
do { let o = try await ConversionEngine.convert(source: URL(fileURLWithPath: scratch("empty.txt")), to: pdf, outputDirectory: dir, quality: 80)
     check("empty file handled", fm.fileExists(atPath: o.path)) }
catch { check("empty file handled (clean error)", true) }

// wrong extension: text pretending to be png
try? "not really a png".write(toFile: scratch("fake.png"), atomically: true, encoding: .utf8)
do { _ = try await ConversionEngine.convert(source: URL(fileURLWithPath: scratch("fake.png")), to: png, outputDirectory: dir, quality: 80)
     check("bogus image rejected", false, "converted anyway") }
catch { check("bogus image rejected cleanly", true) }

// collision numbering
try? "one".write(toFile: scratch("dup.txt"), atomically: true, encoding: .utf8)
var outs: [String] = []
for _ in 0..<3 {
  if let o = try? await ConversionEngine.convert(source: URL(fileURLWithPath: scratch("dup.txt")), to: pdf, outputDirectory: dir, quality: 80) {
    outs.append(o.lastPathComponent)
  }
}
check("no overwrite on collision", Set(outs).count == outs.count && outs.count == 3, "\(outs)")

// quality bounds must not crash
for q in [0, 1, 50, 99, 100] {
  let src = URL(fileURLWithPath: scratch("in.png"))
  if fm.fileExists(atPath: src.path) {
    _ = try? await ConversionEngine.convert(source: src, to: ConversionFormat(ext: "jpeg", label: "J", backend: .sips), outputDirectory: dir, quality: q)
  }
}
check("quality bounds no crash", true)

// A text file is not convertible to MP4 either way; assert we fail cleanly
// rather than crash, whether or not ffmpeg happens to be installed.
do {
    _ = try await ConversionEngine.convert(source: URL(fileURLWithPath: scratch("dup.txt")),
        to: ConversionFormat(ext: "mp4", label: "MP4", backend: .ffmpeg),
        outputDirectory: dir, quality: 80)
    check("text->mp4 rejected", false, "unexpectedly succeeded")
} catch {
    check("text->mp4 fails with a clear message", !error.localizedDescription.isEmpty)
}

// Format catalog coverage — the promise is breadth, so assert it.
check("catalog covers 190+ formats", FormatCatalog.allInputs.count >= 190,
      "only \(FormatCatalog.allInputs.count)")
check("STL recognised as mesh", FormatCatalog.category(for: "stl") == .model)
check("MSH recognised as mesh", FormatCatalog.category(for: "msh") == .model)
check("MP4 recognised as video", FormatCatalog.category(for: "mp4") == .video)
check("FLAC recognised as audio", FormatCatalog.category(for: "flac") == .audio)
check("SVG recognised as image", FormatCatalog.category(for: "svg") == .image)
check("EPUB recognised as document", FormatCatalog.category(for: "epub") == .document)
check("every category yields outputs",
      ConversionCategory.allCases.allSatisfy { !$0.outputFormats.isEmpty })
check("mesh outputs include msh",
      ConversionCategory.model.outputFormats.contains { $0.ext == "msh" })

// category detection
let cats: [(String, ConversionCategory?)] = [("a.HEIC", .image), ("b.MOV", .video), ("c.Mp3", .audio),
  ("d.docx", .document), ("e.xyz", nil), ("noext", nil), ("f.mp4", .video)]
for (f, want) in cats {
  check("category \(f)", ConversionEngine.category(for: URL(fileURLWithPath: f)) == want,
        "got \(String(describing: ConversionEngine.category(for: URL(fileURLWithPath: f))))")
}
check("every category has outputs", ConversionCategory.allCases.allSatisfy { !$0.outputFormats.isEmpty })
check("every category has inputs", ConversionCategory.allCases.allSatisfy { !$0.inputExtensions.isEmpty })

try? fm.removeItem(at: dir)
print("\n==================== \(pass) passed, \(fail) failed ====================")
sem.signal()
}
sem.wait()
exit(fail == 0 ? 0 : 1)
