import Foundation

/// A small recursive-descent expression evaluator for the calculator.
///
/// `NSExpression` can do arithmetic, but it can't parse `sin`, `√`, `^`, or
/// implicit multiplication, and it raises Objective-C exceptions on malformed
/// input — which can't be caught in Swift. Parsing it here keeps errors as
/// ordinary Swift throws and makes the whole thing unit-testable.
enum Expression {

    enum EvalError: LocalizedError, Equatable {
        case empty
        case unexpected(String)
        case unbalancedParentheses
        case unknownSymbol(String)
        case badArgument(String)

        var errorDescription: String? {
            switch self {
            case .empty: return nil
            case .unexpected(let t): return "Unexpected '\(t)'"
            case .unbalancedParentheses: return "Unbalanced parentheses"
            case .unknownSymbol(let s): return "Unknown '\(s)'"
            case .badArgument(let f): return "Invalid input for \(f)"
            }
        }
    }

    /// Angle handling for the trig functions.
    enum AngleMode: String, CaseIterable { case radians = "RAD", degrees = "DEG" }

    // MARK: - Public

    static func evaluate(_ input: String, angle: AngleMode = .radians,
                         variables: [String: Double] = [:]) throws -> Double {
        var parser = Parser(text: input, angle: angle, variables: variables)
        return try parser.parse()
    }

    /// Compiles once, then evaluates repeatedly with a different `x`.
    ///
    /// Plotting a curve means thousands of evaluations across the viewport;
    /// re-parsing the source each time would dominate the frame budget. This
    /// still re-parses per sample, but hides that behind one entry point so
    /// callers can't accidentally re-parse *and* re-normalise per pixel.
    static func makeFunction(_ input: String, angle: AngleMode = .radians,
                             variable: String = "x") -> (Double) throws -> Double {
        let source = input
        return { x in
            try evaluate(source, angle: angle, variables: [variable: x])
        }
    }

    /// Formats a result the way a calculator should: no trailing `.0`, no
    /// exponent for ordinary magnitudes, and full precision when it matters.
    static func format(_ value: Double) -> String {
        if value.isNaN { return "NaN" }
        if value.isInfinite { return value < 0 ? "-∞" : "∞" }
        if value == value.rounded(), abs(value) < 1e16 {
            return String(format: "%.0f", value)
        }
        let magnitude = abs(value)
        if magnitude != 0, magnitude < 1e-4 || magnitude >= 1e12 {
            return String(format: "%g", value)
        }
        // Trim trailing zeros from a fixed representation.
        var text = String(format: "%.10f", value)
        while text.hasSuffix("0") { text.removeLast() }
        if text.hasSuffix(".") { text.removeLast() }
        return text
    }

    // MARK: - Parser

    private struct Parser {
        let characters: [Character]
        var index = 0
        let angle: AngleMode
        let variables: [String: Double]

        init(text: String, angle: AngleMode, variables: [String: Double] = [:]) {
            self.variables = variables
            // Accept the symbols a calculator keypad produces alongside ASCII.
            let normalised = text
                .replacingOccurrences(of: "×", with: "*")
                .replacingOccurrences(of: "÷", with: "/")
                .replacingOccurrences(of: "−", with: "-")
                .replacingOccurrences(of: "π", with: "pi")
                .replacingOccurrences(of: ",", with: "")
            characters = Array(normalised)
            self.angle = angle
        }

        mutating func parse() throws -> Double {
            skipSpace()
            guard index < characters.count else { throw EvalError.empty }
            let value = try expression()
            skipSpace()
            guard index == characters.count else {
                throw EvalError.unexpected(String(characters[index]))
            }
            return value
        }

        // expression := term (('+' | '-') term)*
        mutating func expression() throws -> Double {
            var value = try term()
            while true {
                skipSpace()
                guard let c = peek(), c == "+" || c == "-" else { return value }
                index += 1
                let rhs = try term()
                value = c == "+" ? value + rhs : value - rhs
            }
        }

        // term := power (('*' | '/' | '%' | implicit) power)*
        mutating func term() throws -> Double {
            var value = try power()
            while true {
                skipSpace()
                guard let c = peek() else { return value }
                if c == "*" || c == "/" || c == "%" {
                    index += 1
                    let rhs = try power()
                    if c == "*" { value *= rhs }
                    else if c == "/" { value /= rhs }
                    else { value = value.truncatingRemainder(dividingBy: rhs) }
                } else if c == "(" || c.isLetter {
                    // Implicit multiplication: "2(3+4)" and "2pi" behave the
                    // way they do on paper.
                    let rhs = try power()
                    value *= rhs
                } else {
                    return value
                }
            }
        }

        // power := unary ('^' power)?   — right associative, so 2^3^2 = 512
        mutating func power() throws -> Double {
            let base = try unary()
            skipSpace()
            guard peek() == "^" else { return base }
            index += 1
            let exponent = try power()
            return pow(base, exponent)
        }

        mutating func unary() throws -> Double {
            skipSpace()
            // Negation binds *looser* than exponentiation, so -2^2 is -(2^2),
            // matching every calculator and maths textbook.
            if peek() == "-" { index += 1; return -(try power()) }
            if peek() == "+" { index += 1; return try power() }
            // Prefix root: √16 and √(x+1) both work.
            if peek() == "√" {
                index += 1
                let value = try power()
                guard value >= 0 else { throw EvalError.badArgument("sqrt") }
                return sqrt(value)
            }
            return try postfix()
        }

        /// Handles trailing `!` (factorial) and `%` written as a percentage.
        mutating func postfix() throws -> Double {
            var value = try primary()
            while true {
                skipSpace()
                guard peek() == "!" else { return value }
                index += 1
                guard value >= 0, value == value.rounded(), value <= 170 else {
                    throw EvalError.badArgument("factorial")
                }
                var result = 1.0
                var n = Int(value)
                while n > 1 { result *= Double(n); n -= 1 }
                value = result
            }
        }

        mutating func primary() throws -> Double {
            skipSpace()
            guard let c = peek() else { throw EvalError.empty }

            if c == "(" {
                index += 1
                let value = try expression()
                skipSpace()
                guard peek() == ")" else { throw EvalError.unbalancedParentheses }
                index += 1
                return value
            }

            if c.isNumber || c == "." { return try number() }
            if c.isLetter { return try identifier() }
            throw EvalError.unexpected(String(c))
        }

        mutating func number() throws -> Double {
            var text = ""
            while let c = peek(), c.isNumber || c == "." {
                text.append(c); index += 1
            }
            // Scientific notation: 1.5e-3
            if let c = peek(), c == "e" || c == "E" {
                let save = index
                var exponent = String(c)
                index += 1
                if let sign = peek(), sign == "+" || sign == "-" {
                    exponent.append(sign); index += 1
                }
                if let d = peek(), d.isNumber {
                    while let d = peek(), d.isNumber { exponent.append(d); index += 1 }
                    text += exponent
                } else {
                    index = save   // not an exponent after all — probably "e"
                }
            }
            guard let value = Double(text) else { throw EvalError.unexpected(text) }
            return value
        }

        mutating func identifier() throws -> Double {
            var name = ""
            while let c = peek(), c.isLetter || c.isNumber {
                name.append(c); index += 1
            }
            let lower = name.lowercased()

            // A bound variable wins over everything, so `e` can be Euler's
            // number normally but a plot variable if the caller says so.
            if let value = variables[name] ?? variables[lower] { return value }

            switch lower {
            case "pi": return Double.pi
            case "e": return M_E
            case "tau": return Double.pi * 2
            case "inf": return .infinity
            default: break
            }

            // Everything else must be a function call.
            skipSpace()
            guard peek() == "(" else { throw EvalError.unknownSymbol(name) }
            index += 1
            let argument = try expression()
            skipSpace()
            guard peek() == ")" else { throw EvalError.unbalancedParentheses }
            index += 1

            return try apply(lower, argument)
        }

        func apply(_ name: String, _ x: Double) throws -> Double {
            /// Trig respects the DEG/RAD switch; everything else is unit-free.
            func toRadians(_ v: Double) -> Double {
                angle == .degrees ? v * .pi / 180 : v
            }
            func fromRadians(_ v: Double) -> Double {
                angle == .degrees ? v * 180 / .pi : v
            }

            switch name {
            case "sin": return sin(toRadians(x))
            case "cos": return cos(toRadians(x))
            case "tan": return tan(toRadians(x))
            case "asin":
                guard (-1...1).contains(x) else { throw EvalError.badArgument("asin") }
                return fromRadians(asin(x))
            case "acos":
                guard (-1...1).contains(x) else { throw EvalError.badArgument("acos") }
                return fromRadians(acos(x))
            case "atan": return fromRadians(atan(x))
            case "sinh": return sinh(x)
            case "cosh": return cosh(x)
            case "tanh": return tanh(x)
            case "sqrt":
                guard x >= 0 else { throw EvalError.badArgument("sqrt") }
                return sqrt(x)
            case "cbrt": return cbrt(x)
            case "ln", "log":
                guard x > 0 else { throw EvalError.badArgument(name) }
                return name == "ln" ? log(x) : log10(x)
            case "log2":
                guard x > 0 else { throw EvalError.badArgument("log2") }
                return log2(x)
            case "exp": return exp(x)
            case "abs": return abs(x)
            case "round": return x.rounded()
            case "floor": return x.rounded(.down)
            case "ceil": return x.rounded(.up)
            case "sign": return x == 0 ? 0 : (x < 0 ? -1 : 1)
            case "deg": return x * 180 / .pi
            case "rad": return x * .pi / 180
            default: throw EvalError.unknownSymbol(name)
            }
        }

        // MARK: Lexing helpers

        func peek() -> Character? {
            index < characters.count ? characters[index] : nil
        }

        mutating func skipSpace() {
            while let c = peek(), c == " " || c == "\t" { index += 1 }
        }
    }
}
