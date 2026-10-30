import AppKit
import UniformTypeIdentifiers
func out(_ t: String) { FileHandle.standardOutput.write((t+"\n").data(using:.utf8)!) }
let app = NSApplication.shared
app.setActivationPolicy(.accessory)
var pass = 0, fail = 0
func check(_ n: String, _ c: Bool, _ d: String = "") {
    if c { pass += 1; out("  ok   \(n) \(d)") } else { fail += 1; out("  FAIL \(n) \(d)") }
}
Task {
    // 1. Finder-style file URL
    let tmp = FileManager.default.temporaryDirectory.appendingPathComponent("drop-src.png")
    let png = Data([0x89,0x50,0x4E,0x47,0x0D,0x0A,0x1A,0x0A] + [UInt8](repeating: 0, count: 40))
    try? png.write(to: tmp)
    let p1 = NSItemProvider(contentsOf: tmp)!
    let r1 = await DropRouter.resolve([p1])
    check("file URL drag", r1.first?.lastPathComponent == "drop-src.png", "-> \(r1.first?.lastPathComponent ?? "nil")")

    // 2. Raw image bytes (Safari dragging an <img>)
    let p2 = NSItemProvider(item: png as NSData, typeIdentifier: UTType.png.identifier)
    p2.suggestedName = "from-web"
    let r2 = await DropRouter.resolve([p2])
    check("raw image bytes", r2.first != nil, "-> \(r2.first?.lastPathComponent ?? "nil")")
    if let u = r2.first {
        check("  extension sniffed", u.pathExtension == "png", "-> .\(u.pathExtension)")
        check("  bytes intact", (try? Data(contentsOf: u).count) == png.count)
    }

    // 3. Web URL that must be downloaded
    let p3 = NSItemProvider(object: URL(string: "https://www.google.com/favicon.ico")! as NSURL)
    let r3 = await DropRouter.resolve([p3])
    check("web URL downloaded", r3.first != nil, "-> \(r3.first?.lastPathComponent ?? "nil")")
    if let u = r3.first {
        let size = (try? Data(contentsOf: u).count) ?? 0
        check("  downloaded non-empty", size > 100, "\(size) bytes")
    }

    // 4. Junk is rejected rather than crashing
    let p4 = NSItemProvider(object: "not a url at all" as NSString)
    let r4 = await DropRouter.resolve([p4])
    check("junk rejected", r4.isEmpty, "-> \(r4.count) results")

    // 5. Dragging *between* the app's own tools, for every kind of file.
    //
    // This is the regression that mattered: `NSItemProvider(contentsOf:)`
    // registers only the file's own content type, so a mesh or a video
    // advertised something no drop target accepted and the drag was refused
    // before it ever reached the router. Images and PDFs worked, everything
    // else silently didn't.
    let exotic = ["stl", "msh", "vtu", "mp4", "mkv", "flac", "zip",
                  "docx", "pdf", "png", "obj", "heic", "svg", "bin"]
    let scratch = FileManager.default.temporaryDirectory
        .appendingPathComponent("krackn-drag-test", isDirectory: true)
    try? FileManager.default.createDirectory(at: scratch, withIntermediateDirectories: true)
    var handled = 0
    for ext in exotic {
        let file = scratch.appendingPathComponent("drag-test.\(ext)")
        try? Data(repeating: 0x41, count: 64).write(to: file)

        let provider = NSItemProvider.file(file)
        let conformsToFileURL = provider.hasItemConformingToTypeIdentifier(
            UTType.fileURL.identifier)
        if !conformsToFileURL {
            check("  .\(ext) advertises a file URL", false, "-> no")
            continue
        }
        let resolved = await DropRouter.resolve([provider])
        let ok = resolved.first?.pathExtension == ext
        check("  .\(ext) survives a tool-to-tool drag", ok,
              "-> \(resolved.first?.lastPathComponent ?? "nil")")
        if ok { handled += 1 }
    }
    check("every file type is draggable", handled == exotic.count,
          "\(handled)/\(exotic.count)")

    // The opposite of what this once asserted. Registering `.item` makes a view
    // a universal drop target, and AppKit's tracking for that swallows
    // right-clicks — which killed the tab context menu and every recents-list
    // menu in the app. The file-URL registration is what makes exotic types
    // draggable, as the fourteen cases above show.
    check("targets do NOT register the root type",
          !DropRouter.acceptedTypes.contains(.item),
          "-> \(DropRouter.acceptedTypes.map(\.identifier).joined(separator: ", "))")
    check("hover targets stay narrow",
          !DropRouter.hoverTypes.contains(.item) && DropRouter.hoverTypes.contains(.fileURL),
          "-> \(DropRouter.hoverTypes.map(\.identifier).joined(separator: ", "))")

    out(""); out("\(pass) passed, \(fail) failed")
    exit(fail == 0 ? 0 : 1)
}
DispatchQueue.main.asyncAfter(deadline: .now() + 60) { out("TIMEOUT"); exit(2) }
app.run()
