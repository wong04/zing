# End-to-end-encrypted password sync: how others do it, and what Zing should do

Researched 2026-11-07 for the Zing vault sync design (C# WinUI 3 / WebView2 host, Railway sync server).
Every source has a date, or a note that it has none. **[STALE?]** marks anything that may be out of date.

## Zing's intended design (baseline)

- The local vault is encrypted with DPAPI, and Windows Hello gates every fill.
- Sync is optional. The server is a tiny Railway service with SQLite on a volume and two users. Google OAuth only identifies the user to the server.
- The vault key comes from the Zing account password through scrypt, on the device. The server stores only ciphertext.
- Each login is a separate record with a stable ID and an `updatedAt`. Conflicts are last-writer-wins (LWW), the losing password goes into the login's history, and deletes are tombstones kept for 30 days.

---

## 1. Firefox Sync (Mozilla accounts, onepw, storage format 5)

**Sources**
- onepw protocol wiki: https://github.com/mozilla/fxa-auth-server/wiki/onepw-protocol (last edited 2015-11-25; the repo was archived 2019-04-03 and FxA now lives in `mozilla/fxa`. **[STALE?]** The protocol is still in use as far as I know, but this page is not maintained.)
- Sync storage format 5: https://mozilla-services.readthedocs.io/en/latest/sync/storageformat5.html (no date; the page says these archived docs are no longer maintained. **[STALE?]**)
- application-services logins component: https://github.com/mozilla/application-services/blob/main/components/logins/README.md (main branch, read 2026-11-07)
- Critique: W. Palant, "Can Chrome Sync or Firefox Sync be trusted with sensitive data?", 2018-03-13: https://palant.info/2018/03/13/can-chrome-sync-or-firefox-sync-be-trusted-with-sensitive-data/ **[STALE?]** It is from 2018, but the client-side PBKDF2 count it criticises is still 1000 in the onepw spec.

**Key derivation and wrapping**
- The client computes `quickStretchedPW = PBKDF2-SHA256(password, salt=email, 1000 iterations)`, and HKDF on that gives `authPW` and `unwrapBKey`.
- `authPW` is sent to the server. The server runs scrypt (N=64k, r=8, p=1) with a random `authSalt` and stores `verifyHash`.
- kB is the class-B key, the one Mozilla can't read. The server stores `wrap(wrap(kB))`. The server-side stretch unwraps one layer, and the client XORs the result with `unwrapBKey` to get kB. The spec notes there is no MAC on `wrap(kB)`.
- HKDF(kB, "identity.mozilla.com/picl/v1/oldsync") gives the **Sync Key Bundle** (an AES-256 key and an HMAC-SHA256 key). That bundle encrypts the `crypto/keys` record, which holds random per-collection bulk keys. All records are encrypted with those bulk keys.
- The result is already a key hierarchy: password → kB → sync key → random bulk keys.

**What the server sees**
- Ciphertext: each record payload is `{ciphertext, IV, hmac}` with AES-256-CBC and HMAC-SHA256 (encrypt-then-MAC over the Base64 ciphertext).
- In the clear: the collection name (`passwords`, `bookmarks`, ...), record IDs (random GUIDs), `modified` timestamps, payload sizes, record counts, and the unencrypted `meta/global` record.
- The login's hostname, username and password are all inside the payload.

**Password change, reset, forgetting**
- Password change: the client fetches kB with the old password, re-wraps it under the new one, and kB stays the same. No data is re-encrypted.
- Forgotten password: the reset goes through email and the server creates a **new random kB**. All class-B data (synced logins) is lost from the server, but devices that still hold the data re-upload it.

**Granularity, conflicts, deletes**
- Each login is its own record (BSO).
- The logins engine does a **three-way merge**: local, remote, and a mirror of the last-synced server state as the shared parent. A newer change wins per record, and an older change that touches a field that was also changed locally is not applied (logins README).
- A delete is a tombstone record (`{"id":..., "deleted": true}`) inside the encrypted payload.
- The local store uses AES-256-GCM, with the key "preferably obtained from an OS-level keystore mechanism".

**Authentication vs encryption**
- The same password serves both, separated by HKDF. `authPW` reaches the server, so whoever runs the server can brute-force the password against 1000 PBKDF2 iterations without the scrypt step. That covers a malicious or compromised server, or anyone who captures login traffic.

**Weaknesses**
- The client-side stretch is very weak: 1000 iterations of PBKDF2 (Palant 2018).
- There is no MAC on `wrap(kB)`.
- CBC+HMAC is old-style (fine if done right, but more ways to get it wrong than AEAD).
- Metadata leaks: which collections you use, record counts, sizes, timestamps.
- The account and sign-in pages are served by Mozilla, so a hostile server could serve JavaScript that steals the password.

---

## 2. Chrome / Google Password Manager

**Sources**
- Chromium `nigori_specifics.proto` (key derivation enum, passphrase types): https://chromium.googlesource.com/chromium/src/+/7f4bb5d0966011035ba1091fb979492a2b6c605d/components/sync/protocol/nigori_specifics.proto (snapshot commit, undated)
- Google support, on-device encryption: https://support.google.com/accounts/answer/11350823 (no date; ©2026 footer)
- Google support, sync passphrase: https://support.google.com/chrome/answer/165139 (no date)
- Google Security Blog, "Security of Passkeys in the Google Password Manager", October 2022: https://security.googleblog.com/2022/10/SecurityofPasskeysintheGooglePasswordManager.html
- Google blog, GPM PIN and passkey sync, September 2024: https://blog.google/technology/safety-security/google-password-manager-passkeys-update-september-2024/
- Palant, "Chrome Sync privacy is still very bad", 2023-08-29: https://palant.info/2023/08/29/chrome-sync-privacy-is-still-very-bad/

**Three modes**
1. **Default ("keystore").** Google holds the key, so Google can read the data. The Nigori keybag is encrypted with a server-supplied keystore key, and key derivation for non-custom types is PBKDF2-HMAC-SHA1 with 1003 iterations and a fixed salt. This is not end-to-end encryption.
2. **Custom sync passphrase.** The user picks a passphrase, and the key is scrypt(N=2^13, r=8, p=11) with a random salt (added in Chrome M70). It encrypts most sync types, but not Google Pay payment methods and addresses. Google can't read the data. Forgetting the passphrase means resetting sync, which wipes the server data.
3. **On-device encryption / "trusted vault".** This covers passwords and passkeys only. The keybag is encrypted with "trusted vault keys" that stay on the user's devices. Recovery uses the Google password, or a device screen lock or GPM PIN. The verifier for those sits in Google-side secure hardware that "cannot be read by Google" and enforces **at most 10 guesses** (2022 blog). It can't be turned off once set up, and losing every recovery factor loses every password.

**What the server sees**
- Sync metadata (entity IDs, versions, timestamps, data types) stays visible even with a passphrase, because only the "specifics" payloads are encrypted. This is the same model as Brave (below), which is built on Chromium sync.
- **[unverified]** Exactly which password fields remain in the clear under trusted vault isn't documented in what I found.

**Authentication vs encryption**
- The Google account sign-in authenticates the user. The passphrase or trusted-vault key is separate from it.

**Weaknesses**
- The default is not E2EE.
- The passphrase KDF is scrypt with N=2^13, which is only about 8 MiB and weak by today's standards.
- On-device encryption is opt-in, and recovery relies on Google's HSMs being honest.

---

## 3. Brave Sync v2

**Source:** https://github.com/brave/brave-browser/wiki/Brave-Sync-v2 (last edited 2023-09-01 **[STALE?]**). Server code: https://github.com/brave/go-sync.

**Keys**
- The first device generates a 32-byte random seed, shown as a BIP39 phrase (the "sync chain code") or a QR code.
- HKDF-SHA512 on the seed gives an Ed25519 keypair for authenticating to the server.
- The BIP39 phrase is stretched with scrypt (N=2^13, r=8, p=11) and a random salt. That is Chromium's custom-passphrase path, with the passphrase forced to the seed. The salt is created by the first client and fetched from the server by later ones.
- Records use Chromium's AES-128-CTR + HMAC.

**No account**
- The chain is identified by the public key. The access token is `base64(timestamp|sig(timestamp)|pubkey)` and expires after one day.
- Because the secret is a 256-bit random seed rather than a password, a server breach gives the attacker nothing to brute-force.

**What the server sees**
- Encrypted: only the `EntitySpecifics` payloads, such as URLs, titles and password fields.
- In the clear: device names and types, opaque item and parent IDs, versions, creation times.

**Recovery and conflicts**
- Lose every device and the code, and the data is gone. There is no password to forget.
- Conflicts and granularity follow Chromium sync: per entity, with server versions and per-type conflict resolution.

**Weaknesses**
- Metadata as above.
- The token can fall back to local time.
- Usability: the 24-word code is the only secret.
- **[unverified]** Brave has had sync-chain bugs and resets in the past. I didn't find a primary source this round.

**Takeaway for Zing:** a random high-entropy secret removes the offline-guessing risk entirely.

---

## 4. Bitwarden

**Sources**
- Security whitepaper: https://bitwarden.com/help/bitwarden-security-white-paper/ (latest changelog entry 2025-10-09)
- KDF algorithms help page: https://bitwarden.com/help/kdf-algorithms/ (mentions release 2026.2.1)
- Argon2id defaults change, clients PR #20058 (merged 2026-04-15 per search result): https://github.com/bitwarden/clients/pull/20058
- Vault data (what's encrypted): https://bitwarden.com/help/vault-data/ (no date)
- Server source, `SyncController.cs` and `CipherService.cs` on main, read 2026-11-07: https://github.com/bitwarden/server/blob/main/src/Api/Vault/Controllers/SyncController.cs, https://github.com/bitwarden/server/blob/main/src/Core/Vault/Services/Implementations/CipherService.cs
- ETH Zurich, Scarlata et al., "Zero Knowledge (About) Encryption", IACR ePrint 2026/058 (received 2026-01-14, last revised 2026-08-12, USENIX Security '26): https://eprint.iacr.org/2026/058

**KDF**
- PBKDF2-SHA256 with **600,000 iterations** by default. As of 2026.2.1 that is also the minimum, and accounts below it are prompted to upgrade.
- Argon2id is optional, with defaults of **32 MiB, 6 iterations, parallelism 4**. **[STALE?]** Older docs and posts (2023) say 64 MiB, 3 iterations, 4 lanes, so the defaults changed around April 2026.
- The salt is the email address.

**Key hierarchy**
- The KDF output is the Master Key. HKDF expands it to the 512-bit **Stretched Master Key** (encryption key and MAC key).
- A random 512-bit **User Symmetric Key** is encrypted under the Stretched Master Key. The result is the **Protected Symmetric Key**, which is stored on the server.
- Each item gets a random 64-byte Cipher Key, wrapped by the user key or the organisation key.
- Items use AES-256-CBC + HMAC-SHA256.

**What's encrypted and what isn't**
- Encrypted: names, usernames, passwords, password history, **URIs**, notes, TOTP seeds, custom fields, folder and collection names.
- Not covered by the vault-data page: item type, favourite flag, folder and collection membership IDs, revision dates, counts and sizes. **[unverified]** These are in the clear in the API models.
- The server also keeps administrative data: email, IP addresses, devices, item counts.

**Authentication vs encryption**
- The client sends `MasterPasswordHash = PBKDF2(MasterKey, salt=password, 1 iteration)`, and the server re-hashes it with PBKDF2 at 600k iterations and a random salt.
- Anyone holding that hash can crack the master password at roughly KDF cost, because it's only one cheap step beyond the Master Key. The same applies to a database thief who first reverses the server-side 600k PBKDF2.
- The server also sends the KDF parameters back at login (prelogin). The ETH paper shows that a malicious server can abuse server-supplied parameters and keys: it reports **12 attacks on Bitwarden**, including KDF downgrades and key substitution in recovery and sharing. Bitwarden says all are fixed. The same paper reports 7 on LastPass, 6 on Dashlane and 6 on 1Password. Dashlane's write-up (2026-02-16, https://www.dashlane.com/blog/zero-knowledge-malicious-server) names three attack classes: item injection that downgrades encryption, public-key substitution, and transaction replay/reorder/drop.

**Password change, recovery**
- Changing the password re-wraps the user key. Rotating the key, which re-encrypts everything, is optional.
- A forgotten master password can't be recovered, apart from emergency access or organisation admin recovery, both of which escrow the user key to another key holder.

**Sync**
- **Full sync.** `GET /api/sync` has no `since` parameter and returns the entire vault: folders, all ciphers, collections, sends, policies and profile.
- Clients poll `GET /accounts/revision-date` (a single timestamp per account) and run a full sync only when it changes. Push notifications prompt immediate syncs.
- Single-item writes go through the cipher endpoints.

**Conflicts**
- Optimistic concurrency. Each update carries `LastKnownRevisionDate`, and the server rejects it with "The item cannot be saved because it is out of date..." if that date differs from the server's `RevisionDate` by more than 1 s. So the client must re-sync and the user redoes the edit. There is no automatic merge.

**Deletes**
- A soft delete sets `DeletedDate` (the item goes to trash), and trash is purged after 30 days.
- A permanent delete removes the row, and other clients learn about it by its absence from the next full sync. No tombstone is needed because sync is full.

**Weaknesses**
- PBKDF2 is the default rather than Argon2id.
- Unencrypted structural metadata.
- The malicious-server issues above.
- Legacy "type 0" AES-CBC without a MAC existed for old data.

---

## 5. 1Password

**Source:** 1Password Security Design white paper (HTML edition release 0.5.3, dated 2026-10-28): https://agilebits.github.io/security-design/ and key-derivation chapter https://agilebits.github.io/security-design/deepKeys.html. PDF: https://1passwordstatic.com/files/security/1password-white-paper.pdf. Secret Key facts: https://support.1password.com/secret-key-security/ (no date). Response to the ETH paper, 2026-02-16: https://1password.com/blog/eth-zurich-zero-knowledge-malicious-server-review

**Two-secret key derivation (2SKD)**
- The password is trimmed and NFKD-normalised.
- The 16-byte salt is HKDF'd with the email address.
- The password goes through PBKDF2-HMAC-SHA256 with **650,000 iterations**. Accounts created before 2023-01-27 that haven't changed their password or Secret Key use fewer.
- The **Secret Key** is HKDF'd (salt = account ID) and **XORed** with the PBKDF2 output.
- The Secret Key is 34 characters with **128 bits of entropy**, generated on the device, never sent to 1Password, and printed in the Emergency Kit. Old "A2-" keys have less.

**Two independent keys, derived the same way with different salts**
- The **AUK** (Account Unlock Key) decrypts the user's private key, which unlocks vault keys. Vault keys encrypt items with AES-256-GCM.
- **SRP-x** is used for Secure Remote Password authentication. The server only ever holds an SRP verifier, so login never reveals anything to the server that it could brute-force from traffic. And because of the Secret Key, even a stolen verifier or stolen vault data **can't be cracked by guessing the password alone**.

**Server sees**
- Encrypted vault items, including URLs and titles.
- In the clear: account and vault structure, sharing relationships, item counts and sizes, timestamps.

**Recovery**
- Changing the password re-wraps keys.
- A forgotten password or a lost Secret Key can only be recovered through **recovery groups** (family or business admins, who hold an encrypted copy of the vault keys) or recovery codes. Otherwise the data is gone.

**Weaknesses**
- Public keys are server-distributed without strong authentication, so key substitution by a malicious server is possible for sharing, as 1Password acknowledges.
- The Secret Key is a usability burden.
- The sign-in web client is served by 1Password.

---

## 6. Apple iCloud Keychain (brief)

**Sources:** Apple Platform Security, "Secure keychain syncing" and "Escrow security for iCloud Keychain" (both published 2024-05-07 **[STALE?]**, check for newer Platform Security editions): https://support.apple.com/guide/security/secure-keychain-syncing-sec0a319b35f/web, https://support.apple.com/guide/security/escrow-security-for-icloud-keychain-sec3e341e75d/web

- **Keys and the circle of trust.** Each device has a P-384 syncing identity. Devices form a signed **circle of trust**: a new device must be approved by an existing one, or join through recovery. Items are encrypted per device peer, so Apple can't decrypt them.
- **Granularity and conflicts.** Each keychain item marked synchronizable is synced on its own. When two devices change the same item, "one version is chosen" (eventual consistency, rule not published).
- **Recovery.** Escrow records are protected by the device passcode / iCloud security code and held in **HSM clusters**, with SRP verifying the code. After **10 failed attempts the HSM destroys the record**.
- **Authentication.** The Apple Account authenticates the user and is separate from the keychain keys.
- **Weaknesses.** A short device passcode is the recovery secret, and its security rests on HSM rate limiting rather than on the KDF. Hobby projects can't replicate that.

---

## 7. LastPass 2022 breach: lessons

**Sources:** LastPass incident notices (2022-12 / 2023-03) via https://www.ghacks.net/?p=186432 (secondary); Hive Systems cracking analysis https://www.hivesystems.com/blog/examining-the-lastpass-breach-through-our-password-table (2023, secondary). No primary LastPass page was fetched this round.

- Stolen vault backups contained **website URLs in the clear**. Attackers could see where each user has accounts and target high-value vaults (for example, crypto exchanges).
- Old accounts had PBKDF2 iteration counts as low as **5,000, some as low as 1**. Raising the default later didn't help vaults already stolen, because **a server breach freezes the parameters at the moment of theft**.
- Reported follow-up: cryptocurrency thefts in 2023–2025 attributed to cracked vaults with weak master passwords (secondary sources only).
- **Lessons:**
  1. Encrypt *all* item metadata, including URLs.
  2. Use a strong memory-hard KDF from day one.
  3. Assume the ciphertext will leak, so password strength (or a second secret) is what's left.

---

## Recommendations for Zing

### Where the intended design is wrong or incomplete

1. **"Gated by Windows Hello" with DPAPI only gates the UI, not the crypto.** DPAPI (CurrentUser) decrypts silently for any process running as the user, so malware or another app under the same account gets the whole vault without a Hello prompt. Make Hello a **cryptographic** gate instead: wrap the local vault key with a key that only becomes available after Hello verification. One way is `KeyCredentialManager` (Windows Hello key in the TPM): request a signature over a fixed challenge and HKDF it into a wrapping key. KeePassXC and Bitwarden desktop use variants of this. **[verify]** The details of signature determinism need checking, because the trick relies on RSA PKCS#1 v1.5 signatures being deterministic. Keep DPAPI only as an outer layer.
2. **A client-supplied `updatedAt` is not a safe sync order.** Clocks skew, so a device with a fast clock always wins. And if `updatedAt` is stored in the clear, it leaks edit times. Use **server-assigned revision numbers** for sync order, and keep the user-facing `updatedAt` inside the ciphertext. Use it only to choose a winner in an actual conflict.
3. **A 30-day tombstone expiry resurrects deletes.** A device offline for more than 30 days re-uploads logins that were deleted elsewhere. The server must know its purge horizon (the oldest revision for which tombstones still exist). A client whose `since` is older than that must do a **full resync** and treat local-only items it can't prove are new as "possibly deleted elsewhere". Firefox and Chromium sync both handle this with a reset or full sync.
4. **Password + scrypt alone means a breach of the Railway server allows offline guessing.** The server holds the wrapped vault key, and a single AEAD-tag check per guess confirms it. Google OAuth doesn't help here. Either require a strong password (below) or add a 1Password-style **Secret Key**. The second is the bigger win.

### KDF: Argon2id, not scrypt

- .NET has **neither scrypt nor Argon2 built in** (`System.Security.Cryptography` has PBKDF2 via `Rfc2898DeriveBytes.Pbkdf2`, `AesGcm` and `ChaCha20Poly1305`). Scrypt therefore saves no dependency, so pick the better one, Argon2id (RFC 9106, OWASP's first choice).
- **Library options** (NuGet, read 2026-11-07):

  | Library | Version, date | Notes |
  |---|---|---|
  | **NSec.Cryptography** | 26.4.0, 2026-04-30 | ~19.8M downloads. libsodium-backed: Argon2id, AES-256-GCM, XChaCha20-Poly1305. **Recommended.** libsodium's Argon2id is single-lane (p=1), which is fine on a client. |
  | Geralt | 4.5.0, 2026-10-31 | libsodium-backed, has Argon2id and XChaCha20-Poly1305. Active, smaller user base. |
  | Konscious.Security.Cryptography.Argon2 | 1.3.1, 2024-06-19 | 12.7M downloads, pure managed. Slower, and **not updated in over 2 years [STALE?]**. |
  | Isopoh.Cryptography.Argon2 | 2.0.0, 2023-08-17 | **Stale.** |

- **Parameters.** OWASP's Password Storage Cheat Sheet (https://cheatsheetseries.owasp.org/cheatsheets/Password_Storage_Cheat_Sheet.html, no date, read 2026-11-07) gives *server-side minimums*: Argon2id m=19 MiB/t=2/p=1 or m=46 MiB/t=1/p=1. Its scrypt minimum is N=2^17, r=8, p=1. Those floors are for servers hashing many logins. A client derives the key once per unlock, so it can afford far more.
  - **Recommended for Zing:** Argon2id with **m = 256 MiB, t = 3, p = 1** (libsodium's `MODERATE` level). Aim for about 0.5–1 s on the slowest target PC.
  - **Store the parameters and salt with the wrapped key, authenticated as AEAD associated data, and enforce a hard-coded minimum on the client.** Never accept weaker parameters from the server. This is the KDF-downgrade lesson from the ETH paper.
- Run the KDF only when the password is entered: setup, a new device, or a password change. Daily unlock goes through Windows Hello.

### Key hierarchy

```
password ─(NFKD)─► Argon2id(salt₁₆, m,t,p) ─┐
Secret Key (128-bit, optional but recommended) ─HKDF─┴─XOR/HKDF─► KEK (256-bit)
KEK ──AEAD──► wraps random 256-bit Vault Key (VK)          [server stores: salt, params, wrapped VK]
VK ──HKDF(info="zing item v1")──► item key (or VK directly with random nonces)
item key ──AEAD(aad = userId|itemId|revision|schemaVersion)──► item ciphertext
```

- **Password change:** re-derive the KEK and re-wrap the VK. That is one upload and no item re-encryption.
- **Suspected compromise:** generate a new VK, re-encrypt every item, and bump a `keyVersion`.
- **Forgotten password:** without escrow the synced copy is unrecoverable. A device that is still unlocked can set a new password, because it holds the VK. Offer a printed **recovery key**, which is a second wrap of the VK under a random 128-bit code. For two users that is simple and honest.
- **Secret Key option:** generate it on the device and show it once (QR code plus text). New devices need it. This turns "server breach + weak password" into "server breach + 128 random bits", which is unguessable. Brave's chain code and 1Password show the trade-off.

### Authenticated encryption

- **XChaCha20-Poly1305 via NSec/libsodium.** Its 192-bit nonce can be **random for every encryption** with no counter state across devices. That matters because several devices encrypt under the same VK and none of them coordinate.
- AES-256-GCM (`System.Security.Cryptography.AesGcm`) is fine only if nonce reuse is impossible. With 96-bit random nonces the NIST limit is about 2^32 encryptions per key, which is plenty for two users. But a nonce reuse breaks GCM catastrophically (key-stream and authentication-key recovery). If you do use GCM, derive a **per-item key** (HKDF(VK, itemId‖revision)) so that a nonce collision can only happen within one item version.
- **Always bind context in the AAD:** user ID, item ID, revision and schema version. That stops the server swapping ciphertexts between items or users, which Bitwarden had to fix after the ETH paper. Never use unauthenticated modes, and don't support "legacy" formats. Dashlane's downgrade came from keeping legacy cryptography.

### Metadata to hide

- **Inside the ciphertext:** URL/origin, username, password, password history, notes, `createdAt`, `updatedAt`, last used, and the favourite flag. The LastPass lesson: no URL in the clear. Find logins for autofill **locally** after decrypting. The server never needs to search.
- **Visible to the server (and fine):** a random item ID (a GUID, not derived from the URL), the server revision, the deleted flag, the ciphertext length.
- **Hide sizes:** pad the plaintext to a bucket (for example the next multiple of 256 bytes) so the length doesn't show long notes or URLs.
- **Entry counts:** with two users, hiding the count isn't worth dummy records. Accept it and note it.
- **Item history:** the losing passwords should live **inside the item's ciphertext** (as in Bitwarden), not as separate server rows.

### Server side

- **Never send a password-derived value to the server.** Zing is already better than Firefox and Bitwarden here, because Google OAuth handles authentication. Keep it that way: no "password hash" for login and no verifier. The client learns whether the password is right from the AEAD tag on the wrapped VK, locally. If you ever add password-based login, use an aPAKE (OPAQUE), or at least SRP as 1Password does.
- **Authentication:** verify the Google ID token server-side (audience, issuer, expiry), and then issue Zing's own short-lived session or bearer token. Allow-list the two Google `sub` IDs, not email addresses. Scope every query by `userId` from the token, never from the request body.
- **Rate limits:** per-token and per-IP limits on all endpoints (for example 60 requests per minute), a body size limit (for example 1 MiB per batch), a cap on item count per user, and lockout or alerting on repeated auth failures. Rate limits don't protect against offline guessing after a breach. Only the KDF and password strength do.
- **Rollback and replay protection.** A malicious or restored-from-backup server can serve old ciphertext.
  - The client stores the highest `revision` it has seen for each item and for the whole account, and rejects any item whose revision is lower. Revision is in the AAD, so the server can't relabel an old ciphertext with a new revision.
  - Store an encrypted **account manifest**: a MAC'd {account revision, item ID → revision} map, signed with a key derived from the VK. Then the client can detect items being withheld, deleted by the server, or rolled back as a set. Dashlane accepted the risk of replay, reorder and drop. A manifest is cheap at this scale.
  - Updates use optimistic concurrency: `PUT item {baseRevision}`, and the server returns 409 if the stored revision ≠ `baseRevision`. This is Bitwarden's `LastKnownRevisionDate` idea, but with an integer instead of a timestamp.
- **SQLite on a Railway volume:** turn on WAL mode and take regular backups. A backup restore is a rollback, which the manifest above will catch, and the client should then re-upload.

### Conflicts (revised)

- When the server returns 409, the client fetches the server version, decrypts it, and does a **three-way merge** against the last-synced base (as Firefox does). Only if the same field changed on both sides does it fall back to LWW by the encrypted `updatedAt`, and the losing password goes into history. Then it re-PUTs with the new `baseRevision`.

### Efficiency for a hobby server

- **Delta sync** by a monotonically increasing per-user revision:
  - `GET /sync?since=<rev>` returns every item and tombstone with a revision greater than `since`, plus the current revision and the tombstone horizon.
  - If `since` is older than the horizon, the server returns `410` and the client does a full resync.
- A full sync of a few hundred logins is only tens of KB, so Bitwarden's full-sync approach would also work fine for two users. Delta sync is still the better choice, because the server needs revisions for 409 conflicts and rollback detection anyway.
- **Batch writes:** `POST /items:batch` with up to about 100 items per request, all-or-nothing in one SQLite transaction. Every accepted item gets the next revision.
- **Polling:** check `GET /revision` (one integer) on app start, when the window gets focus, and every 5–15 minutes. Push notifications aren't needed.

### Is "server breach + weak password" the main risk?

**Yes,** given the rest of the design. TLS, OAuth and AEAD with AAD handle network attackers and tampering. The remaining realistic attacks are:

1. A server or volume breach followed by offline guessing. LastPass shows this risk is real.
2. Local malware under the user's account. The DPAPI point above covers this.
3. A malicious server serving old or withheld data. The manifest covers this.

**Minimum password strength (without a Secret Key)**
- Rough cost model: Argon2id at 256 MiB and t=3 limits an attacker with a multi-GPU rig to roughly 10^2–10^4 guesses per second. **[estimate; benchmark before relying on it]**
- At 10^4 guesses per second, a 40-bit password falls in about 1.7 years on average, while a 60-bit one takes about 1.8 million years.
- **Require at least about 60 bits:** a generated 5-word diceware passphrase (about 64 bits), or a zxcvbn score of 4 with an estimate of at least 10^18 guesses. Show the user a generated passphrase by default.
- **With a Secret Key**, the password only has to resist online and shoulder-surfing attacks. A zxcvbn score of 3 or higher is enough, and the breach risk is effectively gone.

### Summary of changes to the intended design

| Intended | Change |
|---|---|
| DPAPI + Hello prompt | Bind the vault key to a Windows Hello (TPM) key; keep DPAPI as an outer layer |
| scrypt | Argon2id (NSec), 256 MiB / t=3 / p=1, parameters authenticated and with a client-side minimum |
| password → key | password (+ optional 128-bit Secret Key) → KEK → wraps random VK; plus a recovery key |
| per-login records | Keep, with XChaCha20-Poly1305, AAD = user\|item\|revision, padded, URL inside |
| `updatedAt` LWW | Server revision + 409 on stale `baseRevision`; three-way merge, LWW by encrypted `updatedAt` only for same-field conflicts |
| 30-day tombstones | Keep, plus a horizon that forces a full resync for stale clients |
| (none) | Per-item revision floor + signed manifest against rollback and withholding |
| Google OAuth | Keep; verify the ID token, allow-list `sub`, and never send password-derived data |
