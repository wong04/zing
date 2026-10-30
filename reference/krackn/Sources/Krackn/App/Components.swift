import SwiftUI
import AppKit

/// Custom controls that replace the stock AppKit look. The point is to stop
/// looking like a default SwiftUI app: everything here is drawn by hand so the
/// whole surface reads as one designed thing rather than a system form.

// MARK: - Pill button

/// The app's button.
///
/// Rounded rectangle with continuous corners, medium-weight label, icon leading
/// — the way macOS lays out a button. Three emphases:
///
/// * `filled` — the one primary action on a screen. Accent fill, no outline.
/// * `outlined` — secondary actions. A neutral control fill and a hairline,
///   so it reads as a button without competing with the primary.
/// * `ghost` — tertiary, inline actions. No resting chrome; a soft fill only
///   under the pointer.
///
/// The earlier capsule-with-a-dark-stroke on every variant, plus a 2% scale
/// bounce on hover, is what made the app read as playful rather than polished.
struct PillButton: View {
    enum Style { case filled, outlined, ghost }
    enum Size { case regular, large, small }

    let title: String
    var icon: String?
    var style: Style = .filled
    var size: Size = .regular
    let action: () -> Void

    /// Unlabelled title and trailing-closure action, so call sites read like
    /// `PillButton("Download", icon: "arrow.down") { … }`.
    init(_ title: String, icon: String? = nil, style: Style = .filled,
         size: Size = .regular, action: @escaping () -> Void) {
        self.title = title
        self.icon = icon
        self.style = style
        self.size = size
        self.action = action
    }

    @State private var hovering = false
    @Environment(\.isEnabled) private var isEnabled

    var body: some View {
        let shape = RoundedRectangle(cornerRadius: Metrics.controlRadius, style: .continuous)
        Button(action: action) {
            HStack(spacing: 6) {
                if let icon {
                    Image(systemName: icon)
                        .font(.system(size: fontSize - 1, weight: .medium))
                }
                Text(title)
            }
            .font(.app(fontSize, .medium))
            .foregroundStyle(foreground)
            .padding(.vertical, verticalPadding)
            .padding(.horizontal, horizontalPadding)
            .background(background, in: shape)
            .overlay(shape.strokeBorder(style == .outlined || !isEnabled ? Palette.separator : .clear,
                                        lineWidth: 1))
            .contentShape(shape)
        }
        .buttonStyle(.plain)
        .onHover { hovering = $0 }
        .animation(.easeOut(duration: 0.12), value: hovering)
    }

    private var fontSize: CGFloat {
        switch size { case .large: return 14; case .regular: return 13; case .small: return 12 }
    }
    private var verticalPadding: CGFloat {
        switch size { case .large: return 10; case .regular: return 7; case .small: return 4.5 }
    }
    private var horizontalPadding: CGFloat {
        switch size { case .large: return 20; case .regular: return 14; case .small: return 10 }
    }

    private var foreground: Color {
        guard isEnabled else { return Palette.textSecondary.opacity(0.85) }
        return style == .filled ? Palette.onAccent : Palette.text
    }

    private var background: Color {
        let live = hovering && isEnabled
        switch style {
        // Disabled primary goes neutral, as macOS draws it. Accent at reduced
        // opacity all but vanished on the dark palette.
        case .filled where !isEnabled: return Palette.controlFill
        case .filled: return live ? Palette.accent.opacity(0.88) : Palette.accent
        case .outlined: return live ? Palette.selection.opacity(0.7) : Palette.controlFill
        case .ghost: return live ? Palette.controlFill : .clear
        }
    }
}

// MARK: - Circular icon button

/// A glyph-only button.
///
/// Borderless at rest, like a macOS toolbar item; a soft fill appears under the
/// pointer. `filled` is the exception — an accent disc for the rare icon that
/// *is* the primary action.
struct IconButton: View {
    let system: String
    var size: CGFloat = 30
    var filled = false
    let action: () -> Void

    @State private var hovering = false
    @Environment(\.isEnabled) private var isEnabled

    var body: some View {
        Button(action: action) {
            Image(systemName: system)
                .font(.system(size: size * 0.44, weight: .regular))
                .foregroundStyle(filled ? Palette.onAccent : Palette.text.opacity(0.75))
                .frame(width: size, height: size)
                .background {
                    if filled {
                        Circle().fill(hovering ? Palette.accent.opacity(0.88) : Palette.accent)
                    } else {
                        RoundedRectangle(cornerRadius: size * 0.26, style: .continuous)
                            .fill(hovering && isEnabled ? Palette.controlFill : .clear)
                    }
                }
                .contentShape(Rectangle())
                .opacity(isEnabled ? 1 : 0.35)
        }
        .buttonStyle(.plain)
        .onHover { hovering = $0 }
        .animation(.easeOut(duration: 0.12), value: hovering)
    }
}

// MARK: - Segmented control

/// Segmented control in the macOS idiom: a recessed track with the selected
/// segment raised on a light, softly shadowed plate.
///
/// The old version flooded the selected segment with accent and outlined it in
/// near-black, so every picker in the app shouted. Selection is now shown the
/// way the system shows it — by elevation, not colour — which leaves accent
/// for the things that genuinely need attention.
struct SegmentedControl<T: Hashable>: View {
    let options: [(value: T, label: String)]
    @Binding var selection: T
    /// Smallest the label may be scaled before it would have to truncate.
    var minimumScale: CGFloat = 0.65
    @Namespace private var namespace

    var body: some View {
        let outer = RoundedRectangle(cornerRadius: Metrics.controlRadius, style: .continuous)
        let inner = RoundedRectangle(cornerRadius: Metrics.controlRadius - 2, style: .continuous)
        HStack(spacing: 2) {
            ForEach(options, id: \.value) { option in
                let isSelected = option.value == selection
                Text(option.label)
                    .font(.app(12.5, isSelected ? .semibold : .medium))
                    .foregroundStyle(isSelected ? Palette.text : Palette.textSecondary)
                    // One line, shrinking if needed — an equal-width segment
                    // with a long label must never wrap out of its plate.
                    .lineLimit(1)
                    .minimumScaleFactor(minimumScale)
                    .padding(.horizontal, 8)
                    .padding(.vertical, 5)
                    .frame(maxWidth: .infinity)
                    .background {
                        if isSelected {
                            inner.fill(Palette.raised)
                                .overlay(inner.strokeBorder(Palette.separator, lineWidth: 1))
                                .shadow(color: .black.opacity(0.08), radius: 2, y: 1)
                                .matchedGeometryEffect(id: "seg", in: namespace)
                        }
                    }
                    .contentShape(Rectangle())
                    .onTapGesture {
                        withAnimation(.snappy(duration: 0.22)) { selection = option.value }
                    }
                    .help(option.label)
            }
        }
        .padding(2)
        .background(Palette.controlFill, in: outer)
        .overlay(outer.strokeBorder(Palette.separator, lineWidth: 1))
        .fixedSize(horizontal: false, vertical: true)
    }
}

// MARK: - Text field

/// Text field in the app's style: a recessed control with a hairline, and a
/// soft accent focus ring — the macOS focus treatment — rather than a hard
/// recolour of the border.
struct PillField: View {
    let placeholder: String
    @Binding var text: String
    var icon: String?
    var onSubmit: (() -> Void)?

    @FocusState private var focused: Bool

    var body: some View {
        let shape = RoundedRectangle(cornerRadius: Metrics.controlRadius, style: .continuous)
        HStack(spacing: 7) {
            if let icon {
                Image(systemName: icon)
                    .font(.system(size: 12, weight: .regular))
                    .foregroundStyle(Palette.textSecondary)
            }
            TextField(placeholder, text: $text)
                .textFieldStyle(.plain)
                .font(.app(13))
                .foregroundStyle(Palette.text)
                .focused($focused)
                .onSubmit { onSubmit?() }
        }
        .padding(.vertical, 7)
        .padding(.horizontal, 10)
        .background(Palette.controlFill, in: shape)
        .overlay(shape.strokeBorder(Palette.separator, lineWidth: 1))
        .overlay(
            shape.inset(by: -2)
                .stroke(Palette.accent.opacity(focused ? 0.55 : 0), lineWidth: 3)
        )
        .animation(.easeOut(duration: 0.14), value: focused)
    }
}

/// Text field that selects everything when it gains focus, the way a browser
/// address bar does — so clicking in and typing replaces the URL rather than
/// inserting into the middle of it.
struct SelectAllField: NSViewRepresentable {
    @Binding var text: String
    var placeholder: String
    /// Mirrors first-responder state back out, so the caller can draw a focus
    /// ring. `@FocusState` can't see inside an `NSViewRepresentable`, so the
    /// browser's address bar ring was permanently off before this existed.
    var isFocused: Binding<Bool>?
    /// Called when the field gives up focus without submitting, so the address
    /// bar can snap back to the URL that's actually loaded.
    var onCancel: (() -> Void)?
    /// Set to ask the field for focus (⌘L); the field clears it once it has.
    var focusRequest: Binding<Bool>?
    /// Shown, centred, while the field isn't being edited — the site's name
    /// in place of its full address, as Safari's bar does. Clicking in swaps
    /// the full address back in, selected.
    var idleText: String?
    var onSubmit: () -> Void

    final class Field: NSTextField {
        var onSubmit: (() -> Void)?
        var onFocusChange: ((Bool) -> Void)?
        /// The real value, for when the idle text is what's on screen.
        var fullText = ""
        var showsIdle = false

        /// The short form shown at rest, kept here so the field can put it
        /// back itself the moment editing ends.
        var idleText: String?

        /// Focus by any route — click, Tab, ⌘L — swaps the full address in
        /// and reports focus. Focus is reported *here*, not from
        /// `controlTextDidBeginEditing`: AppKit only sends that on the first
        /// keystroke, so a click that just selected the address left the app
        /// believing the bar was idle — and with nothing to say editing had
        /// started, nothing ever put the short domain back afterwards.
        /// Set just before a deliberate focus: ⌘L, or a click on the field.
        var focusRequested = false
        /// True while a click is taking focus, so `becomeFirstResponder`
        /// leaves the selecting to `mouseDown`.
        private var selectingFromClick = false

        /// Only a person decides when the bar is being edited. AppKit also
        /// hands focus to the first text field it finds when a window opens or
        /// becomes key — the address bar — which left it already "editing"
        /// before anyone clicked it. The user's click then landed inside an
        /// active field: a caret or a one-letter selection instead of the
        /// whole address, and the full URL showing when nobody asked for it.
        ///
        /// A click counts only through this field's own `mouseDown`, which
        /// sets `focusRequested`. Accepting *any* mouse-down inside the bar
        /// let in AppKit's automatic focus during the click that activates
        /// the window: the bar started editing with nothing selected, and
        /// from then on clicks went to the text editor — a caret, or a
        /// one-character drag — instead of selecting the address.
        private func userAskedForFocus() -> Bool {
            if focusRequested { return true }
            return NSApp.currentEvent?.type == .keyDown   // Tab into it
        }

        /// Take the click that brings the window forward, too: one click on
        /// the bar of a background window selects the address, as in Safari.
        override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

        override func becomeFirstResponder() -> Bool {
            guard userAskedForFocus() else { return false }
            focusRequested = false
            if showsIdle {
                stringValue = fullText
                alignment = .left
                showsIdle = false
            }
            let ok = super.becomeFirstResponder()
            if ok {
                onFocusChange?(true)
                // A click selects all in `mouseDown`, after the click has
                // finished. Selecting from here as well raced it: the select
                // ran while AppKit was still tracking the click, and the
                // mouse-up then dropped a caret where the pointer was —
                // the highlight flashed and vanished.
                let isClick = NSApp.currentEvent?.type == .leftMouseDown || selectingFromClick
                if !isClick {
                    DispatchQueue.main.async { [weak self] in
                        self?.currentEditor()?.selectAll(nil)
                    }
                }
            }
            return ok
        }

        /// The first click into the bar selects the whole address and keeps
        /// it selected, so ⌘C copies it and typing replaces it.
        ///
        /// That click is *not* passed on to AppKit's text handling. Passing it
        /// on and selecting afterwards lost a race either way: selecting first
        /// let the click's tracking replace the selection with a caret, and
        /// selecting after `super` still left the tracking to treat the click
        /// as a tiny drag — one character highlighted. Taking focus and
        /// selecting here, with nothing after it, leaves nothing to undo it.
        /// Once editing, clicks behave normally: caret, drag, double-click.
        override func mouseDown(with event: NSEvent) {
            guard currentEditor() == nil else {
                super.mouseDown(with: event)
                return
            }
            // This *is* the user asking — say so directly rather than relying
            // on reading the current event back.
            focusRequested = true
            selectingFromClick = true
            window?.makeFirstResponder(self)
            selectingFromClick = false
            currentEditor()?.selectAll(nil)
        }

        /// Back to the short form, straight away — not whenever SwiftUI next
        /// happens to redraw.
        func showIdle() {
            guard let idle = idleText, !idle.isEmpty else { return }
            stringValue = idle
            alignment = .center
            showsIdle = true
        }
    }

    func makeNSView(context: Context) -> Field {
        let field = Field()
        field.delegate = context.coordinator
        field.isBordered = false
        field.drawsBackground = false
        field.focusRingType = .none
        field.font = NSFont.systemFont(ofSize: 13)
            ?? .systemFont(ofSize: 13)
        field.placeholderString = placeholder
        field.lineBreakMode = .byTruncatingTail
        field.cell?.usesSingleLineMode = true
        field.cell?.wraps = false
        field.cell?.isScrollable = true
        field.onSubmit = onSubmit
        field.onFocusChange = { focused in
            // Bounced through the next runloop turn: this fires from inside
            // AppKit's responder change, and mutating SwiftUI state during a
            // view update logs a "Modifying state during view update" warning.
            DispatchQueue.main.async { isFocused?.wrappedValue = focused }
        }
        return field
    }

    func updateNSView(_ field: Field, context: Context) {
        // The field editor's existence is the authoritative answer to "is the
        // user typing in here right now" — more reliable than any flag we keep
        // ourselves, because AppKit owns it.
        let editing = field.currentEditor() != nil

        if focusRequest?.wrappedValue == true {
            DispatchQueue.main.async {
                field.focusRequested = true
                field.window?.makeFirstResponder(field)
                focusRequest?.wrappedValue = false
            }
        }

        field.fullText = text
        field.idleText = idleText
        // Never fight the user mid-edit: only push text in when the field
        // isn't focused, otherwise the caret jumps as the page loads.
        if !editing {
            let shown = (idleText?.isEmpty == false) ? idleText! : text
            let idle = idleText?.isEmpty == false
            if field.stringValue != shown { field.stringValue = shown }
            field.alignment = idle ? .center : .left
            field.showsIdle = idle
        }

        // Self-correct a stale focus flag. Even with the delegate callbacks
        // below, focus can be lost in ways that don't route through them — the
        // web view taking first responder, a window losing key. Reconciling on
        // every update means the address bar can't get permanently stuck
        // thinking it's being edited.
        if !editing, isFocused?.wrappedValue == true {
            DispatchQueue.main.async { isFocused?.wrappedValue = false }
        }

        field.placeholderString = placeholder
        field.onSubmit = onSubmit
        // Refreshed every update, so the callbacks never hold a stale binding
        // from whenever the view happened to be created.
        field.onFocusChange = { focused in
            DispatchQueue.main.async { isFocused?.wrappedValue = focused }
        }
        context.coordinator.field = field
        context.coordinator.text = $text
        context.coordinator.onCancel = onCancel
    }

    func makeCoordinator() -> Coordinator { Coordinator(text: $text) }

    final class Coordinator: NSObject, NSTextFieldDelegate {
        var text: Binding<String>
        var onCancel: (() -> Void)?
        weak var field: Field?
        init(text: Binding<String>) { self.text = text }

        // End of editing is reliable here: AppKit posts it when the field
        // editor resigns, whether or not anything was typed.
        // Focus *begins* in `Field.becomeFirstResponder`; this notification
        // only arrives on the first keystroke, too late to mean "focused".

        func controlTextDidEndEditing(_ note: Notification) {
            guard let field = note.object as? Field else { return }
            field.onFocusChange?(false)
            // Whatever was typed and not submitted is abandoned; the bar
            // shows the page it's actually on again.
            field.fullText = text.wrappedValue
            field.showIdle()
        }

        func controlTextDidChange(_ note: Notification) {
            guard let field = note.object as? NSTextField else { return }
            text.wrappedValue = field.stringValue
        }

        func control(_ control: NSControl, textView: NSTextView,
                     doCommandBy selector: Selector) -> Bool {
            switch selector {
            case #selector(NSResponder.insertNewline(_:)):
                (control as? Field)?.onSubmit?()
                // Done editing: give focus back, as Safari does, so the bar
                // shows the domain again and the next click selects it all.
                control.window?.makeFirstResponder(nil)
                return true
            case #selector(NSResponder.cancelOperation(_:)):
                // Escape abandons the edit and restores the live URL, as it
                // does in Safari and Chrome.
                onCancel?()
                control.window?.makeFirstResponder(nil)
                return true
            default:
                return false
            }
        }
    }
}

// MARK: - Dropdown

/// Pop-up menu styled as a control, matching `PillField`.
struct PillMenu<T: Hashable>: View {
    let options: [(value: T, label: String)]
    @Binding var selection: T

    private var currentLabel: String {
        options.first { $0.value == selection }?.label ?? ""
    }

    var body: some View {
        let shape = RoundedRectangle(cornerRadius: Metrics.controlRadius, style: .continuous)
        Menu {
            ForEach(options, id: \.value) { option in
                Button(option.label) { selection = option.value }
            }
        } label: {
            HStack(spacing: 6) {
                Text(currentLabel)
                    .font(.app(13))
                    .foregroundStyle(Palette.text)
                Spacer(minLength: 4)
                Image(systemName: "chevron.up.chevron.down")
                    .font(.system(size: 9, weight: .semibold))
                    .foregroundStyle(Palette.textSecondary)
            }
            .padding(.vertical, 6)
            .padding(.horizontal, 10)
            .background(Palette.controlFill, in: shape)
            .overlay(shape.strokeBorder(Palette.separator, lineWidth: 1))
        }
        // `.button` + `.plain` is the combination that keeps a custom label:
        // `.borderlessButton` hands the label to AppKit, which redraws it as
        // bare text and drops the background and the chevron's placement.
        .menuStyle(.button)
        .buttonStyle(.plain)
        .menuIndicator(.hidden)
        .fixedSize()
    }
}

// MARK: - Theme

/// The user's appearance choice, persisted across launches. `system` follows
/// macOS; the other two pin it regardless of the OS setting.
enum AppTheme: String, CaseIterable, Identifiable {
    case system, light, dark
    var id: String { rawValue }

    var label: String {
        switch self { case .system: return "Auto"; case .light: return "Light"; case .dark: return "Dark" }
    }
    var icon: String {
        switch self {
        case .system: return "circle.lefthalf.filled"
        case .light: return "sun.max.fill"
        case .dark: return "moon.fill"
        }
    }
    /// nil means "follow the system", which is what SwiftUI wants for `system`.
    var colorScheme: ColorScheme? {
        switch self { case .system: return nil; case .light: return .light; case .dark: return .dark }
    }
    var nsAppearance: NSAppearance? {
        switch self {
        case .system: return nil
        case .light: return NSAppearance(named: .aqua)
        case .dark: return NSAppearance(named: .darkAqua)
        }
    }
}

/// Auto / Light / Dark switch — the same raised-plate idiom as
/// `SegmentedControl`, so the two never look like different controls.
struct ThemeSwitcher: View {
    @Binding var theme: AppTheme
    var compact = false
    @Namespace private var plate

    var body: some View {
        let outer = RoundedRectangle(cornerRadius: Metrics.controlRadius, style: .continuous)
        let inner = RoundedRectangle(cornerRadius: Metrics.controlRadius - 2, style: .continuous)
        HStack(spacing: 2) {
            ForEach(AppTheme.allCases) { option in
                let selected = option == theme
                Button {
                    withAnimation(.snappy(duration: 0.22)) { theme = option }
                } label: {
                    Group {
                        if compact {
                            Image(systemName: option.icon)
                        } else {
                            HStack(spacing: 4) {
                                Image(systemName: option.icon)
                                Text(option.label)
                            }
                        }
                    }
                    .font(.app(11.5, selected ? .semibold : .medium))
                    .foregroundStyle(selected ? Palette.text : Palette.textSecondary)
                    .frame(maxWidth: compact ? nil : .infinity)
                    .frame(height: 24)
                    .frame(width: compact ? 30 : nil)
                    .background {
                        if selected {
                            inner.fill(Palette.raised)
                                .overlay(inner.strokeBorder(Palette.separator, lineWidth: 1))
                                .shadow(color: .black.opacity(0.08), radius: 2, y: 1)
                                .matchedGeometryEffect(id: "theme", in: plate)
                        }
                    }
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
            }
        }
        .padding(2)
        .background(Palette.controlFill, in: outer)
        .overlay(outer.strokeBorder(Palette.separator, lineWidth: 1))
    }
}

// MARK: - Slider

/// Slider: thin recessed track, accent fill, white knob with a soft shadow —
/// the system slider's proportions, drawn to match the palette.
struct PillSlider: View {
    @Binding var value: Double
    var range: ClosedRange<Double>
    var step: Double = 1

    var body: some View {
        GeometryReader { geo in
            let width = geo.size.width
            let fraction = CGFloat((value - range.lowerBound) / (range.upperBound - range.lowerBound))
            let clamped = min(max(fraction, 0), 1)
            let knob: CGFloat = 18

            ZStack(alignment: .leading) {
                Capsule().fill(Palette.controlFill).frame(height: 4)
                    .overlay(Capsule().strokeBorder(Palette.separator, lineWidth: 0.5))
                Capsule().fill(Palette.accent)
                    .frame(width: max(knob / 2, clamped * width), height: 4)
                Circle()
                    .fill(Color.white)
                    .frame(width: knob, height: knob)
                    .overlay(Circle().strokeBorder(Color.black.opacity(0.08), lineWidth: 0.5))
                    .shadow(color: .black.opacity(0.18), radius: 2, y: 1)
                    .offset(x: clamped * (width - knob))
            }
            .frame(height: knob)
            .contentShape(Rectangle())
            .gesture(
                DragGesture(minimumDistance: 0).onChanged { g in
                    let ratio = min(max((g.location.x - knob / 2) / (width - knob), 0), 1)
                    let raw = range.lowerBound + Double(ratio) * (range.upperBound - range.lowerBound)
                    let stepped = (raw / step).rounded() * step
                    value = min(max(stepped, range.lowerBound), range.upperBound)
                }
            )
        }
        .frame(height: 18)
    }
}

// MARK: - Toggle

/// Switch in the macOS proportions: accent track when on, recessed when off,
/// and a white knob in both states. The previous near-black knob on an
/// accent track was the most toy-like control in the app.
struct PillToggleStyle: ToggleStyle {
    func makeBody(configuration: Configuration) -> some View {
        HStack {
            configuration.label
                .font(.app(13))
                .foregroundStyle(Palette.text)
            Spacer()
            Capsule()
                .fill(configuration.isOn ? Palette.accent : Palette.controlFill)
                .frame(width: 34, height: 20)
                .overlay(Capsule().strokeBorder(
                    configuration.isOn ? .clear : Palette.separator, lineWidth: 1))
                .overlay(alignment: configuration.isOn ? .trailing : .leading) {
                    Circle()
                        .fill(Color.white)
                        .frame(width: 16, height: 16)
                        .padding(2)
                        .shadow(color: .black.opacity(0.2), radius: 1.5, y: 0.5)
                }
                .contentShape(Capsule())
                .onTapGesture {
                    withAnimation(.snappy(duration: 0.2)) { configuration.isOn.toggle() }
                }
        }
    }
}

extension ToggleStyle where Self == PillToggleStyle {
    static var pill: PillToggleStyle { PillToggleStyle() }
}

// MARK: - Window chrome

/// Reaches up to the hosting `NSWindow` to make the title bar transparent and
/// full-height, and to apply the chosen appearance. The transparent title bar
/// removes the grey system bar — the biggest thing making the app read as a
/// generic macOS form — and lets the theme colour run edge to edge behind the
/// traffic lights.
struct WindowChrome: NSViewRepresentable {
    var appearance: NSAppearance?

    /// How far the traffic lights move in from AppKit's default spot, so they
    /// sit *inside* the inset sidebar with even margins — the way Finder's do
    /// — instead of straddling its rounded top-left corner.
    static let trafficLightOffset = CGSize(width: 10, height: 10)

    final class Coordinator {
        var observers: [NSObjectProtocol] = []
        deinit { observers.forEach(NotificationCenter.default.removeObserver) }
    }

    func makeCoordinator() -> Coordinator { Coordinator() }

    func makeNSView(context: Context) -> NSView {
        let view = NSView()
        DispatchQueue.main.async { [weak view] in
            guard let window = view?.window else { return }
            configure(window)
            observe(window, coordinator: context.coordinator)
        }
        return view
    }

    func updateNSView(_ nsView: NSView, context: Context) {
        DispatchQueue.main.async { [weak nsView] in
            if let window = nsView?.window { configure(window) }
        }
    }

    private func configure(_ window: NSWindow) {
        window.titlebarAppearsTransparent = true
        window.titleVisibility = .hidden
        window.styleMask.insert(.fullSizeContentView)
        // Both off deliberately. `isMovableByWindowBackground` would let a
        // stray drag inside a list or the web view move the window; `isMovable`
        // would let the invisible titlebar band swallow drags meant for the
        // tabs sitting inside it. `WindowDragArea` moves the window instead.
        window.isMovableByWindowBackground = false
        window.isMovable = false
        // The low-profile sidebar slides out when the cursor reaches the
        // window's edge, which it learns from mouse-moved events — and AppKit
        // doesn't deliver those at all unless the window asks for them.
        window.acceptsMouseMovedEvents = true
        // Pinning the window appearance is what makes the manual Light/Dark
        // choice actually flip the palette, since the dynamic colours resolve
        // against the window's effective appearance.
        window.appearance = appearance
        window.backgroundColor = NSColor(Palette.background)
        Self.placeTrafficLights(in: window)
    }

    /// AppKit lays the buttons out again on resize, on entering and leaving
    /// full screen, and when the window changes key state — each of which
    /// puts them back at the default spot. Re-applying on those events is
    /// what keeps them where they belong.
    private func observe(_ window: NSWindow, coordinator: Coordinator) {
        guard coordinator.observers.isEmpty else { return }
        let names: [Notification.Name] = [
            NSWindow.didResizeNotification, NSWindow.didEndLiveResizeNotification,
            NSWindow.didBecomeKeyNotification, NSWindow.didResignKeyNotification,
            NSWindow.didExitFullScreenNotification, NSWindow.didEnterFullScreenNotification,
            NSWindow.didChangeScreenNotification,
        ]
        coordinator.observers = names.map { name in
            NotificationCenter.default.addObserver(forName: name, object: window, queue: .main) {
                [weak window] _ in
                guard let window else { return }
                Self.placeTrafficLights(in: window)
                // A second pass after AppKit's own layout for the same event.
                DispatchQueue.main.async { Self.placeTrafficLights(in: window) }
            }
        }
    }

    static func placeTrafficLights(in window: NSWindow) {
        // Full screen hides the lights in the menu-bar reveal; leave them be.
        guard !window.styleMask.contains(.fullScreen) else { return }
        let kinds: [NSWindow.ButtonType] = [.closeButton, .miniaturizeButton, .zoomButton]
        let buttons = kinds.compactMap { window.standardWindowButton($0) }
        guard buttons.count == 3, let container = buttons[0].superview else { return }
        // AppKit's own spacing between the buttons, kept as is.
        let spacing = buttons[1].frame.minX - buttons[0].frame.minX
        let size = buttons[0].frame.size
        // AppKit's default, measured: 7pt from the left, 6pt from the top of
        // a 28pt band. Moving them below that band is safe — measured too:
        // the titlebar doesn't clip them.
        let left: CGFloat = 7 + trafficLightOffset.width
        let fromTop: CGFloat = 6 + trafficLightOffset.height
        let y = container.isFlipped ? fromTop : container.bounds.height - fromTop - size.height
        for (index, button) in buttons.enumerated() {
            let origin = CGPoint(x: left + CGFloat(index) * spacing, y: y)
            if button.frame.origin != origin { button.setFrameOrigin(origin) }
        }
    }
}

/// A strip that drags the window. Placed only along the top bar, so dragging
/// inside content never moves the window by accident.
///
/// The window is set `isMovable = false` (see `WindowChrome`), because with a
/// transparent full-size-content titlebar AppKit still reserves the top ~28pt
/// for window dragging — clicks fall through to our content, but *drags* get
/// swallowed, which made dragging a tab move the window. With movement
/// disabled, this view performs it manually instead, so only these strips move
/// the window and everything else keeps its own gestures.
struct WindowDragArea: NSViewRepresentable {
    final class DragView: NSView {
        /// Cursor position within the window when the drag began.
        private var grabOffset: CGPoint?

        override func mouseDown(with event: NSEvent) {
            guard let window else { return }
            // Double-click behaves like the titlebar: zoom or minimise,
            // following the user's system preference.
            if event.clickCount == 2 {
                let action = UserDefaults.standard
                    .string(forKey: "AppleActionOnDoubleClick") ?? "Maximize"
                if action == "Minimize" { window.miniaturize(nil) }
                else if action != "None" { window.zoom(nil) }
                return
            }
            let mouse = NSEvent.mouseLocation
            let origin = window.frame.origin
            grabOffset = CGPoint(x: mouse.x - origin.x, y: mouse.y - origin.y)
        }

        override func mouseDragged(with event: NSEvent) {
            guard let window, let grabOffset else { return }
            let mouse = NSEvent.mouseLocation
            window.setFrameOrigin(NSPoint(x: mouse.x - grabOffset.x,
                                          y: mouse.y - grabOffset.y))
        }

        override func mouseUp(with event: NSEvent) { grabOffset = nil }
    }
    func makeNSView(context: Context) -> NSView { DragView() }
    func updateNSView(_ nsView: NSView, context: Context) {}
}

extension View {
    // There is deliberately no `draggableWindowArea()` convenience here.
    // It was `background(WindowDragArea())`, which looks equivalent to placing
    // the view directly but silently never receives a single click: SwiftUI
    // only routes hits to a representable that occupies real layout space, so a
    // drag strip has to *be* the padding rather than sit behind it. Every drag
    // strip in the app therefore uses `WindowDragArea().frame(height:)` inline.

    /// The height to leave clear at the top of the leftmost column so content
    /// doesn't slide under the traffic-light buttons once the title bar is
    /// transparent and full-size.
    static var trafficLightInset: CGFloat { 28 }
}

/// The input row the network features share: a field, and the one action it
/// feeds. Download, Open a Link and Diagnose each grew their own version —
/// a card around the field in one, a bare row in another, the button on a
/// separate line in the third — which is most of why they read as three
/// different apps. They all use this now.
struct CommandBar<Accessory: View>: View {
    let placeholder: String
    @Binding var text: String
    var icon = "link"
    let action: String
    var actionIcon = "arrow.right"
    var isWorking = false
    let onRun: () -> Void
    var onCancel: (() -> Void)?
    @ViewBuilder var accessory: Accessory

    private var canRun: Bool { !text.trimmingCharacters(in: .whitespaces).isEmpty }

    var body: some View {
        HStack(spacing: 8) {
            PillField(placeholder: placeholder, text: $text, icon: icon) {
                if canRun && !isWorking { onRun() }
            }
            ZStack {
                if isWorking {
                    PillButton("Stop", style: .outlined) { onCancel?() }
                        .disabled(onCancel == nil)
                        .overlay(alignment: .leading) {
                            ProgressView().controlSize(.mini).offset(x: -22)
                        }
                        .transition(.opacity)
                } else {
                    PillButton(action, icon: actionIcon, style: .filled, action: onRun)
                        .disabled(!canRun)
                        .transition(.opacity)
                }
            }
            .animation(Motion.quick, value: isWorking)
            accessory
        }
    }
}

extension CommandBar where Accessory == EmptyView {
    init(placeholder: String, text: Binding<String>, icon: String = "link", action: String,
         actionIcon: String = "arrow.right", isWorking: Bool = false,
         onRun: @escaping () -> Void, onCancel: (() -> Void)? = nil) {
        self.init(placeholder: placeholder, text: text, icon: icon, action: action,
                  actionIcon: actionIcon, isWorking: isWorking, onRun: onRun,
                  onCancel: onCancel) { EmptyView() }
    }
}

/// Lays children out left to right, wrapping onto new lines — for chips
/// such as recent links, where a horizontal scroll hides most of them.
struct FlowRow: Layout {
    var spacing: CGFloat = 6

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        let width = proposal.width ?? .infinity
        var x: CGFloat = 0, y: CGFloat = 0, lineHeight: CGFloat = 0, widest: CGFloat = 0
        for view in subviews {
            let size = view.sizeThatFits(.unspecified)
            if x > 0, x + size.width > width {
                y += lineHeight + spacing
                x = 0
                lineHeight = 0
            }
            x += size.width + spacing
            widest = max(widest, x - spacing)
            lineHeight = max(lineHeight, size.height)
        }
        return CGSize(width: min(widest, width), height: y + lineHeight)
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews,
                       cache: inout ()) {
        var x = bounds.minX, y = bounds.minY, lineHeight: CGFloat = 0
        for view in subviews {
            let size = view.sizeThatFits(.unspecified)
            if x > bounds.minX, x + size.width > bounds.maxX {
                y += lineHeight + spacing
                x = bounds.minX
                lineHeight = 0
            }
            view.place(at: CGPoint(x: x, y: y), proposal: ProposedViewSize(size))
            x += size.width + spacing
            lineHeight = max(lineHeight, size.height)
        }
    }
}

/// Columns of unequal-height cards, each placed in whichever column is
/// currently shortest — so a seven-item group and a four-item group sit side
/// by side without leaving a hole under the short one, and everything packs
/// into as little height as it can.
struct Masonry: Layout {
    var columnWidth: CGFloat = 250
    var spacing: CGFloat = 12

    private func columns(for width: CGFloat) -> Int {
        max(1, Int((width + spacing) / (columnWidth + spacing)))
    }

    private func arrange(_ subviews: Subviews, width: CGFloat) -> (frames: [CGRect], height: CGFloat) {
        let count = columns(for: width)
        let colWidth = (width - spacing * CGFloat(count - 1)) / CGFloat(count)
        var heights = Array(repeating: CGFloat(0), count: count)
        var frames: [CGRect] = []
        for view in subviews {
            let column = heights.indices.min { heights[$0] < heights[$1] } ?? 0
            let h = view.sizeThatFits(ProposedViewSize(width: colWidth, height: nil)).height
            frames.append(CGRect(x: CGFloat(column) * (colWidth + spacing), y: heights[column],
                                 width: colWidth, height: h))
            heights[column] += h + spacing
        }
        return (frames, max((heights.max() ?? 0) - spacing, 0))
    }

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        let width = proposal.width ?? 800
        return CGSize(width: width, height: arrange(subviews, width: width).height)
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews,
                       cache: inout ()) {
        let frames = arrange(subviews, width: bounds.width).frames
        for (view, frame) in zip(subviews, frames) {
            view.place(at: CGPoint(x: bounds.minX + frame.minX, y: bounds.minY + frame.minY),
                       proposal: ProposedViewSize(frame.size))
        }
    }
}
