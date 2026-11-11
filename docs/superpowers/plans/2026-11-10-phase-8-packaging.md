# Phase 8: Packaging and Release Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A Velopack per-user installer for Zing that registers it as a browser, updates itself from GitHub Releases behind a "Relaunch to update" pill, ships third-party notices, and is built by a release workflow. Nothing is published in this phase.

**Architecture:** Update and registration logic lives in `Zing.Core` behind small interfaces (`IUpdateSource`, `DefaultBrowserRegistration`) with xUnit tests. `Zing.Host` adds Velopack (startup hooks, the real update source, registry writes) and two thin UI pieces: an update pill layer and a default-browser offer on the existing `NoticeLayer`. Packaging is a local script plus `.github/workflows/release.yml`, both driving `vpk` as a local dotnet tool.

**Tech Stack:** .NET 9, Velopack (NuGet `Velopack`, dotnet tool `vpk`), WebView2, React/TypeScript renderer, vitest, xUnit, Playwright over CDP, GitHub Actions.

**Spec:** `docs/superpowers/specs/2026-11-10-phase-8-packaging-design.md`

## Global Constraints

- Velopack pack ID `ZingBrowser`; install folder `%LOCALAPPDATA%\ZingBrowser`; display name "Zing". `AppPaths` locations don't change.
- Update source: GitHub `https://github.com/wong04/zing`, published releases only, no prereleases. Local override: env `ZING_UPDATE_FEED` (a folder path).
- First check about 30 s after startup, then every 4 h. Restart only on pill click or on quit.
- Setting `checkForUpdates`, default `true`. Its detail text: "Asks GitHub for the latest version every few hours. Sends nothing about you."
- Setting `defaultBrowserDismissed`, default `false`.
- Setting `launchedBefore`, default `false`; set true after a profile's first launch. The default-browser offer waits for it (owner's decision: the import nudge owns the first launch).
- Default-browser offer text: "Make Zing your default browser?" Its button opens `ms-settings:defaultapps?registeredAppUser=Zing`.
- Pill text: "Relaunch to update".
- Internal versions `0.9.x`; first public release `1.0.0`. SemVer from the tag (`v0.9.0` → `0.9.0`).
- Secrets `ZingGoogleClientId` and `ZingGoogleClientSecret` come only from Actions secrets or the local environment. Never in the repo, never echoed. PR builds run without them.
- e2e runs never contact GitHub and never construct Velopack's `UpdateManager`.
- Velopack's `VelopackApp.Build()...Run()` is the first line of `Program.Main` that can exit, before `SingleInstance`.
- Keep `Program.cs` and Settings edits small; Phase 6c (import) is editing nearby.
- Never start `Zing.exe` outside the e2e harness. Playwright only through `C:/Users/USER/AppData/Local/Temp/z6/.superpowers/sdd/phase-6-plan/pw.sh`, in the background. No clipboard. No `test:e2e:real-input`.
- Nothing is published, tagged, pushed to `main`, or submitted to winget. No GitHub settings or secrets are changed by the builder.
- Repo style: tabs, early returns, comments only for a non-obvious why, short lowercase commit messages ending with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

1. **A window opened after an update is ready**, or a second window: every window shows the pill. *Test: Task 6 opens a second window after `update-ready` and expects the pill in both.*
2. **Turning `checkForUpdates` off before the first scheduled check**: no check happens. The setting is read when the check fires, not at startup. *Test: Task 3 `Disabled_at_fire_time_skips_the_check`.*
3. **Offline, or GitHub rate-limited (60 unauthenticated requests per hour)**: no error reaches `TestHooks.Record` or the user. It retries at the next interval. *Test: Task 3 `A_failed_check_retries_at_the_next_interval`.*
4. **An install path with spaces, and a link with `&`, spaces or `%20`**: the registry command quotes both, so the whole URL reaches the running Zing as one argument. *Test: Task 4 `Command_quotes_the_exe_and_the_url`.*
5. **The pill clicked while a download is still in progress, or clicked twice**: it does nothing until the state is Ready, and it restarts once. *Test: Task 3 `Relaunch_before_ready_does_nothing` and `Relaunch_twice_restarts_once`.*

---

### Task 1: Settings keys

**Files:**
- Modify: `src/shared/settings/schema.ts`, `src/shared/settings/settings.vectors.json` (only if it lists all keys)
- Regenerate: `host/Zing.Core/Defaults/settings-schema.json`, `host/Zing.Core/Defaults/settings.json` via `npm run gen:host-defaults`
- Test: existing `src/shared/settings/*.test.ts`, `host/Zing.Core.Tests` settings vector tests

**Interfaces:**
- Produces: setting keys `checkForUpdates: boolean` (default `true`) and `defaultBrowserDismissed: boolean` (default `false`), readable in TS (`Settings`) and C# (`SettingsSchema`).

- [ ] **Step 1:** Add a vitest assertion to the existing schema test that `defaults().checkForUpdates === true` and `defaults().defaultBrowserDismissed === false`. Run `npm test -- settings`. Expected: FAIL.
- [ ] **Step 2:** Add both keys with `flag(...)` in `schema.ts`. Neither is a Krackn key, so don't add them to `kracknKeys`. Run `npm run gen:host-defaults`.
- [ ] **Step 3:** Run `npm test` and `npm run test:host`. Expected: PASS, including the vector parity tests.
- [ ] **Step 4:** Commit with message `settings: update and default-browser keys`.

### Task 2: Renderer beside the exe

**Files:**
- Modify: `host/Zing.Core/RendererLocation.cs`, `host/Zing.Host/Zing.Host.csproj` (the `RendererFolder` metadata), `host/Zing.Host/RendererView.cs` (its call site)
- Test: `host/Zing.Core.Tests/RendererLocationTests.cs`

**Interfaces:**
- Produces: `RendererLocation.Resolve(string? configured, string? built, string appFolder)`. A relative `built` is resolved against `appFolder` (`AppContext.BaseDirectory`). Also the MSBuild property `ZingRendererFolder`: when set (release builds pass `renderer`), it replaces the absolute checkout path in the `RendererFolder` metadata.

- [ ] **Step 1:** Add tests `A_relative_built_folder_is_beside_the_app` (built `"renderer"`, appFolder `_root\app` with `renderer\index.html` → returns `_root\app\renderer`) and `A_relative_built_folder_never_climbs` (built `"..\\renderer"` → throws `DirectoryNotFoundException`). Update existing calls to the new signature. Run `npm run test:host`. Expected: FAIL.
- [ ] **Step 2:** Implement it. Reject a relative path that leaves `appFolder` after `Path.GetFullPath`. Keep the existing `index.html` check. In the csproj, `RendererFolder` uses `$(ZingRendererFolder)` when it's non-empty, else today's absolute path.
- [ ] **Step 3:** Run `npm run test:host`. Expected: PASS. Run `npm run build:host`. Expected: succeeds.
- [ ] **Step 4:** Commit with message `host: load a relative renderer folder beside the exe`.

### Task 3: Updater state machine (Core)

**Files:**
- Create: `host/Zing.Core/Updates.cs`
- Test: `host/Zing.Core.Tests/UpdaterTests.cs`

**Interfaces:**
- Produces:
  - `public interface IUpdateSource { string CurrentVersion { get; } Task<string?> FindAsync(CancellationToken cancel); Task DownloadAsync(CancellationToken cancel); void RestartIntoUpdate(); void ApplyOnExit(); }`. `FindAsync` returns the newer version or null.
  - `public enum UpdateStatus { Idle, Checking, Downloading, Ready, UpToDate, Failed, Unavailable }`
  - `public sealed record UpdateState(UpdateStatus Status, string CurrentVersion, string? ReadyVersion)`
  - `public sealed class Updater(IUpdateSource? source, IScheduler scheduler, Func<bool> enabled)` with `UpdateState State`, `event Action<UpdateState> Changed`, `event Action<Exception> Failed`, `void Start()`, `Task<UpdateState> CheckNowAsync()`, `void Relaunch()`, `void Quitting()`.
  - Constants `Updater.FirstCheck = TimeSpan.FromSeconds(30)` and `Updater.Interval = TimeSpan.FromHours(4)`.
  - A null source means "not an installed build": the state is `Unavailable`, and every method is a no-op.

- [ ] **Step 1:** Write the tests with `ManualScheduler` and a `FakeUpdateSource`:
  - `Start_checks_after_thirty_seconds_then_every_four_hours`
  - `Disabled_at_fire_time_skips_the_check`: `enabled` flips to false before the first fire, so `FindAsync` is never called, and the next interval is still scheduled.
  - `A_found_update_downloads_then_is_ready`: state goes Checking → Downloading → Ready with `ReadyVersion "0.9.1"`, and `Changed` fires for each.
  - `No_update_is_up_to_date`
  - `A_failed_check_retries_at_the_next_interval`: `FindAsync` throws `HttpRequestException`, so the state is Failed, `Failed` fires once, and the next interval checks again.
  - `Ready_stops_further_checks`
  - `Check_now_ignores_the_setting`
  - `Relaunch_before_ready_does_nothing`
  - `Relaunch_twice_restarts_once`
  - `Quitting_when_ready_applies_on_exit` and `Quitting_when_not_ready_does_nothing`
  - `Without_a_source_everything_is_unavailable`

  Run `npm run test:host`. Expected: FAIL (type missing).
- [ ] **Step 2:** Implement `Updates.cs`. Only one check runs at a time: a check started while another is in flight returns the current state. No locks; it runs on the UI thread like the rest of `Zing.Core`.
- [ ] **Step 3:** Run `npm run test:host`. Expected: PASS.
- [ ] **Step 4:** Commit with message `core: updater state machine`.

### Task 4: Default-browser registration (Core)

**Files:**
- Create: `host/Zing.Core/DefaultBrowser.cs`
- Test: `host/Zing.Core.Tests/DefaultBrowserTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record RegistryValue(string Key, string? Name, string Value)`. Key is relative to HKCU; Name null is the default value.
  - `public static class DefaultBrowserRegistration` with `const string ProgId = "ZingHTML"`, `const string ClientName = "Zing"`, `IReadOnlyList<RegistryValue> Values(string exePath)`, `IReadOnlyList<string> KeysToDelete()`, and `bool IsDefault(string? httpUserChoiceProgId)`.
  - Values cover `Software\Classes\ZingHTML` (with `shell\open\command` and `DefaultIcon`), `Software\Clients\StartMenuInternet\Zing` (name, `DefaultIcon`, `shell\open\command`, and `Capabilities` with ApplicationName, ApplicationDescription, `URLAssociations` http and https, and `FileAssociations` .htm and .html, all → ZingHTML), and `Software\RegisteredApplications` value `Zing` → `Software\Clients\StartMenuInternet\Zing\Capabilities`.
  - `KeysToDelete` returns the two keys Zing owns. The `RegisteredApplications` value is removed by name; the host does that.

- [ ] **Step 1:** Write the tests:
  - `Command_quotes_the_exe_and_the_url`: exePath `C:\Users\A B\AppData\Local\ZingBrowser\current\Zing.exe`, so the command is `"C:\Users\A B\AppData\Local\ZingBrowser\current\Zing.exe" "%1"`.
  - `Every_association_points_at_the_prog_id`
  - `Registered_applications_points_at_capabilities`
  - `Is_default_only_for_zing_prog_id` (null, `"ChromeHTML"`, `"ZingHTML"`)
  - `Keys_to_delete_are_only_zings`: every key is under `Software\Classes\ZingHTML` or `Software\Clients\StartMenuInternet\Zing`.

  Also add an end-to-end-ish test that `LaunchArgs.Urls(["https://a.example/?x=1&y=2 3"])` returns the URL whole. Run it. Expected: FAIL.
- [ ] **Step 2:** Implement `DefaultBrowser.cs`.
- [ ] **Step 3:** Run `npm run test:host`. Expected: PASS.
- [ ] **Step 4:** Commit with message `core: default-browser registry values`.

### Task 5: Velopack in the host

**Files:**
- Modify: `host/Zing.Host/Zing.Host.csproj` (add `Velopack` PackageReference, pinned), `host/Zing.Host/Program.cs` (first line of `Main`; `ZingApp` owns one `Updater`; quit calls `Updater.Quitting()`)
- Create: `host/Zing.Host/Installation.cs` (Velopack hooks and registry writes), `host/Zing.Host/VelopackUpdateSource.cs`, `host/Zing.Host/FakeUpdateSource.cs`
- Modify: `host/Zing.Host/TestHooks.cs` (action `update-ready`)

**Interfaces:**
- Consumes: Task 3 `Updater` and `IUpdateSource`; Task 4 `DefaultBrowserRegistration`.
- Produces:
  - `Installation.Run()`, called first in `Main`. It wraps `VelopackApp.Build().OnAfterInstallFastCallback(Register).OnAfterUpdateFastCallback(Register).OnBeforeUninstallFastCallback(Unregister).Run()`.
  - `Installation.IsInstalled` (from `UpdateManager.IsInstalled`; always false in e2e).
  - `Installation.ExePath` (the path written to the registry; use Velopack's stable `current\Zing.exe`).
  - `Installation.IsDefaultBrowser()`, which reads `HKCU\Software\Microsoft\Windows\Shell\Associations\UrlAssociations\http\UserChoice` ProgId.
  - `ZingApp.Updates`: an `Updater`.
    - Real installs: `VelopackUpdateSource` over `new UpdateManager(ZING_UPDATE_FEED ?? new GithubSource("https://github.com/wong04/zing", null, false))`.
    - Not installed: null source.
    - e2e: `FakeUpdateSource`. Its `FindAsync` returns null until the test action `{ kind: "test", action: "update-ready", version: "0.9.1" }` makes the next `CheckNowAsync` find and "download" it. `RestartIntoUpdate` records a call the test can read back (`{ action: "update-restarts", file }`), and never restarts.
  - The `Updater.Failed` event is wired to `Faults.Warning("update check", error)`. It's a warning, not a fault, so a failed check never fails an e2e run, and a real install writes no log.

- [ ] **Step 1:** Add the package and `Installation.Run()` as the first statement in `Main`. Run `npm run build:host`. Expected: builds. Run `npm run test:e2e -- e2e/core.spec.ts` through the lock wrapper. Expected: PASS, which shows the Velopack hook doesn't disturb startup.
- [ ] **Step 2:** Implement `Installation`, `VelopackUpdateSource` and `FakeUpdateSource`. Wire `ZingApp.Updates` (`Start()` once the first window is ready; `enabled` reads `checkForUpdates`), and call `Quitting()` from the app's quit path. Use the Velopack APIs `CheckForUpdatesAsync`, `DownloadUpdatesAsync`, `ApplyUpdatesAndRestart`, and `WaitExitThenApplyUpdates`.
- [ ] **Step 3:** Run `npm run build:host`, `npm run test:host` and `npm run lint`. Expected: PASS.
- [ ] **Step 4:** Commit with message `host: velopack hooks, registration and updater`.

### Task 6: Update pill and About controls

**Files:**
- Modify: `src/shared/ipc.ts` (types `UpdateStatus` and `UpdateState` mirroring Task 3; channels `'update:get': { args: []; result: UpdateState }`, `'update:check': { args: []; result: UpdateState }`, `'update:relaunch': { args: []; result: void }`; event `'update:state': UpdateState`; plus the bridge `onUpdateState`)
- Modify: `host/Zing.Core/ChromeApi.cs` (handle the three channels through the app's `Updater`, reached via `IWindowHost`), `host/Zing.Host/MainWindow.cs` (push `update:state` to the window on `Changed`)
- Create: `host/Zing.Host/UpdatePillLayer.cs`, modelled on `NoticeLayer`. It's a transparent renderer view (route `update-pill`), placed at the content area's bottom-right with a 16 px margin, sized to the renderer's reported size, and visible only while the state is `Ready`. Create it when the window starts, so a window opened after Ready shows the pill at once.
- Create: `src/renderer/src/update/UpdatePill.tsx`, `src/renderer/src/update/update.css`
- Modify: `src/renderer/src/main.tsx` (route `update-pill`), `src/renderer/src/settings/rows.ts` (toggle `checkForUpdates` with the detail text from Global Constraints), `src/renderer/src/settings/CustomRows.tsx` (`About`: version from `UpdateState.currentVersion`, a "Check now" button that shows "You're up to date", "Downloading…", "Ready: relaunch to update" or "Couldn't check", and "Updates work in installed builds" when the status is Unavailable)
- Test: `src/renderer/src/update/UpdatePill.test.tsx`, `e2e/update.spec.ts`

**Interfaces:**
- Consumes: Task 5 `ZingApp.Updates` and the test action `update-ready`.

- [ ] **Step 1:** Write the vitest `UpdatePill renders only when ready and invokes update:relaunch once on click`. Write the e2e `update.spec.ts`:
  - The pill is hidden at start.
  - After the test action `update-ready` and Settings → About → "Check now", the pill shows "Relaunch to update".
  - A second window opened afterwards shows it too.
  - Clicking it records exactly one restart.
  - The About section shows the toggle, on by default.
  - Light and dark screenshots of the pill and of About go to `docs/screenshots/phase-8/`.

  Run vitest. Expected: FAIL.
- [ ] **Step 2:** Implement the IPC, host layer, pill, and Settings changes. Use theme tokens and the shared components only.
- [ ] **Step 3:** Run `npm test`, `npm run typecheck`, `npm run lint`, then `update.spec.ts` in the background through the lock wrapper. Expected: PASS.
- [ ] **Step 4:** Commit with message `update pill and about controls`.

### Task 7: Default-browser offer and Settings row

**Files:**
- Modify: `src/shared/ipc.ts`: `Notice` gains `defaultBrowser?: true`; new type `DefaultBrowserState { registered: boolean; isDefault: boolean }`; channels `'default-browser:get'`, `'default-browser:open-settings'` and `'default-browser:dismiss'` (sets `defaultBrowserDismissed`).
- Modify: `host/Zing.Host/NoticeLayer.cs` (`ShowDefaultBrowserOfferAsync()`, using the existing offer height and its 20 s timeout), `host/Zing.Core/ChromeApi.cs`, `host/Zing.Host/Program.cs`.
  - In `ZingApp.Ready`, after startup notices: show the offer when `Installation.IsInstalled`, Zing isn't the default, `defaultBrowserDismissed` is false, and `launchedBefore` is true. After that check, set `launchedBefore` to true.
  - **Owner's decision (2026-11-10):** Phase 6c's import nudge takes the first launch. The default-browser offer never shows on a fresh profile's first launch; it first appears on the second launch. This is judged only from Phase 8's own `launchedBefore` setting, never from 6c's nudge state.
  - In e2e, the test action `show-default-browser-offer` shows it.
- Modify: the renderer overlay notice component, which shows the "Make default" and "Don't ask again" buttons for `defaultBrowser`. A timeout is not a dismissal, so the offer comes back next launch *(design call)*.
- Modify: `src/renderer/src/settings/rows.ts` and `CustomRows.tsx`: a "Default browser" row. It shows "Zing is your default browser", or a "Make default" button that opens Settings, or "Install Zing to set it as default" when not registered.
- Test: `e2e/default-browser.spec.ts`; vitest for the row text by state.

**Interfaces:**
- Consumes: Task 5 `Installation.IsInstalled` and `IsDefaultBrowser()`, and Task 1 `defaultBrowserDismissed`.
- `default-browser:open-settings` launches `ms-settings:defaultapps?registeredAppUser=Zing` through the shell. In e2e it's recorded through the existing `ZING_DIALOGS_FILE` pattern and never launched.

- [ ] **Step 1:** Write the e2e test:
  - No offer at startup in e2e.
  - After `show-default-browser-offer`, the offer shows the text from Global Constraints.
  - "Make default" records the ms-settings launch.
  - "Don't ask again" sets `defaultBrowserDismissed`, so a relaunch on the same profile, re-triggered by the test, still shows nothing.
  - A fresh profile's first launch shows no offer, even with the test seam on, and the next launch on that profile does.
  - Light and dark screenshots.

  Write the vitest for the row. Run it. Expected: FAIL.
- [ ] **Step 2:** Implement it.
- [ ] **Step 3:** Run `npm test`, `npm run typecheck`, `npm run lint`, `npm run test:host`, then `default-browser.spec.ts` through the lock wrapper. Expected: PASS.
- [ ] **Step 4:** Commit with message `default-browser offer and settings row`.

### Task 8: Third-party notices

**Files:**
- Create: `scripts/gen-notices.mjs` with its logic in `scripts/notices.ts` (pure formatting) and the test `scripts/notices.test.ts`
- Modify: `package.json` (`gen:notices`; `build:host` runs it after `gen:reader`)
- Output: `host/Zing.Host/Assets/THIRD-PARTY-NOTICES.txt`. It's gitignored, since `Assets/` already is, and copied by the existing Assets content glob.
- Modify: `src/shared/ipc.ts` (`'about:credits': { args: []; result: string }`), `host/Zing.Core/ChromeApi.cs` (reads the file; a missing file throws `FileNotFoundException`, with no fallback), `CustomRows.tsx` (`About` gets a "Credits" button that opens a scrollable sheet with the text)

**Interfaces:**
- `formatNotices(packages: readonly NoticePackage[]): string`, where `NoticePackage = { name: string; version: string; license: string; licenseText: string; source: 'npm' | 'nuget' }`. Output is sorted by name and deduplicated by name@version. A header names Zing's own MIT licence. Fixed sections at the end name the Microsoft Edge WebView2 Runtime and the Windows App SDK, with their licence URLs.
- npm packages: production dependencies only (`npm ls --omit=dev --all --json`), with licence text from each package's LICENSE or LICENCE file.
- NuGet packages: `dotnet list host/Zing.Host package --include-transitive --format json`, with licence text from the package folder's licence file or, failing that, the nuspec `license` expression or URL.
- A package with no licence found fails the script, naming the package.

- [ ] **Step 1:** Write the vitest cases `sorts and dedupes`, `ends with the WebView2 and Windows App SDK sections`, and `throws naming a package without a licence`. Run them. Expected: FAIL.
- [ ] **Step 2:** Implement the script and the About Credits sheet. Add an assertion to `update.spec.ts` that Credits opens and contains "@mozilla/readability".
- [ ] **Step 3:** Run `npm test`, `npm run build:host`, `npm run lint` and `npm run typecheck`. Expected: PASS, and the file exists.
- [ ] **Step 4:** Commit with message `credits: generated third-party notices`.

### Task 9: Packaging script, release workflow, winget manifest

**Files:**
- Create: `.config/dotnet-tools.json` (local tool `vpk`, the same version as the NuGet `Velopack` package), `scripts/pack.ps1`, `package.json` script `"pack": "powershell -NoProfile -File scripts/pack.ps1"`
- Create: `.github/workflows/release.yml`, `winget/manifests/` (the three-file manifest set for `wong04.Zing`: version `1.0.0` placeholder, `InstallerType: exe`, `Scope: user`, silent switch `--silent`, URL template pointing at the release asset)
- Modify: `.gitignore` (`artifacts/`)

**Interfaces:**
- `scripts/pack.ps1 -Version <semver> [-Channel win]` does the following:
  1. `npm ci` is assumed already done. It runs `npm run build` and then `npm run gen:reader`.
  2. `npm run gen:notices`.
  3. `dotnet publish host/Zing.Host -c Release -r win-x64 --self-contained -p:Version=$Version -p:ZingRendererFolder=renderer -o artifacts/publish`.
  4. Copy `out/renderer` to `artifacts/publish/renderer`.
  5. `dotnet tool restore`, then `dotnet vpk pack -u ZingBrowser -v $Version -p artifacts/publish -e Zing.exe --packTitle Zing --packAuthors "Aaden Wong" --icon build/icons/icon.ico --splashImage build/installer/splash.png --framework webview2 -o artifacts/releases`.

  The OAuth values pass through from the environment if set, and are never printed.
- `release.yml` jobs:
  - `checks`: the same steps as `ci.yml`'s check job.
  - `pack`: windows-latest. It runs `pack.ps1` with the version from the tag (or `0.0.0-pr.<run>` on PRs, `0.0.0-dispatch.<run>` on dispatch), passing secrets as env only on tag and dispatch, and uploads `artifacts/releases` as an artifact.
  - `sign`: a step with `if: ${{ secrets.AZURE_SIGNING_ACCOUNT != '' }}`. Its body is a comment naming where Azure Trusted Signing or SignPath would plug in (`vpk pack --azureTrustedSignFile` or `--signTemplate`). It never runs now.
  - `release`: tag pushes only. It runs `dotnet vpk download github` (for deltas) and then `dotnet vpk upload github --repoUrl https://github.com/wong04/zing --token ${{ secrets.GITHUB_TOKEN }} --publish false --releaseName "Zing $VERSION" --tag $TAG`, which creates a **draft**. Permissions: `contents: write` on this job only.
  - `winget`: `if: false` with a comment saying it's enabled at 1.0. It would run `wingetcreate update` on a published release.
  - Triggers: `push: tags: ['v*']`, `workflow_dispatch`, and `pull_request` with `paths` covering `scripts/pack.ps1`, `.github/workflows/release.yml`, `.config/dotnet-tools.json`, `winget/**` and `host/Zing.Host/Zing.Host.csproj`.

- [ ] **Step 1:** Run `npm run pack -- -Version 0.9.0` locally, in the background. Expected: `artifacts/releases/ZingBrowser-win-Setup.exe` and a `releases.win.json` exist. Check that `artifacts/publish/Zing.exe` has file version 0.9.0, that `artifacts/publish/renderer/index.html` exists, and that `artifacts/publish/Assets/THIRD-PARTY-NOTICES.txt` exists.
- [ ] **Step 2:** Validate the workflow with `actionlint` if available (skip silently otherwise) and the manifest with `winget validate --manifest winget/manifests` if winget is present.
- [ ] **Step 3:** Commit with message `release: pack script, workflow and winget manifest`. Push the branch, which needs the owner's OK under the session rules, so the PR run builds an installer artifact. Check that its pack job is green and that no secret-dependent step ran.

### Task 10: Hand check, docs, PARITY

**Files:**
- Create: `docs/release.md`: how to cut a release (tag, draft, publish), the Actions secrets the owner sets, the signing options (Azure Trusted Signing, about $10/month; SignPath Foundation, free for open source), the winget enable step, where user data lives and that uninstall keeps it, and the `ZING_UPDATE_FEED` hand-check recipe.
- Modify: `PARITY.md`: P1 (rename to "Velopack installer"), P2, P4 and P5 Done with evidence; P3 Open with the decision; B25 Done; P6 and P7 unchanged. Update the B25 note under the B table.

- [ ] **Step 1:** Build `0.9.0` and `0.9.1` with `pack.ps1` into `artifacts/feed` (packing 0.9.1 into the same output folder makes a feed). Hand the owner the recipe:
  1. Run `artifacts/feed/ZingBrowser-win-Setup.exe` from 0.9.0.
  2. Check Default apps lists Zing, make it the default, and open a link from another app.
  3. Quit Zing, set the user env var `ZING_UPDATE_FEED` to the feed folder, launch Zing, and open About → Check now.
  4. Click the pill and confirm the version is 0.9.1 and the data is intact.
  5. Uninstall and confirm `%APPDATA%\Zing` and `%LOCALAPPDATA%\Zing` remain.
  6. Clear the env var.

  Wait for the owner's report. Don't run the installer yourself.
- [ ] **Step 2:** Run the full suite: `npm run lint`, `npm run typecheck`, `npm test`, `npm run test:host`, and `npm run test:e2e` in the background through the lock wrapper. Expected: all PASS.
- [ ] **Step 3:** Write `docs/release.md` and update PARITY with the owner's hand-check result as evidence. Commit with message `docs: release guide and phase 8 parity`.
- [ ] **Step 4:** Open the PR against `main`, after the owner's OK to push.
