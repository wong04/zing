import Foundation
var pass = 0, fail = 0
func eq(_ expr: String, _ want: Double, tol: Double = 1e-9, angle: Expression.AngleMode = .radians) {
    do {
        let got = try Expression.evaluate(expr, angle: angle)
        if abs(got - want) <= tol || (want != 0 && abs((got-want)/want) <= 1e-12) {
            pass += 1
        } else { fail += 1; print("  FAIL \(expr) = \(got), want \(want)") }
    } catch { fail += 1; print("  FAIL \(expr) threw \(error.localizedDescription)") }
}
func bad(_ expr: String) {
    do { let v = try Expression.evaluate(expr); fail += 1; print("  FAIL \(expr) should have thrown, got \(v)") }
    catch { pass += 1 }
}

print("arithmetic & precedence")
eq("1+2", 3); eq("2*3+4", 10); eq("4+2*3", 10); eq("(4+2)*3", 18)
eq("10/4", 2.5); eq("7%3", 1); eq("-5+2", -3); eq("--5", 5); eq("+-3", -3)
eq("2^3", 8); eq("2^3^2", 512)          // right associative
eq("-2^2", -4)                           // unary binds looser than ^
eq("1.5e3", 1500); eq("2e-3", 0.002)

print("implicit multiplication")
eq("2(3+4)", 14); eq("(1+1)(2+2)", 8); eq("3pi", 3 * Double.pi)

print("functions")
eq("sqrt(16)", 4); eq("cbrt(27)", 3); eq("abs(-7)", 7)
eq("ln(e)", 1); eq("log(1000)", 3); eq("log2(8)", 3); eq("exp(0)", 1)
eq("round(2.5)", 3); eq("floor(2.9)", 2); eq("ceil(2.1)", 3); eq("sign(-9)", -1)
eq("sin(0)", 0); eq("cos(0)", 1)
eq("sin(90)", 1, angle: .degrees); eq("cos(180)", -1, angle: .degrees)
eq("tan(45)", 1, tol: 1e-9, angle: .degrees)
eq("asin(1)", 90, angle: .degrees)
eq("deg(pi)", 180); eq("rad(180)", Double.pi)

print("factorial & constants")
eq("5!", 120); eq("0!", 1); eq("3!+2", 8)
eq("pi", Double.pi); eq("tau", 2 * Double.pi)

print("keypad symbols")
eq("6×7", 42); eq("9÷3", 3); eq("√16", 4); eq("1,000+1", 1001)

print("errors are thrown, not crashes")
bad(""); bad("2+"); bad("(1+2"); bad("1+2)"); bad("foo(2)"); bad("sqrt(-1)")
bad("ln(0)"); bad("asin(5)"); bad("2 3 &")

print("formatting")
func fmt(_ v: Double, _ want: String) {
    let got = Expression.format(v)
    if got == want { pass += 1 } else { fail += 1; print("  FAIL format(\(v)) = \(got), want \(want)") }
}
fmt(3, "3"); fmt(2.5, "2.5"); fmt(-0.125, "-0.125"); fmt(1.0/3.0, "0.3333333333")
fmt(1e15, "1000000000000000"); fmt(Double.nan, "NaN"); fmt(.infinity, "∞")

print("\n\(pass) passed, \(fail) failed")
exit(fail == 0 ? 0 : 1)
