import { parseCsvRecords } from './csv'

export interface ImportedLogin {
	title: string
	url: string
	username: string
	password: string
	note: string
}

export type PasswordCsvFormat = 'chromium' | 'bitwarden' | 'onepassword' | 'firefox'

export class UnknownPasswordCsvError extends Error {
	override name = 'UnknownPasswordCsvError'
}

interface ColumnMap {
	title?: string
	url: string
	username: string
	password: string
	note?: string
}

// Chrome, Edge, Brave and Dia all export the Chromium password manager's CSV.
const FORMATS: Record<PasswordCsvFormat, ColumnMap> = {
	chromium: { title: 'name', url: 'url', username: 'username', password: 'password', note: 'note' },
	bitwarden: { title: 'name', url: 'login_uri', username: 'login_username', password: 'login_password', note: 'notes' },
	onepassword: { title: 'title', url: 'url', username: 'username', password: 'password', note: 'notes' },
	firefox: { url: 'url', username: 'username', password: 'password' },
}

export function detectPasswordCsvFormat(headers: readonly string[]): PasswordCsvFormat {
	const has = (...names: string[]) => names.every((n) => headers.includes(n))
	if (has('login_uri', 'login_username', 'login_password')) return 'bitwarden'
	if (has('name', 'url', 'username', 'password')) return 'chromium'
	if (has('title', 'url', 'username', 'password')) return 'onepassword'
	if (has('url', 'username', 'password', 'httprealm')) return 'firefox'
	throw new UnknownPasswordCsvError(`Unrecognised password export columns: ${headers.join(', ')}`)
}

export interface PasswordImport {
	format: PasswordCsvFormat
	logins: ImportedLogin[]
	skipped: { row: number; reason: string }[]
}

// Rows without a web address or password can't be filled into a page, so
// they're reported back rather than silently dropped.
export function parsePasswordCsv(text: string): PasswordImport {
	const { headers, records } = parseCsvRecords(text)
	const format = detectPasswordCsvFormat(headers)
	const map = FORMATS[format]
	const logins: ImportedLogin[] = []
	const skipped: PasswordImport['skipped'] = []
	records.forEach((record, index) => {
		const row = index + 2
		if (format === 'bitwarden' && record['type'] && record['type'] !== 'login') {
			skipped.push({ row, reason: `Bitwarden ${record['type']} item, not a login` })
			return
		}
		const url = (record[map.url] ?? '').trim()
		const password = record[map.password] ?? ''
		if (!isWebUrl(url)) {
			skipped.push({ row, reason: url ? `Not a web address: ${url}` : 'No web address' })
			return
		}
		if (!password) {
			skipped.push({ row, reason: 'No password' })
			return
		}
		logins.push({
			title: (map.title ? record[map.title] : undefined)?.trim() || new URL(url).hostname,
			url,
			username: record[map.username] ?? '',
			password,
			note: (map.note ? record[map.note] : undefined) ?? '',
		})
	})
	return { format, logins, skipped }
}

function isWebUrl(value: string): boolean {
	if (!URL.canParse(value)) return false
	const { protocol } = new URL(value)
	return protocol === 'https:' || protocol === 'http:'
}
