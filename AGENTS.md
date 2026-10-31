# Working on Zing with agents

Read `CLAUDE.md` first. This file covers how to run **several agents in parallel** without them breaking each other or the app. It comes from Phase 3, where six agents shared one checkout and produced a run of crashes and flaky tests, and from Phase W, where port agents worked side by side on the C# host. The table below records Electron-era failures; their file names are gone, but the rules still hold.

## What went wrong in Phase 3, and the rule that prevents each

| What happened | Root cause | Rule now |
|---|---|---|
| The user launched the app and got "Unable to find Electron app" or `Cannot read properties of undefined (reading 'subscribe')` | Six agents rebuilt the same `out/` folder and edited the same files, so any launch could hit a half-built or half-edited state | One git worktree per agent. The main checkout is only touched at integration, so it stays runnable. |
| Agents' `tsc`/e2e failed on each other's unfinished edits; spurious launch failures | Shared checkout and shared `out/` | Same: isolated worktrees, integrated one at a time. |
| Startup crash `FindController.activeChanged` reading `state` | A cross-file `sed` rename changed a field's name but not its uses | No regex/`sed` renames across files. Rename, then run `npm run typecheck` immediately. |
| Crash when a popup closed itself (`isDestroyed` of undefined) | Electron detaches `view.webContents` before `'destroyed'` reaches us | Read `webContents` defensively at teardown, and use `isViewLive` (see "Code rules" below). |
| 9 "passing" e2e tests hid unhandled main-process rejections: overlay loads cut off by a closing window, disposed frames, a notice shown into a destroyed window | Nothing reported main-process errors to tests | The e2e harness now fails any test whose main process threw (`src/main/test-hooks.ts` + `e2e/zing.ts`), and `src/main/web-contents.ts` handles a page that's gone. |
| "Zing could not start: reg.exe took longer than 5000 ms" | A cosmetic icon check was fatal and had a tight timeout; the PC was busy with agents | Optional or cosmetic work never fails startup. It logs the error and carries on. |
| Find bar and panels 28 px off under the Favourites bar | Each layer hard-coded the chrome height | The page's position comes from one place: `ZingWindow.chromeInsets()` (`src/shared/layout/chrome.ts`). |
| Sidebar test failed depending on where the mouse was | Edge reveal read the real OS cursor | Tests never depend on real input. Use test seams (`cursorPoint()` in `test-hooks.ts`). |
| An agent reported "address bar Enter doesn't navigate" | Its own test used a fixture field that didn't exist | A bug report needs a failing test, checked to fail for the reason claimed. |
| A usage limit stopped all six agents mid-edit | Partial edits sat in the shared tree | Worktrees keep partial work contained. Only finished, green branches are integrated. |
| `git worktree remove --force` deleted the shared `node_modules` | It followed the `node_modules` junction | Use `scripts/agent-worktree.sh remove`, which unlinks first. |
| A download test failed once in about 10 runs | A real race: after a fixed 2 s wait we wrote our Mark of the Web, and on a busy PC Chromium's later generic mark replaced it, losing the HostUrl | Treat a flaky test as a race in the app until proven otherwise. Find it and fix it (`holdMarkOfTheWeb`); never add retries to hide it. |

## Parallel agent workflow (for the orchestrator)

1. **Land shared contracts first, yourself.** These go on the phase branch, green, before any agent starts: new IPC channels, option interfaces, settings keys, layout. Agents then build against them and don't need to edit the same hub files (`src/shared/ipc.ts`, `src/preload/index.ts`, `src/main/ipc-handlers.ts`, `src/main/zing-window.ts`, `src/main/index.ts`).
2. **One worktree per agent:** `bash scripts/agent-worktree.sh create <name>`. This creates branch `agent/<name>` at `~/zing-worktrees/<name>`, with `node_modules` linked. Give the agent that absolute path and an explicit list of files it owns.
3. **Agents:**
   - work only inside their worktree
   - never commit
   - never touch files outside their list
   - never launch the app from the main checkout
4. **An agent is done** when, inside its worktree, `npm test`, `npm run lint`, `npm run typecheck` and `npm run test:e2e` are all green. Its report lists the files it changed.
5. **Integrate one branch at a time.**
   - Commit the agent's worktree to `agent/<name>` and merge it into the phase branch.
   - Run the full suite after each merge.
   - The orchestrator resolves conflicts; agents don't.
6. **Hand-testing waits.** Tell the user it's safe to run the app only after integration is green.
7. **Clean up** with `bash scripts/agent-worktree.sh remove <name>`, then `git branch -D agent/<name>`.

Briefs for smaller models (Haiku) need:
- exact file paths
- one narrow job
- the shared contract to build against
- the verification commands to run
- "report, don't fix" for anything outside their files

## Code rules that prevent the Phase 3 bugs

- **Construction order.** Nothing may call back into an object before it exists. A `MainWindow` is listed by `ZingApp` before `Start()` runs, and the chrome's bridge channels are registered before its view loads and asks for state.
- **After any `await` on a view or page,** check it's still there. `RendererView.CreateAsync` throws `ViewGoneException` when its window closed meanwhile, and `TestHooks.Background` treats that as an expected result while recording everything else. `WebView2Page` checks `IsOpen` after starting.
- **UI thread only.** WinUI and WebView2 calls fail anywhere else (`RPC_E_WRONG_THREAD`). `Program.Main` installs the dispatcher synchronization context; timers go through `IWindowHost.Scheduler`; anything that may be called from another thread (e.g. `Notify`) hops to the dispatcher.
- **Optional features never abort startup or a command.** Their failures are logged with the reason. Real errors still raise.
- **Layout comes from `ChromeApi.Insets()`.** Never hard-code chrome heights or widths in a layer.
- **e2e tests:**
  - always go through `launchZing()` (`e2e/zing.ts`), which fails the test on any error the host records
  - always end with `zing.close()`, which quits as a person would and waits until the engine releases the profile
  - serve fixtures from a local server, never live sites; `ZING_E2E_OFFLINE=1` points every page at a dead proxy
  - never read the real mouse or keyboard
  - drive timers with `ZING_TEST_CLOCK=1` and the `advance-clock` test message
- **Never start `Zing.exe` by hand** without `ZING_E2E=1` and a scratch `ZING_USER_DATA`: without them it opens on the user's screen with their real profile. Go through `e2e/zing.ts`.
- **Write text containing backslashes or backticks with the Write or Edit tool,** never inside a shell string. A Git Bash heredoc collapsed `\\` to `\`, and backticks in a double-quoted string ran as commands (one ran a `.ts` file as a shell script).
- **Line endings are LF** (`.gitattributes`, `.editorconfig`), and indentation uses tabs.

## Test runs must not disturb the person at the PC

Agents and CI run hundreds of app launches while the user may be working on the same machine. So in e2e runs (`ZING_E2E=1`, unpackaged builds only):

- **Windows are kept out of the way.** In an e2e run every window opens at (-20000, -20000) and out of the switchers (`MainWindow.Place`), and `Reveal()` never takes focus.
- **No dialogs or OS actions.** Startup failures go to `ZING_ERRORS_FILE`, never an error box. Opening a file, Explorer, another app or a link outside Zing (`WindowsShell.OpenExternal`, `WindowsDownloads.OpenFile`/`ShowInFolder`) throws in an e2e run, so a test that reaches one fails.
- **Nothing on the real profile.** e2e runs must set `ZING_USER_DATA` (the harness does) and `ZING_DOWNLOADS_DIR`; the engine's data goes into the same scratch folder.
- **Code mustn't depend on focus to tidy up.** Test windows never have focus, and a user can switch apps at any moment. For example, the edge-reveal sidebar now puts itself away when Zing loses focus.
- **Tests must be deterministic.** Never compare two values that each stamp the current time. Freeze the clock (`vi.useFakeTimers`) or compare without the timestamp.
