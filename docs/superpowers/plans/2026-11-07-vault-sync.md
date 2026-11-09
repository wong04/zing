# Password Vault and Sync Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Zing saves, fills and manages everyday passwords behind Windows Hello. It syncs them, end-to-end encrypted, between the user's PCs through a small Zing server on Railway, and recovers them after a reinstall.

**Architecture:**
- Each login has two parts:
  - a summary (site, username and the rest) under an index key that DPAPI seals
  - a password under a secret key sealed by a Windows Hello key held in the TPM
- Both keys are random. They're wrapped by an Argon2id key from the Zing password, and again by a recovery key, for sync and recovery.
- **The host owns all keys and crypto.** The renderer only ever sees summaries, and a password only when the user reveals it on the manage page.
- **The server** is a separate ASP.NET minimal API. It stores ciphertext in SQLite, assigns revisions, and knows users only by their Google `sub`.

**Tech Stack:** C# .NET 9, xUnit, WebView2, NSec.Cryptography (libsodium), Windows Hello (`KeyCredentialManager`), ASP.NET Core minimal API, Microsoft.Data.Sqlite; TypeScript, React, Vitest, Playwright over CDP.

**Spec:** `docs/superpowers/specs/2026-11-07-vault-sync-design.md`. Research: `docs/superpowers/specs/vault-sync-research.md`.

## Global Constraints

- Tabs; early returns; specific error types raised explicitly; no silent fallbacks. Match the surrounding code.
- **Windows Hello is required.** If it isn't set up, raise `HelloUnavailableException`, and the UI shows "Set up Windows Hello to use the password vault". There is never a password-only path on the PC.
- **The vault is off until the user turns it on.** While it holds entries, the Settings privacy banner warns.
- **Nothing derived from the Zing password leaves the PC.** The server never receives a password, a hash or a verifier.
- **Argon2id floor:** 256 MiB of memory, t=3, p=1. A client refuses lower parameters from anywhere.
- **Encryption:** XChaCha20-Poly1305 with a random 24-byte nonce. Payloads are padded to the next multiple of 256 bytes. Associated data is `user ID | item ID | item version | part`, where part is `summary`, `password` or `history`.
- **Sync numbers:**
  - writes are batched, at most 100 items per request
  - sync runs at start, 2 s after a local change, and every 5 minutes
  - a soft delete becomes a tombstone after 30 days
  - the server keeps tombstones another 30 days
- **Sign-in:**
  - a successful sign-in is a submitted form with a password box, then landing within 10 s on a page in that origin with no visible password box
  - the sync password needs at least 12 characters, a digit, a character that's neither a letter nor a digit, and zxcvbn score 3 (changed from score 4 on 2026-11-09)
- e2e never reaches Google, Railway or any outside service. Windows Hello is faked only when `ZING_E2E=1`, and the real prompt is checked by hand with the owner.
- WebView2's own password saving and general autofill are off in every profile (`IsPasswordAutosaveEnabled = false`, `IsGeneralAutofillEnabled = false`).
- Pure rules shared by TS and C# use `*.vectors.json`, as elsewhere.
- No secrets in the repo. The Google client ID, the allow-list and the server's signing key live in Railway variables. Zing's own copy of the client ID comes from a build property.
- **Before each PR:** `npm run lint`, `npm run typecheck`, `npm test`, `npm run test:host`, `npm run test:e2e`, plus `dotnet test server/Zing.Server.Tests` once that project exists. During a task, run only what the task names. Commit messages are short and lowercase.
- **Three PRs:**
  - A: the local vault, Tasks 1–7, on `phase-6b/vault`
  - B: the server, Tasks 8–10, on `phase-6b/server`
  - C: sync, Tasks 11–15, on `phase-6b/sync`

  A and B branch from `main` and don't depend on each other. C branches after both merge.

## Decisions this plan makes

For the owner to check:

- **The `passwordAutofill` setting's default changes from `true` to `false`.** It becomes the vault's on/off switch. Krackn's default was for Keychain, which stored nothing of its own; the spec makes the vault opt-in.
- **Files:**
  - `vault/vault.zing` in the profile holds the encrypted items, the same format the server stores
  - `vault/keys.bin` holds the sealed index and secret keys
  - `vault/sync.json` holds the sync state: server revision, per-item version floors, and the queued changes
- **The Windows Hello key is created with `KeyCredentialManager.RequestCreateAsync("Zing vault", ReplaceExisting)`.** The secret key is wrapped with a key derived by HKDF from the Hello key's RSA signature over a fixed 32-byte challenge that's stored with it. This is Check C1's technique; if C1 picks the other method, Task 3 follows C1.
- **Password strength:** `zxcvbn-core` in the host enforces the floor, and `@zxcvbn-ts/core` in the renderer draws the meter. Shared vectors pin clear cases to the same verdict.
- **The recovery key** is 26 base32 characters, shown in groups of four (`ABCD-EFGH-…`).
- **The server** lives in `server/Zing.Server`, with tests in `server/Zing.Server.Tests`. It deploys to Railway from a `Dockerfile`, with a volume mounted at `/data`.
- **e2e runs the server locally** (`dotnet run --project server/Zing.Server`). Its fake Google issuer is turned on with `ZING_SERVER_FAKE_GOOGLE=1`. Zing points at it through `ZING_E2E_SYNC_URL`.

## Review Focus

1. **Sign-in pages that keep the password box after success**, such as a "confirm password" step or a single-page app. Zing must not prompt to save on every one of these pages. If it does, the success rule is wrong for that site, not something to accept. Test: a single-page sign-in that swaps the form for a dashboard without navigating gets one prompt, and a page that re-renders the same form gets none.
2. **Two PCs editing the same login offline, both changing the password.** One wins, and the other password is in the history on both PCs. Test in Task 13.
3. **Hello cancelled or timed out mid-fill.** Nothing is filled, no error dialog appears, and the picker closes. Test in Task 4.
4. **Zing closed during the first upload after sync is turned on.** The next start resumes, nothing is lost, and nothing is uploaded twice under different IDs. Test in Task 13.
5. **A Chrome CSV with duplicates, blank passwords, `android://` rows and non-web origins.** Duplicates merge, invalid rows are listed as skipped with the reason, and nothing silently disappears. Test in Task 6.

---

### Task 0: Checks (throwaway, `spikes/vault/`, not merged)

These come from the spec. Each gets one report line in the ledger. **If C1 or C2 fails, stop and bring the owner the options.**

- [ ] **C1, tying the secret key to Windows Hello:** a console app uses `KeyCredentialManager` to create a key, then seals and unseals 32 random bytes.
  - Show that the unseal can't succeed without a real Hello prompt, after a restart.
  - Show that the signature over the stored challenge is deterministic.
  - Compare with Windows Hello's key protection API, if present, and pick one.
- [ ] **C2, passkeys:** in Zing, sign in with a passkey on webauthn.io, using Windows Hello and then a phone via QR code. If it fails, record what happens.
- [ ] **C3, WebView2's own password saving:** with `IsPasswordAutosaveEnabled` and `IsGeneralAutofillEnabled` both false, submit a sign-in form. No WebView2 prompt or suggestion may appear.
- [ ] **C4, Google sign-in:** the unpackaged `Zing.exe` opens the system browser for Google OAuth, with PKCE and a `http://127.0.0.1:<port>/` redirect, and receives the code. Use a test OAuth client the owner creates. Note the console steps for the PR body.

---

## PR A: The local vault (V1–V3)

### Task 1: Vault model v2

**Files:** Modify `src/shared/vault/vault.ts`, `vault.test.ts`. Create `src/shared/vault/vault.vectors.json`, `host/Zing.Core/Vault/VaultModel.cs`. Test: `host/Zing.Core.Tests/Vault/VaultModelTests.cs`.

**Interfaces:**
- **Produces, in TS:**
  - `LoginSummary { id, origin, username, title, note, createdAt, updatedAt, passwordUpdatedAt, deletedAt: number | null }`
  - `NeverSave { origin }`
  - `saveDecision(summaries, origin, username, passwordChanged: boolean): 'save' | 'update' | 'none'`, which replaces `saveLogin`'s outcome logic
  - `entriesFor(summaries, context)`, now over summaries; deleted ones are excluded
- **The C# twin, `VaultModel`:** the same shapes as records, plus:
  - `SaveDecision(...)`
  - `EntriesFor(...)`
  - `RecentlyDeleted(summaries, now)`
  - `DueForTombstone(summary, now)`, which is true once 30 days have passed

  The C# version is the source of truth, and the TS one exists for the renderer's display logic.

- [ ] **Step 1: Tests, as shared vectors:**
  - an origin match is exact (`https://accounts.google.com` ≠ `https://google.com`)
  - a cross-origin frame gets nothing
  - a new origin and username gives `save`; the same username with a changed password gives `update`; the same password gives `none`
  - a "never save" origin gives `none`
  - a login deleted 29 days ago is in Recently deleted, and one deleted 30 days ago is due for a tombstone
- [ ] **Step 2: Run** `npm test -- vault` and `npm run test:host`. Expected: FAIL.
- [ ] **Step 3: Build.** `parseVault` and `serializeVault` go, because the host owns the file format now (Task 3).
- [ ] **Step 4: Run.** Expected: PASS. **Commit** `vault model v2`.

### Task 2: Vault crypto

**Files:** Create `host/Zing.Core/Vault/{VaultCrypto,Padding,KdfParams,RecoveryKey}.cs`. Modify `Zing.Core.csproj` to add `NSec.Cryptography`. Test: `host/Zing.Core.Tests/Vault/VaultCryptoTests.cs`, `vaultCrypto.vectors.json`.

**Interfaces:**
- **Produces:**
  - `VaultCrypto.Seal(ReadOnlySpan<byte> key, byte[] plain, ItemAd ad): byte[]`, which pads, then returns nonce and ciphertext
  - `VaultCrypto.Open(ReadOnlySpan<byte> key, byte[] sealedBytes, ItemAd ad): byte[]`, which raises `VaultTamperedException` on a failed tag
  - `record ItemAd(string UserId, string ItemId, long Version, ItemPart Part)`
  - `KdfParams(MemoryKiB, Iterations, Parallelism, byte[] Salt)` with `EnforceFloor()`, which raises `KdfTooWeakException`
  - `PasswordKey.Derive(string password, KdfParams): byte[]`, using Argon2id
  - `RecoveryKey.Generate(): string` and `RecoveryKey.ToKey(string): byte[]`, using HKDF-SHA256 with info `zing-recovery-v1`; it raises `RecoveryKeyFormatException`
  - `KeyWrap.Wrap(byte[] wrappingKey, byte[] key, string label)` and `KeyWrap.Unwrap(...)`, the same AEAD with `label` as associated data
- [ ] **Step 1: Tests:**
  - known-answer vectors for Argon2id (RFC 9106 test vector), HKDF (RFC 5869) and XChaCha20-Poly1305 (draft-irtf-cfrg-xchacha appendix)
  - padding lengths (0 → 256, 256 → 512)
  - changing any part of `ItemAd` makes `Open` raise `VaultTamperedException`
  - `KdfParams` with 64 MiB raises `KdfTooWeakException`
  - recovery key and password key wrap round trips
  - a recovery key with a mistyped group raises `RecoveryKeyFormatException`
- [ ] **Step 2: Run** `npm run test:host`. Expected: FAIL.
- [ ] **Step 3: Build** on NSec. Wipe key buffers with `CryptographicOperations.ZeroMemory` after use.
- [ ] **Step 4: Run.** Expected: PASS. **Commit** `vault crypto`.

### Task 3: Vault store and Windows Hello

**Files:** Create:
- `host/Zing.Core/Vault/{VaultStore,VaultKeys,IHelloSealer}.cs`
- `host/Zing.Host/HelloSealer.cs` (C1's method)
- `host/Zing.Host/FakeHelloSealer.cs` (e2e only)

Modify `Program.cs`, `ChromeApi.cs` (`AppServices` gains `VaultStore? Vault`), `channels.json`, `ipc.ts`, `zingApi.ts`. Test: `host/Zing.Core.Tests/Vault/VaultStoreTests.cs`.

**Interfaces:**
- **`IHelloSealer`:**
  - `Task<byte[]> SealAsync(byte[] key)`
  - `Task<byte[]> UnsealAsync(byte[] sealedBytes)`, which raises `HelloCancelledException` or `HelloUnavailableException`
  - `bool Available`
- **`VaultStore`:**
  - `Summaries(): IReadOnlyList<LoginSummary>`, with no Hello
  - `Task<string> PasswordAsync(string id)`, which asks Hello
  - `Task SaveAsync(NewLogin)`, `Task UpdatePasswordAsync(string id, string password)`, `UpdateSummary(id, title, note, username)`, `Delete(id)`, `Restore(id)`, `History(id)`, and `Task<string> HistoryPasswordAsync(id, index)`
  - `Changed` event
- **`record LoginRecord(string ItemId, long Version, byte[] Summary, byte[] Password, byte[][] History, bool Tombstone)`:** one stored item, sealed and exactly as the server stores it. Tasks 12 and 13 use it.
- **Version counting:** every change bumps the item's version and is queued in `sync.json`, which Task 13 drains.
- **Bridge:**
  - `vault:summaries`
  - `vault:reveal(id)`, after Hello
  - `vault:save`, `vault:update`, `vault:delete`, `vault:restore`, `vault:history`
  - `vault:state` pushes `{ enabled, helloAvailable, count }`

- [ ] **Step 1: Tests, with a fake sealer:**
  - save, then reopen the store, then the password still reads back
  - `Summaries()` never calls the sealer
  - `PasswordAsync` calls the sealer once per call, with no caching
  - a cancelled sealer raises `HelloCancelledException` and changes nothing
  - an unavailable sealer makes every write raise `HelloUnavailableException`
  - an updated password moves the old one into the history
  - each change bumps the version and queues one change
  - `vault.zing` contains no origin or username in clear text (search the file's bytes)
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Build.** The index key is sealed with `DpapiCipher`, and the secret key with `IHelloSealer` and then `DpapiCipher`. The store is created only when `passwordAutofill` is on. `FakeHelloSealer` is used only when `TestHooks.IsEndToEndRun`, and its result is set through a test hook: `vault-hello` set to `pass`, `cancel` or `unavailable`.
- [ ] **Step 4: Run.** Expected: PASS. **Commit** `vault store and windows hello`.

### Task 4: Autofill and the account picker

**Files:** Modify `src/shared/vault/autofillScript.ts` (drop `autofillWorldId`), `host/Zing.Host/WebView2Page.cs` (inject the script; turn off WebView2 autosave and autofill), `src/renderer/src/overlay/PageUi.tsx`. Create `host/Zing.Core/Vault/Autofill.cs`, `src/renderer/src/overlay/AccountPicker.tsx`. Test: `e2e/vault.spec.ts`, `host/Zing.Core.Tests/Vault/AutofillTests.cs`.

**Interfaces:**
- **Consumes** `VaultStore` and `VaultModel.EntriesFor`.
- **Produces:**
  - `Autofill.FieldFocused(PageId, frameSource: Uri, CredentialField)`, which shows the picker for the frame's *actual* origin
  - the bridge pushes `pageui:account-picker { pageId, rect, entries: {id, username}[] }`
  - `vault:fill(pageId, id)` asks Hello, then runs `fillScript` in that frame

- [ ] **Step 1: Tests:**
  - **e2e:** with a local sign-in page and one saved login, focusing the username box shows the picker with that username; choosing it fills both boxes
  - **Review Focus 3:** with `vault-hello` set to `cancel`, choosing fills nothing and closes the picker
  - an iframe from another local port gets no picker
  - a page posting a forged `zingCredentialField` message for another origin gets a picker only for its own origin
  - with the vault off, no picker appears
  - the page never sees usernames: `document.body.innerText` doesn't contain the saved username before a choice is made
  - **host:** a frame source that differs from the page's origin is refused by `canFill`
- [ ] **Step 2: Run** `npx playwright test e2e/vault.spec.ts`. Expected: FAIL.
- [ ] **Step 3: Build.** The picker lives in the page UI layer, under the field's rect, and is keyboard-navigable. Use C3's result for the WebView2 settings.
- [ ] **Step 4: Run.** Expected: PASS. **Commit** `autofill and account picker`.

### Task 5: Save and update prompts

**Files:** Create `src/shared/vault/signInOutcome.ts` with `signInOutcome.vectors.json`, and `host/Zing.Core/Vault/SignInWatch.cs`. Modify `autofillScript.ts` (report submits), `PageUi.tsx`. Test: `e2e/vault.spec.ts`, `host/Zing.Core.Tests/Vault/SignInWatchTests.cs`.

**Interfaces:**
- **Produces:**
  - `SignInWatch.Submitted(PageId, origin, username, password)`, which holds the credentials in memory only
  - `SignInWatch.Landed(PageId, origin, hasVisiblePasswordBox)` turns that into a prompt, or into nothing
  - the bridge pushes `pageui:save-login { kind: 'save' | 'update', origin, username, replaces?: username }`
  - `vault:answer-save(pageId, 'save' | 'not-now' | 'never')`
- **Rules for the held credentials:** they're dropped after 10 s, on a prompt answer, or when the tab closes, and they're never written anywhere.

- [ ] **Step 1: Tests:**
  - **e2e:**
    - a successful sign-in to a new site prompts Save, and Save stores the login after Hello
    - a wrong password (the page shows the form again) gives no prompt
    - a changed password prompts an update naming the old username
    - "Never for this site" stops later prompts
  - **Review Focus 1:**
    - a single-page sign-in that swaps the form for a dashboard prompts once
    - a page that re-renders the same form doesn't prompt
  - **host:** the credentials are gone after 10 s without a landing
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Build.** "Never" is stored as a `NeverSave` summary item, so it syncs.
- [ ] **Step 4: Run.** Expected: PASS. **Commit** `save and update prompts`.

### Task 6: Manage page, import and export

**Files:** Create `src/renderer/src/settings/VaultSection.tsx`, `host/Zing.Core/Vault/VaultCsv.cs`. Modify `SettingsSheet.tsx`, `rows.ts`, `privacy.ts` (register the vault's stores-data condition), `schema.ts` and `Defaults/settings.json` (`passwordAutofill` defaults to `false`), and `src/shared/import/passwords.ts`. Test:
- `e2e/vault.spec.ts`
- `e2e/vault-screenshots.spec.ts`
- `host/Zing.Core.Tests/Vault/VaultCsvTests.cs`
- `src/shared/import/passwords.test.ts`

**Interfaces:**
- **Consumes** Task 3's bridge.
- **Produces:**
  - `vault:import-csv(token)` returns `{ added, merged, skipped: {row, reason}[] }`
  - `vault:export-csv()` asks Hello, then saves through `OutputPlacement` into Downloads in Chrome's CSV format

- [ ] **Step 1: Tests:**
  - **e2e:**
    - the toggle turns the vault on, and the privacy banner warns once there's an entry
    - search filters by site and username
    - reveal and copy ask Hello
    - edit, delete and restore from Recently deleted
    - the history lists the old password
    - export, after a warning, writes a CSV that re-imports to the same entries
  - **Review Focus 5:** a Chrome CSV with two duplicate rows, a blank password, an `android://` row and `not a url` gives one merged entry, plus three skipped rows, each with its reason
  - **Screenshots** in light and dark: the empty state, a list, and the Hello-unavailable error
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Build** in the Settings style. Add a "Generate" button, using the X21 generator, to the edit form.
- [ ] **Step 4: Run.** Expected: PASS. **Commit** `vault manage page`.

### Task 7: PR A

- [ ] Do a real Windows Hello check with the owner: save, fill, reveal, and a cancel.
- [ ] Mark V1–V3 Done in PARITY.md, linking the evidence. Add C2's result to the README's Known differences if passkeys failed.
- [ ] Run every command in the Global Constraints, then open the PR `phase-6b/vault` → `main` (closes #95–#97).

---

## PR B: The sync server

### Task 8: Server, storage and sign-in

**Files:** Create:
- `server/Zing.Server/{Program.cs,Db.cs,GoogleTokens.cs,Sessions.cs,RateLimits.cs,Zing.Server.csproj}`
- `server/Zing.Server.Tests/*`
- `server/Dockerfile`

Add both projects to the solution.

**Interfaces:**
- **Produces:**
  - `POST /auth/google { idToken }` → `{ session, refresh, userId }`
  - `POST /auth/refresh { refresh }` → `{ session, refresh }`
  - `GET /health`
- **Tables:**
  - `users(sub PRIMARY KEY, user_id, keys_blob, keys_version, profile_blob, manifest_blob, manifest_version, revision)`
  - `items(user_id, item_id, version, revision, tombstone, summary, password, history, stored_at)`
- **Users:** `user_id` is a random ID, never the `sub`. Only `sub` values in `ZING_ALLOWED_SUBS`, comma-separated, are accepted.
- **Sessions:** a session token is an HMAC-signed 15-minute token keyed by `ZING_SESSION_KEY`. Refresh tokens are random, stored hashed, and rotated on each use.

- [ ] **Step 1: Tests, with `WebApplicationFactory` and the fake Google issuer:**
  - an allowed `sub` gets a session; another `sub` gets 403
  - an expired or forged ID token gets 401
  - a token for the wrong audience gets 401
  - a reused refresh token gets 401 and revokes that user's refresh tokens
  - the 31st request in a minute from one user, or the 61st from one IP, gets 429
  - `/health` gets 200
- [ ] **Step 2: Run** `dotnet test server/Zing.Server.Tests`. Expected: FAIL.
- [ ] **Step 3: Build.** The real ID token check uses Google's JWKS, and the audience is `ZING_GOOGLE_CLIENT_ID`. The fake issuer is only on with `ZING_SERVER_FAKE_GOOGLE=1`.
- [ ] **Step 4: Run.** Expected: PASS. **Commit** `sync server and sign-in`.

### Task 9: Keys, changes and the manifest

**Files:** Create `server/Zing.Server/{Keys,Changes,Manifest}.cs`. Test: `server/Zing.Server.Tests/{Keys,Changes,Manifest}Tests.cs`.

**Interfaces:**
- **Keys:** `GET /keys` → `{ blob, version }`. `PUT /keys { blob, baseVersion }` returns `409` with the current one when stale.
- **Changes:**
  - `GET /changes?since=<rev>&limit=200` → `{ items: [...], revision, horizon, more }`
  - `POST /changes { items: [{ itemId, version, baseRevision, tombstone, summary, password, history }] }`, at most 100 items, accepts or `409`s per item: `{ accepted: [{itemId, revision}], conflicts: [{current item}] }`
- **Manifest:** `GET /manifest` and `PUT /manifest { blob, baseVersion }`, with the same `409` rule.
- **Tombstones:** purged 30 days after they're stored. `horizon` is the lowest revision a PC may resume from without a full resync.

- [ ] **Step 1: Tests:**
  - **revisions:** each accepted write gets the next revision; a stale `baseRevision` gets a conflict carrying the current copy
  - **paging:** `since` returns only newer items, in pages
  - **limits:** 101 items gets 400
  - **tombstones:** one older than 30 days is purged and the horizon moves past it
  - **isolation:** one user can't read or write another's items
  - **keys and manifest:** a stale `PUT` gets `409`
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Build.** Each request runs in a single SQLite transaction (WAL mode).
- [ ] **Step 4: Run.** Expected: PASS. **Commit** `sync endpoints`.

### Task 10: Railway deployment, then PR B

**Files:** Create `server/railway.json`, `server/README.md` (setup steps).

- [ ] **Step 1:** `railway.json` uses the Dockerfile, has a health check on `/health`, and mounts the volume at `/data`. `server/README.md` lists:
  - the variables: `ZING_GOOGLE_CLIENT_ID`, `ZING_ALLOWED_SUBS`, `ZING_SESSION_KEY`
  - the Google Cloud OAuth client setup, from C4
  - setting a Railway usage limit
- [ ] **Step 2:** Build the image locally, run it, and check `/health`.
- [ ] **Step 3:** Open the PR `phase-6b/server` → `main`. **Deploying to Railway is the owner's step:** the service and variables live in their account. Walk them through it, using the README.

---

## PR C: Sync in Zing

### Task 11: Google sign-in in Zing

**Files:** Create `host/Zing.Host/GoogleSignIn.cs`, `host/Zing.Core/Sync/SyncClient.cs` (HTTP calls only). Modify `ChromeApi.cs`, `channels.json`, `ipc.ts`. Test: `host/Zing.Core.Tests/Sync/SyncClientTests.cs`, `e2e/sync.spec.ts`.

**Interfaces:**
- **Produces:**
  - `GoogleSignIn.SignInAsync(CancellationToken): Task<string idToken>`, using the system browser, PKCE and a loopback port
  - `SyncClient(Uri server)`, with `SignInAsync(idToken)`, `GetKeys`, `PutKeys`, `GetChanges`, `PostChanges`, `GetManifest`, `PutManifest`; it raises `SyncHttpException(status)`
- **The session:** the refresh token is stored under DPAPI in `vault/sync.json`.
- **In e2e,** `GoogleSignIn` is replaced by one that asks the local server's fake issuer for a token.

- [ ] **Step 1: Tests:**
  - **host,** with the local server: sign-in, then a 401 refreshes once and retries; a second 401 raises
  - **e2e:** the Settings button "Turn on sync" reaches a signed-in state against the local server
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Build** per C4.
- [ ] **Step 4: Run.** Expected: PASS. **Commit** `google sign-in for sync`.

### Task 12: Turning sync on

**Files:** Modify `host/Zing.Core/Account.cs` (add `ChangePassword(string current, string next)`), `VaultSection.tsx`. Create `host/Zing.Core/Sync/SyncSetup.cs`, `src/renderer/src/settings/SyncSetup.tsx`, `src/shared/vault/passwordStrength.vectors.json`. Test: `host/Zing.Core.Tests/Sync/SyncSetupTests.cs`, `AccountTests.cs`, `e2e/sync.spec.ts`.

**Interfaces:**
- **`SyncSetup.TurnOnAsync(string password): Task<string recoveryKey>`:**
  - it requires a Zing account, or raises `AccountException(NoAccount)`
  - it requires zxcvbn 4, or raises `SyncPasswordTooWeakException`
  - it creates the KDF parameters, wraps the index and secret keys with both the password key and the recovery key, encrypts the account profile, and uploads the keys, then every item, then the manifest
- **Storing the recovery key:** it's kept in `vault/keys.bin`, sealed under the secret key, so it can only be read after Windows Hello.
- **A password change while sync is on:** `AccountStore.ChangePassword` re-derives the password key and re-wraps both keys, then calls `PutKeys` with the stored keys version. A `409` raises `SyncKeysChangedElsewhereException`, and the user is asked for the newest password first.
- **The bridge:** `sync:turn-on(password)`, `sync:confirm-recovery-saved`, `sync:show-recovery` (asks Hello), and pushes of `sync:state`.

- [ ] **Step 1: Tests:**
  - a guest gets `NoAccount`
  - `correct horse` is refused, and the generated five-word passphrase is accepted (shared strength vectors)
  - after setup, the server's blobs contain no origin, username or password bytes
  - `ChangePassword` with a wrong current password raises `WrongPassword`
  - with sync on, `ChangePassword` re-wraps the keys, the old password no longer unwraps them, and the new one does
  - `sync:show-recovery` with `vault-hello` set to `cancel` reveals nothing
  - **e2e:** the recovery key screen blocks until it's confirmed
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Build.** The strength meter uses `@zxcvbn-ts/core`. The passphrase generator reuses X21's word list.
- [ ] **Step 4: Run.** Expected: PASS. **Commit** `turn on sync`.

### Task 13: The sync engine

**Files:** Create `host/Zing.Core/Sync/{SyncEngine,Merge,RollbackGuard}.cs`, `src/shared/vault/merge.vectors.json`. Test: `host/Zing.Core.Tests/Sync/{Merge,SyncEngine,RollbackGuard}Tests.cs`, `e2e/sync.spec.ts`.

**Interfaces:**
- **`Merge.Items(LoginRecord common, LoginRecord mine, LoginRecord theirs): LoginRecord`:**
  - it works field by field, and the later `updatedAt` breaks a tie
  - for passwords, it compares `passwordUpdatedAt` and moves the losing ciphertext into the history unchanged, without decrypting
- **`SyncEngine.SyncAsync()`:** it pulls since the last revision, pushes the queue in batches of 100, merges on a conflict and retries, then writes the manifest.
- **`RollbackGuard`:** it raises `ServerRolledBackException` on a lower server revision, an older manifest, or an item version below this PC's floor. It raises `ServerWithheldItemException` when an item in the manifest is missing.
- **The schedule:** at start, 2 s after a change, and every 5 min.
- **Tombstones:** the engine turns soft-deleted items that are 30 days old into tombstones. If the last revision is below the horizon, it does a full resync.

- [ ] **Step 1: Tests:**
  - **shared merge vectors:** one side changed; both changed different fields; **Review Focus 2,** both changed the password, so the newer one wins and the older one is in the history
  - **engine,** against the local server and two stores:
    - an edit on A appears on B
    - a delete on A shows in Recently deleted on B, and a restore on B comes back to A
    - a PC past the horizon resyncs fully, and a deleted login doesn't come back
    - **Review Focus 4:** a crash after 50 of 120 items are uploaded resumes without duplicates
  - **rollback:** the server serving an older copy of an item is refused; a server whose revision drops raises `ServerRolledBackException`, and the UI offers "Upload this PC's copy"
  - **e2e:** two Zing profiles share a login through the local server
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Build.** Sync needs no Windows Hello (spec decision 10). Errors go to `sync:state` as `{ error: code, message }`, and are never swallowed.
- [ ] **Step 4: Run.** Expected: PASS. **Commit** `sync engine`.

### Task 14: A new PC, recovery, and a password changed elsewhere

**Files:** Create `host/Zing.Core/Sync/SyncRestore.cs`, `src/renderer/src/settings/SyncRestore.tsx`. Modify the account gate (`shellState.ts` and its screen) to offer "Restore from sync". Test: `host/Zing.Core.Tests/Sync/SyncRestoreTests.cs`, `e2e/sync.spec.ts`.

**Interfaces:**
- **`SyncRestore`:**
  - `RestoreWithPasswordAsync(string password)` and `RestoreWithRecoveryKeyAsync(string key)` download, unwrap, and seal the keys on this PC, then create the local account from the profile. They raise `WrongPassword` or `RecoveryKeyFormatException`.
  - After a recovery key restore, `MustSetNewPassword = true` until `ChangePasswordAfterRecovery(next)`.
- **The key version:** `SyncEngine` notices when the server's keys version is newer than this PC's, and pushes `sync:password-changed-elsewhere`. The next start then asks for the new password, re-checks it by unwrapping, and updates the local `AccountStore` check.

- [ ] **Step 1: Tests:**
  - **e2e,** with a fresh profile and the local server:
    - Restore with the password brings back every login
    - Restore with the recovery key forces a new password, and the old one stops working on the next restore
    - a wrong password is refused, and nothing is created
    - a password changed on profile A makes profile B ask once on its next start
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Build.**
- [ ] **Step 4: Run.** Expected: PASS. **Commit** `restore from sync`.

### Task 15: PR C

- [ ] Do a real check with the owner, against their deployed Railway server:
  - turn on sync on one PC
  - restore on a second profile or PC
  - edit on both, and confirm the history keeps the losing password
- [ ] Check every piece of user-facing text in the vault and sync UI (Settings rows and their descriptions, buttons, confirmations, banners, errors, the account gate and restore screens, the privacy banner) against what the code does in each state: vault off, on, unreadable; sync off, on, signed out, restored with the password, restored with the recovery key, turned off on this PC. Fix any line that is false or stale in some state (for example "Passwords are kept only in the vault on this PC" while sync is on), make button styles consistent, and pin state-dependent text with tests. Added at the owner's request during the hand check (2026-11-09).
- [ ] Update `docs/PLAN.md` (6b, plus sync for passwords pulled forward), `PARITY.md` and the README (privacy section: what the server stores).
- [ ] Run every command in the Global Constraints, including the server tests, then open the PR `phase-6b/sync` → `main`.
