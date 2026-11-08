// The password vault's display-side rules, over login summaries: origin
// matching, whether to offer saving, and what "Recently deleted" holds. The
// host owns the file format, the encryption and every password; the C# twin,
// Zing.Core.Vault.VaultModel, is the source of truth and vault.vectors.json
// holds both to the same cases. Nothing here ever sees a password.

// What the host lets the renderer know about a login: everything but the
// password. Times are Unix milliseconds.
export interface LoginSummary {
	id: string
	origin: string
	username: string
	title: string
	note: string
	createdAt: number
	updatedAt: number
	passwordUpdatedAt: number
	deletedAt: number | null
}

// A login to save (vault:save). The host stores the origin of `origin`, so any
// address on the site will do. The password goes to the host and never comes back
// except through vault:reveal, after Windows Hello.
export interface NewLogin {
	origin: string
	username: string
	password: string
	title: string
	note: string
}

// An edit (vault:update). With `password` the host asks Windows Hello and moves
// the old password into the history; without it, nothing is asked.
export interface LoginEdit {
	username: string
	title: string
	note: string
	password?: string
}

// One old password, without the password; newest first.
export interface PasswordHistoryEntry {
	replacedAt: number
}

// What the host pushes as vault:state. `enabled` follows passwordAutofill;
// without Windows Hello the vault reads but refuses every write.
export interface VaultState {
	enabled: boolean
	helloAvailable: boolean
	// Logins not in Recently deleted.
	count: number
	// Set when a vault file couldn't be read at launch: the vault is off and its files are left as they are.
	error?: 'unreadable'
	// Vault files are on disk, whether or not the vault is on or could be read. The privacy banner warns about them.
	filesExist: boolean
}

// Why a PC isn't signed in to sync, when Settings has something to say about it: the server ended the
// sign-in (a refused refresh token), the saved sign-in can't be decrypted on this PC, or (from a sign-out
// only) the server couldn't be reached, so its copy of the sign-in expires within 90 days.
export type SyncNotice = 'signed-out' | 'unreadable' | 'kept-on-server'

// Why the last sync stopped. `server-rolled-back` (the server went back to an older copy) and `server-withheld-item` (it held a
// login back) are answered with sync:upload-local; the rest clear on a sync that works.
export type SyncError =
	| 'server-rolled-back'
	| 'server-withheld-item'
	| 'server-tampered'
	| 'server-invalid'
	| 'unreachable'
	| 'server-error'
	| 'account-changed'
	| 'failed'

// The errors Upload this PC's copy answers.
export function offersUploadLocal(error: SyncError | undefined): boolean {
	return error === 'server-rolled-back' || error === 'server-withheld-item'
}

// Sync as Settings shows it (sync:get, sync:state). The bridge leaves out null fields. `on`: this PC turned sync on for the
// signed-in account; `recoveryPending`: this PC turned sync on and the recovery key isn't yet confirmed saved, whoever is signed
// in now, so Settings shows nothing else. While on: `unsent`, the logins that haven't uploaded yet, and `error`/`message`, the last
// sync's error (unset after one that worked).
export type SyncState =
	| {
			signedIn: true
			email: string
			on: boolean
			recoveryPending: boolean
			unsent: number
			error?: SyncError
			message?: string
			/** Sync found the Zing password changed on another PC: the next start asks for the new one. */
			passwordChangedElsewhere?: boolean
			/** keys.bin holds sync keys (pending ones included), for any account: Settings offers Turn off sync on this PC. */
			keysHere: boolean
	  }
	| { signedIn: false; notice?: SyncNotice; on: false; recoveryPending: boolean; keysHere: boolean }

// The recovery key, as sync:turn-on and sync:show-recovery give it: 26 base32 characters in groups of four.
export interface RecoveryKeyShown {
	recoveryKey: string
}

// "Never for this site", kept per exact origin apart from the logins.
export interface NeverSave {
	origin: string
}

export class NotAWebOriginError extends Error {
	override name = 'NotAWebOriginError'
}

export const DELETED_RETENTION_MS = 30 * 24 * 60 * 60 * 1000

// Exact origin, not the registrable domain: a password saved on
// accounts.example.com must not be offered to evil.example.com. URL already
// lower-cases the host and drops default ports.
export function originOf(url: string): string {
	if (!URL.canParse(url)) throw new NotAWebOriginError(`Not a URL: ${url}`)
	const parsed = new URL(url)
	if (parsed.protocol !== 'https:' && parsed.protocol !== 'http:') throw new NotAWebOriginError(`Only http(s) pages have saved passwords: ${url}`)
	return parsed.origin
}

export interface FillContext {
	topUrl: string
	frameUrl: string
}

// Krackn's rule: fill the main frame and same-origin frames only. A
// cross-origin frame is someone else's page embedded in this one, so it is
// never offered anything, even if it has its own saved logins.
export function canFill(context: FillContext): boolean {
	return originOf(context.frameUrl) === originOf(context.topUrl)
}

export function entriesFor(summaries: readonly LoginSummary[], context: FillContext): LoginSummary[] {
	if (!canFill(context)) return []
	const origin = originOf(context.topUrl)
	return summaries.filter((s) => s.deletedAt === null && s.origin === origin).sort((a, b) => b.updatedAt - a.updatedAt)
}

export type SaveDecision = 'save' | 'update' | 'none'

// The host compares the typed password with the stored one, since only it can
// read passwords, and passes the answer in. One login per (origin, username):
// a changed password on the site updates it. A login in Recently deleted
// doesn't count as saved.
export function saveDecision(summaries: readonly LoginSummary[], origin: string, username: string, passwordChanged: boolean, neverSave: readonly NeverSave[]): SaveDecision {
	if (neverSave.some((n) => n.origin === origin)) return 'none'
	const saved = summaries.some((s) => s.deletedAt === null && s.origin === origin && s.username === username)
	if (!saved) return 'save'
	return passwordChanged ? 'update' : 'none'
}

export function recentlyDeleted(summaries: readonly LoginSummary[], now: number): LoginSummary[] {
	return summaries.filter((s) => s.deletedAt !== null && !dueForTombstone(s, now)).sort((a, b) => (b.deletedAt ?? 0) - (a.deletedAt ?? 0))
}

export function dueForTombstone(summary: LoginSummary, now: number): boolean {
	if (summary.deletedAt === null) return false
	return now - summary.deletedAt >= DELETED_RETENTION_MS
}

// Holding any login, deleted ones included, or a "never for this site" mark
// (Ruling 39: it names a site) is a stored record, so Settings' privacy banner
// warns.
export function vaultKeepsData(summaries: readonly LoginSummary[], neverSave: readonly NeverSave[]): boolean {
	return summaries.length > 0 || neverSave.length > 0
}

// What vault:import-csv did: logins added, rows that matched a login the vault
// already held (or an earlier row) and were merged into it, and each row it
// could not use. `row` counts the file's header as row 1.
export interface VaultImportResult {
	added: number
	merged: number
	skipped: { row: number; reason: string }[]
}
