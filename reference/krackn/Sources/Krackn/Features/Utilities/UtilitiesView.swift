import SwiftUI

/// Tools grouped by discipline, so a researcher, a designer and a backend
/// engineer can each find their own corner quickly.
enum UtilityTool: String, CaseIterable, Identifiable {
    // Setup
    case tools = "Engines"
    // Data & encoding
    case json = "JSON / YAML"
    case base64 = "Base64"
    case urlCode = "URL Encoding"
    case numberBase = "Number Bases"
    case regex = "Regex Tester"
    // Crypto & auth
    case hash = "Hashes"
    case hmac = "HMAC"
    case jwt = "JWT Decoder"
    case password = "Passwords"
    // Text
    case caseConvert = "Text Case"
    case textStats = "Text Stats"
    case linkCleaner = "Link Cleaner"
    // Science & units
    case calculator = "Scientific Calculator"
    case graphing = "Graphing Calculator"
    case units = "Unit Converter"
    case timestamp = "Timestamps"
    case cron = "Cron Parser"
    // Design
    case color = "Color Converter"
    case bgRemove = "Background Remover"
    // Generators
    case qr = "QR Code"
    case uuid = "UUID"

    var id: String { rawValue }

    var icon: String {
        switch self {
        case .tools: return "shippingbox"
        case .qr: return "qrcode"
        case .hash: return "number"
        case .hmac: return "signature"
        case .jwt: return "person.badge.key"
        case .base64: return "text.badge.checkmark"
        case .urlCode: return "percent"
        case .numberBase: return "01.square"
        case .regex: return "magnifyingglass.circle"
        case .json: return "curlybraces"
        case .password: return "key"
        case .linkCleaner: return "scissors"
        case .caseConvert: return "textformat"
        case .textStats: return "chart.bar.doc.horizontal"
        case .calculator: return "function"
        case .graphing: return "chart.xyaxis.line"
        case .units: return "ruler"
        case .timestamp: return "clock"
        case .cron: return "calendar.badge.clock"
        case .color: return "paintpalette"
        case .bgRemove: return "person.and.background.dotted"
        case .uuid: return "barcode"
        }
    }
}

/// The Toolbox: every feature that isn't the browser, as one launcher.
///
/// Two ways in, both always available. **Browse**: six groups by job, each a
/// compact panel listing its tools, packed into columns so all of them are on
/// screen at once — no scrolling past headings to find the one you want.
/// **Search**: the field is focused when the page opens, matches what people
/// actually type ("sha256", "youtube", "epoch" — see `ToolboxSearch`), and
/// ↑ ↓ Return drive the results without touching the mouse.
struct ToolboxView: View {
    let onOpen: (PanelContent) -> Void
    var onTearOff: ((PanelContent, CGPoint) -> Void)?
    @State private var query: String
    @State private var highlighted = 0
    @FocusState private var searchFocused: Bool

    /// Bumped by ⌘F (Edit › Find) to put the cursor back in the search field.
    var focusToken = 0

    init(onOpen: @escaping (PanelContent) -> Void,
         onTearOff: ((PanelContent, CGPoint) -> Void)? = nil,
         focusToken: Int = 0,
         query: String = "") {
        self.onOpen = onOpen
        self.onTearOff = onTearOff
        self.focusToken = focusToken
        _query = State(initialValue: query)
    }

    private var results: [PanelContent] {
        ToolboxSearch.rank(query, ToolboxGroup.searchEntries)
            .compactMap { PanelContent(id: $0.id) }
    }

    private var isSearching: Bool { !query.trimmingCharacters(in: .whitespaces).isEmpty }

    var body: some View {
        FadingScrollView(fadeHeight: 36) {
            VStack(alignment: .leading, spacing: 22) {
                HStack(alignment: .center, spacing: 16) {
                    Text("Toolbox")
                        .font(.app(26, .semibold))
                        .tracking(-0.4)
                        .foregroundStyle(Palette.text)
                    Spacer(minLength: 12)
                    searchField.frame(maxWidth: 280)
                }

                ZStack(alignment: .topLeading) {
                    if isSearching {
                        resultsList.transition(.opacity)
                    } else {
                        Masonry(columnWidth: 250, spacing: 12) {
                            ForEach(ToolboxGroup.allCases) { group in
                                GroupPanel(group: group, onOpen: onOpen, onTearOff: onTearOff)
                            }
                        }
                        .transition(.opacity)
                    }
                }
                .animation(Motion.quick, value: isSearching)
            }
            .frame(maxWidth: 900, alignment: .leading)
            .frame(maxWidth: .infinity, alignment: .center)
            .padding(.horizontal, 32)
            .padding(.top, Self.trafficLightInset + 26)
            .padding(.bottom, 40)
        }
        .onAppear {
            // Focused on arrival, like Spotlight: open the Toolbox and start
            // typing. A short delay lets the page transition settle first.
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.15) { searchFocused = true }
        }
        .onChange(of: query) { _, _ in highlighted = 0 }
        .onChange(of: focusToken) { _, _ in searchFocused = true }
    }

    private var searchField: some View {
        let shape = RoundedRectangle(cornerRadius: Metrics.controlRadius, style: .continuous)
        return HStack(spacing: 7) {
            Image(systemName: "magnifyingglass")
                .font(.system(size: 12))
                .foregroundStyle(Palette.textSecondary)
            TextField("Search tools", text: $query)
                .textFieldStyle(.plain)
                .font(.app(13))
                .focused($searchFocused)
                .onSubmit {
                    if results.indices.contains(highlighted) { onOpen(results[highlighted]) }
                }
                .onKeyPress(.downArrow) {
                    guard isSearching else { return .ignored }
                    highlighted = min(highlighted + 1, max(results.count - 1, 0))
                    return .handled
                }
                .onKeyPress(.upArrow) {
                    guard isSearching else { return .ignored }
                    highlighted = max(highlighted - 1, 0)
                    return .handled
                }
                .onKeyPress(.escape) {
                    guard isSearching else { return .ignored }
                    query = ""
                    return .handled
                }
            if isSearching {
                Button { query = "" } label: {
                    Image(systemName: "xmark.circle.fill")
                        .font(.system(size: 12))
                        .foregroundStyle(Palette.textSecondary.opacity(0.7))
                }
                .buttonStyle(.plain)
                .transition(.opacity)
            }
        }
        .padding(.vertical, 7)
        .padding(.horizontal, 10)
        .background(searchFocused ? Palette.raised : Palette.controlFill, in: shape)
        .overlay(shape.strokeBorder(Palette.separator, lineWidth: 1))
        .overlay(shape.inset(by: -2)
            .stroke(Palette.accent.opacity(searchFocused ? 0.5 : 0), lineWidth: 3))
        .animation(Motion.hover, value: searchFocused)
        .animation(Motion.quick, value: isSearching)

    }

    @ViewBuilder
    private var resultsList: some View {
        if results.isEmpty {
            VStack(spacing: 6) {
                Text("No tools match \u{201C}\(query)\u{201D}")
                    .font(.app(13.5))
                    .foregroundStyle(Palette.text)
                Text("Try what you want to do — \u{201C}compress\u{201D}, \u{201C}unblock\u{201D}, \u{201C}hex\u{201D}.")
                    .font(.app(12))
                    .foregroundStyle(Palette.textSecondary)
            }
            .frame(maxWidth: .infinity)
            .padding(.top, 48)
        } else {
            VStack(spacing: 2) {
                ForEach(Array(results.enumerated()), id: \.element.id) { index, item in
                    ToolRow(item: item, detail: ToolboxGroup.group(of: item)?.rawValue,
                            isHighlighted: index == highlighted, size: .large)
                        .tearable(item, enabled: onTearOff != nil,
                                  onTap: { onOpen(item) },
                                  onTearOff: { c, p in onTearOff?(c, p) })
                        .onHover { if $0 { highlighted = index } }
                }
            }
            .padding(6)
            .cardSurface()
            .frame(maxWidth: 600, alignment: .leading)
        }
    }
}

/// One category: its name, then its tools as compact rows.
private struct GroupPanel: View {
    let group: ToolboxGroup
    let onOpen: (PanelContent) -> Void
    var onTearOff: ((PanelContent, CGPoint) -> Void)?

    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            HStack(spacing: 7) {
                Image(systemName: group.icon)
                    .font(.system(size: 11.5, weight: .medium))
                    .foregroundStyle(Palette.textSecondary)
                Text(group.rawValue)
                    .font(.app(12, .semibold))
                    .foregroundStyle(Palette.textSecondary)
            }
            .padding(.horizontal, 8)
            .padding(.top, 4)
            .padding(.bottom, 6)

            ForEach(group.items) { item in
                ToolRow(item: item)
                    // Click opens it here; hold and drag pops it out.
                    .tearable(item, enabled: onTearOff != nil,
                              onTap: { onOpen(item) },
                              onTearOff: { c, p in onTearOff?(c, p) })
            }
        }
        .padding(8)
        .frame(maxWidth: .infinity, alignment: .leading)
        .cardSurface()
    }
}

/// A tool as a row: tinted icon tile, name, and — in search results — the
/// group it lives in, so you learn where things are as you search for them.
private struct ToolRow: View {
    enum Size { case regular, large }
    let item: PanelContent
    var detail: String?
    var isHighlighted = false
    var size: Size = .regular
    @State private var hovering = false

    /// Not a `Button` — the tear-off gesture owns the click.
    var body: some View {
        let tile: CGFloat = size == .large ? 28 : 24
        HStack(spacing: 10) {
            Image(systemName: item.icon)
                .font(.system(size: size == .large ? 13 : 11.5, weight: .regular))
                .foregroundStyle(Palette.accentText)
                .frame(width: tile, height: tile)
                .background(Palette.accent.opacity(0.16),
                            in: RoundedRectangle(cornerRadius: tile * 0.26, style: .continuous))
            Text(item.title)
                .font(.app(13))
                .foregroundStyle(Palette.text)
                .lineLimit(1)
            Spacer(minLength: 6)
            if let detail {
                Text(detail)
                    .font(.app(11.5))
                    .foregroundStyle(Palette.textSecondary)
            }
            if isHighlighted {
                Image(systemName: "return")
                    .font(.system(size: 10.5, weight: .medium))
                    .foregroundStyle(Palette.textSecondary)
                    .transition(.opacity)
            }
        }
        .padding(.horizontal, 6)
        .frame(height: size == .large ? 40 : 34)
        .background(
            RoundedRectangle(cornerRadius: Metrics.controlRadius, style: .continuous)
                .fill(isHighlighted ? Palette.selection.opacity(0.85)
                      : (hovering ? Palette.text.opacity(0.05) : .clear))
        )
        .contentShape(Rectangle())
        .onHover { hovering = $0 }
        .animation(Motion.hover, value: hovering)
        .animation(Motion.quick, value: isHighlighted)
        .help(item.title)
    }
}

/// One tool's page, scrolled and column-width — the same frame whether it's
/// shown in the main area or in a popped-out window.
struct ToolDetailView: View {
    let tool: UtilityTool
    @ObservedObject var backgroundRemover: BackgroundRemoverModel

    var body: some View {
        PageContainer {
            switch tool {
            case .tools: ToolSetupView()
            case .numberBase: NumberBaseTool()
            case .regex: RegexTool()
            case .hmac: HMACTool()
            case .jwt: JWTTool()
            case .textStats: TextStatsTool()
            case .calculator: CalculatorTool()
            case .graphing: GraphingCalculatorTool()
            case .units: UnitTool()
            case .cron: CronTool()
            case .color: ColorTool()
            case .bgRemove: BackgroundRemoverTool(model: backgroundRemover)
            case .qr: QRTool()
            case .hash: HashTool()
            case .base64: Base64Tool()
            case .urlCode: URLTool()
            case .json: JSONTool()
            case .password: PasswordTool()
            case .linkCleaner: LinkCleanerTool()
            case .caseConvert: CaseTool()
            case .timestamp: TimestampTool()
            case .uuid: UUIDTool()
            }
        }
    }
}

// MARK: - Shared pieces

/// Tools used to open with their name in large type and a sentence under it.
/// The bar above every tool — the page's top bar or the popped-out window's
/// title — already names it, so this now draws nothing. Kept as a type so the
/// tools read the same and a header can come back in one place if needed.
private struct ToolHeader: View {
    let title: String
    let subtitle: String

    var body: some View { EmptyView() }
}

private struct OutputBox: View {
    let text: String
    var monospaced = true

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            ScrollView {
                Text(text.isEmpty ? "—" : text)
                    .font(monospaced ? .system(size: 12, design: .monospaced) : .app(13))
                    .textSelection(.enabled)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding(9)
            }
            .frame(minHeight: 80, maxHeight: 240)
            .wellSurface()

            CopyButton(text: text).controlSize(.small)
        }
    }
}

// MARK: - QR

private struct QRTool: View {
    @State private var text = ""
    @State private var image: NSImage?

    var body: some View {
        ToolHeader(title: "QR Code", subtitle: "Turn any text or link into a scannable code.")

        TextEditor(text: $text)
            .scrollContentBackground(.hidden)
            .font(.app(13))
            .frame(height: 90)
            .padding(4)
            .wellSurface()
            .onChange(of: text) { _, new in
                image = UtilityTools.makeQRCode(from: new)
            }

        if let image {
            VStack(alignment: .leading, spacing: 10) {
                Image(nsImage: image)
                    .interpolation(.none)   // keep the modules crisp, not blurred
                    .resizable()
                    .frame(width: 220, height: 220)
                    .background(.white)
                    .padding(9)
                    .background(.white, in: RoundedRectangle(cornerRadius: 8))

                HStack {
                    PillButton("Save PNG", icon: "square.and.arrow.down", style: .outlined, size: .small) { save(image) }
                    PillButton("Copy Image", style: .ghost, size: .small) {
                        NSPasteboard.general.clearContents()
                        NSPasteboard.general.writeObjects([image])
                    }
                }
            }
        }
    }

    private func save(_ image: NSImage) {
        let panel = NSSavePanel()
        panel.nameFieldStringValue = "qrcode.png"
        guard panel.runModal() == .OK, let url = panel.url,
              let tiff = image.tiffRepresentation,
              let rep = NSBitmapImageRep(data: tiff),
              let png = rep.representation(using: .png, properties: [:])
        else { return }
        try? png.write(to: url)
    }
}

// MARK: - Hash

private struct HashTool: View {
    @State private var input = ""
    @State private var algorithm: UtilityTools.HashAlgorithm = .sha256
    @State private var fileResult: String?

    var body: some View {
        ToolHeader(title: "Hashes", subtitle: "Checksum text or a file.")

        SegmentedControl(
            options: UtilityTools.HashAlgorithm.allCases.map { ($0, $0.rawValue) },
            selection: $algorithm)

        if algorithm.isLegacy {
            InfoBanner(
                kind: .info,
                text: "\(algorithm.rawValue) is broken for security purposes — fine for "
                    + "verifying a download, not for anything that needs to resist tampering."
            )
        }

        TextEditor(text: $input)
            .scrollContentBackground(.hidden)
            .font(.system(size: 12, design: .monospaced))
            .frame(height: 90)
            .padding(4)
            .wellSurface()

        OutputBox(text: input.isEmpty ? "" : UtilityTools.hash(input, using: algorithm))

        Divider()

        PillButton("Hash a File", icon: "doc", style: .outlined, size: .small) { hashFile() }
        if let fileResult {
            OutputBox(text: fileResult)
        }
    }

    private func hashFile() {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = false
        guard panel.runModal() == .OK, let url = panel.url else { return }
        do {
            let digest = try UtilityTools.hashFile(at: url, using: algorithm)
            fileResult = "\(url.lastPathComponent)\n\(digest)"
        } catch {
            fileResult = error.localizedDescription
        }
    }
}

// MARK: - Base64

private struct Base64Tool: View {
    @State private var input = ""
    @State private var decoding = false

    private var output: String {
        guard !input.isEmpty else { return "" }
        if decoding {
            return UtilityTools.base64Decode(input) ?? "Not valid base64 (or not UTF-8 text)."
        }
        return UtilityTools.base64Encode(input)
    }

    var body: some View {
        ToolHeader(title: "Base64", subtitle: "Encode or decode. Handles URL-safe input too.")

        SegmentedControl(options: [(false, "Encode"), (true, "Decode")], selection: $decoding)

        TextEditor(text: $input)
            .scrollContentBackground(.hidden)
            .font(.system(size: 12, design: .monospaced))
            .frame(height: 110)
            .padding(4)
            .wellSurface()

        OutputBox(text: output)
    }
}

// MARK: - URL encoding

private struct URLTool: View {
    @State private var input = ""
    @State private var decoding = false

    var body: some View {
        ToolHeader(title: "URL Encoding", subtitle: "Percent-encode or decode a string.")

        SegmentedControl(options: [(false, "Encode"), (true, "Decode")], selection: $decoding)

        TextEditor(text: $input)
            .scrollContentBackground(.hidden)
            .font(.system(size: 12, design: .monospaced))
            .frame(height: 110)
            .padding(4)
            .wellSurface()

        OutputBox(text: input.isEmpty ? ""
                  : (decoding ? UtilityTools.urlDecode(input) : UtilityTools.urlEncode(input)))
    }
}

// MARK: - JSON

private struct JSONTool: View {
    @State private var input = ""
    @State private var pretty = true

    var body: some View {
        ToolHeader(title: "JSON", subtitle: "Pretty-print or minify, with a validity check.")

        SegmentedControl(options: [(true, "Pretty"), (false, "Minify")], selection: $pretty)

        TextEditor(text: $input)
            .scrollContentBackground(.hidden)
            .font(.system(size: 12, design: .monospaced))
            .frame(height: 150)
            .padding(4)
            .wellSurface()

        switch UtilityTools.formatJSON(input, pretty: pretty) {
        case .success(let text):
            if !input.isEmpty {
                InfoBanner(kind: .success, text: "Valid JSON.")
            }
            OutputBox(text: input.isEmpty ? "" : text)
        case .failure(let problem):
            if !input.isEmpty {
                InfoBanner(kind: .warning, text: problem.message)
            }
        }
    }
}

// MARK: - Passwords

private struct PasswordTool: View {
    @State private var length: Double = 20
    @State private var symbols = true
    @State private var digits = true
    @State private var uppercase = true
    @State private var passwords: [String] = []

    var body: some View {
        ToolHeader(
            title: "Passwords",
            subtitle: "Generated locally with the system CSPRNG. Nothing leaves this Mac."
        )

        VStack(alignment: .leading, spacing: 11) {
            HStack {
                Text("Length")
                PillSlider(value: $length, range: 8...64, step: 1)
                Text("\(Int(length))").monospacedDigit().frame(width: 28)
            }
            Toggle("Uppercase letters", isOn: $uppercase).toggleStyle(.pill)
            Toggle("Digits", isOn: $digits).toggleStyle(.pill)
            Toggle("Symbols", isOn: $symbols).toggleStyle(.pill)

            Text("Ambiguous characters (0/O, 1/l/I) are excluded so they're safe to read aloud.")
                .font(.app(11.5))
                .foregroundStyle(Palette.textSecondary)

            PillButton("Generate 5", icon: "arrow.clockwise", style: .filled) {
                passwords = (0..<5).map { _ in
                    UtilityTools.generatePassword(
                        length: Int(length), symbols: symbols,
                        digits: digits, uppercase: uppercase)
                }
            }
        }

        ForEach(passwords, id: \.self) { password in
            HStack {
                Text(password)
                    .font(.system(size: 13, design: .monospaced))
                    .textSelection(.enabled)
                Spacer()
                CopyButton(text: password).controlSize(.small).labelStyle(.iconOnly)
            }
            .padding(8)
            .wellSurface(radius: 9)
        }
    }
}

// MARK: - Link cleaner

private struct LinkCleanerTool: View {
    @State private var input = ""

    var body: some View {
        ToolHeader(
            title: "Link Cleaner",
            subtitle: "Strips tracking parameters so a link doesn't carry where you found it."
        )

        PillField(placeholder: "Paste a link", text: $input, icon: "link")

        if !input.isEmpty {
            if let cleaned = UtilityTools.cleanLink(input) {
                OutputBox(text: cleaned.url, monospaced: false)
                if cleaned.removed.isEmpty {
                    InfoBanner(kind: .info, text: "No tracking parameters found — already clean.")
                } else {
                    InfoBanner(
                        kind: .success,
                        text: "Removed \(cleaned.removed.count): "
                            + cleaned.removed.joined(separator: ", ")
                    )
                }
            } else {
                InfoBanner(kind: .warning, text: "That doesn't parse as a URL.")
            }
        }
    }
}

// MARK: - Case

private struct CaseTool: View {
    @State private var input = ""

    var body: some View {
        ToolHeader(title: "Text Case", subtitle: "Convert between naming conventions.")

        TextEditor(text: $input)
            .scrollContentBackground(.hidden)
            .font(.app(13))
            .frame(height: 80)
            .padding(4)
            .wellSurface()

        ForEach(UtilityTools.CaseStyle.allCases) { style in
            HStack {
                Text(style.rawValue)
                    .font(.app(11.5))
                    .foregroundStyle(Palette.textSecondary)
                    .frame(width: 90, alignment: .leading)
                Text(input.isEmpty ? "—" : UtilityTools.convertCase(input, to: style))
                    .font(.system(size: 12, design: .monospaced))
                    .textSelection(.enabled)
                    .lineLimit(2)
                Spacer()
                CopyButton(text: UtilityTools.convertCase(input, to: style))
                    .controlSize(.small)
                    .labelStyle(.iconOnly)
            }
            .padding(7)
            .wellSurface(radius: 9)
        }

        let characters = input.count
        let words = input.split { $0.isWhitespace || $0.isNewline }.count
        Text("\(characters) characters · \(words) words")
            .font(.app(11.5))
            .foregroundStyle(Palette.textSecondary)
    }
}

// MARK: - Timestamps

private struct TimestampTool: View {
    @State private var input = String(Int(Date().timeIntervalSince1970))

    var body: some View {
        ToolHeader(
            title: "Timestamps",
            subtitle: "Unix time in or out. Seconds and milliseconds are auto-detected."
        )

        HStack {
            PillField(placeholder: "1753800000", text: $input, icon: "clock")
            PillButton("Now", style: .outlined, size: .small) {
                input = String(Int(Date().timeIntervalSince1970))
            }
        }

        let rows = UtilityTools.describeTimestamp(input)
        if rows.isEmpty && !input.isEmpty {
            InfoBanner(kind: .warning, text: "That isn't a number.")
        }
        ForEach(rows, id: \.0) { label, value in
            HStack {
                Text(label)
                    .font(.app(11.5))
                    .foregroundStyle(Palette.textSecondary)
                    .frame(width: 140, alignment: .leading)
                Text(value)
                    .font(.system(size: 12, design: .monospaced))
                    .textSelection(.enabled)
                Spacer()
                CopyButton(text: value).controlSize(.small).labelStyle(.iconOnly)
            }
            .padding(7)
            .wellSurface(radius: 9)
        }
    }
}

// MARK: - UUID

private struct UUIDTool: View {
    @State private var uuids: [String] = [UUID().uuidString]
    @State private var lowercased = false

    var body: some View {
        ToolHeader(title: "UUID", subtitle: "Random version-4 identifiers.")

        HStack {
            PillButton("Generate 5", icon: "arrow.clockwise", style: .filled) {
                uuids = (0..<5).map { _ in UUID().uuidString }
            }
            Toggle("Lowercase", isOn: $lowercased).toggleStyle(.pill).fixedSize()
        }

        ForEach(uuids, id: \.self) { value in
            let display = lowercased ? value.lowercased() : value
            HStack {
                Text(display)
                    .font(.system(size: 12, design: .monospaced))
                    .textSelection(.enabled)
                Spacer()
                CopyButton(text: display).controlSize(.small).labelStyle(.iconOnly)
            }
            .padding(8)
            .wellSurface(radius: 9)
        }

        CopyButton(text: uuids.map { lowercased ? $0.lowercased() : $0 }
            .joined(separator: "\n"))
            .controlSize(.small)
    }
}
