# Browser import: owner hand check

Phase 6c is tested only against synthetic profiles and files. This check covers what those tests can't: real profiles, real browsers and real exports. It closes PARITY I1–I4 (#98–#101).

**Before you start**
- Run Zing on a scratch profile, not under e2e. In PowerShell, from the repo root after `npm run build` and `npm run build:host`, set `$env:ZING_USER_DATA` to a new empty folder, then start `host\Zing.Host\bin\Release\...\Zing.exe`.
- Nothing from your profiles goes into the repo, an issue, or a report. Record only pass or fail, and what was wrong.
- Close Chrome, Edge and Brave before step 2, except where a step says to open one.

## 1. The New Tab nudge

1. Open a New Tab.
   - **Expect:** if Chrome, Edge, Brave or Arc is your default browser, the card reads "Bring your stuff from {browser}?". It gives the bookmark count and names your last-used profile.
   - **Expect:** if Zing (or another browser) is the default, the card reads "Bring your bookmarks from another browser?".
2. **Import from {browser}.**
   - **Expect:** the card changes to "Imported N bookmarks.".
   - **Expect:** your favourites bar holds that browser's bar items.
   - **Expect:** a folder "Imported from {browser} ({profile})" holds the rest.
   - **Expect:** Settings → Privacy now warns that imported bookmarks are written to disk.
3. Click **Import passwords too** (Chrome, Edge or Brave only). If your browser is Arc, click **Done** instead.
   - **Expect:** Settings → Import opens, already waiting for that browser's export. Cancel it there.
   - **Expect:** the card is gone from every New Tab.
   - **Expect:** after restarting Zing on the same scratch profile, the card is still gone.

## 2. Settings → Import, bookmarks

1. Open Settings → Import.
   - **Expect:** every browser you have is listed, one row per profile, with a bookmark count.
   - **Expect:** a profile with no bookmarks file isn't listed, and a damaged one shows its error on its row while the others still work.
2. With Chrome **running**, import a Chrome profile.
   - **Expect:** it imports. A running Chrome doesn't block the read.
3. Import the same profile again.
   - **Expect:** "Nothing new to import".
4. **Dia for Windows, if installed.**
   - **Expect:** one "Dia (profile)" row per profile, found in `%LOCALAPPDATA%\Packages\TheBrowserCompany.Dia_*\LocalCache\Local\Dia\User Data`, with no Import passwords button.
   - **Expect:** importing it adds an "Imported from Dia (…)" folder.
   - **Record:** whether Dia has a password export, and its menu path, so a later change can offer it.
5. **Arc, if installed.**
   - **Expect:** the row shows Arc's Spaces as folders after import.
   - If Arc isn't listed, check that `%LOCALAPPDATA%\Packages\TheBrowserCompany.Arc_*\LocalCache\Local\Arc\StorableSidebar.json` exists, and note the actual path if it doesn't.
   - If Arc is your default browser, check that the New Tab card offers Arc. If it doesn't, note Arc's https ProgId: in PowerShell, `Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice'` shows it. Zing expects it to start with `Arc`.

## 3. Passwords from a browser

1. On a Chrome row, click **Import passwords**.
   - If the vault is off, **expect** "Importing passwords turns on Zing's password vault…". Choose **Turn on and continue**.
   - **Expect:** Chrome opens in that row's profile, with no profile picker, whether or not Chrome was already running.
   - Chromium drops a `chrome://` address given on its command line (`startup_tab_provider.cc` keeps web pages and the reset-settings page only), so Chrome likely shows a plain new tab. Zing's flow then reads "If Chrome shows a new tab instead, open ⋮ menu > Passwords and autofill > Google Password Manager > Settings."
   - **Record:** did the page open directly, and does that menu path match Chrome's real menus?
2. In Chrome, click **Export passwords**, confirm with Windows, and save to Downloads.
   - **Expect:** Zing picks the file up within a few seconds and imports it into the vault.
   - **Expect:** Zing then asks "The exported file has every password in plain text." → **Move it to the Recycle Bin**.
   - **Expect:** the CSV is gone from Downloads and is in the Recycle Bin.
3. Repeat with Edge and Brave, if installed, and with a second Chrome profile ("Profile 1"). Record whether each opened in the right profile, whether the page opened, and whether the menu path Zing shows matches.
4. Start an import and then cancel it. Wait past 10 minutes, or save the export somewhere other than Downloads.
   - **Expect:** "Choose the file myself…" imports a file you pick, and offers the same Recycle Bin step.

## 4. Files from a Mac (Dia, Safari, Arc)

1. Export bookmarks and passwords from Dia, Safari and Arc on the Mac, following the steps under each row in Settings → Import → From a Mac.
   - **Record** any step that doesn't match the real menus. Dia's password step is unconfirmed: note where Dia actually keeps passwords.
2. In Zing, use each row's **Bookmarks file…** and **Passwords file…** on its export.
   - **Expect:** bookmarks land in a folder named after the file.
   - **Expect:** passwords land in the vault.
   - **Expect:** an export that isn't UTF-8 shows a named error, never garbled text.

## 5. Open copy questions from review

- **Settings nav position.** The Import category sits last, after About, because the shared lists were append-only during Phase 8. Decide whether to move it. It's a one-line change in each of three lists.
- **Damaged profile copy.** A damaged Chrome profile reads "Bookmarks looks like a bookmarks file but is damaged or incomplete.", where "Bookmarks" is Chrome's file name. Decide whether to reword it.

## After the check

- Fix anything found, with a failing test first.
- Then set I1–I4 to Done in `PARITY.md`, drop their "awaiting" markers, and close #98–#101.
- If a menu path in step 3 is wrong, fix it in `exportMenuPath` (`src/renderer/src/settings/importText.ts`).
