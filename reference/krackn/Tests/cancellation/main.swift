import Foundation
var pass = 0, fail = 0
func check(_ n: String, _ c: Bool, _ d: String = "") { if c { pass += 1; print("   ok   \(n)") } else { fail += 1; print("   FAIL \(n) \(d)") } }
let sem = DispatchSemaphore(value: 0)

Task {
    // 1. Cancel mid-fetch must return promptly, not wait out the timeout.
    for delay in [0.02, 0.15, 0.5] {
        let t0 = Date()
        let task = Task { () -> Bool in
            let engine = RelayEngine()
            do {
                _ = try await engine.fetch(urlString: "https://en.wikipedia.org/wiki/Wi-Fi",
                                           enabled: Set(RelayStrategy.allCases))
                return false   // completed before we cancelled
            } catch is CancellationError { return true }
            catch { return false }
        }
        try? await Task.sleep(for: .seconds(delay))
        task.cancel()
        let cancelled = await task.value
        let elapsed = Date().timeIntervalSince(t0)
        check("cancel@\(delay)s returns fast", elapsed < 12, "took \(String(format: "%.1f", elapsed))s")
        print("      (elapsed \(String(format: "%.2f", elapsed))s, cancelled=\(cancelled))")
    }

    // 2. Cancelling the raw HTTP client must not leak the task.
    for _ in 0..<5 {
        let t0 = Date()
        let task = Task { () -> String in
            do {
                _ = try await PinnedHTTPClient.fetch(
                    url: URL(string: "https://en.wikipedia.org/wiki/Computer_network")!, timeout: 60)
                return "completed"
            } catch is CancellationError { return "cancelled" }
            catch { return "error: \(error.localizedDescription)" }
        }
        try? await Task.sleep(for: .milliseconds(60))
        task.cancel()
        let outcome = await task.value
        check("client cancel resumes (\(outcome))", Date().timeIntervalSince(t0) < 12,
              "hung \(Int(Date().timeIntervalSince(t0)))s")
    }

    // 3. Cancelling many at once must not deadlock.
    let t0 = Date()
    await withTaskGroup(of: Void.self) { g in
        for _ in 0..<15 {
            g.addTask {
                let t = Task {
                    _ = try? await PinnedHTTPClient.fetch(url: URL(string: "https://en.wikipedia.org/wiki/Wi-Fi")!, timeout: 60)
                }
                try? await Task.sleep(for: .milliseconds(Int.random(in: 20...300)))
                t.cancel()
                _ = await t.value
            }
        }
    }
    check("15 concurrent cancels, no deadlock", Date().timeIntervalSince(t0) < 30,
          "took \(Int(Date().timeIntervalSince(t0)))s")

    // 4. A normal fetch still works after all that.
    let engine = RelayEngine()
    let ok = (try? await engine.fetch(urlString: "https://example.com", enabled: [.dohPinned])) != nil
    check("normal fetch unaffected", ok)

    print("\n\(pass) passed, \(fail) failed")
    sem.signal()
}
sem.wait()
exit(fail == 0 ? 0 : 1)
