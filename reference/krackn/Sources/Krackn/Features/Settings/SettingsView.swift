import SwiftUI
import WebKit

/// Settings, as a sheet over the window.
///
/// It used to be a page in the sidebar: one long scroll of nine cards, each
/// control followed by a paragraph explaining it. Now it's a category list
/// beside flat rows — a name, at most one line of detail, the control on the
/// right — the way macOS and every well-made Mac app lays settings out. The
/// long explanations that matter for honesty (what private browsing can't
/// hide) are still here, one ⓘ away instead of always on screen.
///
/// Every control reads back out of `AppSettings`, and every key there is
/// consumed by something.
struct SettingsSheet: View {
    @ObservedObject var browser: BrowserModel
    @Binding var isPresented: Bool
    @State private var category: SettingsCategory

    /// Reports the open category, so a rebuild (an accent change) can reopen it.
    var onCategoryChange: (SettingsCategory) -> Void = { _ in }

    init(browser: BrowserModel, isPresented: Binding<Bool>, initialCategory: SettingsCategory = .general,
         appearsInstantly: Bool = false, onCategoryChange: @escaping (SettingsCategory) -> Void = { _ in }) {
        self.browser = browser
        self._isPresented = isPresented
        self._category = State(initialValue: initialCategory)
        self._shown = State(initialValue: appearsInstantly)
        self.onCategoryChange = onCategoryChange
    }
    @State private var shown = false
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        GeometryReader { geo in
            ZStack {
                // Everything behind blurs and dims — the window stays visible
                // as context, but plainly out of reach until this closes.
                Rectangle()
                    .fill(.ultraThinMaterial)
                    .overlay(Color.black.opacity(0.22))
                    .opacity(shown ? 1 : 0)
                    .onTapGesture(perform: close)

                HStack(spacing: 0) {
                    sidebar
                    Rectangle().fill(Palette.separator).frame(width: 1)
                    content
                }
                .frame(width: min(860, geo.size.width - 80),
                       height: min(640, geo.size.height - 70))
                .background(Palette.background)
                .clipShape(RoundedRectangle(cornerRadius: 16, style: .continuous))
                .overlay(RoundedRectangle(cornerRadius: 16, style: .continuous)
                    .strokeBorder(Palette.separator, lineWidth: 1))
                .overlay(alignment: .topTrailing) {
                    BarButton(system: "xmark", help: "Close (Esc)", action: close)
                        .keyboardShortcut(.cancelAction)
                        .padding(12)
                }
                .shadow(color: .black.opacity(0.28), radius: 40, y: 18)
                .shadow(color: .black.opacity(0.08), radius: 2, y: 1)
                .scaleEffect(shown || reduceMotion ? 1 : 0.965)
                .offset(y: shown || reduceMotion ? 0 : 10)
                .opacity(shown ? 1 : 0)
            }
            .frame(width: geo.size.width, height: geo.size.height)
        }
        .onAppear { if !shown { withAnimation(Motion.surface) { shown = true } } }
        .onChange(of: category) { _, now in onCategoryChange(now) }
    }

    private func close() {
        withAnimation(Motion.standard) { shown = false }
        Task {
            try? await Task.sleep(for: .milliseconds(220))
            isPresented = false
        }
    }

    private var sidebar: some View {
        VStack(alignment: .leading, spacing: 1) {
            Text("Settings")
                .font(.app(12, .medium))
                .foregroundStyle(Palette.textSecondary)
                .padding(.leading, 10)
                .padding(.top, 22)
                .padding(.bottom, 8)
            ForEach(SettingsCategory.allCases) { item in
                SettingsNavRow(category: item, isSelected: item == category) {
                    withAnimation(Motion.quick) { category = item }
                }
            }
            Spacer()
        }
        .padding(.horizontal, 10)
        .frame(width: 204)
        .background(Palette.tint.opacity(0.6))
    }

    private var content: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 0) {
                Text(category.rawValue)
                    .font(.app(20, .semibold))
                    .tracking(-0.3)
                    .foregroundStyle(Palette.text)
                    .padding(.bottom, 14)
                SettingsPane(category: category, browser: browser)
                    .id(category)
                    .transition(.opacity.combined(with: .offset(y: 6)))
            }
            .padding(.horizontal, 32)
            .padding(.top, 26)
            .padding(.bottom, 32)
            .frame(maxWidth: .infinity, alignment: .leading)
        }
        .scrollIndicators(.automatic)
        .animation(Motion.quick, value: category)
    }
}

enum SettingsCategory: String, CaseIterable, Identifiable {
    case general = "General"
    case browsing = "Browsing"
    case newTab = "New Tab"
    case tabs = "Tabs"
    case files = "Files"
    case privacy = "Privacy"
    case advanced = "Advanced"
    case engines = "Engines"
    case account = "Account"
    case about = "About"

    var id: String { rawValue }

    var icon: String {
        switch self {
        case .general: return "gearshape"
        case .browsing: return "globe"
        case .newTab: return "square.grid.2x2"
        case .tabs: return "square.on.square"
        case .files: return "folder"
        case .privacy: return "hand.raised"
        case .advanced: return "slider.horizontal.3"
        case .engines: return "shippingbox"
        case .account: return "person.crop.circle"
        case .about: return "info.circle"
        }
    }
}

private struct SettingsNavRow: View {
    let category: SettingsCategory
    let isSelected: Bool
    let action: () -> Void
    @State private var hovering = false

    var body: some View {
        HStack(spacing: 9) {
            Image(systemName: category.icon)
                .font(.system(size: 13))
                .foregroundStyle(isSelected ? Palette.accentText : Palette.textSecondary)
                .frame(width: 18)
            Text(category.rawValue)
                .font(.app(13))
                .foregroundStyle(Palette.text)
            Spacer()
        }
        .padding(.horizontal, 9)
        .frame(height: 30)
        .background(RoundedRectangle(cornerRadius: Metrics.controlRadius, style: .continuous)
            .fill(isSelected ? Palette.selection.opacity(0.85)
                  : (hovering ? Palette.text.opacity(0.05) : .clear)))
        .contentShape(Rectangle())
        .onTapGesture(perform: action)
        .onHover { hovering = $0 }
        .animation(Motion.hover, value: hovering)
    }
}

// MARK: - Rows

/// One setting: a name, at most one line of detail, the control on the right.
struct SettingsRow<Control: View>: View {
    let title: String
    var detail: String?
    var warning = false
    @ViewBuilder var control: Control

    var body: some View {
        HStack(alignment: .center, spacing: 24) {
            VStack(alignment: .leading, spacing: 2) {
                Text(title)
                    .font(.app(13))
                    .foregroundStyle(Palette.text)
                if let detail {
                    Text(detail)
                        .font(.app(11.5))
                        .foregroundStyle(warning ? Palette.caution : Palette.textSecondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
            Spacer(minLength: 0)
            control
        }
        .padding(.vertical, 11)
        .frame(minHeight: 44)
    }
}

/// A hairline between rows.
struct SettingsDivider: View {
    var body: some View { Rectangle().fill(Palette.separator).frame(height: 1) }
}

/// A heading that starts a new group of rows within a category.
struct SettingsHeading: View {
    let title: String
    var body: some View {
        Text(title)
            .font(.app(13, .semibold))
            .foregroundStyle(Palette.text)
            .padding(.top, 26)
            .padding(.bottom, 4)
    }
}

/// A bare switch, for the right-hand side of a row.
struct SettingSwitch: View {
    @Binding var isOn: Bool
    var body: some View {
        Toggle(isOn: $isOn) { EmptyView() }
            .toggleStyle(.pill)
            .fixedSize()
    }
}

/// ⓘ that opens a short explanation. Where a sentence of detail isn't
/// enough but a paragraph on screen all the time would be clutter.
struct InfoPopoverButton: View {
    let text: String
    @State private var open = false
    @State private var hovering = false

    var body: some View {
        Button { open.toggle() } label: {
            Image(systemName: "info.circle")
                .font(.system(size: 13))
                .foregroundStyle(hovering ? Palette.text : Palette.textSecondary)
        }
        .buttonStyle(.plain)
        .onHover { hovering = $0 }
        .popover(isPresented: $open, arrowEdge: .bottom) {
            Text(.init(text))
                .font(.app(12.5))
                .foregroundStyle(Palette.text)
                .lineSpacing(2)
                .fixedSize(horizontal: false, vertical: true)
                .padding(16)
                .frame(width: 320)
        }
    }
}

// MARK: - Panes

private struct SettingsPane: View {
    let category: SettingsCategory
    @ObservedObject var browser: BrowserModel
    @ObservedObject private var account = AccountStore.shared

    @AppStorage(AppSettings.Key.theme) private var themeRaw = AppTheme.system.rawValue
    @AppStorage(AppSettings.Key.accent) private var accentRaw = AccentTheme.yellow.rawValue
    @AppStorage(AppSettings.Key.homepage) private var homepage = ""
    @AppStorage(AppSettings.Key.searchEngine) private var searchEngine = SearchEngine.google.rawValue
    @AppStorage(AppSettings.Key.blockTrackers) private var blockTrackers = true
    @AppStorage(AppSettings.Key.confirmQuit) private var confirmQuit = false
    @AppStorage(AppSettings.Key.defaultQuality) private var defaultQuality = 80.0
    @AppStorage(AppSettings.Key.preserveOriginals) private var preserveOriginals = true
    @AppStorage(AppSettings.Key.dohProvider) private var dohProvider = "Cloudflare"
    @AppStorage(AppSettings.Key.tabOrganization) private var tabOrganization = "manual"
    @AppStorage(AppSettings.Key.autoCloseEnabled) private var autoCloseEnabled = false
    @AppStorage(AppSettings.Key.autoCloseRule) private var autoCloseRule = "day"
    @AppStorage(AppSettings.Key.autoCloseCustomHours) private var autoCloseCustomHours = 12.0
    @AppStorage(AppSettings.Key.autoCloseAtTimeEnabled) private var autoCloseAtTime = false
    @AppStorage(AppSettings.Key.autoCloseHour) private var autoCloseHour = 3
    @AppStorage(AppSettings.Key.autoCloseMinute) private var autoCloseMinute = 0
    @AppStorage(AppSettings.Key.persistentProfile) private var persistentProfile = false
    @AppStorage(AppSettings.Key.defaultUserAgent) private var defaultUserAgent = false
    @AppStorage(AppSettings.Key.acceleratedCanvas) private var acceleratedCanvas = true
    @AppStorage(AppSettings.Key.restoreTabs) private var restoreTabs = false
    @AppStorage(AppSettings.Key.passwordAutofill) private var passwordAutofill = true
    @AppStorage(AppSettings.Key.tabDropdowns) private var tabDropdowns = false

    @State private var editingProfile = false
    @State private var draftName = ""
    @State private var draftEmail = ""
    @State private var confirmDelete = false
    @State private var isDefaultBrowser = DefaultBrowser.isKrackn
    @State private var cleared = false

    private var theme: Binding<AppTheme> {
        Binding(get: { AppTheme(rawValue: themeRaw) ?? .system },
                set: { themeRaw = $0.rawValue })
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            switch category {
            case .general: general
            case .browsing: browsing
            case .newTab: ShortcutEditor()
            case .tabs: tabs
            case .files: files
            case .privacy: privacy
            case .advanced: advanced
            case .engines: ToolSetupView(showsTitle: false)
            case .account: accountPane
            case .about: about
            }
        }
        .animation(Motion.standard, value: autoCloseEnabled)
        .animation(Motion.standard, value: autoCloseAtTime)
        .animation(Motion.standard, value: autoCloseRule)
        .animation(Motion.standard, value: restoreTabs)
    }

    // MARK: General

    @ViewBuilder private var general: some View {
        SettingsRow(title: "Appearance") {
            ThemeSwitcher(theme: theme).frame(width: 220)
        }
        SettingsDivider()
        SettingsRow(title: "Accent colour") {
            AccentPicker(selection: $accentRaw)
        }
        SettingsDivider()
        SettingsRow(title: "Default browser",
                    detail: isDefaultBrowser ? "Links from other apps open in Krackn."
                                             : "Links from other apps open elsewhere.") {
            if isDefaultBrowser {
                Label("Default", systemImage: "checkmark")
                    .font(.app(12.5))
                    .foregroundStyle(Palette.textSecondary)
            } else {
                PillButton("Make Default", style: .outlined, size: .small) {
                    DefaultBrowser.request { granted in isDefaultBrowser = granted }
                }
            }
        }
        SettingsDivider()
        SettingsRow(title: "Confirm before quitting") {
            SettingSwitch(isOn: $confirmQuit)
        }
    }

    // MARK: Browsing

    @ViewBuilder private var browsing: some View {
        SettingsRow(title: "Search engine") {
            PillMenu(options: SearchEngine.allCases.map {
                ($0.rawValue, $0.isPrivate ? "\($0.rawValue) (private)" : $0.rawValue)
            }, selection: $searchEngine)
        }
        SettingsDivider()
        SettingsRow(title: "Homepage", detail: homepage.isEmpty ? "The built-in new-tab page." : nil) {
            PillField(placeholder: "New-tab page", text: $homepage)
                .frame(width: 260)
        }
        SettingsDivider()
        SettingsRow(title: "Fill passwords from Keychain",
                    detail: "A key appears in sign-in fields. Your Mac's Passwords fills them after Touch ID; Krackn never stores them.") {
            SettingSwitch(isOn: $passwordAutofill)
        }
        SettingsDivider()
        SettingsRow(title: "Block trackers",
                    detail: "Refuses \(TrackerRules.domains.count) tracking domains and strips tracking parameters.") {
            SettingSwitch(isOn: $blockTrackers)
        }
        SettingsDivider()
        SettingsRow(title: "Reopen tabs from last time",
                    detail: restoreTabs ? "Your open tabs are saved to a file on this Mac." : nil,
                    warning: restoreTabs) {
            SettingSwitch(isOn: $restoreTabs)
        }
        if restoreTabs {
            HStack(spacing: 6) {
                PillButton("Show File", style: .ghost, size: .small) {
                    NSWorkspace.shared.activateFileViewerSelecting([SessionStore.fileURL])
                }
                PillButton("Forget Saved Tabs", style: .ghost, size: .small) { SessionStore.clear() }
            }
            .padding(.bottom, 8)
            .transition(.opacity)
        }
    }

    // MARK: Tabs

    @ViewBuilder private var tabs: some View {
        SettingsRow(title: "Arrange tabs",
                    detail: TabOrganization(rawValue: tabOrganization)?.detail) {
            PillMenu(options: TabOrganization.allCases.map { ($0.rawValue, $0.label) },
                     selection: $tabOrganization)
        }
        SettingsDivider()
        SettingsRow(title: "Tab style",
                    detail: tabDropdowns ? "One tab per site — click it to pick the page."
                                         : "Every page gets its own tab.") {
            SegmentedControl(options: [(false, "Separate"), (true, "Dropdown")],
                             selection: $tabDropdowns)
                .frame(width: 200)
        }
        SettingsDivider()
        SettingsRow(title: "Close inactive tabs",
                    detail: "Pinned tabs and the one you're on are always kept.") {
            SettingSwitch(isOn: $autoCloseEnabled)
        }
        if autoCloseEnabled {
            SettingsDivider()
            SettingsRow(title: "After") {
                SegmentedControl(options: AutoCloseRule.allCases.map { ($0.rawValue, $0.label) },
                                 selection: $autoCloseRule)
                    .frame(width: 300)
            }
            if autoCloseRule == AutoCloseRule.custom.rawValue {
                SettingsRow(title: "\(Int(autoCloseCustomHours)) hours") {
                    PillSlider(value: $autoCloseCustomHours, range: 1...168, step: 1)
                        .frame(width: 300)
                }
                .transition(.opacity)
            }
            SettingsDivider()
            SettingsRow(title: "Also close at a set time") {
                HStack(spacing: 8) {
                    if autoCloseAtTime {
                        PillMenu(options: (0..<24).map { ($0, String(format: "%02d", $0)) },
                                 selection: $autoCloseHour)
                        Text(":").foregroundStyle(Palette.textSecondary)
                        PillMenu(options: [0, 15, 30, 45].map { ($0, String(format: "%02d", $0)) },
                                 selection: $autoCloseMinute)
                    }
                    SettingSwitch(isOn: $autoCloseAtTime)
                }
            }
        }
    }

    // MARK: Files

    @ViewBuilder private var files: some View {
        SettingsRow(title: "Default quality", detail: "For lossy formats.") {
            HStack(spacing: 12) {
                PillSlider(value: $defaultQuality, range: 10...100, step: 5)
                    .frame(width: 200)
                Text("\(Int(defaultQuality))")
                    .font(.app(12.5)).monospacedDigit()
                    .foregroundStyle(Palette.textSecondary)
                    .frame(width: 26, alignment: .trailing)
            }
        }
        SettingsDivider()
        SettingsRow(title: "Never overwrite files",
                    detail: preserveOriginals ? "A clashing name gets a number added."
                                              : "Converted files replace existing ones.",
                    warning: !preserveOriginals) {
            SettingSwitch(isOn: $preserveOriginals)
        }
        SettingsDivider()
        SettingsRow(title: "Save to") {
            HStack(spacing: 6) {
                Text(AppSettings.downloadsDirectory.lastPathComponent)
                    .font(.app(12.5))
                    .foregroundStyle(Palette.textSecondary)
                BarButton(system: "folder", help: "Show in Finder") {
                    NSWorkspace.shared.activateFileViewerSelecting([AppSettings.downloadsDirectory])
                }
            }
        }
    }

    // MARK: Privacy

    @ViewBuilder private var privacy: some View {
        // Reflects what's actually configured. A privacy claim that stays
        // cheerful while a switch quietly contradicts it is worse than none.
        let traceless = !persistentProfile && !restoreTabs
        HStack(alignment: .center, spacing: 12) {
            Image(systemName: traceless ? "checkmark.shield" : "exclamationmark.shield")
                .font(.system(size: 22, weight: .light))
                .foregroundStyle(traceless ? Palette.positive : Palette.caution)
            VStack(alignment: .leading, spacing: 2) {
                Text(traceless ? "Browsing leaves nothing on this Mac"
                               : "Some browsing data is saved on this Mac")
                    .font(.app(13.5, .medium))
                    .foregroundStyle(Palette.text)
                Text(traceless
                     ? "Cookies, cache and history live in memory and vanish on quit."
                     : [persistentProfile ? "cookies and cache" : nil,
                        restoreTabs ? "your open tabs" : nil]
                        .compactMap { $0 }.joined(separator: " and ").capitalizedFirst
                        + " are written to disk.")
                    .font(.app(11.5))
                    .foregroundStyle(Palette.textSecondary)
            }
            Spacer()
            InfoPopoverButton(text:
                "**What this can't hide.** Your network still sees *that* you connected "
                + "somewhere — DNS lookups and the TLS server name travel in the open "
                + "whatever an app does locally. Encrypted DNS covers the lookup; "
                + "**Network & VPN → Generate Profile** covers it for the whole Mac. "
                + "Nothing on this computer can cover the rest.\n\n"
                + "Staying signed in to sites needs a persistent profile, because a "
                + "saved sign-in *is* a stored trace.")
        }
        .padding(14)
        .background(Palette.controlFill.opacity(0.6),
                    in: RoundedRectangle(cornerRadius: Metrics.cardRadius, style: .continuous))
        .padding(.bottom, 8)

        SettingsRow(title: "Stay signed in to sites",
                    detail: persistentProfile ? "Cookies and cache are saved to disk. Relaunch to apply."
                                              : "Off: every sign-in ends when Krackn quits.",
                    warning: persistentProfile) {
            SettingSwitch(isOn: $persistentProfile)
        }
        SettingsDivider()
        SettingsRow(title: "Encrypted DNS",
                    detail: "For Krackn's own lookups. System-wide is in Network & VPN.") {
            PillMenu(options: DoHResolver.Provider.allCases.map {
                ($0.rawValue, $0.filtersMalware ? "\($0.rawValue) (filtered)" : $0.rawValue)
            }, selection: $dohProvider)
        }
        SettingsDivider()
        SettingsRow(title: "Clear browsing session",
                    detail: cleared ? "Cleared." : "Signs you out of everything now.") {
            PillButton("Clear Now", style: .outlined, size: .small) {
                browser.clearEverything()
                withAnimation(Motion.quick) { cleared = true }
            }
        }
    }

    // MARK: Advanced

    /// The ways this browser differs from Safari, each switchable. When a site
    /// misbehaves here and nowhere else, these are the entire suspect list.
    @ViewBuilder private var advanced: some View {
        if WebKitTuning.supportsCanvasSwitch {
            SettingsRow(title: "Draw canvas on the GPU",
                        detail: "Faster on canvas-heavy pages. Relaunch to apply.") {
                SettingSwitch(isOn: $acceleratedCanvas)
            }
            SettingsDivider()
        }
        SettingsRow(title: "Identify as WebKit, not Safari",
                    detail: "Some sites then serve a lesser page.") {
            SettingSwitch(isOn: $defaultUserAgent)
        }
        SettingsDivider()
        SettingsRow(title: "App folder") {
            BarButton(system: "folder", help: "Show in Finder") {
                NSWorkspace.shared.activateFileViewerSelecting(
                    [ToolInstaller.binDirectory.deletingLastPathComponent()])
            }
        }
        Text("If a site works in Safari but not here, change one of these at a time and relaunch.")
            .font(.app(11.5))
            .foregroundStyle(Palette.textSecondary)
            .padding(.top, 14)
    }

    // MARK: Account

    @ViewBuilder private var accountPane: some View {
        if let user = account.account {
            HStack(spacing: 12) {
                Circle().fill(Palette.accent)
                    .frame(width: 44, height: 44)
                    .overlay(Text(initials(user.displayName))
                        .font(.app(15, .medium)).foregroundStyle(Palette.onAccent))
                VStack(alignment: .leading, spacing: 2) {
                    Text(user.displayName).font(.app(14, .medium)).foregroundStyle(Palette.text)
                    Text(user.email.isEmpty ? "@\(user.username)" : user.email)
                        .font(.app(12)).foregroundStyle(Palette.textSecondary)
                }
                Spacer()
                if !editingProfile {
                    PillButton("Edit", style: .outlined, size: .small) {
                        draftName = user.displayName
                        draftEmail = user.email
                        withAnimation(Motion.standard) { editingProfile = true }
                    }
                }
            }
            .padding(.bottom, 10)

            if editingProfile {
                VStack(alignment: .leading, spacing: 8) {
                    PillField(placeholder: "Display name", text: $draftName)
                    PillField(placeholder: "Email", text: $draftEmail)
                    HStack {
                        PillButton("Save", style: .filled, size: .small) {
                            account.updateProfile(displayName: draftName, email: draftEmail)
                            withAnimation(Motion.standard) { editingProfile = false }
                        }
                        PillButton("Cancel", style: .ghost, size: .small) {
                            withAnimation(Motion.standard) { editingProfile = false }
                        }
                    }
                }
                .padding(.bottom, 10)
                .transition(.opacity)
            }

            SettingsDivider()
            SettingsRow(title: "Ask for password at launch") {
                SettingSwitch(isOn: Binding(get: { account.requiresPasswordOnLaunch },
                                            set: { account.requiresPasswordOnLaunch = $0 }))
            }
            SettingsDivider()
            SettingsRow(title: "Sign out") {
                PillButton("Sign Out", style: .outlined, size: .small) { account.signOut() }
            }
            SettingsDivider()
            SettingsRow(title: "Delete account",
                        detail: confirmDelete ? "Removes your profile and Keychain entry. Files are kept." : nil,
                        warning: confirmDelete) {
                if confirmDelete {
                    HStack(spacing: 6) {
                        PillButton("Keep", style: .ghost, size: .small) { confirmDelete = false }
                        PillButton("Delete", style: .filled, size: .small) {
                            account.deleteAccount()
                            confirmDelete = false
                        }
                    }
                } else {
                    PillButton("Delete…", style: .ghost, size: .small) {
                        withAnimation(Motion.quick) { confirmDelete = true }
                    }
                }
            }
        } else {
            SettingsRow(title: "No account",
                        detail: "Create one to keep your profile and preferences.") {
                PillButton("Create Account", style: .filled, size: .small) { account.signOut() }
            }
        }
    }

    // MARK: About

    @ViewBuilder private var about: some View {
        HStack(spacing: 14) {
            KracknLogo(size: 44)
            VStack(alignment: .leading, spacing: 2) {
                Text("Krackn").font(.app(17, .semibold)).foregroundStyle(Palette.text)
                Text("Version \(Bundle.appVersion)")
                    .font(.app(12)).foregroundStyle(Palette.textSecondary)
            }
        }
        .padding(.bottom, 14)
        SettingsDivider()
        SettingsRow(title: "File formats") {
            Text("\(FormatCatalog.allInputs.count)").font(.app(12.5)).foregroundStyle(Palette.textSecondary)
        }
        SettingsDivider()
        SettingsRow(title: "Tools") {
            Text("\(ToolboxGroup.allItems.count)").font(.app(12.5)).foregroundStyle(Palette.textSecondary)
        }
    }

    private func initials(_ name: String) -> String {
        let letters = name.split(separator: " ").prefix(2).compactMap { $0.first }
        return letters.isEmpty ? "?" : String(letters).uppercased()
    }
}

private extension String {
    var capitalizedFirst: String { prefix(1).uppercased() + dropFirst() }
}

extension Bundle {
    /// Reads the version out of the bundle so it can't drift from what
    /// `bundle.sh` stamps into the Info.plist.
    static var appVersion: String {
        let short = main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String
        return short ?? "1.1"
    }
}

// MARK: - Accent picker

/// The colour families as swatches. Each draws its family's actual accent
/// rather than a generic dot, so what you pick is what you get. Selection is a
/// ring around the swatch, as in System Settings.
private struct AccentPicker: View {
    @Binding var selection: String

    var body: some View {
        HStack(spacing: 9) {
            ForEach(AccentTheme.allCases) { accent in
                let isSelected = accent.rawValue == selection
                Button {
                    withAnimation(Motion.quick) { selection = accent.rawValue }
                } label: {
                    Circle()
                        .fill(swatch(accent))
                        .frame(width: 18, height: 18)
                        .overlay(Circle().strokeBorder(Color.black.opacity(0.1), lineWidth: 0.5))
                        .padding(3)
                        .overlay(Circle().strokeBorder(
                            isSelected ? Palette.textSecondary : .clear, lineWidth: 1.5))
                        .contentShape(Circle())
                }
                .buttonStyle(PressSettleStyle())
                .help(accent.label)
            }
        }
    }

    /// Drawn from the family's *own* tokens, not through `Palette` — `Palette`
    /// resolves to whichever family is active, so every swatch would match.
    private func swatch(_ accent: AccentTheme) -> Color {
        Color(nsColor: NSColor(name: nil) { appearance in
            let dark = appearance.bestMatch(from: [.aqua, .darkAqua]) == .darkAqua
            let hex = dark ? accent.darkTokens.accent : accent.lightTokens.accent
            let (r, g, b) = PaletteTokens.rgb(from: hex)
            return NSColor(srgbRed: r, green: g, blue: b, alpha: 1)
        })
    }
}

// MARK: - Default browser

enum DefaultBrowser {
    static var bundleID: String {
        Bundle.main.bundleIdentifier ?? "com.krackn.app"
    }

    /// Whether macOS currently routes http(s) here.
    static var isKrackn: Bool {
        guard let url = URL(string: "https://example.com"),
              let handler = NSWorkspace.shared.urlForApplication(toOpen: url)
        else { return false }
        return Bundle(url: handler)?.bundleIdentifier == bundleID
    }

    /// Asks macOS to make Krackn the default. The user still has to approve it
    /// in the system prompt — an app can't silently take over http(s), and
    /// shouldn't be able to.
    static func request(completion: @escaping (Bool) -> Void) {
        guard let app = Bundle.main.bundleURL as URL? else {
            completion(false)
            return
        }
        NSWorkspace.shared.setDefaultApplication(at: app, toOpenURLsWithScheme: "http") { _ in
            NSWorkspace.shared.setDefaultApplication(at: app, toOpenURLsWithScheme: "https") { _ in
                DispatchQueue.main.async { completion(isKrackn) }
            }
        }
    }
}

// MARK: - New Tab shortcuts

/// The sites on the new-tab page: rename in place, drag to reorder, remove,
/// add your own. Every change is saved straight away and shows in open
/// new-tab pages (`WindowRoot` reloads them).
struct ShortcutEditor: View {
    @AppStorage(AppSettings.Key.startShortcuts) private var stored = ""
    @State private var list: [StartShortcut] = StartShortcuts.current
    @State private var newName = ""
    @State private var newAddress = ""
    @State private var dragging: String?
    @State private var dragStart = 0
    @State private var dragOffset: CGFloat = 0

    /// Row height plus its hairline.
    static let pitch: CGFloat = 45

    private var candidate: StartShortcut? { StartShortcuts.make(title: newName, address: newAddress) }
    private var isDuplicate: Bool { candidate.map { c in list.contains { $0.url == c.url } } ?? false }
    private var isFull: Bool { list.count >= StartShortcuts.limit }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            SettingsRow(title: "Shortcuts",
                        detail: "The sites on the new-tab page. Drag to reorder; click a name to rename it.") {
                PillButton("Restore Defaults", style: .ghost, size: .small) {
                    StartShortcuts.resetToDefaults()
                    withAnimation(Motion.standard) { list = StartShortcuts.defaults }
                }
                .disabled(stored.isEmpty)
            }
            SettingsDivider()

            VStack(spacing: 0) {
                ForEach(list) { item in
                    ShortcutRow(shortcut: binding(for: item), isDragging: dragging == item.id,
                                onRemove: { remove(item) },
                                onDrag: { drag(item, $0) },
                                onDragEnd: endDrag,
                                onCommit: save)
                        .offset(y: dragging == item.id ? dragOffset : 0)
                        .zIndex(dragging == item.id ? 1 : 0)
                }
            }
            .animation(Motion.quick, value: list.map(\.id))

            if list.isEmpty {
                Text("No shortcuts — the new-tab page shows just the clock and search.")
                    .font(.app(12))
                    .foregroundStyle(Palette.textSecondary)
                    .padding(.vertical, 14)
                SettingsDivider()
            }

            SettingsHeading(title: "Add a site")
            HStack(spacing: 8) {
                PillField(placeholder: "Name (optional)", text: $newName, onSubmit: add)
                    .frame(width: 150)
                PillField(placeholder: "Address, e.g. apple.com", text: $newAddress, icon: "link", onSubmit: add)
                PillButton("Add", style: .filled, size: .regular, action: add)
                    .disabled(candidate == nil || isDuplicate || isFull)
            }
            .padding(.top, 6)
            Group {
                if isFull {
                    Text("That's the most the page holds (\(StartShortcuts.limit)). Remove one to add another.")
                } else if isDuplicate {
                    Text("That site is already on the page.")
                } else if !newAddress.isEmpty && candidate == nil {
                    Text("That isn't a web address.")
                }
            }
            .font(.app(11.5))
            .foregroundStyle(Palette.textSecondary)
            .padding(.top, 6)
        }
        // Restore Defaults from elsewhere, or another window's edit.
        .onChange(of: stored) { _, _ in
            if dragging == nil { list = StartShortcuts.current }
        }
    }

    private func binding(for item: StartShortcut) -> Binding<StartShortcut> {
        Binding(get: { list.first { $0.id == item.id } ?? item },
                set: { new in
                    guard let i = list.firstIndex(where: { $0.id == item.id }) else { return }
                    list[i] = new
                })
    }

    private func save() { StartShortcuts.current = list }

    private func add() {
        guard let new = candidate, !isDuplicate, !isFull else { return }
        withAnimation(Motion.standard) { list.append(new) }
        save()
        newName = ""
        newAddress = ""
    }

    private func remove(_ item: StartShortcut) {
        withAnimation(Motion.standard) { list.removeAll { $0.id == item.id } }
        save()
    }

    /// Fixed-pitch rows: the slot is the start plus whole rows travelled, and
    /// the row is drawn under the cursor as the others make way.
    private func drag(_ item: StartShortcut, _ dy: CGFloat) {
        if dragging != item.id {
            guard let start = list.firstIndex(where: { $0.id == item.id }) else { return }
            dragging = item.id
            dragStart = start
        }
        let target = min(max(dragStart + Int((dy / Self.pitch).rounded()), 0), list.count - 1)
        if let current = list.firstIndex(where: { $0.id == item.id }), current != target {
            withAnimation(Motion.quick) { list.insert(list.remove(at: current), at: target) }
        }
        dragOffset = dy - CGFloat(target - dragStart) * Self.pitch
    }

    private func endDrag() {
        withAnimation(Motion.standard) {
            dragOffset = 0
            dragging = nil
        }
        save()
    }
}

private struct ShortcutRow: View {
    @Binding var shortcut: StartShortcut
    let isDragging: Bool
    let onRemove: () -> Void
    let onDrag: (CGFloat) -> Void
    let onDragEnd: () -> Void
    let onCommit: () -> Void
    @State private var hovering = false
    @State private var dragged = false
    @FocusState private var editing: Bool

    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: 10) {
                Image(systemName: "line.3.horizontal")
                    .font(.system(size: 11, weight: .medium))
                    .foregroundStyle(Palette.textSecondary.opacity(hovering || isDragging ? 1 : 0.45))
                    .frame(width: 18, height: 28)
                    .contentShape(Rectangle())
                    // Measured in window space: the row moves as it's reordered,
                    // so in its own space the distance lost a row at every swap.
                    .gesture(DragGesture(minimumDistance: 2, coordinateSpace: .global)
                        .onChanged { value in
                            dragged = true
                            onDrag(value.translation.height)
                        }
                        .onEnded { _ in
                            if dragged { dragged = false; onDragEnd() }
                        })
                    .onHover { inside in
                        if inside { NSCursor.openHand.push() } else { NSCursor.pop() }
                    }
                    .help("Drag to reorder")
                Text(StartShortcuts.glyph(for: shortcut))
                    .font(.app(12, .medium))
                    .foregroundStyle(Palette.accentText)
                    .frame(width: 24, height: 24)
                    .background(Palette.accent.opacity(0.18),
                                in: RoundedRectangle(cornerRadius: 6, style: .continuous))
                VStack(alignment: .leading, spacing: 1) {
                    TextField("Name", text: $shortcut.title)
                        .textFieldStyle(.plain)
                        .font(.app(13))
                        .foregroundStyle(Palette.text)
                        .focused($editing)
                        .onSubmit(commit)
                        .onChange(of: editing) { _, now in if !now { commit() } }
                    Text(shortcut.url.replacingOccurrences(of: "https://", with: ""))
                        .font(.app(11))
                        .foregroundStyle(Palette.textSecondary)
                        .lineLimit(1)
                        .truncationMode(.middle)
                }
                Spacer(minLength: 8)
                Button(action: onRemove) {
                    Image(systemName: "minus.circle.fill")
                        .font(.system(size: 14))
                        .foregroundStyle(Palette.textSecondary)
                }
                .buttonStyle(.plain)
                .opacity(hovering ? 1 : 0.35)
                .help("Remove from the new-tab page")
            }
            .frame(height: 44)
            .padding(.horizontal, 4)
            .background(RoundedRectangle(cornerRadius: 8, style: .continuous)
                .fill(isDragging ? Palette.raised : .clear)
                .shadow(color: .black.opacity(isDragging ? 0.14 : 0), radius: 6, y: 2))
            SettingsDivider().opacity(isDragging ? 0 : 1)
        }
        .onHover { hovering = $0 }
    }

    /// A name cleared to nothing goes back to one made from the site.
    private func commit() {
        let trimmed = shortcut.title.trimmingCharacters(in: .whitespacesAndNewlines)
        if trimmed.isEmpty, let url = URL(string: shortcut.url) {
            shortcut.title = StartShortcuts.name(for: url)
        } else if trimmed != shortcut.title {
            shortcut.title = String(trimmed.prefix(40))
        }
        onCommit()
    }
}
