# Zing

A privacy-first browser for Windows, built on Chromium. It keeps nothing by default, puts tabs wherever you want them (top, left or right), does split view, and comes in actual colours.

Zing is a remake of [Krackn](https://github.com/yityler/Krackn), a macOS browser + toolbox written in SwiftUI on WebKit. Krackn's features (the private session, tab organisation, Open a Link, Diagnose, Convert, Download, the file viewer and the utilities) are being ported to Windows: a C# WinUI 3 app hosting Edge's WebView2 (Chromium), with the UI in React and TypeScript.

> **Status: in development.** The browser core runs (tabs, start page, find, downloads, reader, sleeping tabs, per-site purge, account gate); the Toolbox is in, with Open a Link, Diagnose, Network & VPN, Convert, media download, the file viewer, the background remover, PDF tools and the developer, math, text and links utilities; later phases are still to come. Follow progress on the [milestones](https://github.com/wong04/zing/milestones).

## Build and run

Needs Node 24, the .NET 9 SDK and the WebView2 runtime (part of Windows 11).

```
npm ci
npm start
```

`npm test`, `npm run test:host` and `npm run test:e2e` run the TypeScript, C# and end-to-end tests.

A build only has password sync when `ZingGoogleClientId` and `ZingGoogleClientSecret` (the Google Desktop OAuth client, see [server/README.md](server/README.md#google-oauth-client)) are set in the environment it builds in, as for `npm run build:host` or a Release build. Without them, Turn on sync says "Sync isn't set up in this build of Zing". With only one of them, the build refuses to start.

## What's different from Krackn

- **Chromium and Windows** instead of WebKit and macOS
- **Tab bar on the top, left or right**, with split view
- **Colourful themes** with presets and a custom theme from one colour
- **Built-in password vault** (Windows Hello) instead of the macOS Keychain bridge, with optional end-to-end encrypted sync between your PCs
- **Import** from Chrome, Edge, Brave, Dia and Arc
- **Full ad and tracker blocking** (EasyList/EasyPrivacy)

## Privacy

Zing stores nothing until you turn something on. The password vault and its sync are both off by default, and the Settings privacy banner turns to a warning once the vault holds a login. The banner follows what is on this PC only, so turning on sync doesn't change it; this section is where sync's data leaving the PC is described.

If you turn on sync, your passwords go to a small Zing server (`server/`, run on Railway). You sign in to it with Google only, and the server accepts just the Google accounts on its allow-list.

- **What the server stores:** your Google account ID (the `sub`, not your email), a random ID of its own for you, your keys wrapped by your Zing password and your recovery key, your account name and email as ciphertext, and a sealed list of your logins. Per login it keeps an ID, a version number, a revision number, whether it is deleted, the encrypted data, and the time the server received the write. It also keeps a hash of each sign-in refresh token.
- **What it can't see:** site names, usernames, passwords, notes, password history and your edit times. They are encrypted on your PC (XChaCha20-Poly1305) with keys the server never gets, and sizes are padded to the next 256 bytes. Nothing derived from your Zing password is sent, so there is no hash to attack.
- **What it can see:** how many logins you have, when you sync, and your IP address, as any server does.
- **What Google sees:** each time you sign in to sync, Google sees that you signed in to Zing, as with any "Sign in with Google". It sees nothing of your passwords.
- **What it can do:** refuse or delay your data, or serve an older copy. Your PCs notice most of this and stop with an error instead of syncing. A few cases from a hostile server are accepted and listed in the [design](docs/superpowers/specs/2026-11-07-vault-sync-design.md). It can't read or change a password.
- **The one real risk:** someone who steals the server's data and guesses your Zing password offline. Turning on sync asks for a strong password (about 60 bits) and uses Argon2id with 256 MiB of memory to make that slow.

If you forget the password, the recovery key restores your vault. Without both, the passwords can't be recovered by anyone, including the server.

## Docs

- [docs/PLAN.md](docs/PLAN.md): the plan, including the stack decision and tradeoffs
- [PARITY.md](PARITY.md): every feature, with its issue and status
- [reference/krackn](reference/krackn): Krackn's Swift source at the baseline commit, kept read-only until parity, then deleted

## Known differences on Windows

Anything Krackn does that Zing can't is listed here.

- **Mica.** Solid tab cards cover the window, so Windows' Mica material isn't visible behind Zing's sidebar.
- **User agent.** Krackn sent a cleaned Safari user agent. Zing runs on Edge's WebView2 and presents Edge's own, which sites (Google sign-in included) accept as it is.

## License

[MIT](LICENSE). Krackn by [yityler](https://github.com/yityler).
