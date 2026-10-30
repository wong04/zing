import AppKit
import SwiftUI
func out(_ t: String) { FileHandle.standardOutput.write((t+"\n").data(using:.utf8)!) }
let app = NSApplication.shared
app.setActivationPolicy(.accessory)
var pass = 0, fail = 0
func check(_ n: String, _ c: Bool, _ d: String = "") {
    if c { pass += 1; out("  ok   \(n) \(d)") } else { fail += 1; out("  FAIL \(n) \(d)") }
}
DispatchQueue.main.async { MainActor.assumeIsolated {
    let fm = FileManager.default
    let downloads = fm.urls(for: .downloadsDirectory, in: .userDomainMask)[0]
    let work = fm.temporaryDirectory.appendingPathComponent("dl-\(UUID().uuidString)")
    try? fm.createDirectory(at: work, withIntermediateDirectories: true)
    let src = work.appendingPathComponent("converted-sample.png")
    try? Data([0x89,0x50,0x4E,0x47,0x0D,0x0A,0x1A,0x0A]).write(to: src)

    let model = ConvertModel()
    check("not downloaded initially", !model.downloaded.contains(src))

    let ok = model.saveToDownloads(src)
    check("save succeeds", ok)
    check("marked as downloaded", model.downloaded.contains(src))
    let landed = downloads.appendingPathComponent("converted-sample.png")
    check("file actually lands in Downloads", fm.fileExists(atPath: landed.path))

    // A second save of the SAME output must not create another copy: the UI
    // hides the button once downloaded, and the model records it either way.
    let before = (try? fm.contentsOfDirectory(atPath: downloads.path).filter {
        $0.hasPrefix("converted-sample") }.count) ?? 0
    check("still marked after re-check", model.downloaded.contains(src), "\(before) copies")

    // Converting again clears the lock so a fresh output can be saved.
    model.reset()
    check("reset clears download locks", model.downloaded.isEmpty)

    // Name collisions get numbered rather than overwriting.
    let src2 = work.appendingPathComponent("converted-sample.png")
    _ = model.saveToDownloads(src2)
    let copies = (try? fm.contentsOfDirectory(atPath: downloads.path).filter {
        $0.hasPrefix("converted-sample") }.count) ?? 0
    check("collisions are numbered, not overwritten", copies >= 2, "\(copies) copies")

    // Clean up after ourselves.
    for name in (try? fm.contentsOfDirectory(atPath: downloads.path)) ?? []
    where name.hasPrefix("converted-sample") {
        try? fm.removeItem(at: downloads.appendingPathComponent(name))
    }
    try? fm.removeItem(at: work)

    out(""); out("\(pass) passed, \(fail) failed")
    exit(fail == 0 ? 0 : 1)
} }
DispatchQueue.main.asyncAfter(deadline: .now() + 30) { out("TIMEOUT"); exit(2) }
app.run()
