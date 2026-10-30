import SwiftUI

// MARK: - Minimal SVG path parser

/// Turns an SVG path `d` string into a SwiftUI `Path`.
///
/// Deliberately small: it handles exactly the commands the Krackn artwork uses
/// (`M L C Z`) plus the near-neighbours a design tool might emit on a re-export
/// (`H V`, and the lowercase relative forms), and nothing else. No arcs, no
/// smooth-curve reflection — if a future SVG needs them this refuses loudly in a
/// test rather than drawing something subtly wrong. Kept dependency-free, like
/// the rest of the app, and pure so it can be unit-tested against a known
/// bounding box.
enum SVGPathParser {

    static func path(from d: String) -> Path {
        var path = Path()
        var scanner = NumberScanner(d)
        var current = CGPoint.zero
        var subpathStart = CGPoint.zero
        var command: Character = " "

        func point(_ x: CGFloat, _ y: CGFloat, relative: Bool) -> CGPoint {
            relative ? CGPoint(x: current.x + x, y: current.y + y) : CGPoint(x: x, y: y)
        }

        while let token = scanner.nextCommandOrStay(after: command) {
            command = token
            let relative = command.isLowercase
            switch command.uppercased().first! {
            case "M":
                guard let x = scanner.number(), let y = scanner.number() else { return path }
                current = point(x, y, relative: relative)
                subpathStart = current
                path.move(to: current)
                // Extra coordinate pairs after an M are implicit L, per spec.
                command = relative ? "l" : "L"
            case "L":
                guard let x = scanner.number(), let y = scanner.number() else { return path }
                current = point(x, y, relative: relative)
                path.addLine(to: current)
            case "H":
                guard let x = scanner.number() else { return path }
                current = relative ? CGPoint(x: current.x + x, y: current.y)
                                   : CGPoint(x: x, y: current.y)
                path.addLine(to: current)
            case "V":
                guard let y = scanner.number() else { return path }
                current = relative ? CGPoint(x: current.x, y: current.y + y)
                                   : CGPoint(x: current.x, y: y)
                path.addLine(to: current)
            case "C":
                guard let x1 = scanner.number(), let y1 = scanner.number(),
                      let x2 = scanner.number(), let y2 = scanner.number(),
                      let x = scanner.number(), let y = scanner.number() else { return path }
                let c1 = point(x1, y1, relative: relative)
                let c2 = point(x2, y2, relative: relative)
                current = point(x, y, relative: relative)
                path.addCurve(to: current, control1: c1, control2: c2)
            case "Z":
                path.closeSubpath()
                current = subpathStart
            default:
                // An unsupported command would otherwise loop forever consuming
                // nothing. Stop and let the (empty-ish) path surface the problem.
                return path
            }
        }
        return path
    }

    /// Walks the string yielding numbers and command letters. SVG lets numbers
    /// run together with commas, spaces, or a leading sign/decimal as the only
    /// separator ("1.5.5" is two numbers), so a hand-rolled scanner is simpler
    /// and more correct here than splitting on whitespace.
    private struct NumberScanner {
        private let chars: [Character]
        private var i = 0
        init(_ s: String) { chars = Array(s) }

        private mutating func skipSeparators() {
            while i < chars.count, chars[i] == " " || chars[i] == "," || chars[i] == "\n"
                    || chars[i] == "\t" || chars[i] == "\r" { i += 1 }
        }

        /// Returns the next command letter, or — if the next token is a number —
        /// keeps `previous` so a repeated command (e.g. several `C` groups) runs
        /// without the letter being restated.
        mutating func nextCommandOrStay(after previous: Character) -> Character? {
            skipSeparators()
            guard i < chars.count else { return nil }
            let c = chars[i]
            if c.isLetter {
                i += 1
                return c
            }
            // A number here means "repeat the previous command".
            return previous == " " ? nil : previous
        }

        mutating func number() -> CGFloat? {
            skipSeparators()
            guard i < chars.count else { return nil }
            var s = ""
            if chars[i] == "-" || chars[i] == "+" { s.append(chars[i]); i += 1 }
            var seenDot = false
            while i < chars.count {
                let c = chars[i]
                if c.isNumber { s.append(c); i += 1 }
                else if c == "." && !seenDot { seenDot = true; s.append(c); i += 1 }
                else if c == "e" || c == "E" {
                    s.append(c); i += 1
                    if i < chars.count, chars[i] == "-" || chars[i] == "+" {
                        s.append(chars[i]); i += 1
                    }
                } else { break }
            }
            return Double(s).map { CGFloat($0) }
        }
    }
}

// MARK: - Shape

/// One layer of the logo — the supplied `d` strings, aspect-fit and centred in
/// the target rect. A `Shape` so it can be filled with any `Color` and animates
/// like any other SwiftUI shape.
struct SVGShape: Shape {
    let paths: [String]
    var viewBox: CGSize = KracknLogoArt.viewBox

    func path(in rect: CGRect) -> Path {
        guard viewBox.width > 0, viewBox.height > 0 else { return Path() }
        let scale = min(rect.width / viewBox.width, rect.height / viewBox.height)
        let drawn = CGSize(width: viewBox.width * scale, height: viewBox.height * scale)
        let offset = CGPoint(x: rect.midX - drawn.width / 2,
                             y: rect.midY - drawn.height / 2)
        var transform = CGAffineTransform(translationX: offset.x, y: offset.y)
            .scaledBy(x: scale, y: scale)

        var combined = Path()
        for d in paths {
            combined.addPath(SVGPathParser.path(from: d), transform: transform)
        }
        return combined
    }
}

// MARK: - Logo

/// The Krackn creature, tinted by the current theme.
///
/// Two layers painted in the SVG's own order: the body, then the eyes on top.
/// The defaults come from `Palette`, so every accent family and light/dark
/// pairing renders its own version with no per-theme artwork — the whole reason
/// to carry the logo as vectors rather than a fixed-colour image.
struct KracknLogo: View {
    var size: CGFloat = 22
    /// The body colour. Accent by default, matching the mark it replaces.
    var bodyColor: Color = Palette.accent
    /// The eyes. `onAccent` by default — the token guaranteed to read on the
    /// body colour in every theme, so the eyes are never lost against it.
    var eyeColor: Color = Palette.onAccent

    var body: some View {
        ZStack {
            SVGShape(paths: KracknLogoArt.body).fill(bodyColor)
            SVGShape(paths: KracknLogoArt.eyes).fill(eyeColor)
        }
        .frame(width: size, height: size)
        // The artwork isn't perfectly square (236×231); this keeps the drawn
        // mark centred within the square frame callers expect.
        .aspectRatio(KracknLogoArt.viewBox.width / KracknLogoArt.viewBox.height,
                     contentMode: .fit)
        .accessibilityLabel("Krackn")
    }
}


/// The Krackn wordmark's creature, from `Group 2.svg`, as vector paths so
/// it stays crisp at any size and takes the theme's colours. Two layers:
/// the body (the black shapes) and the eyes (the white ones). Rendered
/// with `Palette` tokens, so every accent family and light/dark pairing
/// gets its own version for free — the point of translating the SVG rather
/// than shipping a fixed-colour bitmap.
///
/// The `d` strings are lifted verbatim from the SVG (viewBox 236×231); the
/// parser below handles the M/L/C/Z it uses. Do not hand-edit these — regen
/// from the source SVG if the artwork changes.
enum KracknLogoArt {
    static let viewBox = CGSize(width: 236, height: 231)

    /// The body — filled with the primary colour.
    static let body: [String] = [
        "M114.502 0.271195C124.487 -1.4018 135.904 4.93644 142.644 11.8379C152.489 21.9162 160.107 35.7749 160.354 50.1507C160.424 54.2022 159.294 58.0482 158.927 62.0514C158.644 64.9312 159.072 68.1232 159.032 71.0244C158.889 80.9139 154.289 83.3942 148.357 89.3697C149.302 95.5519 154.192 99.5167 160.034 100.833C165.432 102.03 171.084 101.03 175.742 98.0554C179.122 95.8674 182.329 92.2037 183.569 88.3622C189.544 69.8217 169.202 57.5389 173.602 38.1927C174.844 32.6194 178.277 27.7809 183.127 24.7669C201.582 13.4749 220.297 37.7587 204.362 46.2164C205.009 44.2159 205.622 42.2607 205.664 40.1382C205.739 37.2757 204.642 34.5069 202.629 32.4712C198.402 28.2422 190.754 28.2797 186.857 32.6949C177.554 43.2304 191.204 57.5647 195.604 67.0042C198.039 72.1414 199.309 77.7534 199.322 83.4387C199.339 92.6684 195.622 101.512 189.017 107.96C181.054 115.813 171.542 119.05 160.539 118.993C172.134 127.44 191.902 137.915 206.357 135.64C212.434 134.83 217.854 131.963 221.622 127.048C226.414 120.795 227.267 110.918 220.229 105.806C213.562 100.962 204.147 106.717 206.289 114.915C206.522 115.808 206.992 116.86 206.777 117.755C204.942 118.1 202.127 113.46 201.812 111.775C200.457 104.501 205.887 98.8452 212.589 97.3499C217.079 96.3864 221.769 97.2272 225.647 99.6907C230.399 102.721 233.662 108.027 234.737 113.47C237.977 131.043 223.797 146.885 207.082 150.043C191.797 152.93 177.289 148.408 163.342 142.668C154.892 139.193 147.919 135.14 140.362 130.093C145.074 139.738 147.102 144.318 148.539 155.023C151.067 173.818 137.597 184.875 135.837 202.125C135.562 210.815 140.142 217.843 148.782 219.808C161.092 222.608 170.852 206.413 158.944 199.755C155.189 197.655 152.362 200.478 149.449 200.143L149.337 199.773C151.072 196.545 154.224 194.105 157.852 194.443C173.892 195.935 175.019 214.633 165.714 223.67C161.262 228 155.257 230.36 149.049 230.218C141.637 230.058 134.592 226.963 129.464 221.608C120.602 212.263 119.389 196.84 123.647 185.378C130.922 165.79 134.149 154.708 118.272 137.965C118.074 137.773 117.809 137.61 117.577 137.445C116.254 137.718 111.409 144.418 110.418 145.908C95.3034 168.598 120.592 185.583 112.202 210.723C109.776 218.058 104.518 224.115 97.6006 227.553C86.3871 233.09 71.1611 229.98 65.4806 218.14C61.7621 210.388 63.4791 199.39 71.9489 195.585C75.9216 193.798 81.4966 193.883 84.4076 197.498C85.3956 198.698 85.7481 199.628 86.3466 201.035C83.8181 199.58 81.0571 198.348 78.1054 199.048C72.3281 200.415 69.9976 205.47 71.2876 210.915C72.0141 214.043 74.0106 216.728 76.7976 218.323C84.2729 222.653 93.9144 218.683 97.5654 211.298C104.166 197.763 92.0446 184.58 88.4021 171.813C83.6646 155.208 88.1696 143.355 95.8106 128.893C88.2609 135.46 78.7474 139.548 69.4029 143.633C46.2481 153.755 14.0021 156.668 1.99689 129.033C-0.607862 121.9 -0.765355 115.543 2.1229 108.358C7.2304 95.6512 26.8814 91.9589 32.9309 105.807C34.5624 109.543 33.1976 116.175 28.3859 117.685L28.0969 117.47C29.0369 114.075 30.0909 111.805 28.0444 108.336C26.7921 106.24 24.7211 104.763 22.3319 104.26C16.4499 102.987 11.1289 107.471 10.0856 113.165C8.07188 124.155 17.1171 133.698 27.5231 135.483C37.3194 137.163 47.0951 134.14 55.9124 130.053C62.6649 126.918 68.8921 123.368 75.0019 119.15C66.2251 118.728 58.9019 117.433 51.4744 112.405C45.1309 108.112 40.4034 102.242 37.8804 94.9842C28.2906 67.3959 54.6171 54.1587 51.2491 38.3987C50.6094 35.2829 48.7039 32.5732 45.9884 30.9169C43.3551 29.3389 40.1004 28.9937 37.1669 29.8542C34.4024 30.6519 32.0886 32.5532 30.7696 35.1102C28.7564 39.0277 29.7271 42.5519 30.9701 46.4172C29.3014 45.4047 28.2914 44.8457 27.0811 43.2547C22.5096 37.2439 26.1834 28.8762 31.8394 25.0624C35.8884 22.3322 40.7416 21.4267 45.5051 22.2927C50.7064 23.2164 55.3239 26.1779 58.3329 30.5199C60.5454 33.7809 61.8666 37.5639 62.1654 41.4934C63.2754 55.5514 53.6326 63.8349 50.9666 76.6062C49.8556 81.9282 50.7744 88.1032 53.9619 92.6372C57.1974 97.1584 62.0924 100.214 67.5754 101.134C75.8916 102.566 85.0394 98.4954 86.6456 89.5279C84.6856 87.2587 81.9876 85.5072 79.9424 82.7517C77.6404 80.0132 75.6324 73.8814 76.2534 70.1947C77.3459 63.7062 76.0044 60.6232 75.1529 54.2927C73.4426 41.5812 79.7216 27.5437 87.3384 17.6557C94.3304 8.57894 102.839 1.77495 114.502 0.271195Z",
        "M148.946 139.821C150.699 140.418 163.764 146.631 164.579 147.598C174.241 159.046 175.966 179.266 191.856 184.941C195.486 186.238 200.174 186.136 203.594 184.333C206.421 182.848 208.511 180.266 209.374 177.191C211.259 170.568 206.249 163.858 199.161 165.393C196.924 165.878 195.674 166.566 193.754 167.691C196.854 160.703 201.886 158.898 209.009 161.661C217.411 166.193 219.404 177.151 214.899 185.003C207.546 197.826 191.284 199.141 179.251 192.393C169.336 186.833 164.766 179.046 158.504 170.006C156.724 167.436 154.139 164.566 152.956 161.638C152.266 159.131 152.474 154.523 152.009 151.713C151.334 147.656 150.089 143.766 148.946 139.821Z",
        "M85.6787 139.928C85.8182 140.006 85.9576 140.086 86.0972 140.166C85.4907 143.646 83.9334 147.573 83.3297 151.058C82.7729 154.258 83.0257 158.011 82.2892 161.141C81.4062 164.683 78.3644 167.286 76.5037 170.258C67.0552 185.356 54.3054 200.006 34.4537 195.586C23.5727 193.163 15.7112 182.438 18.6847 171.173C19.7919 166.978 22.2974 163.883 26.0829 161.716C32.7204 158.538 38.8739 160.806 41.3792 167.816C35.9752 164.351 29.2362 163.508 26.4174 170.086C22.0687 180.231 32.7117 187.938 41.9614 185.306C58.8524 180.496 60.9832 159.883 70.5007 147.881C71.9899 146.003 82.9474 141.188 85.6787 139.928Z",
    ]

    /// The eyes — filled with the contrasting colour, punched over the body.
    static let eyes: [String] = [
        "M84.6834 65.9985C93.1554 67.3525 95.6297 75.519 95.2627 82.871C87.5554 80.5998 83.4424 73.9378 84.6834 65.9985Z",
        "M149.422 66.1406C150.179 66.2114 149.844 66.0836 150.459 66.5546C152.022 74.4751 147.694 79.3026 140.972 82.3544C140.257 82.5529 140.574 82.6094 139.979 82.3654C139.942 82.2726 139.904 82.1799 139.867 82.0869C139.267 76.5146 141.282 70.7731 146.159 67.6871C147.474 66.8559 148.229 66.5871 149.422 66.1406Z",
    ]
}
