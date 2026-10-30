import SwiftUI
import AppKit
import UniformTypeIdentifiers

/// The real Finder icon for a file, so a `.stl` looks like a `.stl` and a PDF
/// looks like a PDF. A generic glyph per broad category told you almost
/// nothing; this is the icon the user already recognises from Finder.
struct FileIcon: View {
    let url: URL
    var size: CGFloat = 18

    var body: some View {
        Image(nsImage: Self.icon(for: url, size: size))
            .interpolation(.high)
            .resizable()
            .frame(width: size, height: size)
    }

    /// Icons are cached by extension: `NSWorkspace.icon(forFile:)` hits the
    /// launch services database, and a list of thirty recents would otherwise
    /// do thirty lookups on every redraw.
    private static var cache: [String: NSImage] = [:]

    static func icon(for url: URL, size: CGFloat) -> NSImage {
        let key = "\(url.pathExtension.lowercased())-\(Int(size))"
        if let hit = cache[key] { return hit }

        let image: NSImage
        if FileManager.default.fileExists(atPath: url.path) {
            image = NSWorkspace.shared.icon(forFile: url.path)
        } else if let type = UTType(filenameExtension: url.pathExtension) {
            image = NSWorkspace.shared.icon(for: type)
        } else {
            image = NSWorkspace.shared.icon(for: .data)
        }
        image.size = NSSize(width: size, height: size)
        cache[key] = image
        return image
    }
}

/// One file, shown as icon + name. The shape every file reference in the app
/// uses, so a filename looks the same in the viewer, the converter and the
/// background remover.
struct FileRow: View {
    let url: URL
    var subtitle: String?
    var iconSize: CGFloat = 20
    var isSelected = false

    var body: some View {
        HStack(spacing: 10) {
            FileIcon(url: url, size: iconSize)
            VStack(alignment: .leading, spacing: 1) {
                Text(url.lastPathComponent)
                    .font(.app(12.5, isSelected ? .semibold : .medium))
                    .foregroundStyle(isSelected ? Palette.onAccent : Palette.text)
                    .lineLimit(1)
                    .truncationMode(.middle)
                if let subtitle {
                    Text(subtitle)
                        .font(.app(10.5))
                        .foregroundStyle(isSelected ? Palette.onAccent.opacity(0.75)
                                         : Palette.textSecondary)
                        .lineLimit(1)
                }
            }
            Spacer(minLength: 0)
        }
    }
}

/// The recent-files list shared by the viewer, the converter and the background
/// remover.
///
/// A vertical list rather than the horizontal swipe strip these used to have:
/// a row of chips you have to scrub sideways through hides most of its contents
/// and can't show a file's type or folder. Capped in height with frosted fades
/// at both ends so a long list stays contained without needing a scrollbar to
/// announce itself.
struct RecentFilesList: View {
    let files: [URL]
    var maxHeight: CGFloat = 190
    var onSelect: (URL) -> Void
    var onReveal: ((URL) -> Void)?

    @State private var atTop = true
    @State private var atBottom = true
    @State private var contentHeight: CGFloat = 0

    private let space = "krackn.recents"

    var body: some View {
        VStack(alignment: .leading, spacing: 7) {
            HStack(spacing: 6) {
                Text("RECENT")
                    .font(.app(9.5, .semibold))
                    .foregroundStyle(Palette.textSecondary)
                Text("\(files.count)")
                    .font(.app(9.5, .semibold))
                    .foregroundStyle(Palette.textSecondary.opacity(0.7))
                Spacer()
            }

            ScrollView(.vertical, showsIndicators: false) {
                VStack(spacing: 3) {
                    ForEach(files, id: \.self) { url in
                        RecentRow(url: url,
                                  onSelect: { onSelect(url) },
                                  onReveal: onReveal.map { reveal in { reveal(url) } })
                    }
                }
                .background(
                    GeometryReader { inner in
                        Color.clear
                            .onAppear { contentHeight = inner.size.height }
                            .onChange(of: inner.size.height) { _, new in contentHeight = new }
                            .preference(key: RecentsOffsetKey.self,
                                        value: -inner.frame(in: .named(space)).minY)
                    })
            }
            .coordinateSpace(name: space)
            .frame(maxHeight: min(maxHeight, max(contentHeight, 1)))
            .onPreferenceChange(RecentsOffsetKey.self) { offset in
                atTop = offset <= 1
                atBottom = contentHeight - offset - maxHeight <= 1
            }
            .scrollEdgeFade(top: !atTop, bottom: !atBottom, height: 22)
        }
    }
}

private struct RecentsOffsetKey: PreferenceKey {
    static var defaultValue: CGFloat { 0 }
    static func reduce(value: inout CGFloat, nextValue: () -> CGFloat) {
        value = nextValue()
    }
}

private struct RecentRow: View {
    let url: URL
    let onSelect: () -> Void
    var onReveal: (() -> Void)?
    @State private var hovering = false

    /// The containing folder, so two files with the same name are still
    /// tellable apart.
    private var folder: String {
        url.deletingLastPathComponent().lastPathComponent
    }

    var body: some View {
        Button(action: onSelect) {
            FileRow(url: url, subtitle: folder, iconSize: 19)
                .padding(.horizontal, 9)
                .padding(.vertical, 6)
                .background(
                    RoundedRectangle(cornerRadius: 9)
                        .fill(hovering ? Palette.selection.opacity(0.55) : Palette.well.opacity(0.4))
                )
                .overlay(
                    RoundedRectangle(cornerRadius: 9)
                        .strokeBorder(hovering ? Palette.accent.opacity(0.7)
                                      : Palette.outline.opacity(0.18), lineWidth: 1)
                )
                .contentShape(RoundedRectangle(cornerRadius: 9))
        }
        .buttonStyle(.plain)
        .onHover { hovering = $0 }
        // Recents are drag sources too, so a file can be pulled straight from
        // one tool's list into another tool.
        .onDrag { NSItemProvider.file(url) }
        .contextMenu {
            Button("Open") { onSelect() }
            if onReveal != nil {
                Button("Reveal in Finder") { onReveal?() }
            }
            Button("Copy Path") {
                NSPasteboard.general.clearContents()
                NSPasteboard.general.setString(url.path, forType: .string)
            }
        }
        .help(url.path)
    }
}

/// The shared empty state for the viewer, the converter and the background
/// remover — one shape, three captions, so the three tools read as siblings
/// rather than three unrelated screens.
struct ToolEmptyState<Actions: View>: View {
    let icon: String
    let title: String
    let message: String
    var isTargeted: Bool
    let recents: [URL]
    var onSelectRecent: (URL) -> Void
    @ViewBuilder var actions: Actions

    var body: some View {
        VStack(spacing: 0) {
            Spacer(minLength: 20)
            VStack(spacing: 16) {
                Image(systemName: icon)
                    .font(.system(size: 44, weight: .light))
                    .foregroundStyle(isTargeted ? Palette.accentText
                                     : Palette.textSecondary.opacity(0.7))
                VStack(spacing: 5) {
                    Text(title)
                        .font(.app(20, .semibold))
                        .foregroundStyle(Palette.text)
                    Text(message)
                        .font(.app(13))
                        .foregroundStyle(Palette.textSecondary)
                        .multilineTextAlignment(.center)
                        .fixedSize(horizontal: false, vertical: true)
                }
                actions

                if !recents.isEmpty {
                    RecentFilesList(files: recents, onSelect: onSelectRecent) { url in
                        NSWorkspace.shared.activateFileViewerSelecting([url])
                    }
                    .frame(width: 420)
                    .padding(.top, 8)
                }
            }
            .frame(maxWidth: 460)
            Spacer(minLength: 20)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(Palette.background)
        .overlay {
            if isTargeted {
                RoundedRectangle(cornerRadius: 16, style: .continuous)
                    .strokeBorder(Palette.accent, style: StrokeStyle(lineWidth: 1.5, dash: [8, 6]))
                    .padding(10)
                    .allowsHitTesting(false)
            }
        }
    }
}
