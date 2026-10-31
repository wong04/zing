# Zing

Windows-only browser, a remake of Krackn (macOS, SwiftUI/WebKit). A C# WinUI 3 host (`host/`) runs Edge's WebView2 for pages and Zing's React UI (`src/renderer`), with shared TS logic in `src/shared`. It moved off Electron in Phase W (`docs/webview2-migration.md`), because Google blocks sign-in in Electron.

- **Plan:** `docs/PLAN.md`. **Feature tracking:** `PARITY.md`, one GitHub issue per row (`[ID] title`).
- **`reference/krackn/` is read-only** Swift source at the baseline commit. Its own `CLAUDE.md` describes the *Swift app* (bundle.sh, `/Applications`, AppKit gotchas), not Zing. Read it for behaviour and hard-won gotchas, never edit it.
- When porting Krackn logic, port its tests from `reference/krackn/Tests/` with it; the Swift assertions are the spec.
- Close a parity issue only with evidence: a test, or light + dark screenshots for UI.
- Privacy model: keeps nothing by default. Anything that stores data (restore tabs, persistent profile, vault, import, future sync) is opt-in and flips the Settings privacy banner to a warning.
- No AI features.
- Weekly `upstream` issues list new Krackn commits; port or skip each, then update `reference/KRACKN_SHA`.
- **Parallel agents: read `AGENTS.md` before launching any.** One worktree per agent (`scripts/agent-worktree.sh`), shared contracts landed first, integrate one branch at a time, full suite after each merge. It lists the Phase 3 failures and the rule that prevents each.
- **Where logic lives:** app logic is C# in `host/Zing.Core` (no UI, xUnit-tested); `host/Zing.Host` is the thin WinUI/WebView2 layer. Logic ported from TS is held to it by shared `*.vectors.json` test vectors (read by vitest and by `host/Zing.Core.Tests/Vectors.cs`).
- **Checks:** `npm test` (vitest), `npm run test:host` (xUnit), `npm run lint`, `npm run typecheck`, `npm run test:e2e` (builds the renderer and `Zing.exe`, then Playwright over CDP).
- After any `await` on a view or page, check it is still there: a view that goes with its closing window throws `ViewGoneException`, which `TestHooks.Background` treats as expected. Page position only from `ChromeApi.Insets()`. Optional features never fail startup.
- Every `await` must resume on the UI thread: `Program.Main` installs WinUI's `DispatcherQueueSynchronizationContext`. Delayed work uses `IWindowHost.Scheduler` (a `DispatcherScheduler`); `Zing.Core` objects don't lock.
- e2e runs fail on any error the host records (`host/Zing.Host/TestHooks.cs`, `ZING_ERRORS_FILE`); don't add handlers that hide errors from it.
