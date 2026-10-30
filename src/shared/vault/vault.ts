// The password vault's pure side: entries, origin matching, import, and the
// on-disk shape. Encryption (safeStorage/DPAPI) and the Windows Hello prompt
// live in the main process; nothing here ever sees a key.

export interface VaultEntry {
	id: string
	origin: string
	username: string
	password: string
	title: string
	note: string
	createdAt: number
	updatedAt: number
}

export interface Vault {
	version: 1
	entries: VaultEntry[]
}

export class VaultFormatError extends Error {
	override name = 'VaultFormatError'
}

export class NotAWebOriginError extends Error {
	override name = 'NotAWebOriginError'
}

export function emptyVault(): Vault {
	return { version: 1, entries: [] }
}

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

export function entriesFor(vault: Vault, context: FillContext): VaultEntry[] {
	if (!canFill(context)) return []
	const origin = originOf(context.topUrl)
	return vault.entries.filter((entry) => entry.origin === origin).sort((a, b) => b.updatedAt - a.updatedAt)
}

export interface NewLogin {
	url: string
	username: string
	password: string
	title?: string
	note?: string
}

export type SaveOutcome = { kind: 'added'; entry: VaultEntry } | { kind: 'updated'; entry: VaultEntry } | { kind: 'unchanged'; entry: VaultEntry }

// One entry per (origin, username): saving again with a new password updates
// it, which is what a password change on the site should do.
export function saveLogin(vault: Vault, login: NewLogin, now = Date.now()): { vault: Vault; outcome: SaveOutcome } {
	const origin = originOf(login.url)
	const existing = vault.entries.find((e) => e.origin === origin && e.username === login.username)
	if (existing && existing.password === login.password) return { vault, outcome: { kind: 'unchanged', entry: existing } }
	if (existing) {
		const entry = { ...existing, password: login.password, updatedAt: now }
		return { vault: { ...vault, entries: vault.entries.map((e) => (e.id === existing.id ? entry : e)) }, outcome: { kind: 'updated', entry } }
	}
	const entry: VaultEntry = {
		id: crypto.randomUUID(),
		origin,
		username: login.username,
		password: login.password,
		title: login.title?.trim() || new URL(origin).hostname,
		note: login.note ?? '',
		createdAt: now,
		updatedAt: now,
	}
	return { vault: { ...vault, entries: [...vault.entries, entry] }, outcome: { kind: 'added', entry } }
}

export function removeEntry(vault: Vault, id: string): Vault {
	if (!vault.entries.some((e) => e.id === id)) throw new VaultFormatError(`No vault entry ${id}`)
	return { ...vault, entries: vault.entries.filter((e) => e.id !== id) }
}

export interface ImportSummary {
	added: number
	updated: number
	unchanged: number
}

export function importLogins(vault: Vault, logins: readonly NewLogin[], now = Date.now()): { vault: Vault; summary: ImportSummary } {
	const summary: ImportSummary = { added: 0, updated: 0, unchanged: 0 }
	let current = vault
	for (const login of logins) {
		const result = saveLogin(current, login, now)
		current = result.vault
		summary[result.outcome.kind]++
	}
	return { vault: current, summary }
}

// Holding any entry is a stored record, so Settings' privacy banner warns.
export function vaultKeepsData(vault: Vault): boolean {
	return vault.entries.length > 0
}

export function serializeVault(vault: Vault): string {
	return JSON.stringify(vault)
}

export function parseVault(json: string): Vault {
	const data = JSON.parse(json) as Partial<Vault>
	if (data.version !== 1) throw new VaultFormatError(`Unsupported vault version: ${String(data.version)}`)
	if (!Array.isArray(data.entries)) throw new VaultFormatError('Vault has no entries array')
	data.entries.forEach((entry, index) => assertEntry(entry, index))
	return { version: 1, entries: data.entries }
}

function assertEntry(entry: unknown, index: number): asserts entry is VaultEntry {
	const e = entry as Record<string, unknown>
	const strings = ['id', 'origin', 'username', 'password', 'title', 'note'] as const
	const numbers = ['createdAt', 'updatedAt'] as const
	for (const key of strings) if (typeof e[key] !== 'string') throw new VaultFormatError(`Entry ${index}: "${key}" must be a string`)
	for (const key of numbers) if (typeof e[key] !== 'number') throw new VaultFormatError(`Entry ${index}: "${key}" must be a number`)
}
