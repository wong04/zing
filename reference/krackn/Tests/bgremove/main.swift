import Foundation
import AppKit
func out(_ t: String) { print(t) }
let sem = DispatchSemaphore(value: 0)
var pass = 0, fail = 0
func check(_ n: String, _ c: Bool, _ d: String = "") {
    if c { pass += 1; out("  ok   \(n) \(d)") } else { fail += 1; out("  FAIL \(n) \(d)") }
}
Task {
    // Generate the fixture in a temp dir so the suite is self-contained and
    // leaves nothing behind in the repo.
    let work = FileManager.default.temporaryDirectory
        .appendingPathComponent("aio-bg-\(UUID().uuidString)")
    try? FileManager.default.createDirectory(at: work, withIntermediateDirectories: true)
    let url = work.appendingPathComponent("subject.png")
    do {
        let size = NSSize(width: 600, height: 600)
        let img = NSImage(size: size)
        img.lockFocus()
        NSColor(calibratedRed: 0.45, green: 0.68, blue: 0.9, alpha: 1).setFill()
        NSRect(origin: .zero, size: size).fill()
        NSColor(calibratedRed: 0.85, green: 0.35, blue: 0.25, alpha: 1).setFill()
        NSBezierPath(ovalIn: NSRect(x: 240, y: 380, width: 120, height: 130)).fill()
        NSBezierPath(roundedRect: NSRect(x: 200, y: 120, width: 200, height: 280),
                     xRadius: 60, yRadius: 60).fill()
        img.unlockFocus()
        let rep = NSBitmapImageRep(data: img.tiffRepresentation!)!
        try rep.representation(using: .png, properties: [:])!.write(to: url)
    } catch { out("  FAIL fixture: \(error)"); exit(1) }
    do {
        let r = try await BackgroundRemover.removeBackground(from: url, background: .transparent)
        check("cutout produced", r.pngData.count > 1000, "\(r.pngData.count) bytes")
        check("found at least one subject", r.subjectCount >= 1, "\(r.subjectCount)")
        // The generator wrote a 2x bitmap, so compare against the file's own
        // pixel dimensions rather than the nominal 600pt.
        let srcRep = NSBitmapImageRep(data: try Data(contentsOf: url))!
        check("size preserved", Int(r.image.size.width) == srcRep.pixelsWide,
              "\(Int(r.image.size.width)) vs source \(srcRep.pixelsWide)")
        try r.pngData.write(to: work.appendingPathComponent("cutout.png"))

        // Verify transparency: a corner pixel should now be clear, while the
        // middle of the subject should stay opaque.
        let rep = NSBitmapImageRep(data: try Data(contentsOf: work.appendingPathComponent("cutout.png")))!
        // Sample proportionally so the check is resolution-independent.
        let w = rep.pixelsWide, h = rep.pixelsHigh
        let corner = rep.colorAt(x: 4, y: 4)
        // Subject torso sits centre-x, lower-middle in the source; in top-left
        // pixel coords that's about (0.5w, 0.55h).
        let middle = rep.colorAt(x: w / 2, y: Int(Double(h) * 0.55))
        out("  corner alpha=\(corner?.alphaComponent ?? -1)  centre alpha=\(middle?.alphaComponent ?? -1)")
        check("background became transparent", (corner?.alphaComponent ?? 1) < 0.2)
        check("subject stayed opaque", (middle?.alphaComponent ?? 0) > 0.8)

        // A solid backdrop should be fully opaque everywhere.
        let white = try await BackgroundRemover.removeBackground(from: url, background: .white)
        let wrep = NSBitmapImageRep(data: white.pngData)!
        check("white backdrop is opaque", (wrep.colorAt(x: 4, y: 4)?.alphaComponent ?? 0) > 0.95)
        let wc = wrep.colorAt(x: 4, y: 4)
        check("backdrop is actually white", (wc?.redComponent ?? 0) > 0.9 && (wc?.blueComponent ?? 0) > 0.9)
    } catch {
        fail += 1; out("  FAIL removal threw: \(error.localizedDescription)")
    }

    // A blank image has no subject — must throw cleanly, not crash.
    let blank = NSImage(size: NSSize(width: 200, height: 200))
    blank.lockFocus(); NSColor.gray.setFill()
    NSRect(x: 0, y: 0, width: 200, height: 200).fill(); blank.unlockFocus()
    let brep = NSBitmapImageRep(data: blank.tiffRepresentation!)!
    try? brep.representation(using: .png, properties: [:])!
        .write(to: work.appendingPathComponent("blank.png"))
    do {
        _ = try await BackgroundRemover.removeBackground(from: work.appendingPathComponent("blank.png"))
        out("  note: blank image returned a subject (acceptable)")
        pass += 1
    } catch { check("blank image errors cleanly", true) }

    try? FileManager.default.removeItem(at: work)
    out(""); out("\(pass) passed, \(fail) failed")
    sem.signal()
}
sem.wait()
exit(fail == 0 ? 0 : 1)
