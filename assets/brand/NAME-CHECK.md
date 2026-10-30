# Name check: "Zing"

Checked 2026-10-30 with web search plus a local `winget search` (winget and Microsoft Store sources).

> I'm not a lawyer. This is a quick collision scan, not a clearance search or legal advice.

## Verdict: medium-high risk

Nobody ships a browser called Zing, and winget has no `Zing` package id. The problem is the trademark register: **ZING is already a registered software mark** (Azul Systems' JVM), several other live ZING filings cover downloadable software (Nice classes 9 and 42), and the Microsoft Store already lists a privacy-themed app called "Zing". For a hobby or open-source release this is probably workable. If Zing is ever sold, put in the Store under that name, or filed as a trademark, expect the plain word ZING in class 9 to be refused or opposed.

## Findings

### Browsers and Windows software

| What | Relevance | Source |
| --- | --- | --- |
| No browser named "Zing" found | A "zing browser" search turns up only unrelated apps | (search) |
| **Microsoft Store: "Zing – Private Chat Messenger"** (Store id `9MVPPH9DQMN6`) | Same store, same "private" positioning. This is the most direct consumer-facing clash on Windows | `winget search zing` (msstore source) |
| winget: no `Zing` id or similar | `Zing.Zing` / `*.Zing` look free | `winget search zing` (winget source) |
| "Zing!" Chrome extension (v0.9.2) | Lives in the browser-extension space | https://zing.en.softonic.com/chrome/extension |
| Zing Network (1998): "free software… while Web pages are downloading" | Dead (its USPTO mark lapsed in 2008), but it was web-browsing software | https://www.latimes.com/archives/la-xpm-1998-jul-13-fi-3283-story.html · https://www.trademarkelite.com/trademark/trademark-detail/75382795/ZING |

### Trademarks (software classes)

| Mark / owner | Class / goods | Status | Source |
| --- | --- | --- | --- |
| **ZING** (Azul Systems): Java runtime, now "Azul Zing Builds of OpenJDK" under Azul Prime | Software | Registered "in the U.S. and elsewhere", per Azul | https://www.azul.com/products/components/azul-zing-builds-of-openjdk |
| ZING (OPIO Connect, Inc.) reg. 7503368 | 9, 10 | Registered | https://trademarkdir.com/trademark/zing-97550326 |
| ZING (Optiks Solutions, Inc.) ser. 97051117 | 9, 42: downloadable software, mobile apps | Live / pending | https://markinton.com/trademark/zing-97051117 |
| ZING (Gls Co., Ltd.) ser. 97413799 | 9: NFC / wireless content delivery software | Live application | https://uspto.report/TM/97413799 |
| ZING (Zing Tech Inc.) ser. 97132920 | 9: ordering-app software | Live application | https://uspto.report/TM/97132920 |
| ZING APPS (Zing Apps LLC) reg. 5328637 | 9: application software for phones and computers | Registered (a cancellation attempt was terminated in 2020) | https://trademarks.justia.com/873/94/zing-87394848.html |
| ZING (KMA Concepts / Zing Toys) | 28, 35, 40 (toys); WIPO designations include EUIPO | Registered | https://www.trademarkelite.com/wipo/trademark/trademark-detail/1351381/ZING |

I couldn't query EUIPO eSearch or TMview directly (they are script-only search apps). The only EU coverage I found is the KMA Concepts toy mark, which is outside software. **Before committing, run a proper TMview search for ZING in classes 9 and 42**: https://www.tmdn.org/tmview/

### Prominent brands with the name

| Brand | Notes | Source |
| --- | --- | --- |
| **Zing (VNG Corporation, Vietnam)** | A household tech brand in Vietnam: Zing MP3 (Vietnam's #1 music app since 2007), Zing TV, the zing.vn portal. Android package ids use `com.zing.*` (for example `com.zing.zalo`) | https://www.vng.com.vn/aboutvng.html?lang=en · https://play.google.com/store/apps/details?id=com.zing.mp3 |
| **ZingChart / ZingSoft** | Commercial JS charting library, on npm and NuGet as `zingchart` | https://www.zingchart.com/ · https://github.com/zingchart/ZingChart |
| Zing (Microsoft Research) | Model checker used for Windows driver development. Old research project, low consumer visibility | https://www.microsoft.com/en-us/research/project/zing |
| Zing Coach | "AI coach" fitness app (iOS). Unrelated field, but it ties "Zing" to AI, which Zing (the browser) is avoiding | https://apps.apple.com/jp/app/id1552207792 |
| Zing Data, Zing! (education app), Zing Trade (Chrome extension) | Smaller software products. They add search noise | https://chromewebstore.google.com/detail/zing-trade/epibngeobhgllkfbeghnemppimnljhfb · https://apps.apple.com/au/app/zing-app/id1590313367 |

### Practical consequences

- **Searchability is poor.** "zing" results are dominated by VNG, ZingChart and Azul. "Zing browser" is free as a phrase, though.
- **Microsoft Store listing:** a second "Zing" next to "Zing – Private Chat Messenger" invites confusion and possibly a name-reservation conflict.
- **Package ids:** `Zing.Browser` (winget) and a reverse-DNS app id are available. Avoid `com.zing.*`, which is VNG's namespace on Android.

## Alternatives

These are quick checks (web search plus `winget search`, which also covers the Microsoft Store), not clearance searches.

| Name | Why it fits | Quick availability notes | Risk |
| --- | --- | --- | --- |
| **Paneo** | From "pane": tabs on any side and split view. Short, coined, easy to say | Only hit: a small iOS panorama-sharing app (https://apps.apple.com/dk/app/paneo/id1465723084). Nothing in winget or the Store. No browser | Low |
| **Tinge** | A touch of colour, so it fits the colourful themes. Calm and grown-up | Small unrelated apps: Tinge paint-colour app (https://www.tingedesign.co/), a 2012 iOS duotone filter, a tiny PyPI lib. USPTO TINGE filings are in non-software classes (stationery, medical). Nothing in winget or the Store | Low-medium |
| **Zinnia** | Keeps the Z and the energy. A famously bright flower | Zinnia (insurance tech) holds US marks covering software in class 42 (https://uspto.report/TM/97064493), and a small open-source archive app called Zinnia ships for Windows (https://rosie.run/zinnia). Nothing in winget | Medium |
| **Zingo** | Closest to Zing in sound | "Zingo!" is a privacy-focused Zcash wallet and messenger (https://zingolabs.org/), the same "private" positioning. Nothing in winget | Medium |
| **Pique** | "Piques your interest": lively, short | Pique is an AI note-taker desktop app (https://www.pique.so/), which brings back the AI association Zing avoids. Nothing in winget | Medium |

Checked and **rejected**: **Vela** (an existing privacy browser with 50+ colour themes: https://velabrowser.com/), **Tangram** (an existing GNOME browser for pinned tabs: https://apps.gnome.org/Tangram), **Tilo** (a tabbed, tiling OS: https://tilo.so/), and **Zuno** (already in winget as `noFAYZ.Zuno`).

**If you rename:** Paneo is the cleanest of these, and Tinge is the safest real word. Logo concepts B (Panes) and C (Slice) don't depend on the letter Z, so they survive a rename. A (Bolt) leans on the meaning of "zing", and D (Ribbon) is literally a Z.
