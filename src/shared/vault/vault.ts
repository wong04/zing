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
	count: number
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

// Holding any login, deleted ones included, is a stored record, so Settings'
// privacy banner warns.
export function vaultKeepsData(summaries: readonly LoginSummary[]): boolean {
	return summaries.length > 0
}
