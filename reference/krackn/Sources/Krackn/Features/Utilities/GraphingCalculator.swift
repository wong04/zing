import SwiftUI

/// Graphing calculator laid out like Desmos: a numbered expression list down the
/// left, the plot filling everything to its right, and points of interest
/// (intersections, roots, extrema) marked on the curve.
struct GraphingCalculatorTool: View {

    struct Plot: Identifiable {
        let id = UUID()
        var text: String
        var colorIndex: Int
        var visible = true
    }

    /// Distinct hues legible on both the beige and dark backgrounds. Ordered
    /// the way Desmos cycles its palette.
    static let plotColors: [Color] = [
        Color(red: 0.78, green: 0.33, blue: 0.24),
        Color(red: 0.24, green: 0.45, blue: 0.72),
        Color(red: 0.31, green: 0.56, blue: 0.33),
        Color(red: 0.60, green: 0.33, blue: 0.62),
        Color(red: 0.85, green: 0.60, blue: 0.16),
        Color(red: 0.29, green: 0.58, blue: 0.61),
    ]

    /// A marked point on the plot.
    struct Marker: Identifiable {
        let id = UUID()
        let x: Double
        let y: Double
        let kind: Kind
        let colorIndex: Int

        enum Kind: String { case intersection, root, extremum
            var label: String {
                switch self {
                case .intersection: return "intersection"
                case .root: return "x-intercept"
                case .extremum: return "turning point"
                }
            }
        }
    }

    @State private var plots: [Plot] = [
        .init(text: "sin(x)", colorIndex: 0),
        .init(text: "x/3", colorIndex: 1),
    ]
    @State private var viewport = GraphEngine.Viewport.default
    @State private var angle = Expression.AngleMode.radians
    @State private var hover: CGPoint?
    @State private var showPoints = true
    @State private var selected: Marker?
    @Environment(\.inFloatingPanel) private var inPanel

    var body: some View {
        ToolTitle(title: "Graphing Calculator",
                  subtitle: "Scroll to pan, pinch or ⌘-scroll to zoom, drag to move. Points of interest are marked automatically.")

        // Desmos proportions: a fixed expression column beside a large plot.
        // Both shrink in a panel — at full size the expression list alone would
        // be over half the panel's width.
        HStack(spacing: 0) {
            expressionPanel
                .frame(width: inPanel ? 185 : 260)
            Rectangle().fill(Palette.separator).frame(width: 1)
            plotPanel
        }
        .frame(height: inPanel ? 330 : 460)
        .background(Palette.surface, in: RoundedRectangle(cornerRadius: 14))
        .overlay(RoundedRectangle(cornerRadius: 14)
            .strokeBorder(Palette.separator, lineWidth: 1))
        .clipShape(RoundedRectangle(cornerRadius: 14))

        if let selected {
            SectionCard(title: selected.kind.label.capitalized) {
                HStack {
                    Text("(\(Expression.format(selected.x)), \(Expression.format(selected.y)))")
                        .font(.system(size: 16, weight: .semibold, design: .monospaced))
                        .foregroundStyle(Palette.text)
                        .textSelection(.enabled)
                    Spacer()
                    CopyButton(text: "(\(Expression.format(selected.x)), \(Expression.format(selected.y)))")
                    PillButton("Dismiss", style: .ghost, size: .small) { self.selected = nil }
                }
            }
        }
    }

    // MARK: - Expression list

    private var expressionPanel: some View {
        VStack(spacing: 0) {
            // Header row with the add button, as Desmos has above its list.
            HStack(spacing: 6) {
                Text("Expressions")
                    .font(.app(11, .semibold))
                    .foregroundStyle(Palette.textSecondary)
                Spacer()
                IconButton(system: "plus", size: 22) {
                    withAnimation(.easeOut(duration: 0.15)) {
                        plots.append(Plot(text: "",
                                          colorIndex: plots.count % Self.plotColors.count))
                    }
                }
            }
            .padding(.horizontal, 11)
            .padding(.vertical, 9)
            .background(Palette.tint)

            Rectangle().fill(Palette.separator).frame(height: 1)

            ScrollView {
                VStack(spacing: 0) {
                    ForEach(Array($plots.enumerated()), id: \.element.id) { index, $plot in
                        ExpressionRow(
                            index: index + 1,
                            plot: $plot,
                            color: Self.plotColors[plot.colorIndex % Self.plotColors.count],
                            angle: angle,
                            canDelete: plots.count > 1,
                            onDelete: {
                                withAnimation(.easeOut(duration: 0.15)) {
                                    plots.removeAll { $0.id == plot.id }
                                }
                            })
                        Rectangle().fill(Palette.separator).frame(height: 1)
                    }
                }
            }

            Spacer(minLength: 0)

            // Footer: view controls, mirroring Desmos's corner buttons.
            VStack(spacing: 7) {
                Toggle("Show key points", isOn: $showPoints)
                    .toggleStyle(.pill)
                HStack(spacing: 6) {
                    SegmentedControl(
                        options: Expression.AngleMode.allCases.map { ($0, $0.rawValue) },
                        selection: $angle)
                    IconButton(system: "arrow.counterclockwise", size: 24) {
                        withAnimation(.easeOut(duration: 0.2)) {
                            viewport = .default
                            selected = nil
                        }
                    }
                    .help("Reset view")
                }
            }
            .padding(10)
            .background(Palette.tint)
        }
    }

    // MARK: - Plot

    private var plotPanel: some View {
        GeometryReader { geo in
            let size = geo.size
            ZStack(alignment: .topTrailing) {
                Canvas { context, canvasSize in
                    drawGrid(&context, size: canvasSize)
                    drawCurves(&context, size: canvasSize)
                    if showPoints { drawMarkers(&context, size: canvasSize) }
                    drawCrosshair(&context, size: canvasSize)
                }

                // Zoom buttons, as Desmos places top-right.
                VStack(spacing: 5) {
                    IconButton(system: "plus", size: 26) { zoom(0.8) }
                    IconButton(system: "minus", size: 26) { zoom(1.25) }
                    IconButton(system: "scope", size: 26) {
                        withAnimation(.easeOut(duration: 0.2)) { viewport = .default }
                    }
                }
                .padding(9)

                if let hover {
                    VStack {
                        Spacer()
                        HStack {
                            Text("(\(Expression.format(hover.x)), \(Expression.format(hover.y)))")
                                .font(.system(size: 10.5, design: .monospaced))
                                .foregroundStyle(Palette.textSecondary)
                                .padding(.horizontal, 8).padding(.vertical, 4)
                                .background(Palette.surface.opacity(0.9), in: Capsule())
                            Spacer()
                        }
                    }
                    .padding(9)
                }
            }
            .background(
                GraphSurface(viewport: $viewport,
                             onHover: { hover = $0 },
                             onClick: { point in selectNearestMarker(to: point, size: size) })
            )
        }
    }

    private func zoom(_ factor: Double) {
        let centre = (x: (viewport.xMin + viewport.xMax) / 2,
                      y: (viewport.yMin + viewport.yMax) / 2)
        withAnimation(.easeOut(duration: 0.15)) {
            viewport = viewport.zoomed(by: factor, around: centre)
        }
    }

    // MARK: - Points of interest

    /// Roots, turning points, and every pairwise intersection of the visible
    /// curves — the same set Desmos surfaces as grey dots.
    private var markers: [Marker] {
        let active = plots.enumerated().filter { $0.element.visible && !$0.element.text.isEmpty }
        var result: [Marker] = []

        for (_, plot) in active {
            let f = Expression.makeFunction(plot.text, angle: angle)
            for x in GraphEngine.roots(f, viewport: viewport).prefix(40) {
                result.append(Marker(x: x, y: 0, kind: .root, colorIndex: plot.colorIndex))
            }
            for point in GraphEngine.extrema(f, viewport: viewport).prefix(20) {
                result.append(Marker(x: point.x, y: point.y, kind: .extremum,
                                     colorIndex: plot.colorIndex))
            }
        }

        // Pairwise intersections.
        for i in active.indices {
            for j in active.indices where j > i {
                let f = Expression.makeFunction(active[i].element.text, angle: angle)
                let g = Expression.makeFunction(active[j].element.text, angle: angle)
                for point in GraphEngine.intersections(f, g, viewport: viewport).prefix(40) {
                    result.append(Marker(x: point.x, y: point.y, kind: .intersection,
                                         colorIndex: active[i].element.colorIndex))
                }
            }
        }
        return result
    }

    private func selectNearestMarker(to point: CGPoint, size: CGSize) {
        guard showPoints else { return }
        let px = GraphEngine.screenX(point.x, viewport: viewport, size: size)
        let py = GraphEngine.screenY(point.y, viewport: viewport, size: size)

        var best: (marker: Marker, distance: Double)?
        for marker in markers {
            let mx = GraphEngine.screenX(marker.x, viewport: viewport, size: size)
            let my = GraphEngine.screenY(marker.y, viewport: viewport, size: size)
            let distance = hypot(mx - px, my - py)
            if distance < 14, best == nil || distance < best!.distance {
                best = (marker, distance)
            }
        }
        withAnimation(.easeOut(duration: 0.15)) { selected = best?.marker }
    }

    // MARK: - Drawing

    private func drawGrid(_ context: inout GraphicsContext, size: CGSize) {
        let xStep = GraphEngine.tickStep(range: viewport.width, target: Int(size.width / 80))
        let yStep = GraphEngine.tickStep(range: viewport.height, target: Int(size.height / 60))

        // Desmos draws a fine sub-grid at a fifth of the major spacing.
        for (step, isMinor) in [(xStep / 5, true), (xStep, false)] {
            for x in GraphEngine.ticks(min: viewport.xMin, max: viewport.xMax, step: step) {
                let px = GraphEngine.screenX(x, viewport: viewport, size: size)
                var path = Path()
                path.move(to: CGPoint(x: px, y: 0))
                path.addLine(to: CGPoint(x: px, y: size.height))
                context.stroke(path, with: .color(
                    Palette.outline.opacity(isMinor ? 0.07 : 0.16)), lineWidth: 1)
            }
        }
        for (step, isMinor) in [(yStep / 5, true), (yStep, false)] {
            for y in GraphEngine.ticks(min: viewport.yMin, max: viewport.yMax, step: step) {
                let py = GraphEngine.screenY(y, viewport: viewport, size: size)
                var path = Path()
                path.move(to: CGPoint(x: 0, y: py))
                path.addLine(to: CGPoint(x: size.width, y: py))
                context.stroke(path, with: .color(
                    Palette.outline.opacity(isMinor ? 0.07 : 0.16)), lineWidth: 1)
            }
        }

        let originX = GraphEngine.screenX(0, viewport: viewport, size: size)
        let originY = GraphEngine.screenY(0, viewport: viewport, size: size)
        if originX >= 0, originX <= size.width {
            var path = Path()
            path.move(to: CGPoint(x: originX, y: 0))
            path.addLine(to: CGPoint(x: originX, y: size.height))
            context.stroke(path, with: .color(Palette.outline.opacity(0.6)), lineWidth: 1.5)
        }
        if originY >= 0, originY <= size.height {
            var path = Path()
            path.move(to: CGPoint(x: 0, y: originY))
            path.addLine(to: CGPoint(x: size.width, y: originY))
            context.stroke(path, with: .color(Palette.outline.opacity(0.6)), lineWidth: 1.5)
        }

        let labelY = min(max(originY, 10), size.height - 6)
        for x in GraphEngine.ticks(min: viewport.xMin, max: viewport.xMax, step: xStep)
        where abs(x) > xStep / 1000 {
            context.draw(
                Text(GraphEngine.axisLabel(x, step: xStep))
                    .font(.system(size: 9, design: .monospaced))
                    .foregroundStyle(Palette.textSecondary),
                at: CGPoint(x: GraphEngine.screenX(x, viewport: viewport, size: size),
                            y: labelY + 8), anchor: .top)
        }
        let labelX = min(max(originX, 6), size.width - 20)
        for y in GraphEngine.ticks(min: viewport.yMin, max: viewport.yMax, step: yStep)
        where abs(y) > yStep / 1000 {
            context.draw(
                Text(GraphEngine.axisLabel(y, step: yStep))
                    .font(.system(size: 9, design: .monospaced))
                    .foregroundStyle(Palette.textSecondary),
                at: CGPoint(x: labelX - 5,
                            y: GraphEngine.screenY(y, viewport: viewport, size: size)),
                anchor: .trailing)
        }
    }

    private func drawCurves(_ context: inout GraphicsContext, size: CGSize) {
        for plot in plots where plot.visible && !plot.text.isEmpty {
            let colour = Self.plotColors[plot.colorIndex % Self.plotColors.count]
            let f = Expression.makeFunction(plot.text, angle: angle)
            for segment in GraphEngine.sample(f, viewport: viewport, size: size,
                                              samples: Int(size.width))
            where segment.count > 1 {
                var path = Path()
                path.addLines(segment)
                context.stroke(path, with: .color(colour),
                               style: StrokeStyle(lineWidth: 2.2, lineCap: .round,
                                                  lineJoin: .round))
            }
        }
    }

    private func drawMarkers(_ context: inout GraphicsContext, size: CGSize) {
        for marker in markers {
            let px = GraphEngine.screenX(marker.x, viewport: viewport, size: size)
            let py = GraphEngine.screenY(marker.y, viewport: viewport, size: size)
            guard px >= -10, px <= size.width + 10, py >= -10, py <= size.height + 10
            else { continue }

            let isSelected = selected.map {
                abs($0.x - marker.x) < 1e-9 && abs($0.y - marker.y) < 1e-9
            } ?? false
            let radius: CGFloat = isSelected ? 5.5 : 3.8
            let colour = Self.plotColors[marker.colorIndex % Self.plotColors.count]

            let dot = Path(ellipseIn: CGRect(x: px - radius, y: py - radius,
                                             width: radius * 2, height: radius * 2))
            // Filled when selected, hollow otherwise — Desmos's convention.
            if isSelected {
                context.fill(dot, with: .color(colour))
                context.stroke(dot, with: .color(Palette.surface), lineWidth: 2)
            } else {
                context.fill(dot, with: .color(Palette.surface))
                context.stroke(dot, with: .color(colour.opacity(0.85)), lineWidth: 1.8)
            }
        }
    }

    private func drawCrosshair(_ context: inout GraphicsContext, size: CGSize) {
        guard let hover else { return }
        let px = GraphEngine.screenX(hover.x, viewport: viewport, size: size)
        let py = GraphEngine.screenY(hover.y, viewport: viewport, size: size)
        var vertical = Path()
        vertical.move(to: CGPoint(x: px, y: 0))
        vertical.addLine(to: CGPoint(x: px, y: size.height))
        var horizontal = Path()
        horizontal.move(to: CGPoint(x: 0, y: py))
        horizontal.addLine(to: CGPoint(x: size.width, y: py))
        let style = StrokeStyle(lineWidth: 1, dash: [3, 3])
        context.stroke(vertical, with: .color(Palette.outline.opacity(0.3)), style: style)
        context.stroke(horizontal, with: .color(Palette.outline.opacity(0.3)), style: style)
    }
}

// MARK: - One expression row

private struct ExpressionRow: View {
    let index: Int
    @Binding var plot: GraphingCalculatorTool.Plot
    let color: Color
    let angle: Expression.AngleMode
    let canDelete: Bool
    let onDelete: () -> Void

    @FocusState private var focused: Bool

    private var problem: String? {
        let trimmed = plot.text.trimmingCharacters(in: .whitespaces)
        guard !trimmed.isEmpty else { return nil }
        do {
            _ = try Expression.evaluate(trimmed, angle: angle, variables: ["x": 1])
            return nil
        } catch {
            let message = error.localizedDescription
            return message.isEmpty ? "Invalid expression" : message
        }
    }

    var body: some View {
        HStack(alignment: .top, spacing: 0) {
            // Numbered gutter with the colour swatch, exactly Desmos's layout.
            VStack(spacing: 4) {
                Text("\(index)")
                    .font(.system(size: 9, design: .monospaced))
                    .foregroundStyle(Palette.textSecondary.opacity(0.8))
                Button { plot.visible.toggle() } label: {
                    Circle()
                        .fill(plot.visible ? color : .clear)
                        .frame(width: 13, height: 13)
                        .overlay(Circle().strokeBorder(color, lineWidth: 2))
                }
                .buttonStyle(.plain)
                .help(plot.visible ? "Hide" : "Show")
            }
            .frame(width: 26)
            .padding(.top, 10)

            VStack(alignment: .leading, spacing: 3) {
                HStack(spacing: 4) {
                    Text("y=")
                        .font(.system(size: 12, design: .monospaced))
                        .foregroundStyle(Palette.textSecondary)
                    TextField("f(x)", text: $plot.text)
                        .textFieldStyle(.plain)
                        .font(.system(size: 13, design: .monospaced))
                        .foregroundStyle(Palette.text)
                        .focused($focused)
                }
                if let problem {
                    Text(problem)
                        .font(.app(9.5))
                        .foregroundStyle(Palette.caution)
                        .lineLimit(2)
                }
            }
            .padding(.vertical, 9)

            if canDelete {
                Button(action: onDelete) {
                    Image(systemName: "xmark")
                        .font(.system(size: 8, weight: .semibold))
                        .foregroundStyle(Palette.textSecondary)
                }
                .buttonStyle(.plain)
                .padding(.top, 12)
                .padding(.trailing, 8)
            }
        }
        .padding(.leading, 4)
        .background(focused ? Palette.accent.opacity(0.08) : .clear)
    }
}
