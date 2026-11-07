# Password vault and sync: design

Phase 6b, PARITY rows V1–V3 (issues #95–#97), plus encrypted password sync, which this phase pulls forward from the post-v1 roadmap. Written 2026-11-07 from a conversation with the owner. Research behind the security choices: [vault-sync-research.md](vault-sync-research.md).

## Intent

The owner's everyday passwords live in Chrome's password manager today. Zing should hold them instead, fill them with an account picker the way Chrome does, and keep them on more than one Windows PC. The vault has to survive a Windows reinstall. The owner and one friend will each use it.

The important accounts stay where they are: passkeys go through Windows' own passkey prompt, and 2FA codes stay in the authenticator app on the owner's phone. The vault must not get in the way of either.

## Goals

- Save, fill and manage everyday website passwords, with Windows Hello asked **every time** a password is filled.
- Sync the vault, end-to-end encrypted, between the user's Windows PCs through a small Zing server on Railway.
- Recover the vault after a reinstall: sign in with Google, then enter the Zing account password. If the password is forgotten, a recovery key works instead.
- Two users on one server, each with a separate vault.
- The server never learns site names, usernames, passwords or edit times.

## Non-goals

- Passkeys, TOTP or 2FA codes. Passkeys are Windows' and WebView2's job; Zing only has to make sure they work (see Checks).
- Syncing bookmarks, the Reading List, tabs or settings. The server is built so other record types can be added later without changing it, but this phase syncs passwords only.
- Sharing passwords between users.
- Any sign-in other than Google.
- Browser import other than CSV. Phase 6c covers reading Chrome, Edge and Brave profiles directly.

## Anti-goals: a failure even if it works

- The server, or anyone who steals its data, can read a password or a site name.
- Windows Hello is only a prompt: any program running as the user can decrypt a password without it. Site names and usernames are deliberately readable without Hello (decision 10).
- A failed sign-in overwrites a saved password, or a sync conflict loses one.
- A deleted login comes back.
- A page, or a frame from another site, can list or read saved logins.
- Errors are swallowed, or Zing quietly falls back to a weaker mode. For example, if Windows Hello isn't set up, Zing shows an explicit "set up Windows Hello" error. It never fills without Hello.
- Hosting costs more than Railway Hobby's included $5 a month for two users.

## Constraints

- The project rules apply throughout: tabs, early returns, explicit errors, no fallbacks, and no scope creep.
- The vault is **off until the user turns it on**, following the rule that anything Zing stores is opt-in. While it holds entries, the Settings privacy banner shows a warning, as `restoreTabs` does (`vaultKeepsData` already exists).
- The app logic lives in the C# host, `host/Zing.Core`. The pure TS logic in `src/shared/vault` and `src/shared/import` is kept, and it's held in step with C# by shared `*.vectors.json`, as elsewhere.
- WebView2's own password saving and autofill (`IsPasswordAutosaveEnabled`, `IsGeneralAutofillEnabled`) must be off in every profile. Two password managers fighting would be a bug.
- **Server:** Railway Hobby. A single small service storing SQLite on a Railway volume, with no separate database. Set a usage limit in Railway. Secrets go in Railway's variables, never in the repo.
- e2e calls no outside service. Tests use a local copy of the sync server, and the Google sign-in is faked. Never touch the real Railway service from tests.

## What exists already

- **`src/shared/vault/vault.ts`:** entries, `originOf` and `canFill` (exact origin, no cross-origin frames), `saveLogin` (added, updated or unchanged), `removeEntry`, `importLogins`, `vaultKeepsData`, and serialization. Written for Electron, with tests.
- **`src/shared/vault/autofillFields.ts` and `autofillScript.ts`:** sign-in field detection and the fill script. Written for an Electron isolated world (`autofillWorldId`). WebView2 has no isolated worlds, so the script runs in the page's world and must treat everything the page can touch as hostile (see Autofill).
- **`src/shared/import/passwords.ts` and `csv.ts`:** CSV parsers for Chrome/Edge, Bitwarden, 1Password and Firefox exports.
- **`AccountStore`** (`host/Zing.Core/Account.cs`): the local Zing account. It has a password (minimum 8 characters) checked against a DPAPI-sealed scrypt verifier. Today it only gates the UI and encrypts nothing.
- The password generator tool (X21), the page UI bar used by permission prompts, and the Settings privacy banner.

## Decisions the owner made

1. The vault holds everyday website passwords only.
2. **Picking an account:** clicking a username or password box on a site with saved logins shows a list of the saved usernames under the box. Picking one fills both boxes. One saved login still shows the list, and nothing fills until the user picks.
3. **Windows Hello is asked every time** a password is filled.
4. **Saving:**
   - after a *successful* sign-in with a login Zing doesn't have, Zing offers to save it
   - after a successful sign-in with a saved username but a different password, it offers to update that login, and names which one
   - a failed sign-in never prompts
5. **Sync:** passwords sync between the user's Windows PCs through a Zing server on Railway. Two users, each with their own vault.
6. **Google is the only sign-in to the server.** It identifies the user and nothing more.
7. **The Zing account password unlocks a synced vault** on a new or reinstalled PC.
8. **A recovery key** is a fallback for a forgotten password only. It's never needed to sign in.
9. This phase syncs **passwords only**.
10. **Only passwords are behind Windows Hello.** The list of sites and usernames can be read without Hello, so that the picker appears instantly, the manage page can be searched, and sync runs in the background. The owner accepted the cost: malware already running as the user could see which sites they have accounts on, and the usernames, but never a password. Chrome is about the same.

## Calls I made

The owner hasn't confirmed these yet; they are open to change.

- **What counts as a successful sign-in.** A form with a password box was submitted, and the page the browser lands on (after redirects, within 10 seconds) has no visible password box in the same origin's form. If the sign-in page comes back with a password box, it failed.
- **The save prompt** goes in the page UI bar, with Save, Not now and Never for this site. "Never" is stored per exact origin, inside the vault, so it syncs.
- **Saving, updating, revealing, copying and exporting** all ask for Windows Hello, the same as filling.
- **Conflicts.** If the same login was edited on two PCs, the later edit wins for each field. The password that loses goes into the login's **password history**, shown on the manage page. A conflict can never lose a password.
- **Deleted logins** go to **Recently deleted** for 30 days, with Restore, on every PC. They're then purged everywhere (see Sync design).
- **The Zing account and the vault:**
  - The vault works on its own PC without a Zing account, including for guests. Sync needs an account.
  - The account password is the sync password, and it's the same on every PC.
  - Changing it on one PC re-wraps the keys and uploads them. Each other PC asks for the new password once, the next time it starts, then updates its local check.
  - Restoring on a new PC creates the local account from an encrypted account profile (name and email) stored with the keys.
  - `AccountStore` gains a change-password operation, which today it doesn't have.
  - If the password is weaker than the sync floor, turning on sync asks for a new one as part of the same flow.
- **The recovery key** is shown once, when sync is turned on, and the user must confirm they've saved or printed it before carrying on. It can be shown again later from Settings, after Windows Hello.
- **Password strength.** Turning on sync requires a Zing password of about 60 bits or more (zxcvbn score 4), and offers a generated five-word passphrase. Today's 8-character minimum still applies when sync is off.
- **The manage page lives in Settings:**
  - a searchable list by site, with username, password (hidden) and note
  - edit and delete
  - password history and Recently deleted
  - CSV import and CSV export
  - sync status and the sync controls
- **Export** writes a plain CSV in Chrome's format, after Windows Hello and a warning that the file isn't encrypted.

## Security design

From the research. Each point names the threat it closes.

### Keys

```
Zing password ──Argon2id──▶ password key ─┐               ┌─▶ index key  ──encrypts──▶ each login's summary (site, username, ...)
                                          ├──wraps both──▶┤
recovery key (random 128-bit) ──HKDF──────┘               └─▶ secret key ──encrypts──▶ each login's password (and old passwords)

on this PC:  index key  ──sealed by──▶ DPAPI
             secret key ──sealed by──▶ Windows Hello key (TPM), with DPAPI as an outer layer
```

- **Two random keys, made once per user:**
  - **The index key** encrypts each login's summary: site, username, title, note, times and marks.
  - **The secret key** encrypts each password, and each old password in the history.

  A password change re-wraps both keys and doesn't re-encrypt any login.
- **On this PC, the index key is sealed by DPAPI only.** Zing can show the picker, search the manage page and sync without a Hello prompt (decision 10).
- **The password key** is derived with **Argon2id** from the Zing password and a per-user random salt: 256 MiB of memory, t=3, p=1, using NSec.Cryptography (libsodium).
  - The parameters are stored with the wrapped key and authenticated with it.
  - The client refuses parameters below this floor, because a server could try to downgrade them (the ETH Zurich 2026 attack).
  - The account's existing local scrypt check stays as it is. Only the vault uses Argon2id.
- **The recovery key** is 128 random bits, shown as a grouped base32 code. HKDF turns it into a second key that also wraps the vault key.
- **On this PC, the secret key is sealed by a Windows Hello key held in the TPM** (`KeyCredentialManager`). DPAPI is an outer layer.
  - For passwords, this closes the hole where DPAPI alone decrypts silently for any process running as the user, which would make Hello only a prompt.
  - The method has to be proven first (see Checks). Possible methods are a deterministic signature over a fixed challenge, used as key material (the technique KeePass's Windows Hello plugins use), or Hello's key protection API.
  - Each fill asks Hello. The secret key is held in memory for the shortest time possible, never written unsealed, and cleared after each operation.
- **No Windows Hello means no vault.** Zing shows an explicit "set up Windows Hello" error. There's no password-only fallback on the PC.

### Encryption of each login

- **XChaCha20-Poly1305** with a random 192-bit nonce per encryption. Random nonces are safe across several PCs sharing one key, with no coordination needed.
- **Each login is two ciphertexts:** a summary under the index key and a password under the secret key. Each history entry is its own secret-key ciphertext.
- **Each login has a version** that goes up by one whenever any PC changes it. The version is sent in the clear next to the ciphertext.
- **The associated data** for every ciphertext is the user ID, the item ID, the item version, and which part it is (summary, password or history). So the server can't move an item to another user, swap items or their parts, or relabel an old copy as new.
- **Everything about a login is inside the ciphertexts:** origin, username, password, title, note, password history, created, updated and deleted times, and the "never save" marks. Payloads are padded to the next multiple of 256 bytes, so their sizes don't reveal anything. The server sees only the item ID, its version, the server revision, a tombstone flag and the ciphertexts.

### The server and authentication

- **Google OAuth** runs in the system browser, with PKCE and a loopback redirect. The server checks the Google ID token and identifies the user by the token's `sub`.
  - **Allow-list:** only the two `sub` values in a Railway variable can use the server. Anyone else gets a 403.
  - The server issues its own short-lived session token. Its refresh token is stored on the PC under DPAPI.
- **Nothing derived from the password ever leaves the PC.** No hash or verifier is sent; Google is the only authentication. This avoids the weakness in Firefox Sync and Bitwarden, where a password-derived value on the server can be attacked offline.
- **Rate limits:** a small per-user and per-IP limit on every endpoint.
- **Rollback protection:**
  - **For one item:** each PC remembers the highest version it has seen for every item, and refuses an older one.
  - **For the whole account**, for example if the server is restored from a backup:
    - each PC remembers the highest server revision it has seen
    - a server reporting a lower one, or an older account manifest, means the server has gone back
    - sync stops with an explicit error and offers **Upload this PC's copy**, which re-uploads everything this PC has
  - **Dropped items:** an encrypted **account manifest**, under the index key, lists every item ID and its version.
    - Every write updates it, with optimistic concurrency: the manifest has its own version, and a stale write gets a `409`.
    - A PC compares the manifest with what the server sends, so it notices an item the server held back.
- **The main risk that remains** is a stolen server copy plus a weak password, attacked offline. That's why the 60-bit password floor and heavy Argon2id settings exist.

## Sync design

- **The server assigns revisions.** Each user has a counter that goes up by one with every accepted write. A PC sends its changes along with the revision each one is based on. If the server's copy has moved on, the write is refused with `409` and the current copy.
- **Merging.** On a `409`, the PC merges the two copies field by field against the last copy they had in common:
  - a field changed on only one side takes that side's value
  - a field changed on both sides takes the edit with the later encrypted edit time
  - a losing password always goes into the password history

  The PC then bumps the item's version and retries. Device clocks never decide the order of writes; they only break ties within a field.
- **Merging never needs Windows Hello.** Summaries are merged with the index key. Passwords are merged without being decrypted: each password's edit time is in the summary, so the winning ciphertext is kept as it is, and the losing one moves into the history as it is.
- **Delta sync:** `GET /changes?since=<revision>` returns what changed, in pages. Writes are batched, up to 100 items per request. Sync runs when Zing starts, after any local change (debounced by 2 seconds), and every 5 minutes while Zing is open.
- **Deletes have two stages.**
  - **Deleting** sets an encrypted deleted time in the summary and syncs like any edit. Every PC shows the login in Recently deleted, and any PC can restore it.
  - **30 days later**, the first PC to sync turns it into a real tombstone: a tombstone flag with empty, padded ciphertexts.
  - **The server keeps tombstones** for another 30 days, so a delete is final everywhere within about 60 days.
  - **The server reports a tombstone horizon.** A PC whose last sync is older than that does a full resync, so a PC that was offline for a long time can't bring a deleted login back.
- **Offline:** the local vault keeps working, and changes queue up until the next successful sync.
- **New PC or recovery:**
  1. Sign in with Google.
  2. Download the wrapped keys, the account profile and all items.
  3. Enter the Zing password, or the recovery key, to unwrap the keys.
  4. Seal the index key with DPAPI and the secret key with this PC's Windows Hello.
  5. Create the local Zing account.
  6. If the recovery key was used, set a new Zing password straight away.

## The server

- **What it is:** a small HTTP service in its own top-level folder (`server/`). My call: ASP.NET minimal API on .NET 9, so it shares the host's language and its tests can reuse the crypto vectors.
- **Storage:** SQLite on a Railway volume, with three tables:
  - users: Google `sub`, the wrapped keys, the KDF parameters, the encrypted account profile, and the encrypted manifest with its version
  - items: user, item ID, item version, server revision, tombstone flag, the ciphertexts, and when the server stored it (for its own bookkeeping only)
  - each user's revision counter
- **Endpoints:**
  - `POST /auth/google`
  - `POST /auth/refresh`
  - `GET /keys`, `PUT /keys`: wrapped keys, KDF parameters and the account profile. The `PUT` is versioned, so a stale write gets a `409`.
  - `GET /changes?since=`, `POST /changes`
  - `GET /manifest`, `PUT /manifest`
- **Deployment** to Railway is from the repo, with variables for the Google client ID and the allow-list. There's a usage limit, and a health check.
- **Tests run against the server locally,** with a fake Google token issuer. No test reaches Google or Railway.

## Autofill in WebView2

- The detection script is injected with `AddScriptToExecuteOnDocumentCreatedAsync`. It reports focused sign-in boxes to the host.
- **The page can read and forge anything the script does,** because there's no isolated world. So the host decides which origin the picker shows logins for from the frame's real source, which WebView2 reports, and never from the message. A forged message can at most open the picker for the page's own origin.
- **Cross-origin frames never get the picker,** following `canFill`.
- **The picker is drawn by Zing,** in the page UI layer, not inside the page, so the page never sees the list of usernames.
- A password is only put into the page after the user picks a login and passes Windows Hello. Fill inserts it straight into the box the user clicked.

## Checks to run first

Run these before building on them. Each one can change the design:

1. **Tying the vault key to Windows Hello.** Prove that a key sealed with Windows Hello can't be opened without a Hello prompt, and that it survives a Zing restart. If neither method works, stop and come back to the owner.
2. **Passkeys work in Zing:** a WebAuthn sign-in through Windows Hello, and through a phone via QR code, on a real site. If they don't, record it in the README's Known differences, and raise it with the owner.
3. **WebView2's built-in password saving can be turned off** with no prompt of its own left over.
4. **Google sign-in through the system browser**, with a loopback redirect, from the unpackaged `Zing.exe`.

## Testing

- **Pure logic, in vitest and xUnit with shared vectors:**
  - origin matching
  - the save and update decision
  - the field-by-field merge, including password history
  - the tombstone horizon
  - padding
  - CSV import and export round trips
- **Cryptography:**
  - known-answer tests for Argon2id, HKDF and XChaCha20-Poly1305
  - wrapping round trips with the password key and with the recovery key
  - associated data that doesn't match is refused
  - KDF parameters below the floor are refused
- **Server:**
  - a Google `sub` not on the allow-list is refused
  - the revision counter, and `409` on a stale write
  - delta paging
  - the tombstone horizon
  - rate limits
- **e2e:**
  - the picker appears, and Windows Hello is faked in e2e only
  - a fill
  - save on a successful sign-in, and no prompt on a failed one
  - an update names the login it updates
  - a cross-origin frame never gets the picker
  - the privacy banner changes
  - two Zing profiles syncing through a local server, including a conflict and a delete
  - recovery with the password and with the recovery key
- **A real Windows Hello prompt** is checked by hand with the owner. It can't be automated.

## Left to the builder

- File and class layout. The cut between C# and TS follows the existing pattern: pure rules in `src/shared`, which C# mirrors through vectors.
- The manage page's layout. It follows the existing Settings style, with screenshots in light and dark.
- How work is split into PRs. Suggested order: the checks, then the local vault and autofill (V1–V3, usable with sync off), then the server, then sync and recovery.
- Which zxcvbn port to use, and how the recovery key is formatted for printing.
