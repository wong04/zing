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
3. **Done.**
   - **Expect:** the card is gone from every New Tab.
   - **Expect:** after restarting Zing on the same scratch profile, the card is still gone.

## 2. Settings → Import, bookmarks

1. Open Settings → Import.
   - **Expect:** every browser you have is listed, one row per profile, with a bookmark count.
   - **Expect:** a profile with no bookmarks file says so on its row. No row should crash the section.
2. With Chrome **running**, import a Chrome profile.
   - **Expect:** it imports. A running Chrome doesn't block the read.
3. Import the same profile again.
   - **Expect:** "Nothing new to import".
4. **Arc, if installed.**
   - **Expect:** the row shows Arc's Spaces as folders after import.
   - If Arc isn't listed, note your Arc package folder under `%LOCALAPPDATA%\Packages\` (only its name) and Arc's https ProgId from Settings → Apps → Default apps. Task 5 assumes `TheBrowserCompany.Arc_ttt1ap7aakyb4`.

## 3. Passwords from a browser

1. On a Chrome row, click **Import passwords**.
   - If the vault is off, **expect** "Importing passwords turns on Zing's password vault…". Choose **Turn on and continue**.
   - **Expect:** Chrome opens on `chrome://password-manager/settings`.
   - **Record:** did the page open directly, or did Chrome show a new tab or a blocked page? This is the open question from Task 11.
2. In Chrome, click **Export passwords**, confirm with Windows, and save to Downloads.
   - **Expect:** Zing picks the file up within a few seconds and imports it into the vault.
   - **Expect:** Zing then asks "The exported file has every password in plain text." → **Move it to the Recycle Bin**.
   - **Expect:** the CSV is gone from Downloads and is in the Recycle Bin.
3. Repeat with Edge, if installed, on `edge://wallet/passwords`, and with Brave, if installed, on `brave://password-manager/settings`. Record whether each page opened.
4. Start an import and then cancel it. Wait past 10 minutes, or save the export somewhere other than Downloads.
   - **Expect:** "Choose the file myself…" imports a file you pick, and offers the same Recycle Bin step.

## 4. Files from the Mac (Dia, Safari)

1. Export bookmarks from Dia and from Safari on the Mac. Export passwords too, if each offers it.
   - Check each `fileHelp` step in Settings → Import ("How to export from…") against the real menus.
   - **Record** any wording that doesn't match.
2. In Zing, use **Bookmarks file…** and **Passwords file…** on each export.
   - **Expect:** bookmarks land in a folder named after the file.
   - **Expect:** passwords land in the vault.
   - **Expect:** an export that isn't UTF-8 shows a named error, never garbled text.

## 5. Open copy questions from review

- **Settings nav position.** The Import category sits last, after About, because the shared lists were append-only during Phase 8. Decide whether to move it. It's a one-line change in each of three lists.
- **Damaged profile copy.** A damaged Chrome profile reads "Bookmarks looks like a bookmarks file but is damaged or incomplete.", where "Bookmarks" is Chrome's file name. Decide whether to reword it.

## After the check

- Fix anything found, with a failing test first.
- Then set I1–I4 to Done in `PARITY.md`, drop their "awaiting" markers, and close #98–#101.
- If a browser didn't open its password page in step 3, `ExportPages.Url` returns null for it, and the renderer shows that browser's menu path instead (Task 11 note).
