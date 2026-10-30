// Port of Krackn App/ToolboxSearch.swift.
//
// Finding a tool by what you want to do, not by what it happens to be called.
// Nobody looks for "Hashes" — they look for "sha256", or "checksum". Nobody
// thinks "Open a Link" when a site is blocked; they think "unblock". Every
// Toolbox item carries the words people actually reach for, and results are
// ranked so the obvious answer is first and Return opens it.

export type Entry = {
	/** `panelId`, e.g. "tool:Hashes". */
	readonly id: string
	readonly title: string
	readonly group: string
	readonly keywords: readonly string[]
}

/** Best first. Entries that don't match are left out; ties keep catalog order, so related tools stay together. */
export function rank(query: string, entries: readonly Entry[]): Entry[] {
	const tokens = query
		.toLowerCase()
		.split(/[ \-_]/)
		.filter((token) => token.length > 0)
	if (tokens.length === 0) return [...entries]
	return entries
		.map((entry, index) => ({ entry, index, score: score(tokens, entry) }))
		.filter((scored): scored is { entry: Entry; index: number; score: number } => scored.score !== null)
		.sort((a, b) => (a.score !== b.score ? b.score - a.score : a.index - b.index))
		.map((scored) => scored.entry)
}

/**
 * Every word typed has to match something; the total is the sum of each
 * word's best match, so "url encode" beats "url" alone on URL Encoding.
 */
export function score(tokens: readonly string[], entry: Entry): number | null {
	// A phrase that matches whole ("remove background") outranks the same
	// words matched one at a time somewhere else.
	const phrase = tokens.length > 1 ? best(tokens.join(' '), entry) : null
	let total = phrase ?? 0
	for (const token of tokens) {
		const s = best(token, entry)
		if (s === null) return null
		total += s
	}
	return total
}

function best(q: string, entry: Entry): number | null {
	const title = entry.title.toLowerCase()
	const words = title.split(/[^\p{L}\p{N}]+/u).filter((word) => word.length > 0)
	const keys = entry.keywords.map((key) => key.toLowerCase())
	const length = [...q].length
	const scores: number[] = []

	if (title === q) scores.push(100)
	if (title.startsWith(q)) scores.push(90)
	if (words.some((word) => word.startsWith(q))) scores.push(80)
	if (keys.includes(q)) scores.push(75)
	if (keys.some((key) => key.startsWith(q))) scores.push(65)
	if (length >= 2 && title.includes(q)) scores.push(50)
	if (entry.group.toLowerCase().startsWith(q)) scores.push(30)
	if (length >= 3 && keys.some((key) => key.includes(q))) scores.push(25)
	// Typing the start of each word — "gc" for Graphing Calculator, "jwtd"
	// for JWT Decoder — is how people abbreviate.
	if (length >= 2 && isSubsequence(q, title.replace(/\p{White_Space}/gu, ''))) scores.push(10)
	return scores.length > 0 ? Math.max(...scores) : null
}

function isSubsequence(needle: string, haystack: string): boolean {
	const rest = haystack[Symbol.iterator]()
	for (const c of needle) {
		let found = false
		for (let h = rest.next(); !h.done; h = rest.next()) {
			if (h.value === c) {
				found = true
				break
			}
		}
		if (!found) return false
	}
	return true
}

/** What people type when they want each feature. Keyed by `panelId`. */
export const keywords: Readonly<Record<string, readonly string[]>> = {
	'section:Files': ['preview', 'open', 'view', 'viewer', 'pdf', 'image', 'video', '3d', 'stl', 'model', 'quicklook'],
	'section:Convert': [
		'convert',
		'format',
		'mp4',
		'mp3',
		'png',
		'jpg',
		'jpeg',
		'heic',
		'webp',
		'gif',
		'pdf',
		'docx',
		'obj',
		'stl',
		'compress',
		'resize',
		'ffmpeg',
		'export',
	],
	'section:Download': ['youtube', 'video', 'audio', 'save page', 'yt-dlp', 'music', 'save video', 'rip'],
	'tool:Background Remover': ['remove background', 'cutout', 'transparent', 'photo', 'erase background', 'mask'],
	'section:Open a Link': ['unblock', 'blocked', 'relay', 'proxy', 'bypass', 'reader', 'censored'],
	'section:Diagnose': ['blocked', 'why', 'test site', 'censorship', 'firewall', 'dns', 'troubleshoot'],
	'section:Network & VPN': ['vpn', 'dns', 'doh', 'wireguard', 'profile', 'encrypted dns', 'tunnel'],
	'tool:Link Cleaner': ['tracking', 'utm', 'clean url', 'strip', 'shorten'],
	'section:Security': [
		'malware',
		'phishing',
		'virus',
		'scan',
		'gatekeeper',
		'xprotect',
		'safe',
		'antivirus',
		'check file',
	],
	'tool:Passwords': ['password', 'generate password', 'passphrase', 'random', 'strength'],
	'tool:Hashes': ['sha256', 'sha1', 'sha512', 'md5', 'checksum', 'hash', 'digest'],
	'tool:HMAC': ['signature', 'sign', 'mac', 'sha256', 'webhook'],
	'tool:JWT Decoder': ['jwt', 'token', 'bearer', 'auth', 'claims', 'decode token'],
	'tool:JSON / YAML': ['json', 'yaml', 'format', 'pretty', 'prettify', 'minify', 'validate', 'beautify'],
	'tool:Base64': ['base64', 'b64', 'encode', 'decode'],
	'tool:URL Encoding': ['percent', 'encode', 'decode', 'escape', 'query string', 'urlencode'],
	'tool:Regex Tester': ['regex', 'regexp', 'regular expression', 'pattern', 'match'],
	'tool:UUID': ['guid', 'uuid', 'identifier', 'random id', 'unique id'],
	'tool:Timestamps': ['epoch', 'unix time', 'date', 'time', 'iso 8601', 'timestamp'],
	'tool:Cron Parser': ['cron', 'crontab', 'schedule'],
	'tool:Scientific Calculator': ['calculator', 'math', 'sin', 'cos', 'log', 'compute', 'arithmetic'],
	'tool:Graphing Calculator': ['graph', 'plot', 'function', 'desmos', 'curve', 'chart'],
	'tool:Unit Converter': [
		'units',
		'metric',
		'imperial',
		'temperature',
		'length',
		'weight',
		'celsius',
		'fahrenheit',
		'miles',
		'km',
	],
	'tool:Number Bases': ['hex', 'hexadecimal', 'binary', 'octal', 'decimal', 'base'],
	'tool:Text Case': ['camelcase', 'snake case', 'uppercase', 'lowercase', 'title case', 'kebab', 'capitalize'],
	'tool:Text Stats': ['word count', 'characters', 'reading time', 'count', 'words'],
	'tool:Color Converter': ['colour', 'color', 'hex', 'rgb', 'hsl', 'picker', 'palette'],
	'tool:QR Code': ['qr', 'barcode', 'code', 'share link'],
}
