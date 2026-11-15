# Local AI over MCP: design

> **Not part of v1. This is post-v1 work and must not block or delay the v1.0.0 release.**

## Intent

The owner wants their own AI tools on their own PC (Claude Code, the Claude desktop app, LM Studio, Ollama-based agents) to drive Zing. The AI should do research, work on pages and documents the owner already has open, download files, take screenshots, and fill in sites, logins included.

Zing never calls a model and needs no API key. It exposes MCP tools only. The owner's existing Claude subscription, or a local model, does the thinking.

## Goals

- An opt-in MCP server built into Zing, off by default, switched on in Settings.
- Reachable only from this PC, by this Windows user.
- The AI only sees tabs the owner has shared with it, plus the tabs the AI opens itself.
- Reading tools (tabs, page snapshot, text, screenshot) and action tools (open, close, navigate, click, type, scroll, download, vault fill).
- Per-site confirmation levels the owner chooses.

## Non-goals

- Zing hosting or calling any model, or a chat panel in Zing.
- Access from other machines, the cloud, or claude.ai web connectors.
- Exposing the raw DevTools protocol or `--remote-debugging-port`.

## Anti-goals (a failure even if it works)

- The AI reads a tab the owner didn't share.
- The AI ever sees a password, cookie or vault entry, or reaches Settings, sync, Zing's own `zing://` pages or the Zing chrome.
- A purchase, send or submit goes through on a site set to "ask" without the owner clicking Allow.
- Turning the feature on silently stores data. The privacy banner must warn once anything is kept.
- Any of this delays v1.0.0.

## Constraints

- The logic is C# in `host/Zing.Core` (no UI, xUnit-tested), and `host/Zing.Host` stays a thin WebView2 layer (`CLAUDE.md`).
- Every `await` resumes on the UI thread. After an await on a view, check the view is still there (`ViewGoneException`).
- Optional features never fail startup: if the port or pipe can't be opened, Settings shows the error and Zing runs on.
- Privacy model: the switch, the token and the per-site rules are stored. While the feature is on, the Settings privacy banner shows a warning.

## Approaches considered

| | Approach | Trade-off | Verdict |
|---|---|---|---|
| **A** | Send DevTools commands to each shared tab's own view (`CoreWebView2.CallDevToolsProtocolMethodAsync`) | More code than C, but Zing decides per call which tab is reachable. Real input events. A snapshot built from the accessibility tree that page scripts can't alter. | **Picked by the owner** |
| B | Inject a script that reads the DOM and fires synthetic events | Quick, but events are `isTrusted: false` (many sites ignore them), and page scripts can see and tamper with what the AI reads | Rejected |
| C | Wrap Playwright MCP or Chrome DevTools MCP behind `--remote-debugging-port` | Least code, but the port has no auth and reaches every target: the chrome, Zing's own `zing://` pages and cookies | Rejected |

### What the e2e machinery offers

- **Test-only, stays test-only:**
  - `TestHooks.DebuggingPort` / `--remote-debugging-port` (`host/Zing.Host/TestHooks.cs:22`), which Playwright reaches through `connectOverCDP` (`e2e/zing.ts:285`);
  - the JSON hooks in `TestHooks.TryHandle`;
  - the fake sealers and clocks.

  None of this may be switched on in production.
- **Reusable:**
  - the knowledge of which CDP commands work inside WebView2 (`Accessibility.getFullAXTree`, `DOM.getBoxModel`, `Input.dispatchMouseEvent` / `dispatchKeyEvent` / `insertText`, `Page.captureScreenshot`);
  - the existing `DownloadHost` flow;
  - the vault fill path (the key icon fill, with Hello);
  - `ViewerGuests` for reaching PDF guest frames through `Target.attachToTarget` sessions.

  The e2e suite will also test this feature through its own MCP endpoint, not through CDP.

## Design (as decided)

### Connecting (owner's decisions: no API key; Claude Code and the Claude desktop app)

- **Localhost HTTP:** Zing serves the MCP Streamable HTTP transport on `127.0.0.1:<port>`.
  - It rejects any request whose `Host` or `Origin` isn't loopback.
  - It needs `Authorization: Bearer <token>`. The token is random (32 bytes), kept DPAPI-sealed, shown in Settings, and can be regenerated. Regenerating cuts off old clients.
  - Claude Code, LM Studio and HTTP-speaking agents use this.
- **stdio shim:** `zing-mcp.exe` ships next to `Zing.exe`. The Claude desktop app (and any stdio-only client) launches it.
  - It relays stdio to the running Zing over a named pipe whose ACL allows only the current user, so it needs no token.
  - If Zing isn't running or the feature is off, it returns a clear MCP error. It never starts Zing itself.
- **Connect Claude:** Settings shows ready-to-paste config for Claude Code (`claude mcp add ...`) and for the Claude desktop config JSON.

### Which tabs (owner's decision: only the ones shared)

- **Share:** tab context menu "Let AI use this tab".
  - Tabs the AI opens with `open_tab` start out shared, and so do popups (`target=_blank`) opened from a shared tab. (There are no tab groups in Zing; the outline is the mark.)
  - Zing's own `zing://` pages, the chrome, and any tab while it shows a `zing://` URL are never shareable. Navigating a shared tab to one unshares it.
- **The mark:** a shared tab has a coloured outline and an "AI is using this tab" strip with **Stop**.
  - Stop unshares the tab, cancels its pending calls, and dismisses its confirm bar.
  - A **Stop all** in the strip and in Settings unshares every tab.
- **What the AI sees:** `list_tabs` returns shared tabs only, each with an opaque id that is not the internal tab id. Every call re-checks that the tab is still shared.
- **Background tabs (owner's decision: stay in the background).** Zing never brings a shared tab to the front for the AI. Shared tabs are kept awake (no sleeping, suspending or auto-closing). Reading tools and input work in the background. A `screenshot` of a hidden tab and a confirm on one put a badge on the tab and wait until the owner switches to it, under the same 2-minute limit (a screenshot returns `NotVisible` when it runs out, and a confirm returns `Denied`).

### Tools

- **Reading (milestone 1):**
  - `list_tabs`;
  - `snapshot(tab)`: an accessibility-tree outline with refs such as `e12` for things the AI can act on;
  - `read_text(tab)`: the main text, which also covers PDFs and documents in the viewer;
  - `screenshot(tab, ref?)`: a PNG of the visible page or one element.
- **Actions (milestone 2):**
  - `open_tab(url)`, `close_tab`, `navigate`, `back`, `forward`;
  - `click(ref)`, `type(ref, text, submit?)`, `select(ref, value)`, `scroll`, `press_key`;
  - `wait_for(text|ref, timeout)`;
  - `download(url|ref)`;
  - `fill_login(tab)`.
- **Refs** are tied to one snapshot. A ref from an older snapshot fails with `StaleRef`, and the AI takes a new snapshot.
- **Input** is real input sent through `Input.dispatch*`, at viewport points from `DOM.getBoxModel`. Before each click, `DOM.getNodeForLocation` at that point must resolve to the ref's node, or the call fails, so an overlay cannot redirect a click.

### Confirmations (owner's decision: per site, up to the owner)

- **Levels.** Each site (the full Public Suffix List domain from `PublicSuffix`, so `alice.github.io` and `bob.github.io` are separate) has one of three:
  - **Ask before submit/buy/send** (the default);
  - **Ask before every action**;
  - **Don't ask**.
- **What counts as a submit:** a submit of a form, an Enter key press in a form, or a click on a control whose accessible name or role matches pay, buy, order, checkout, send, post, publish, delete or confirm. That word list sits in a shared vectors file so tests pin it down.
- **The confirm bar.** When an action needs the owner's OK, the call waits. The tab shows "AI wants to <action> — Allow / Deny / Always allow on this site".
  - Deny or a 2-minute timeout returns `Denied` to the AI.
  - The owner can edit the per-site list in Settings.
- **Downloads** follow the same level and go through the normal `DownloadHost` flow into Downloads, shown in the downloads list. The AI never picks a path.

### Logins (owner's decision: the AI may fill from the vault)

- `fill_login(tab)` asks Zing to fill the login form on that tab from the vault, for that exact origin, after Windows Hello.
- One saved login: fill it. More than one: Zing asks the owner which, and the AI never sees the choices. None: `NoSavedLogin`.
- The AI only ever learns `Filled`, `Denied` or `NoSavedLogin`. No tool returns a credential, and snapshots mask password fields.

### Never reachable

- These have no tools at all: the vault contents, passwords, cookies and storage, Settings, sync, history, bookmarks, the Zing chrome, and Zing's own `zing://` pages.
- Arbitrary script evaluation is never exposed.
- Only `http`/`https` URLs: never `file:`, `javascript:`, `edge:`, `view.zing` or `zing:`.
- Fields Zing filled from the vault stay masked in snapshots and blacked out in screenshots, whatever their input type, so a show-password toggle cannot leak them.
- Enter in any editable (a form or a `contenteditable` like Gmail or Slack) counts as a submit. As a backstop, a top-level form POST navigation that follows an AI action is paused for confirmation on sites set to ask (XHR/fetch is not paused: autosave and analytics would make it constant).

## Left to the builder

- The class layout inside `Zing.Core.Mcp`, the snapshot text format, and the port number (fixed, or picked and shown in Settings).
- Whether `zing-mcp.exe` is a separate small project or a mode of `Zing.exe`. A separate project is preferred, to keep it tiny.
- The MCP library: the official C# MCP SDK (`ModelContextProtocol`) if it fits, otherwise a hand-written JSON-RPC layer.
- Frame sessions for cross-origin iframes (milestone 1 covers the main frame and the PDF guest only).

## Calls I made (not discussed with the owner)

- Localhost HTTP as well as stdio, with a bearer token for HTTP and a user-only pipe for stdio.
- Snapshot refs in the Playwright MCP style.
- The 2-minute confirm timeout, and the submit word list.
- Confirmation levels keyed by `PublicSuffix` site.
- No `evaluate`/script tool.
- Only http(s) pages can be shared or navigated to. `navigate` and `open_tab` go through `TabService`, so ThreatGate and the blocker apply.
