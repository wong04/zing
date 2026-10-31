# Zing

Windows-only browser, a remake of Krackn (macOS, SwiftUI/WebKit). **Moving from Electron to WebView2 (C# WinUI 3 host + the existing React UI and TS logic): see `docs/webview2-migration.md`.**

- **Plan:** `docs/PLAN.md`. **Feature tracking:** `PARITY.md`, one GitHub issue per row (`[ID] title`).
- **`reference/krackn/` is read-only** Swift source at the baseline commit. Its own `CLAUDE.md` describes the *Swift app* (bundle.sh, `/Applications`, AppKit gotchas), not Zing. Read it for behaviour and hard-won gotchas, never edit it.
- When porting Krackn logic, port its tests from `reference/krackn/Tests/` with it; the Swift assertions are the spec.
- Close a parity issue only with evidence: a test, or light + dark screenshots for UI.
- Privacy model: keeps nothing by default. Anything that stores data (restore tabs, persistent profile, vault, import, future sync) is opt-in and flips the Settings privacy banner to a warning.
- No AI features.
- Weekly `upstream` issues list new Krackn commits; port or skip each, then update `reference/KRACKN_SHA`.
- **Parallel agents: read `AGENTS.md` before launching any.** One worktree per agent (`scripts/agent-worktree.sh`), shared contracts landed first, integrate one branch at a time, full suite after each merge. It lists the Phase 3 failures and the rule that prevents each.
- After any `await` on a view or page, use `src/main/web-contents.ts` (`isViewLive`, `loadUnlessGone`, `runInPage`). Page position only from `ZingWindow.chromeInsets()`. Optional features never fail startup.
- e2e runs fail on any main-process error (`src/main/test-hooks.ts`); don't add handlers that hide errors from it.
