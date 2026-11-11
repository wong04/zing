# Releasing Zing

Zing ships as a per-user Velopack installer (`ZingBrowser-win-Setup.exe`), updates itself from GitHub Releases, and will be on winget from 1.0. The design is in [the Phase 8 spec](superpowers/specs/2026-11-10-phase-8-packaging-design.md).

Versions `0.9.x` are internal builds. The first public release is **1.0.0**, and nothing is published before then: no release, no winget submission.

## Actions secrets

Set these in GitHub under Settings → Secrets and variables → Actions. Never commit them, and never paste them into a script or an issue.

| Secret | Used for | Without it |
|---|---|---|
| `ZING_GOOGLE_CLIENT_ID` | Sync's Google sign-in. `release.yml` passes it to the build as `ZingGoogleClientId`. | The build has no sync. |
| `ZING_GOOGLE_CLIENT_SECRET` | The same, as `ZingGoogleClientSecret`. | The build has no sync. |
| `AZURE_SIGNING_ACCOUNT` | Turns on the sign step (see [Signing](#signing)). | Unsigned build. Leave it unset for now. |
| `WINGET_TOKEN` | The winget job, from 1.0 (see [winget](#winget-at-10)). | Not needed until then. |

Pull-request runs never get the secrets. For a local build with sync, set `ZingGoogleClientId` and `ZingGoogleClientSecret` in your own environment before `npm run pack`; the script never reads or prints them.

## Cutting a release

1. Make sure `main` is green.
2. Tag and push: `git tag v1.0.0` then `git push origin v1.0.0`. The tag must be `v<semver>`; the version comes from it (`v1.0.0` → `1.0.0`).
3. The `release` workflow runs the checks, downloads the latest published release (so vpk can build a delta package), packs, and uploads everything as a **draft** release named "Zing 1.0.0".
4. Open the draft on GitHub, write the notes, and **publish it by hand**. A prerelease tag (`v1.1.0-beta.1`) is uploaded the same way; tick "Set as a pre-release" when publishing.

Installed copies only see published releases, and never prereleases, so a draft reaches nobody.

To build locally: `npm ci`, then `npm run pack -- -Version 0.9.0`. The output is `artifacts/releases/` (`ZingBrowser-win-Setup.exe`, `ZingBrowser-win-Portable.zip`, the full `.nupkg`, a delta `.nupkg` when an earlier version is in the folder, and `releases.win.json`). Packing a second version into the same folder makes a local update feed.

## Signing

Builds are unsigned for now, so SmartScreen warns on first install. The decision is to sign later, with one of:

- **Azure Trusted Signing**: about $10/month, and Microsoft checks your identity first.
- **SignPath Foundation**: free for open-source projects, after they accept the project.

To turn it on:

1. Set up the account with either provider.
2. In `release.yml`, replace the placeholder `Sign` step with the real setup, and pass the signing options through `scripts/pack.ps1` to `vpk pack`: `--azureTrustedSignFile metadata.json` for Azure Trusted Signing, or `--signTemplate "<command> {{file}}"` for SignPath.
3. Add the `AZURE_SIGNING_ACCOUNT` secret (any non-empty value turns the step on; the step reads it through the job's env, since a step's `if:` can't read secrets).

## winget at 1.0

The manifest is in `winget/manifests/` (`wong04.Zing`) and passes `winget validate --manifest winget/manifests`. Its `InstallerSha256` is a placeholder until 1.0.0 exists.

1. After publishing 1.0.0, put the SHA256 of its `ZingBrowser-win-Setup.exe` in `wong04.Zing.installer.yaml`, run `winget validate --manifest winget/manifests` again, and submit the first version by hand (`wingetcreate submit winget/manifests`, or a PR to `microsoft/winget-pkgs`). `wingetcreate update` only works once the package exists there.
2. Add the `WINGET_TOKEN` secret: a GitHub token that can fork and open PRs on `microsoft/winget-pkgs`.
3. In `release.yml`, change the `winget` job's `if: false` to run on tag pushes. That job runs right after the draft is uploaded, so the Setup.exe URL is only live once the draft is published: publish the draft first, or re-run the job after publishing.

## Updates

An installed Zing asks GitHub for the latest published release about 30 seconds after it starts, then every 4 hours. It sends nothing about the user. A found update downloads in the background, and a "Relaunch to update" pill appears; the update applies when the pill is clicked or Zing quits.

Settings → About has **Check now** and the **Check for updates** switch, which turns the automatic check off. A build that isn't installed (a dev build, an e2e run) never checks.

`ZING_UPDATE_FEED`, a folder path in the environment, replaces GitHub as the update source. It is for testing only.

## Where data lives

| Folder | What |
|---|---|
| `%LOCALAPPDATA%\ZingBrowser` | The app itself (Velopack's install folder; the pack ID is `ZingBrowser`). Uninstall deletes it. |
| `%APPDATA%\Zing` | Settings and anything the user opted to keep. |
| `%LOCALAPPDATA%\Zing` | Local data: the WebView2 profile, engines, DNS backup. |
| `%LOCALAPPDATA%\velopack` | Velopack's own install and update log (`velopack.log`). It holds lifecycle lines only (install, update, uninstall), never browsing data. |

Uninstall removes the app and its browser registration, and **keeps** `%APPDATA%\Zing` and `%LOCALAPPDATA%\Zing`. Delete those two folders by hand to remove everything.

## Keeping Velopack in step

The `Velopack` NuGet package (`host/Zing.Host/Zing.Host.csproj`) and the `vpk` tool (`.config/dotnet-tools.json`) must be the same version (both `1.2.161` now). Dependabot's `velopack` group bumps both in one PR; check that PR has both, then run `npm run pack` once.

## Hand check

The installer can only be checked by hand, on a real install. Run it on a PC where Zing isn't installed yet. It needs two things in the checkout's `artifacts\` folder:

- the 0.9.0 installer: `artifacts\setup-0.9.0\ZingBrowser-win-Setup.exe`
- the update feed holding 0.9.0 and 0.9.1: `artifacts\releases` (use its full path below)

To build them: `npm run pack -- -Version 0.9.0`, copy `artifacts\releases\ZingBrowser-win-Setup.exe` to `artifacts\setup-0.9.0\`, then `npm run pack -- -Version 0.9.1`. The second pack overwrites the Setup.exe in `artifacts\releases`, which is why the 0.9.0 one is copied out first.

1. **Install 0.9.0.** Run the 0.9.0 installer above. Expect no admin prompt, and Zing to open when it finishes. Settings → About shows 0.9.0. Then change a setting you will recognise (a theme, say), so there is data to check in step 7. Zing keeps no tabs by default, so tabs are not the check.
2. **Check Default apps and Open with.** Windows Settings → Apps → Default apps lists Zing. In File Explorer, right-click any `.html` file → Open with: Zing is in the list.
3. **Make Zing the default.** In Default apps → Zing, set it as the default (or at least for HTTPS and HTTP).
4. **Open a link from another app.** Click a link in Mail, a PDF or a chat app. It opens in Zing.
5. **Quit Zing and set `ZING_UPDATE_FEED`.** Close every Zing window. Then Start → "Edit environment variables for your account" → under "User variables", New → name `ZING_UPDATE_FEED`, value the update feed path above → OK → OK. Or in PowerShell: `[Environment]::SetEnvironmentVariable('ZING_UPDATE_FEED', '<feed path>', 'User')`.
6. **Launch Zing, then Settings → About → Check now.** Start Zing from the Start menu, so it picks up the new variable.
7. **See the pill, click it, and confirm 0.9.1 with data intact.** The "Relaunch to update" pill appears. Click it; Zing restarts once. Settings → About shows 0.9.1, the setting from step 1 is still there, and Zing is still the default browser.
8. **Uninstall, and check what stays and what goes.** Settings → Apps → Installed apps → Zing → Uninstall. Then:
	- `%APPDATA%\Zing` and `%LOCALAPPDATA%\Zing` are still there.
	- `%LOCALAPPDATA%\ZingBrowser` is gone, and Zing is gone from Default apps.
	- In `regedit`, under `HKEY_CURRENT_USER`, these are gone: the keys `Software\Classes\ZingHTML` and `Software\Clients\StartMenuInternet\Zing`, the `Zing` value in `Software\RegisteredApplications`, and the `ZingHTML` value in `Software\Classes\.htm\OpenWithProgids` and `Software\Classes\.html\OpenWithProgids`.
9. **Clear `ZING_UPDATE_FEED`.** The same dialog as step 5 → select it → Delete → OK. Or `[Environment]::SetEnvironmentVariable('ZING_UPDATE_FEED', $null, 'User')`.

Record the result (date, and any step that failed) in PARITY.md's Phase 8 note, then mark P1, P2 and B25 Done.
