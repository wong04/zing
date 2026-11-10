# Phase 8: packaging and release (design)

## Intent

The owner wants to install Zing as their everyday browser: one `ZingSetup.exe`, and from then on Zing keeps itself up to date. When an update is ready, a "Relaunch to update" indicator appears in the bottom-right corner, and one click restarts Zing on the new version.

Nothing is posted publicly yet. The first public release is **1.0.0**, tagged by the owner once every phase is done. Until then, Phase 8 builds and proves the whole pipeline locally: the owner installs a `0.9.0` build and updates it to `0.9.1` from a local folder.

Covers `docs/PLAN.md` phase 8 and `PARITY.md` M8 rows P1–P5, and finishes B25.

## Goals

1. **Installer (P1).** A Velopack per-user installer, `ZingSetup.exe`. No admin prompt. Installs under `%LOCALAPPDATA%\ZingBrowser`. The Velopack pack ID is **`ZingBrowser`** *(my call)*, not `Zing`: Velopack installs to `%LOCALAPPDATA%\<packId>` and deletes that folder on uninstall, and `%LOCALAPPDATA%\Zing` already holds Zing's local data (WebView2 profile, engines, DNS backup). The pack ID is permanent once released (it keys updates and the winget package), so it is fixed here. The display name everywhere is still "Zing". It adds a Start menu shortcut and an uninstall entry, and launches Zing when done. Uses the existing art in `build/installer` (splash; the MSI bitmaps are unused unless an MSI is also produced, which is not a goal).
2. **WebView2 runtime on Windows 10.** The installer installs the Evergreen WebView2 runtime when it's missing (Velopack's `webview2` prerequisite). The app's existing check (`EngineRuntime` in `Program.cs`) stays as the fallback.
3. **Auto-update (P2).** Velopack `UpdateManager` with a GitHub Releases source (`wong04/zing`, published releases only, no prereleases).
   - Checks shortly after startup and then every 4 hours *(builder may tune)*. Downloads the update silently.
   - When an update is downloaded, every open window shows a small "Relaunch to update" pill in its bottom-right corner. Clicking it applies the update and restarts Zing (tabs come back only if Restore tabs is on, as for any quit).
   - If ignored, the update applies when Zing next quits (Velopack's wait-exit-then-apply).
   - A Settings toggle, "Check for updates automatically", **on by default**. Its detail text says it asks GitHub for the latest version and sends nothing about the user. The About section shows the current version and a "Check now" button.
   - Update failures (offline, GitHub down) are silent to the user, retried at the next interval, and recorded through the host's normal error path in test runs. They never block startup.
   - A local feed override for testing: an environment variable (`ZING_UPDATE_FEED`, a folder path) points the updater at a local Velopack release folder in place of GitHub. This is how the 0.9.0 → 0.9.1 hand check works.
4. **Default browser (B25).**
   - The installer (Velopack install and update hooks) registers Zing as a browser for the current user: `HKCU\Software\Clients\StartMenuInternet\Zing` with Capabilities, `RegisteredApplications`, and a ProgID for http, https, .htm and .html. The uninstall hook removes it. So Zing appears in Windows' Default apps and "Open with".
   - On each launch of an **installed** build *(my call)*, while Zing isn't the default for http and the bar hasn't been dismissed, a dismissible bar asks "Make Zing your default browser?". Not shown in dev builds (not registered, so the Settings page would have nothing to show) or in e2e runs unless a test hook turns it on. Its button opens Windows Settings at Zing's Default apps page (`ms-settings:defaultapps?registeredAppUser=Zing`); Windows doesn't let apps set themselves. Dismissing it records that in settings, and it doesn't come back.
   - Settings gets a "Default browser" row: shows whether Zing is default, with the same button.
   - Links from other apps already work in a running Zing (`SingleInstance`, `LaunchArgs`, `LinkInbox`). The registry command line must pass the URL so they reach it.
5. **Code signing (P3): later.** No signing now. The release workflow has one signing step that does nothing unless signing secrets exist, so the owner can turn it on later with Azure Trusted Signing or SignPath Foundation (free for open source) without reworking anything. P3 stays Open, with the options written in its issue.
6. **winget (P4).** A manifest (`winget/` folder, current schema, per-user installer type matching Velopack's). A workflow step to submit it on a published release exists but is **disabled** until 1.0. Nothing is submitted to `microsoft/winget-pkgs` in this phase.
7. **Credits (P5).** A third-party notices file generated at build time from the npm packages bundled into the renderer and the NuGet packages shipped with the host, plus Microsoft's WebView2 and Windows App SDK notices. Shipped next to `Zing.exe` and shown from Settings → About ("Credits"). Generation runs in CI so it can't go stale.
8. **Release workflow.** `.github/workflows/release.yml`:
   - On a pushed tag `v*`: run the checks, build the renderer, publish `Zing.exe` self-contained with the version from the tag, pack with `vpk`, and create a **draft** GitHub release with the Velopack assets. The owner publishes it by hand; nothing reaches the updater until then.
   - Also `workflow_dispatch`, which builds the installer as a workflow artifact and creates no release.
   - Also on pull requests that touch packaging files *(my call)*: build and pack only, **without** the OAuth secrets (so no sync), uploading the installer as an artifact. This is how the workflow is proven before it's on `main`, since `workflow_dispatch` only works for workflows already on the default branch.
   - `ZingGoogleClientId` and `ZingGoogleClientSecret` come from Actions secrets the owner sets (passed as MSBuild properties or environment). Never in the repo, never printed. A build without them still succeeds, without sync, as today.
   - These values end up inside the shipped `Zing.dll` (the csproj bakes them in, Ruling 48). That's expected: Google treats an installed app's client secret as non-confidential. "No secrets in artifacts" means no deploy tokens, signing keys or other credentials, and no secrets in logs.
9. **Local packaging script.** One command (`npm run pack`, wrapping a script) that builds and packs an installer locally with a given version, so the owner and the hand check don't depend on CI.

## Non-goals

- Publishing any GitHub release, tag, or winget submission in this phase.
- Signing now; paying for anything.
- Beta or preview update channels; delta-only updates (Velopack's defaults are fine).
- An MSI, a portable zip as a supported product, the Microsoft Store, or MSIX.
- P6 (web-compat smoke tests in CI) and P7 (dependency updates): left Open for later *(my call; tell me if you want them in)*.
- An uninstaller option to delete profile data. Uninstall removes the app and leaves `%APPDATA%\Zing` and `%LOCALAPPDATA%\Zing` data alone; the Credits/About text or README says where it lives.

## Anti-goals

- An update that loses the user's data (settings, vault, library, sessions). Install location changes; `AppPaths` locations don't.
- Any update check or network call in e2e runs or before the user could turn it off (respect the toggle on the very first check).
- An installed Zing that loads its renderer from a build checkout. Today `RendererFolder` is an absolute path baked in at build time; an installed build must load the renderer shipped beside `Zing.exe`, and must never search parent folders (keep `RendererLocation`'s rule).
- A Zing that updates while the owner is mid-task without asking (restart happens only on click or on quit).
- Secrets in the repo, logs or artifacts.

## Constraints

- Follow `CLAUDE.md` and `AGENTS.md`: logic in `Zing.Core` with xUnit tests; `Zing.Host` stays thin; optional features never fail startup; every `await` resumes on the UI thread; delayed work uses `IWindowHost.Scheduler`; check views still exist after awaits; no handlers that hide errors from `TestHooks`.
- Velopack's startup hook (`VelopackApp.Build()...Run()`) must run first in `Main`, before `SingleInstance`. Keep `Program.cs` changes small: Phase 6c (import) is editing nearby in parallel.
- e2e: never start `Zing.exe` outside the harness; Playwright only through the lock wrapper; e2e runs never contact GitHub (the updater is off or pointed at a test feed under `ZING_E2E`).
- UI changes need light and dark screenshots for parity evidence. The update pill and default-browser bar use the shared components and theme tokens.
- Versions are SemVer. Internal builds are `0.9.x`; the first public release is `1.0.0`.
- The owner approves anything that publishes, spends money, creates cloud resources or changes GitHub settings (including adding secrets, which they do themselves).

## Done

- PRs ready to merge, with tests: updater state logic and registry/registration logic in `Zing.Core` (xUnit), the pill and the bar in e2e with screenshots, the release workflow's PR build producing an installer artifact.
- Hand check by the owner: install `0.9.0` from `ZingSetup.exe`, see Zing in Default apps and make it default, open a link from another app, then with `ZING_UPDATE_FEED` pointing at a folder holding `0.9.1`, see the pill, click it, and land on `0.9.1` with data intact. Then uninstall and confirm data stays.
- `PARITY.md`: P1, P2, P4, P5 Done with evidence; P3 Open with the decision recorded; B25 Done.

## Left to the builder

- Exact update interval and first-check delay; Velopack version; `vpk` flags.
- How the host tells the renderer an update is ready (an existing host→renderer message pattern), and pill placement details within the bottom-right corner.
- The licence-collection tooling for npm and NuGet.
- How the release build passes the renderer folder (relative property, `ZING_RENDERER_DIR`-style default, or similar), as long as the anti-goal holds.
- Whether the "Check now" button reports "You're up to date" inline.
