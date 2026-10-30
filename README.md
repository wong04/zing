# Zing

A privacy-first browser for Windows, built on Chromium. It keeps nothing by default, puts tabs wherever you want them (top, left or right), does split view, and comes in actual colours.

Zing is a remake of [Krackn](https://github.com/yityler/Krackn), a macOS browser + toolbox written in SwiftUI on WebKit. Krackn's features (the private session, tab organisation, Open a Link, Diagnose, Convert, Download, Security, the file viewer and the utilities) are being ported to Electron + TypeScript on Windows.

> **Status: planning.** No app code yet. Follow progress on the [milestones](https://github.com/wong04/zing/milestones).

## What's different from Krackn

- **Chromium and Windows** instead of WebKit and macOS
- **Tab bar on the top, left or right**, with split view
- **Colourful themes** with presets and a custom theme from one colour
- **Built-in password vault** (Windows Hello) instead of the macOS Keychain bridge
- **Import** from Chrome, Edge, Brave, Dia and Arc
- **Full ad and tracker blocking** (EasyList/EasyPrivacy)
- **No AI.** Not now, not later.

## Docs

- [docs/PLAN.md](docs/PLAN.md): the plan, including the stack decision and tradeoffs
- [PARITY.md](PARITY.md): every feature, with its issue and status
- [reference/krackn](reference/krackn): Krackn's Swift source at the baseline commit, kept read-only until parity, then deleted

## Known differences on Windows

None yet. Anything Krackn does that Zing can't is listed here.

## License

[MIT](LICENSE). Krackn by [yityler](https://github.com/yityler).
