# Phase 6: the Toolbox

Rows X1–X23 in `PARITY.md`, reduced to what the owner will use, plus a page of links to outside tools. Today the rail's Toolbox button shows "The Toolbox isn't implemented yet" (`ShellModel.ToolboxNotice`), and much of the ported logic has no caller.

## Why

The owner wants one place for the things they keep leaving the browser to do: convert a file, open a phone's photo or video, save something from YouTube, get past a blocked site on a school or work network, and do small jobs like a sum or a password. Each of those means another app or a sketchy website today. Files should stay on the owner's machine.

The owner picked from Krackn's Toolbox rather than keeping all of it, so this phase is no longer full parity with Krackn. Anything cut is marked Dropped in `PARITY.md` with its reason, so the parity audit (M9) still adds up.

## Decided by the owner (2026-11-03)

**Keep:**

- **Toolbox page,** with search and recent tools.
- **Convert files on the machine.** Nothing is uploaded.
- **PDF tools:** merging, editing and similar jobs, working with the file converter. *New, not in Krackn. What "editing" covers is my reading; see goal X25.*
- **File viewer** that opens more than Windows does. Starting list: HEIC and HEIF; MOV, MKV, AVI and other formats FFmpeg reads; RAW camera photos if the engine handles them well; everything Chromium opens already.
- **Background remover,** using a ready-made model. No training.
- **Media download and Save page,** including YouTube to MP3 and MP4.
- **Link inspection.**
- **Graphing calculator, calculator, developer tools, text and design tools.**
- **Password generator and link cleaner.**
- **Links to useful tools,** such as Figma, Notion and AI chatbots.
- **Removing the tracking tags AI tools add to links,** automatically.
- **Dragging files between Zing's tools.** Dropping a file onto a web page is not required; copying it to the clipboard is enough.
- **Getting past blocked sites:** Open a Link, Diagnose and encrypted DNS. The networks that matter are school and work networks, on a computer where the user has full access.

**Drop:**

- **A real VPN or proxy in Zing:** both the WireGuard client and the "your own proxy" setting. *My reading of "ditch the other methods"; the owner then moved on without correcting it.*
- **Media bias ratings.** The owner uses a Chrome extension for that.
- **3D models in the viewer.**
- **The "No AI" statement** in `README.md` and `docs/PLAN.md`. Zing's own stance changes: a local background model and links to chatbots are fine.

**Later:** extension support waits until after v1.

## Dropped rows in `PARITY.md`

| Row | Why |
|---|---|
| X12 Domain reputation | The owner didn't choose it |
| X13 Download checks | The owner didn't choose it; Windows does part of this |
| X14 System posture | The owner didn't choose it |
| X15, part | 3D models |

Add new rows for the links page and for the engine downloader (see X10 below).

## Goals

### 1. Toolbox page and small tools (PR A)

- **X22 Toolbox page.** `src/shared/toolbox/catalog.ts` and `toolboxSearch.ts` exist and are tested. Build the page and replace `ToolboxNotice`. Recents use the existing `recentFeatures` setting and rail list.
- **Links page (new row).** Tiles that open a site in a normal tab. Defaults: Desmos, Figma, Notion, ChatGPT, Claude, Gemini. The user can add and remove them, and the list is kept like other settings, since the user chose to edit it. *My call.* No sign-in exception: with Zing's default profile, a chatbot asks for sign-in each session unless `persistentProfile` is on. Say so on the page.
- **X17 Graphing calculator,** built in, using `src/shared/math/graphEngine.ts`. *My reading: the owner listed it among the things they'll use, after first mentioning it as a link. Desmos is on the links page as well.*
- **X18 Calculator, units, number bases.**
- **X19 Developer tools.** Logic is in `src/shared/devtools`.
- **X20 Text and design tools,** as Krackn has them.
- **X21 Password generator and link cleaner.**
- **AI link tags.** `LinkCleaner.StripTrackers` already removes every `utm_*` parameter on navigation, which covers `utm_source=chatgpt.com`. Add test vectors for the tags AI tools really use, and add any that aren't `utm_*` after checking what those tools send. *My reading of "automatic AI UTM parameter remover": rules, not AI.*

All of these work offline, and none starts a process.

### 2. Getting past blocks (PR B)

- **X1 Open a Link** tries Krackn's methods in order and shows which one worked: direct, DoH with IP pinning, reader relay, Wayback archive.
- **X2 Relayed pages,** with subresources through the relay. As in Krackn, content sites work and full web apps don't; when that happens Zing says so.
- **X3 Diagnose:** compares DNS answers, tries TCP to 443, tries a TLS handshake with the real SNI. Uses `BlockDiagnostics` logic already ported to TypeScript.
- **Offer from a failed load.** When a page fails to load, the notice offers Open a Link and Diagnose. Nothing is tried without a click. *My addition, from the conversation.*
- **X4 DoH provider setting.**
- **X5 Network & VPN:** a one-click script for encrypted DNS across Windows, shown before it runs and needing the admin prompt. Keeps Krackn's short guide to getting a VPN, and its "what beats what" table.
- **X11 Link inspection,** using `threatModel.ts`, entirely on the machine. As in Krackn: on by default with a setting to turn it off, it checks each navigation before load, warns on a flagged link, and lets the user visit anyway, without warning again for that host this session. It must not noticeably slow navigation. Krackn's false-positive cases stay under test. *My call: following Krackn.*

Rules:

- The reader relay and the Wayback archive are third parties that see the address, and the relay sees the page. Ask before the first use of either in a session, and say why. *Consent covering both is my call.*
- Zing never sends cookies or sign-in details to a relay or archive, so they only ever get what an anonymous visitor would. Pages that need a sign-in will fail there, and Zing says so. *My call: Zing can't tell in advance that a page needs a sign-in.*
- Open a Link's "direct" method is a real attempt whose result is shown. The failed-load offer appears on DNS, connection, TLS and timeout failures, not on HTTP error statuses, so ordinary site errors don't suggest getting around a block. A filter's own block page looks like a normal page, so Diagnose and Open a Link stay reachable from the Toolbox. *My call.*
- The DNS script covers the adapters in use and has an Undo on the same page that puts the previous settings back. *My call: without it, a bad change could cut the network with no way back in Zing.*
- A relayed page is untrusted. It loads on its own origin, with no access to Zing's bridge.
- Say once, plainly, that routing around a school's or employer's filter may break their rules. Krackn's README has the wording. After that it's the user's call.
- Bot checks are not bypassed, and Zing does not imitate Chrome.

### 3. Files and media (PR C)

- **X10, changed: engines on first use.** Zing's installer stays small. FFmpeg, Pandoc, yt-dlp and the background model each download the first time a tool needs them, into `%LOCALAPPDATA%` beside Zing's other data, not winget. Each comes from its project's own versioned release, with the URL and SHA-256 pinned in Zing; Zing hosts nothing itself. A failed check or a blocked download is an explicit error that names what failed and offers a link to get it by hand. Settings lists the engines with sizes and a remove button. yt-dlp breaks when YouTube changes, so it updates on the user's click to its latest release, checked against the SHA-256 file published with that release. *First use over bundling, and these sources, are my calls.*
- **X6–X8 Convert.** Images, documents, audio and video, with Krackn's format matrix where the engines allow. Output never overwrites a file; collisions get numbered (`DownloadPolicy`).
- **X15 File viewer.** Formats Chromium can't show are converted to a temporary copy with the same engines, then shown. Temporary copies go when the file closes, and leftovers go at next start. Long videos may take a moment before they play.
- **X16 Background remover.** Runs a pre-trained model on the machine through ONNX Runtime. Nothing is uploaded.
- **X9 Media download and Save page.** yt-dlp with live progress; MP3 and MP4. Started only when the user asks. Save page uses the relay when the site is blocked.
- **X25 PDF tools (new row).** On the machine. Merge files; split; reorder, rotate and delete pages; add text, images and a signature on top of a page; fill forms. Existing text can't be changed in place, because a PDF stores positioned glyphs rather than flowing text. It works with Convert: images and documents become PDFs to merge, a PDF can become images, and results drag between the two. PDF to Word isn't offered, because Pandoc can't read PDFs. *My reading of "editing": the owner can widen it.*
- **X23 Drag between tools.** Uses `dropRouter`, already ported. Results can also be copied to the clipboard as a file, to paste into a web page.

## Non-goals

- A VPN, proxy or WireGuard client.
- Extension support.
- Media bias ratings or any lookup of visited sites on an outside server.
- Accounts or sign-in handling for linked sites.
- Automatic background updates of engines.

## Constraints

- **Privacy:** nothing about what the user does in a tool is stored. Inputs and outputs live in memory, except files the user saves and the viewer's temporary copies. Stored data is opt-in and flips the privacy banner; engines hold no personal data, so they don't count, but the links list and recents follow existing settings rules.
- **No network until asked.** A tool that needs no network makes no request. Opening the Toolbox makes none.
- **Speed:** every tool is code-split and loads on first open. The Toolbox adds nothing to startup. Keep S10's budgets.
- **Engines are separate programs.** Zing is MIT. Check each engine's licence and how it's obtained, and for FFmpeg which build, including HEVC patent questions and MP3 encoding, before pinning it. Model licence must allow this use.
- **Untrusted input:** file names and URLs reach engines as arguments, never through a shell. A URL can't be read as an option.
- **Layers:** logic with no Windows dependency stays in `src/shared`, as the ported tools do. Processes, sockets, files and engine downloads live in the C# host, behind the bridge. Pinned TLS (DoH with IP pinning) needs care in .NET: test it against a real CDN early.
- **Errors are explicit.** No silent fallbacks: if a method or engine fails, the tool says which and why.
- **Tests must not disturb the user:** off-screen, no focus, no dialogs. e2e must not download engines from the internet; use local fixtures.

## Left to the builder

- How tools are shown: a `zing://toolbox` tab, tools in tabs, with floating panels (`ToolPanels`) kept for popping out. *My suggestion.*
- Image library for HEIC and RAW, and whether FFmpeg alone is enough.
- Which reader relay service, starting from Krackn's, checking that it still works.
- Which background model, subject to its licence and a download size under about 200 MB.
- PR order is A, then B and C, which are independent of each other and can run in parallel. A short throwaway test of the riskiest pieces comes first: pinned TLS, relayed pages on their own origin, and a HEVC MOV playing through a temporary copy. If one fails, stop that piece and bring the owner the options; don't quietly build less. *My call.*

## Done means

- Every kept row is Done in `PARITY.md` with a test or screenshot; dropped rows say why.
- `README.md` and `docs/PLAN.md` no longer say "No AI", and the README's feature text matches what shipped.
- The owner has checked by eye: a real iPhone HEIC and MOV, a YouTube MP3 and MP4, and a blocked site on a school or work network.
