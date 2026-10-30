import Foundation
import CoreGraphics

/// Maths behind the graphing calculator: viewport mapping, gridline choice and
/// curve sampling. Kept free of SwiftUI so every part is unit-testable.
enum GraphEngine {

    /// The visible region in graph coordinates.
    struct Viewport: Equatable {
        var xMin: Double, xMax: Double, yMin: Double, yMax: Double

        static let `default` = Viewport(xMin: -10, xMax: 10, yMin: -6.5, yMax: 6.5)

        var width: Double { xMax - xMin }
        var height: Double { yMax - yMin }

        /// Scales about a point, for scroll-wheel zoom that keeps the cursor
        /// anchored to the same graph coordinate.
        func zoomed(by factor: Double, around anchor: (x: Double, y: Double)) -> Viewport {
            // Clamped so a fast trackpad flick can't collapse the viewport to
            // zero width or blow it up past what Double can represent usefully.
            let newWidth = min(max(width * factor, 1e-9), 1e9)
            let newHeight = min(max(height * factor, 1e-9), 1e9)
            let fx = width == 0 ? 0.5 : (anchor.x - xMin) / width
            let fy = height == 0 ? 0.5 : (anchor.y - yMin) / height
            let x0 = anchor.x - fx * newWidth
            let y0 = anchor.y - fy * newHeight
            return Viewport(xMin: x0, xMax: x0 + newWidth,
                            yMin: y0, yMax: y0 + newHeight)
        }

        func panned(dx: Double, dy: Double) -> Viewport {
            Viewport(xMin: xMin + dx, xMax: xMax + dx,
                     yMin: yMin + dy, yMax: yMax + dy)
        }
    }

    // MARK: - Coordinate mapping

    static func screenX(_ x: Double, viewport: Viewport, size: CGSize) -> Double {
        (x - viewport.xMin) / viewport.width * Double(size.width)
    }

    /// Screen Y is flipped: graph +y goes up, screen +y goes down.
    static func screenY(_ y: Double, viewport: Viewport, size: CGSize) -> Double {
        Double(size.height) - (y - viewport.yMin) / viewport.height * Double(size.height)
    }

    static func graphX(_ px: Double, viewport: Viewport, size: CGSize) -> Double {
        viewport.xMin + px / Double(size.width) * viewport.width
    }

    static func graphY(_ py: Double, viewport: Viewport, size: CGSize) -> Double {
        viewport.yMin + (Double(size.height) - py) / Double(size.height) * viewport.height
    }

    // MARK: - Gridlines

    /// A "nice" tick step — 1, 2 or 5 times a power of ten — chosen so roughly
    /// `target` gridlines span the range. Without this the grid either becomes
    /// unreadable when zoomed out or vanishes when zoomed in.
    static func tickStep(range: Double, target: Int = 10) -> Double {
        guard range > 0, range.isFinite, target > 0 else { return 1 }
        let rough = range / Double(target)
        let magnitude = pow(10, floor(log10(rough)))
        let normalised = rough / magnitude
        let nice: Double
        switch normalised {
        case ..<1.5: nice = 1
        case ..<3: nice = 2
        case ..<7: nice = 5
        default: nice = 10
        }
        return nice * magnitude
    }

    /// Tick positions covering `min...max` at the given step.
    static func ticks(min: Double, max: Double, step: Double) -> [Double] {
        guard step > 0, min < max, step.isFinite else { return [] }
        // Guard against a pathological step producing millions of lines.
        guard (max - min) / step <= 1000 else { return [] }
        let first = (min / step).rounded(.up) * step
        var result: [Double] = []
        var value = first
        while value <= max {
            result.append(value)
            value += step
        }
        return result
    }

    /// Formats an axis label without floating-point noise like `0.30000000004`.
    static func axisLabel(_ value: Double, step: Double) -> String {
        if abs(value) < step / 1000 { return "0" }
        let decimals = Swift.max(0, Int(-floor(log10(step))) + (step < 1 ? 0 : 0))
        if abs(value) >= 1e5 || (abs(value) < 1e-3 && value != 0) {
            return String(format: "%g", value)
        }
        return String(format: "%.\(Swift.min(decimals, 6))f", value)
    }

    // MARK: - Sampling

    /// One continuous run of points. A function is split into several segments
    /// wherever it's undefined or jumps, so the renderer never draws a false
    /// vertical line through an asymptote.
    typealias Segment = [CGPoint]

    /// Samples `f` across the viewport.
    ///
    /// - Parameter jumpFactor: a step larger than this multiple of the viewport
    ///   height is treated as a discontinuity rather than a steep slope. This
    ///   is what stops `tan(x)` and `1/x` being joined across their asymptotes.
    static func sample(
        _ f: (Double) throws -> Double,
        viewport: Viewport,
        size: CGSize,
        samples: Int = 600,
        jumpFactor: Double = 0.5
    ) -> [Segment] {
        guard size.width > 0, size.height > 0, samples > 1 else { return [] }

        var segments: [Segment] = []
        var current: Segment = []
        var previousY: Double?
        let jumpLimit = viewport.height * jumpFactor

        for step in 0...samples {
            let x = viewport.xMin + viewport.width * Double(step) / Double(samples)
            guard let y = try? f(x), y.isFinite else {
                // Undefined here: close the run and start a new one.
                if current.count > 1 { segments.append(current) }
                current = []
                previousY = nil
                continue
            }

            if let previous = previousY, abs(y - previous) > jumpLimit {
                // Either an asymptote or a genuine near-vertical rise. Breaking
                // is the safe choice: a missing hairline is far less misleading
                // than a line that isn't part of the function.
                if current.count > 1 { segments.append(current) }
                current = []
            }

            // Clamp far-off-screen values so one huge number doesn't destroy
            // the precision of the rest of the path.
            let clampedY = Swift.min(Swift.max(y, viewport.yMin - viewport.height),
                                     viewport.yMax + viewport.height)
            current.append(CGPoint(x: screenX(x, viewport: viewport, size: size),
                                   y: screenY(clampedY, viewport: viewport, size: size)))
            previousY = y
        }

        if current.count > 1 { segments.append(current) }
        return segments
    }

    /// Finds every x where `f` crosses zero within the viewport.
    ///
    /// Shared by the roots readout and by intersection finding, which is just
    /// the zeros of `f − g`.
    static func zeros(_ f: (Double) throws -> Double, viewport: Viewport,
                      samples: Int = 600) -> [Double] {
        var found: [Double] = []
        var previousX = viewport.xMin
        var previousY = try? f(previousX)

        // A sample landing exactly on zero is caught twice — once as the right
        // end of a bracket, once as the left end of the next — so near-identical
        // hits collapse into one.
        let tolerance = viewport.width / Double(samples) / 2
        func record(_ root: Double) {
            if found.contains(where: { abs(root - $0) <= tolerance }) { return }
            found.append(root)
        }

        for step in 1...samples {
            let x = viewport.xMin + viewport.width * Double(step) / Double(samples)
            let y = try? f(x)
            defer { previousX = x; previousY = y }

            guard let y0 = previousY, let y1 = y, y0.isFinite, y1.isFinite else { continue }

            if y0 == 0 { record(previousX); continue }
            guard (y0 < 0) != (y1 < 0) else { continue }

            // Bisect into the bracket.
            var lo = previousX, hi = x, loNegative = y0 < 0
            for _ in 0..<50 {
                let mid = (lo + hi) / 2
                guard let midY = try? f(mid), midY.isFinite else { break }
                if midY == 0 { lo = mid; hi = mid; break }
                if (midY < 0) != loNegative { hi = mid } else { lo = mid; loNegative = midY < 0 }
            }

            // A pole also flips sign — tan(x) leaps from +∞ to −∞ at π/2 — so
            // confirm the bracket converged on a zero, not an asymptote.
            let candidate = (lo + hi) / 2
            guard let value = try? f(candidate), value.isFinite,
                  abs(value) < 1e-3 * Swift.max(1, viewport.height)
            else { continue }
            record(candidate)
        }
        return found.sorted()
    }

    /// Where a curve crosses the x-axis.
    static func roots(_ f: (Double) throws -> Double, viewport: Viewport,
                      samples: Int = 600) -> [Double] {
        zeros(f, viewport: viewport, samples: samples)
    }

    /// Where two curves meet — the zeros of their difference.
    static func intersections(
        _ f: (Double) throws -> Double,
        _ g: (Double) throws -> Double,
        viewport: Viewport,
        samples: Int = 600
    ) -> [(x: Double, y: Double)] {
        let difference: (Double) throws -> Double = { x in
            let a = try f(x), b = try g(x)
            guard a.isFinite, b.isFinite else { throw EvalStop.undefined }
            return a - b
        }
        return zeros(difference, viewport: viewport, samples: samples)
            .compactMap { x in
                guard let y = try? f(x), y.isFinite,
                      y >= viewport.yMin - viewport.height,
                      y <= viewport.yMax + viewport.height
                else { return nil }
                return (x, y)
            }
    }

    /// Local minima and maxima, found where the numerical derivative turns.
    static func extrema(_ f: (Double) throws -> Double, viewport: Viewport,
                        samples: Int = 400) -> [(x: Double, y: Double, isMaximum: Bool)] {
        var result: [(Double, Double, Bool)] = []
        let step = viewport.width / Double(samples)
        var previous: (x: Double, y: Double)?
        var previousSlope: Double?

        for i in 0...samples {
            let x = viewport.xMin + step * Double(i)
            guard let y = try? f(x), y.isFinite else { previous = nil; previousSlope = nil; continue }
            if let last = previous {
                let slope = (y - last.y) / (x - last.x)
                if let before = previousSlope, before.sign != slope.sign,
                   abs(slope - before) < viewport.height {
                    // Sign flip in the slope brackets a turning point.
                    result.append((last.x, last.y, before > 0))
                }
                previousSlope = slope
            }
            previous = (x, y)
        }
        return result.map { (x: $0.0, y: $0.1, isMaximum: $0.2) }
    }

    private enum EvalStop: Error { case undefined }
}
