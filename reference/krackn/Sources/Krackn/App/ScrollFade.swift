import SwiftUI

/// Reports how far a scroll view has travelled and how much content it has,
/// so the edge fades know whether there's anything above or below to hint at.
private struct ScrollMetrics: Equatable {
    var offset: CGFloat = 0
    var contentHeight: CGFloat = 0
    var viewportHeight: CGFloat = 0

    var canScrollUp: Bool { offset > 1 }
    var canScrollDown: Bool { contentHeight - offset - viewportHeight > 1 }
}

private struct ScrollMetricsKey: PreferenceKey {
    static var defaultValue: ScrollMetrics { ScrollMetrics() }
    static func reduce(value: inout ScrollMetrics, nextValue: () -> ScrollMetrics) {
        let next = nextValue()
        // Only the reading that carries real geometry wins; the outer reader
        // publishes the viewport and the inner one the content.
        if next.contentHeight > 0 { value.contentHeight = next.contentHeight }
        if next.viewportHeight > 0 { value.viewportHeight = next.viewportHeight }
        value.offset = next.offset != 0 ? next.offset : value.offset
    }
}

/// A scroll view with frosted fades at the top and bottom edges.
///
/// The fade only appears on an edge that has content beyond it, so a short page
/// that doesn't scroll gets no decoration at all — a permanent gradient on a
/// stationary page reads as a rendering artefact rather than an affordance.
/// Material rather than a solid-colour gradient because the content behind it
/// should still be faintly legible as it passes under, which is what makes it
/// read as depth instead of as a bar.
struct FadingScrollView<Content: View>: View {
    var fadeHeight: CGFloat = 40
    var showsIndicators = true
    @ViewBuilder var content: Content

    @State private var metrics = ScrollMetrics()

    private let space = "krackn.fadingScroll"

    var body: some View {
        GeometryReader { outer in
            ScrollView(.vertical, showsIndicators: showsIndicators) {
                content
                    .background(
                        GeometryReader { inner in
                            Color.clear.preference(
                                key: ScrollMetricsKey.self,
                                value: ScrollMetrics(
                                    offset: -inner.frame(in: .named(space)).minY,
                                    contentHeight: inner.size.height,
                                    viewportHeight: 0))
                        })
            }
            .coordinateSpace(name: space)
            .preference(key: ScrollMetricsKey.self,
                        value: ScrollMetrics(offset: 0, contentHeight: 0,
                                             viewportHeight: outer.size.height))
            .overlay(alignment: .top) {
                edge(.top).opacity(metrics.canScrollUp ? 1 : 0)
            }
            .overlay(alignment: .bottom) {
                edge(.bottom).opacity(metrics.canScrollDown ? 1 : 0)
            }
            .animation(.easeOut(duration: 0.22), value: metrics.canScrollUp)
            .animation(.easeOut(duration: 0.22), value: metrics.canScrollDown)
        }
        .onPreferenceChange(ScrollMetricsKey.self) { metrics = $0 }
    }

    /// One frosted edge. The material is masked by a gradient so it's opaque
    /// against the window edge and gone by the time it reaches the content —
    /// a hard-edged blur strip would look like a toolbar.
    private func edge(_ alignment: Alignment) -> some View {
        Rectangle()
            .fill(.ultraThinMaterial)
            .frame(height: fadeHeight)
            .mask(
                LinearGradient(
                    stops: [
                        .init(color: .black, location: 0),
                        .init(color: .black.opacity(0.85), location: 0.35),
                        .init(color: .clear, location: 1),
                    ],
                    startPoint: alignment == .top ? .top : .bottom,
                    endPoint: alignment == .top ? .bottom : .top)
            )
            .allowsHitTesting(false)
    }
}

extension View {
    /// Frosted fades over an existing scroll view whose scrolling state you
    /// already know. Used where the content is a `List`-like column that
    /// manages its own scrolling.
    func scrollEdgeFade(top: Bool, bottom: Bool, height: CGFloat = 28) -> some View {
        overlay(alignment: .top) {
            fadeStrip(height: height, fromTop: true)
                .opacity(top ? 1 : 0)
                .animation(.easeOut(duration: 0.2), value: top)
        }
        .overlay(alignment: .bottom) {
            fadeStrip(height: height, fromTop: false)
                .opacity(bottom ? 1 : 0)
                .animation(.easeOut(duration: 0.2), value: bottom)
        }
    }

    private func fadeStrip(height: CGFloat, fromTop: Bool) -> some View {
        Rectangle()
            .fill(.ultraThinMaterial)
            .frame(height: height)
            .mask(
                LinearGradient(
                    stops: [
                        .init(color: .black, location: 0),
                        .init(color: .black.opacity(0.8), location: 0.4),
                        .init(color: .clear, location: 1),
                    ],
                    startPoint: fromTop ? .top : .bottom,
                    endPoint: fromTop ? .bottom : .top)
            )
            .allowsHitTesting(false)
    }
}
