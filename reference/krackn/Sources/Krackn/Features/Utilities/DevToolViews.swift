import SwiftUI

/// The engineering, science and design tools. Each is a small self-contained
/// view over a pure function in `DevTools`.

// MARK: - Shared

/// A labelled read-only value with a copy button — the shape most of these
/// tools output.
struct ResultRow: View {
    let label: String
    let value: String
    var mono = true

    var body: some View {
        HStack(spacing: 10) {
            Text(label)
                .font(.app(11))
                .foregroundStyle(Palette.textSecondary)
                .frame(width: 108, alignment: .leading)
            Text(value.isEmpty ? "—" : value)
                .font(mono ? .system(size: 12, design: .monospaced) : .app(12.5))
                .foregroundStyle(Palette.text)
                .textSelection(.enabled)
                .lineLimit(2)
            Spacer(minLength: 4)
            CopyButton(text: value).labelStyle(.iconOnly)
        }
        .padding(.horizontal, 10)
        .padding(.vertical, 7)
        .wellSurface(radius: 9)
    }
}

/// See `ToolHeader`: the surrounding bar names the tool, so the in-page title
/// was a duplicate and draws nothing now.
struct ToolTitle: View {
    let title: String
    let subtitle: String

    var body: some View { EmptyView() }
}

// MARK: - Number bases

struct NumberBaseTool: View {
    @State private var input = "255"

    var body: some View {
        ToolTitle(title: "Number Bases",
                  subtitle: "Decimal, hex, binary and octal at once. Accepts 0x, 0b and 0o prefixes.")
        PillField(placeholder: "255, 0xFF, 0b1111_1111…", text: $input, icon: "number")

        if let result = DevTools.convertBase(input) {
            VStack(spacing: 6) {
                ResultRow(label: "Decimal", value: result.decimal)
                ResultRow(label: "Hexadecimal", value: result.hex)
                ResultRow(label: "Binary", value: result.binary)
                ResultRow(label: "Octal", value: result.octal)
                ResultRow(label: "As bytes", value: result.bytes)
            }
        } else if !input.isEmpty {
            InfoBanner(kind: .warning, text: "That isn't a number in any recognised base.")
        }
    }
}

// MARK: - Regex

struct RegexTool: View {
    @State private var pattern = #"(\w+)@(\w+\.\w+)"#
    @State private var subject = "contact alice@example.com or bob@test.org"
    @State private var caseInsensitive = false

    var body: some View {
        ToolTitle(title: "Regex Tester",
                  subtitle: "Live matching with capture groups, using the same engine as Swift and Foundation.")

        PillField(placeholder: "pattern", text: $pattern, icon: "magnifyingglass")
        Toggle("Case insensitive", isOn: $caseInsensitive).toggleStyle(.pill)

        Text("Test string").font(.app(11, .semibold)).foregroundStyle(Palette.textSecondary)
        TextEditor(text: $subject)
            .scrollContentBackground(.hidden)
            .font(.system(size: 12, design: .monospaced))
            .frame(height: 90)
            .padding(5)
            .wellSurface()

        switch DevTools.testRegex(pattern: pattern, against: subject,
                                  caseInsensitive: caseInsensitive) {
        case .success(let matches):
            if matches.isEmpty && !pattern.isEmpty {
                InfoBanner(kind: .info, text: "No matches.")
            } else {
                Text("\(matches.count) match\(matches.count == 1 ? "" : "es")")
                    .font(.app(11, .semibold))
                    .foregroundStyle(Palette.positive)
                VStack(spacing: 6) {
                    ForEach(matches) { match in
                        VStack(alignment: .leading, spacing: 4) {
                            ResultRow(label: "Match \(match.range)", value: match.text)
                            ForEach(Array(match.groups.enumerated()), id: \.offset) { index, group in
                                ResultRow(label: "  group \(index + 1)", value: group)
                            }
                        }
                    }
                }
            }
        case .failure(let problem):
            InfoBanner(kind: .warning, text: problem.message)
        }
    }
}

// MARK: - HMAC

struct HMACTool: View {
    @State private var message = ""
    @State private var key = ""
    @State private var algorithm: UtilityTools.HashAlgorithm = .sha256

    var body: some View {
        ToolTitle(title: "HMAC",
                  subtitle: "Keyed signatures for API requests and webhook verification.")

        SegmentedControl(
            options: UtilityTools.HashAlgorithm.allCases.map { ($0, $0.rawValue) },
            selection: $algorithm)

        PillField(placeholder: "Secret key", text: $key, icon: "key")
        Text("Message").font(.app(11, .semibold)).foregroundStyle(Palette.textSecondary)
        TextEditor(text: $message)
            .scrollContentBackground(.hidden)
            .font(.system(size: 12, design: .monospaced))
            .frame(height: 90)
            .padding(5)
            .wellSurface()

        ResultRow(
            label: "HMAC-\(algorithm.rawValue)",
            value: message.isEmpty && key.isEmpty
                ? "" : DevTools.hmac(message: message, key: key, algorithm: algorithm))
    }
}

// MARK: - JWT

struct JWTTool: View {
    @State private var token = ""

    var body: some View {
        ToolTitle(title: "JWT Decoder",
                  subtitle: "Inspect header and claims. Decoding only — verifying a signature needs the signing key.")

        TextEditor(text: $token)
            .scrollContentBackground(.hidden)
            .font(.system(size: 11, design: .monospaced))
            .frame(height: 90)
            .padding(5)
            .wellSurface()

        if token.isEmpty {
            InfoBanner(kind: .info, text: "Paste a token to inspect it.")
        } else if let parts = DevTools.decodeJWT(token) {
            if let expiry = parts.expiry {
                InfoBanner(kind: expiry.contains("EXPIRED") ? .warning : .success,
                           text: "Expires: \(expiry)")
            }
            Text("Header").font(.app(11, .semibold)).foregroundStyle(Palette.textSecondary)
            codeBlock(parts.header)
            Text("Payload").font(.app(11, .semibold)).foregroundStyle(Palette.textSecondary)
            codeBlock(parts.payload)
            ResultRow(label: "Signature", value: parts.signature)
        } else {
            InfoBanner(kind: .warning, text: "That doesn't parse as a JWT (needs header.payload.signature).")
        }
    }

    private func codeBlock(_ text: String) -> some View {
        ScrollView {
            Text(text)
                .font(.system(size: 11, design: .monospaced))
                .foregroundStyle(Palette.text)
                .textSelection(.enabled)
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(9)
        }
        .frame(maxHeight: 150)
        .wellSurface()
    }
}

// MARK: - Text stats

struct TextStatsTool: View {
    @State private var text = ""

    var body: some View {
        ToolTitle(title: "Text Stats",
                  subtitle: "Counts for writing, and a rough token estimate for LLM work.")

        TextEditor(text: $text)
            .scrollContentBackground(.hidden)
            .font(.app(13))
            .frame(height: 150)
            .padding(5)
            .wellSurface()

        let stats = DevTools.analyze(text)
        VStack(spacing: 6) {
            ResultRow(label: "Characters", value: "\(stats.characters)")
            ResultRow(label: "No spaces", value: "\(stats.charactersNoSpaces)")
            ResultRow(label: "Words", value: "\(stats.words)")
            ResultRow(label: "Lines", value: "\(stats.lines)")
            ResultRow(label: "Sentences", value: "\(stats.sentences)")
            ResultRow(label: "Paragraphs", value: "\(stats.paragraphs)")
            ResultRow(label: "≈ LLM tokens", value: "\(stats.approxTokens)")
            ResultRow(label: "Reading time",
                      value: String(format: "%.1f min", stats.readingMinutes))
        }
    }
}

// MARK: - Units

struct UnitTool: View {
    @State private var kind: DevTools.UnitKind = .length
    @State private var amount = "1"
    @State private var from = "m"
    @State private var to = "ft"

    var body: some View {
        ToolTitle(title: "Unit Converter",
                  subtitle: "Length, mass, temperature, data, pressure, energy and angle.")

        PillMenu(options: DevTools.UnitKind.allCases.map { ($0, $0.rawValue) },
                 selection: $kind)
            .onChange(of: kind) { _, new in
                from = new.units.first?.0 ?? ""
                to = new.units.dropFirst().first?.0 ?? from
            }

        HStack(spacing: 8) {
            PillField(placeholder: "1", text: $amount, icon: "number")
            PillMenu(options: kind.units.map { ($0.0, $0.0) }, selection: $from)
            Image(systemName: "arrow.right").foregroundStyle(Palette.textSecondary)
            PillMenu(options: kind.units.map { ($0.0, $0.0) }, selection: $to)
        }

        if let value = Double(amount),
           let result = DevTools.convertUnit(value, from: from, to: to, kind: kind) {
            ResultRow(label: "Result", value: format(result) + " " + to)
        } else if !amount.isEmpty {
            InfoBanner(kind: .warning, text: "Enter a number to convert.")
        }
    }

    /// Avoids both "1.0000000001" and losing precision on tiny values.
    private func format(_ value: Double) -> String {
        if value != 0 && (abs(value) < 1e-4 || abs(value) >= 1e9) {
            return String(format: "%.6g", value)
        }
        return String(format: "%g", value)
    }
}

// MARK: - Cron

struct CronTool: View {
    @State private var expression = "0 9 * * 1-5"

    var body: some View {
        ToolTitle(title: "Cron Parser",
                  subtitle: "Reads a five-field cron expression back in plain English.")

        PillField(placeholder: "0 9 * * 1-5", text: $expression, icon: "calendar")

        if let description = DevTools.describeCron(expression) {
            InfoBanner(kind: .success, text: description)
        } else if !expression.isEmpty {
            InfoBanner(kind: .warning,
                       text: "Needs five fields: minute hour day-of-month month day-of-week.")
        }

        VStack(spacing: 6) {
            ResultRow(label: "Every weekday 9am", value: "0 9 * * 1-5", mono: true)
            ResultRow(label: "Every 15 minutes", value: "*/15 * * * *", mono: true)
            ResultRow(label: "Midnight daily", value: "0 0 * * *", mono: true)
            ResultRow(label: "First of month", value: "0 0 1 * *", mono: true)
        }
    }
}

// MARK: - Colour

struct ColorTool: View {
    @State private var input = "#D9B872"

    var body: some View {
        ToolTitle(title: "Color Converter",
                  subtitle: "Hex, RGB, normalised floats, HSL and CMYK — for CSS, shaders and print.")

        PillField(placeholder: "#RRGGBB or rgb(r, g, b)", text: $input, icon: "eyedropper")

        if let color = DevTools.convertColor(input) {
            RoundedRectangle(cornerRadius: 12)
                .fill(swatch(color.hex))
                .frame(height: 70)
                .overlay(RoundedRectangle(cornerRadius: 12)
                    .strokeBorder(Palette.separator, lineWidth: 1))

            VStack(spacing: 6) {
                ResultRow(label: "Hex", value: color.hex)
                ResultRow(label: "RGB", value: color.rgb)
                ResultRow(label: "Float (0–1)", value: color.rgbFloat)
                ResultRow(label: "HSL", value: color.hsl)
                ResultRow(label: "CMYK", value: color.cmyk)
            }
        } else if !input.isEmpty {
            InfoBanner(kind: .warning, text: "Enter a hex value like #3366FF, or rgb(51, 102, 255).")
        }
    }

    private func swatch(_ hex: String) -> Color {
        let clean = hex.replacingOccurrences(of: "#", with: "")
        guard let value = Int(clean, radix: 16) else { return .clear }
        return Color(
            red: Double((value >> 16) & 0xFF) / 255,
            green: Double((value >> 8) & 0xFF) / 255,
            blue: Double(value & 0xFF) / 255)
    }
}

// MARK: - Calculator

/// Scientific calculator. Free-form expression entry rather than a button grid,
/// because the audience types `sin(pi/4)*sqrt(2)` faster than they'd tap it.
struct CalculatorTool: View {
    @State private var input = ""
    @State private var angle = Expression.AngleMode.radians
    @State private var history: [(expression: String, result: String)] = []

    /// Live result while typing, so there's feedback before pressing Return.
    private var preview: Result<String, Error>? {
        let trimmed = input.trimmingCharacters(in: .whitespaces)
        guard !trimmed.isEmpty else { return nil }
        do { return .success(Expression.format(try Expression.evaluate(trimmed, angle: angle))) }
        catch { return .failure(error) }
    }

    var body: some View {
        ToolTitle(title: "Calculator",
                  subtitle: "Type an expression. Functions, constants and factorials all work.")

        VStack(alignment: .leading, spacing: 12) {
            HStack(spacing: 10) {
                PillField(placeholder: "e.g. sin(pi/4) * sqrt(2)", text: $input,
                          icon: "function") { commit() }
                SegmentedControl(
                    options: Expression.AngleMode.allCases.map { ($0, $0.rawValue) },
                    selection: $angle)
                    .frame(width: 140)
            }

            // Result line, or the reason the expression isn't valid yet.
            Group {
                switch preview {
                case .success(let value):
                    HStack {
                        Text("=").font(.app(22)).foregroundStyle(Palette.textSecondary)
                        Text(value)
                            .font(.system(size: 30, weight: .semibold, design: .monospaced))
                            .foregroundStyle(Palette.text)
                            .textSelection(.enabled)
                        Spacer()
                        CopyButton(text: value)
                    }
                case .failure(let error):
                    Text(error.localizedDescription.isEmpty ? " " : error.localizedDescription)
                        .font(.app(13))
                        .foregroundStyle(Palette.caution)
                case nil:
                    Text("Press Return to keep a result.")
                        .font(.app(12)).foregroundStyle(Palette.textSecondary)
                }
            }
            .frame(height: 44, alignment: .leading)

            keypad
        }

        if !history.isEmpty {
            SectionCard(title: "History") {
                VStack(alignment: .leading, spacing: 5) {
                    ForEach(Array(history.enumerated()), id: \.offset) { _, entry in
                        HStack {
                            Text(entry.expression)
                                .font(.system(size: 12, design: .monospaced))
                                .foregroundStyle(Palette.textSecondary)
                                .lineLimit(1)
                            Spacer()
                            Text(entry.result)
                                .font(.system(size: 12.5, weight: .semibold, design: .monospaced))
                                .foregroundStyle(Palette.text)
                                .textSelection(.enabled)
                        }
                        .contentShape(Rectangle())
                        .onTapGesture { input = entry.expression }
                    }
                    PillButton("Clear History", style: .ghost, size: .small) { history = [] }
                }
            }
        }

        SectionCard(title: "Supported") {
            VStack(alignment: .leading, spacing: 4) {
                reference("Operators", "+  −  ×  ÷  ^  %  !  ( )")
                reference("Functions", "sin cos tan asin acos atan sinh cosh tanh")
                reference("", "sqrt √ cbrt ln log log2 exp abs round floor ceil sign deg rad")
                reference("Constants", "pi  π  e  tau")
                reference("Shorthand", "2(3+4) = 14 · 3pi · 1.5e3 · 5! = 120")
            }
        }
    }

    private var keypad: some View {
        // Only the symbols that are awkward to type; the rest is the keyboard.
        let keys = ["(", ")", "^", "√", "π", "e", "!", "%"]
        return HStack(spacing: 6) {
            ForEach(keys, id: \.self) { key in
                PillButton(key, style: .outlined, size: .small) { input += key }
            }
            Spacer()
            PillButton("Clear", style: .ghost, size: .small) { input = "" }
        }
    }

    private func reference(_ label: String, _ value: String) -> some View {
        HStack(alignment: .top, spacing: 8) {
            Text(label)
                .font(.app(11, .semibold))
                .foregroundStyle(Palette.textSecondary)
                .frame(width: 74, alignment: .leading)
            Text(value)
                .font(.system(size: 11.5, design: .monospaced))
                .foregroundStyle(Palette.text)
                .fixedSize(horizontal: false, vertical: true)
            Spacer(minLength: 0)
        }
    }

    private func commit() {
        guard case .success(let value)? = preview else { return }
        history.insert((input.trimmingCharacters(in: .whitespaces), value), at: 0)
        history = Array(history.prefix(25))
    }
}

// MARK: - Background remover

/// Owned by `RootView` so the open image and the recents list survive leaving
/// the Toolbox, exactly as the file viewer and converter do. As a bare `@State`
/// inside the tool it was thrown away every time you navigated anywhere.
@MainActor
final class BackgroundRemoverModel: ObservableObject {
    @Published var source: URL?
    @Published var result: BackgroundRemover.Result?
    @Published var background = BackgroundRemover.Background.transparent
    @Published var isWorking = false
    @Published var error: String?
    @Published var savedTo: URL?
    @Published var recents: [URL] = []

    func open(_ url: URL) {
        source = url
        recents.removeAll { $0 == url }
        recents.insert(url, at: 0)
        recents = Array(recents.prefix(10))
    }

    /// Clears the current image but keeps the recents, matching `reset()` on
    /// the other two tools.
    func reset() {
        source = nil
        result = nil
        error = nil
        savedTo = nil
    }

    func run() async {
        guard let source else { return }
        isWorking = true
        error = nil
        savedTo = nil
        defer { isWorking = false }
        do {
            result = try await BackgroundRemover.removeBackground(
                from: source, background: background)
        } catch {
            result = nil
            self.error = error.localizedDescription
        }
    }

    func save() {
        guard let result, let source else { return }
        do {
            savedTo = try BackgroundRemover.save(result, basedOn: source)
        } catch {
            self.error = "Couldn't save: \(error.localizedDescription)"
        }
    }
}

/// Subject cutout using Vision's on-device foreground masking.
struct BackgroundRemoverTool: View {
    @ObservedObject var model: BackgroundRemoverModel
    @Environment(\.inFloatingPanel) private var inPanel
    @State private var isTargeted = false

    var body: some View {
        if model.source == nil {
            ToolEmptyState(
                icon: "person.and.background.dotted",
                title: "Remove a background",
                message: "Drop a photo here to lift the subject out of it. Runs entirely "
                    + "on this Mac — nothing is uploaded.",
                isTargeted: isTargeted,
                recents: model.recents,
                onSelectRecent: { url in
                    model.open(url)
                    Task { await model.run() }
                }
            ) {
                PillButton("Choose Image", icon: "folder", style: .filled) { pick() }
            }
            .frame(minHeight: 380)
            .dropsFiles(isTargeted: $isTargeted, onDrop: accept)
        } else {
            editor
        }
    }

    @ViewBuilder
    private var editor: some View {
        ToolTitle(title: "Background Remover",
                  subtitle: "Lifts the subject out of a photo. Runs entirely on this Mac — nothing is uploaded.")

        if let source = model.source {
            HStack(spacing: 10) {
                FileRow(url: source, iconSize: 20)
                    // The source image can be dragged back out to any other
                    // tool, a browser upload field, or Finder.
                    .onDrag { NSItemProvider.file(source) }
                PillButton("Change", style: .ghost, size: .small) { pick() }
                PillButton("Clear", style: .ghost, size: .small) { model.reset() }
            }
            .padding(9)
            .wellSurface(radius: 10)

            // Label above rather than beside, and the control takes the full
            // width. Pinned at 320pt with the label stealing the row, four
            // segments left "Transparent" about 70pt — it wrapped onto a
            // second line and spilled straight out of its capsule.
            VStack(alignment: .leading, spacing: 5) {
                Text("Backdrop")
                    .font(.app(11, .semibold))
                    .foregroundStyle(Palette.textSecondary)
                SegmentedControl(
                    options: BackgroundRemover.Background.allCases.map { ($0, $0.rawValue) },
                    selection: $model.background)
                    .frame(maxWidth: .infinity)
            }
            .onChange(of: model.background) { _, _ in Task { await model.run() } }
        }

        dropZone

        if model.isWorking {
            HStack(spacing: 9) {
                ProgressView().controlSize(.small)
                Text("Finding the subject…")
                    .font(.app(12.5)).foregroundStyle(Palette.textSecondary)
            }
        }

        if let error = model.error {
            InfoBanner(kind: .warning, text: error)
        }

        if let result = model.result {
            // Checkerboard behind the preview so transparency is visible rather
            // than blending into the page.
            ZStack {
                CheckerboardBackground()
                Image(nsImage: result.image)
                    .resizable()
                    .aspectRatio(contentMode: .fit)
                    .padding(10)
            }
            .frame(height: inPanel ? 200 : 300)
            .clipShape(RoundedRectangle(cornerRadius: 12))
            .overlay(RoundedRectangle(cornerRadius: 12)
                .strokeBorder(Palette.separator, lineWidth: 1))

            HStack(spacing: 9) {
                Text(result.subjectCount == 1
                     ? "1 subject found"
                     : "\(result.subjectCount) subjects found")
                    .font(.app(11.5)).foregroundStyle(Palette.textSecondary)
                Spacer()
                PillButton("Copy", icon: "doc.on.doc", style: .ghost, size: .small) {
                    NSPasteboard.general.clearContents()
                    NSPasteboard.general.writeObjects([result.image])
                }
                PillButton("Save PNG", icon: "arrow.down", style: .filled, size: .small) {
                    model.save()
                }
            }

            if let savedTo = model.savedTo {
                HStack(spacing: 9) {
                    FileIcon(url: savedTo, size: 20)
                    Text("Saved to Downloads as **\(savedTo.lastPathComponent)**")
                        .font(.app(12.5))
                        .foregroundStyle(Palette.text)
                    Spacer()
                    PillButton("Show", style: .ghost, size: .small) {
                        NSWorkspace.shared.activateFileViewerSelecting([savedTo])
                    }
                }
                .padding(10)
                .background(Palette.positive.opacity(0.12), in: RoundedRectangle(cornerRadius: 11))
                .overlay(RoundedRectangle(cornerRadius: 11)
                    .strokeBorder(Palette.positive.opacity(0.3), lineWidth: 1))
                // The finished cutout is itself a drag source.
                .onDrag { NSItemProvider.file(savedTo) }
            }
        }
    }

    private var dropZone: some View {
        VStack(spacing: 10) {
            Image(systemName: "photo.badge.plus")
                .font(.system(size: 26))
                .foregroundStyle(isTargeted ? Palette.accent : Palette.textSecondary)
            Text("Drop another photo")
                .font(.app(12.5)).foregroundStyle(Palette.textSecondary)
            PillButton("Choose Image", icon: "folder", style: .outlined, size: .small) { pick() }
        }
        .frame(maxWidth: .infinity)
        .padding(.vertical, 22)
        .background(RoundedRectangle(cornerRadius: 14)
            .fill(isTargeted ? Palette.accent.opacity(0.08) : .clear))
        .background(RoundedRectangle(cornerRadius: 14)
            .strokeBorder(isTargeted ? Palette.accent : Palette.border,
                          style: StrokeStyle(lineWidth: 1.5, dash: [7, 5])))
        .dropsFiles(isTargeted: $isTargeted, onDrop: accept)
    }

    private func accept(_ urls: [URL]) {
        guard let first = urls.first else { return }
        model.open(first)
        Task { await model.run() }
    }

    private func pick() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.image]
        panel.allowsMultipleSelection = false
        guard panel.runModal() == .OK, let url = panel.url else { return }
        model.open(url)
        Task { await model.run() }
    }
}

/// Grey checkerboard, the conventional way to show transparency.
private struct CheckerboardBackground: View {
    var body: some View {
        Canvas { context, size in
            let tile: CGFloat = 10
            context.fill(Path(CGRect(origin: .zero, size: size)),
                         with: .color(Palette.surface))
            var row = 0
            var y: CGFloat = 0
            while y < size.height {
                var column = 0
                var x: CGFloat = 0
                while x < size.width {
                    if (row + column) % 2 == 0 {
                        context.fill(Path(CGRect(x: x, y: y, width: tile, height: tile)),
                                     with: .color(Palette.outline.opacity(0.08)))
                    }
                    x += tile; column += 1
                }
                y += tile; row += 1
            }
        }
    }
}
