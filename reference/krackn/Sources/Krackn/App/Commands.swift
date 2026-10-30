import SwiftUI
import AppKit

/// Everything the menu bar can ask a window to do.
///
/// Safari's shortcuts live in real menus here, not in invisible buttons:
/// that's where a Mac user looks for them, they show their keys, and AppKit
/// sends them to the frontmost window without any view needing focus.
enum AppCommand: Hashable {
    case newTab, openLocation, closeTab, closeWindow, reopenClosed, print
    case find, findNext, findPrevious
    case toggleSidebar, favouritesBar, bookmarks, readingList, tabOverview, reader
    case reload, stop, zoomIn, zoomOut, actualSize, textBigger, textSmaller
    case back, forward, home
    case addBookmark, addToReadingList
    case nextTab, previousTab, selectTab(Int)
    case settings
}

/// One per window, published as a focused scene object so the menu bar talks
/// to whichever window is in front.
@MainActor
final class WindowActions: ObservableObject {
    var perform: (AppCommand) -> Void = { _ in }
}

struct KracknCommands: Commands {
    @FocusedObject private var actions: WindowActions?
    @Environment(\.openWindow) private var openWindow

    private func run(_ command: AppCommand) { actions?.perform(command) }

    var body: some Commands {
        // File
        CommandGroup(replacing: .newItem) {
            Button("New Tab") { run(.newTab) }.keyboardShortcut("t")
            Button("New Window") { openWindow(id: "main") }.keyboardShortcut("n")
            Divider()
            Button("Open Location…") { run(.openLocation) }.keyboardShortcut("l")
        }
        // Replacing this group removes SwiftUI's own Close (⌘W) — measured —
        // so ⌘W can close a tab and ⇧⌘W the window, as in Safari.
        CommandGroup(replacing: .saveItem) {
            Button("Close Tab") { run(.closeTab) }.keyboardShortcut("w")
            Button("Close Window") { run(.closeWindow) }.keyboardShortcut("w", modifiers: [.command, .shift])
            Button("Reopen Last Closed Tab") { run(.reopenClosed) }
                .keyboardShortcut("t", modifiers: [.command, .shift])
        }
        CommandGroup(replacing: .printItem) {
            Button("Print…") { run(.print) }.keyboardShortcut("p")
        }

        // Edit
        CommandGroup(after: .pasteboard) {
            Divider()
            Button("Find…") { run(.find) }.keyboardShortcut("f")
            Button("Find Next") { run(.findNext) }.keyboardShortcut("g")
            Button("Find Previous") { run(.findPrevious) }.keyboardShortcut("g", modifiers: [.command, .shift])
        }

        // Settings is a sheet over the window, not a page.
        CommandGroup(replacing: .appSettings) {
            Button("Settings…") { run(.settings) }.keyboardShortcut(",")
        }

        // View
        CommandGroup(after: .sidebar) {
            Button("Toggle Sidebar") { run(.toggleSidebar) }
                .keyboardShortcut("s", modifiers: [.control, .command])
            Button("Toggle Favourites Bar") { run(.favouritesBar) }
                .keyboardShortcut("b", modifiers: [.command, .shift])
            Divider()
            Button("Show Tab Overview") { run(.tabOverview) }
                .keyboardShortcut("\\", modifiers: [.command, .shift])
            Button("Show Reader") { run(.reader) }
                .keyboardShortcut("r", modifiers: [.command, .shift])
            Divider()
            Button("Reload Page") { run(.reload) }.keyboardShortcut("r")
            Button("Stop") { run(.stop) }.keyboardShortcut(".")
            Divider()
            Button("Zoom In") { run(.zoomIn) }.keyboardShortcut("+")
            Button("Zoom Out") { run(.zoomOut) }.keyboardShortcut("-")
            Button("Actual Size") { run(.actualSize) }.keyboardShortcut("0")
            Button("Make Text Bigger") { run(.textBigger) }
                .keyboardShortcut("+", modifiers: [.command, .option])
            Button("Make Text Smaller") { run(.textSmaller) }
                .keyboardShortcut("-", modifiers: [.command, .option])
        }

        CommandMenu("History") {
            Button("Back") { run(.back) }.keyboardShortcut("[")
            Button("Forward") { run(.forward) }.keyboardShortcut("]")
            Button("Home") { run(.home) }.keyboardShortcut("h", modifiers: [.command, .shift])
            Divider()
            Button("Reopen Last Closed Tab") { run(.reopenClosed) }
        }

        CommandMenu("Bookmarks") {
            Button("Add to Favourites") { run(.addBookmark) }.keyboardShortcut("d")
            Button("Add to Reading List") { run(.addToReadingList) }
                .keyboardShortcut("d", modifiers: [.command, .shift])
            Divider()
            Button("Show Bookmarks") { run(.bookmarks) }
                .keyboardShortcut("1", modifiers: [.control, .command])
            Button("Show Reading List") { run(.readingList) }
                .keyboardShortcut("2", modifiers: [.control, .command])
        }

        // Window
        CommandGroup(after: .windowSize) {
            Divider()
            Button("Show Previous Tab") { run(.previousTab) }
                .keyboardShortcut(.tab, modifiers: [.control, .shift])
            Button("Show Next Tab") { run(.nextTab) }
                .keyboardShortcut(.tab, modifiers: .control)
        }
    }
}

/// The second key for commands that have two in Safari (⇧⌘] as well as
/// ⌃Tab), and ⌘1–⌘9. A menu item can hold only one shortcut, and nine
/// "Select Tab" items would bury the Window menu — so these are bound here,
/// in the window, where they still need no focus to work.
struct AlternateShortcuts: View {
    let perform: (AppCommand) -> Void

    var body: some View {
        ZStack {
            Button("") { perform(.nextTab) }.keyboardShortcut("]", modifiers: [.command, .shift])
            Button("") { perform(.previousTab) }.keyboardShortcut("[", modifiers: [.command, .shift])
            // ⌘= is the same key as ⌘+ without Shift, which is what people press.
            Button("") { perform(.zoomIn) }.keyboardShortcut("=", modifiers: .command)
            Button("") { perform(.textBigger) }.keyboardShortcut("=", modifiers: [.command, .option])
            ForEach(1...9, id: \.self) { n in
                Button("") { perform(.selectTab(n)) }
                    .keyboardShortcut(KeyEquivalent(Character("\(n)")), modifiers: .command)
            }
        }
        .frame(width: 0, height: 0)
        .opacity(0)
        .accessibilityHidden(true)
    }
}

/// Hands the hosting `NSWindow` to SwiftUI code that needs it — the edge
/// reveal has to ignore other windows' mouse events, and Close Window needs
/// to know which window it is.
struct WindowReader: NSViewRepresentable {
    let onWindow: (NSWindow) -> Void

    func makeNSView(context: Context) -> NSView {
        let view = NSView()
        DispatchQueue.main.async { if let window = view.window { onWindow(window) } }
        return view
    }

    func updateNSView(_ nsView: NSView, context: Context) {}
}
