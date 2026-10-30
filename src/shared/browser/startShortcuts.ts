// Port of Krackn's `StartShortcuts` (Features/Browser/StartShortcuts.swift). Storage is the
// caller's: keep the `encode`d string, and remove it (store nothing) to restore the defaults.
import { classify } from './addressInput'

/** One tile on the new-tab page. Its identity is its `url`. */
export interface StartShortcut {
	title: string
	url: string
}

/** Enough to fill four rows of the page's grid without it turning into a bookmarks list. */
export const limit = 16

export const defaults: readonly StartShortcut[] = [
	{ title: 'GitHub', url: 'https://github.com' },
	{ title: 'Stack Overflow', url: 'https://stackoverflow.com' },
	{ title: 'arXiv', url: 'https://arxiv.org' },
	{ title: 'MDN', url: 'https://developer.mozilla.org' },
	{ title: 'Wikipedia', url: 'https://wikipedia.org' },
	{ title: 'Wolfram Alpha', url: 'https://www.wolframalpha.com' },
	{ title: 'Desmos', url: 'https://www.desmos.com/calculator' },
	{ title: 'Hacker News', url: 'https://news.ycombinator.com' },
]

/** The built-in tiles' own symbols; anything you add shows its initial. */
const glyphs: ReadonlyMap<string, string> = new Map([
	['https://github.com', '◱'],
	['https://stackoverflow.com', '◲'],
	['https://arxiv.org', '✦'],
	['https://developer.mozilla.org', '◳'],
	['https://wikipedia.org', '◈'],
	['https://www.wolframalpha.com', '∑'],
	['https://www.desmos.com/calculator', '∿'],
	['https://news.ycombinator.com', '▲'],
])

const graphemeSegmenter = new Intl.Segmenter(undefined, { granularity: 'grapheme' })

/** User-perceived characters, as Swift's `Character` counts them. */
function graphemes(text: string): string[] {
	return Array.from(graphemeSegmenter.segment(text), (part) => part.segment)
}

export function glyph(shortcut: StartShortcut): string {
	const builtIn = glyphs.get(shortcut.url)
	if (builtIn !== undefined) return builtIn
	const first = graphemes(shortcut.title.trim())[0]
	return first === undefined ? '•' : first.toUpperCase()
}

function isShortcut(value: unknown): value is StartShortcut {
	if (typeof value !== 'object' || value === null) return false
	const { title, url } = value as Record<string, unknown>
	return typeof title === 'string' && typeof url === 'string'
}

function parseShortcutList(raw: string): StartShortcut[] | null {
	let parsed: unknown
	try {
		parsed = JSON.parse(raw)
	} catch (error) {
		if (error instanceof SyntaxError) return null
		throw error
	}
	if (!Array.isArray(parsed) || !parsed.every(isShortcut)) return null
	return parsed.map(({ title, url }) => ({ title, url }))
}

/**
 * Nothing stored, or something unreadable → the built-in set (Krackn's spec:
 * the new-tab page must always have tiles). Entries that aren't web addresses
 * are dropped rather than rendered: the list ends up in a page's `href`s, and
 * `javascript:` must never get there.
 */
export function decode(raw: string | null): StartShortcut[] {
	const list = raw === null ? null : parseShortcutList(raw)
	if (list === null) return [...defaults]
	return list.filter((shortcut) => isWebAddress(shortcut.url)).slice(0, limit)
}

export function encode(list: readonly StartShortcut[]): string {
	return JSON.stringify(list.slice(0, limit).map(({ title, url }) => ({ title, url })))
}

/**
 * A shortcut from what was typed: the address by the same rule as the
 * address bar (`apple.com` → https://apple.com), the name if one was
 * given, else one made from the site. Null if it isn't a web address.
 */
export function make(rawTitle: string, address: string): StartShortcut | null {
	const result = classify(address)
	if (result?.kind !== 'url' || !isWebAddress(result.url)) return null
	const title = rawTitle.trim()
	return {
		title: title === '' ? name(result.url) : graphemes(title).slice(0, 40).join(''),
		url: result.url,
	}
}

/**
 * The host as written. `URL.hostname` would lowercase it and turn `bücher.de`
 * into `xn--bcher-kva.de`, which is no name for a tile.
 */
function hostAsWritten(url: string): string {
	const afterScheme = url.slice(url.indexOf('://') + 3)
	const authority = afterScheme.split(/[/?#]/)[0] ?? ''
	const hostAndPort = authority.slice(authority.lastIndexOf('@') + 1)
	return hostAndPort.replace(/:\d*$/, '')
}

/** `www.apple.com` → "Apple", `docs.google.com` → "Docs". */
export function name(url: string): string {
	let host = hostAsWritten(url)
	if (host === '') host = url
	if (host.startsWith('www.')) host = host.slice(4)
	const first = host.split('.').find((label) => label.length > 0) ?? host
	const [initial = '', ...rest] = graphemes(first)
	return initial.toUpperCase() + rest.join('')
}

export function isWebAddress(text: string): boolean {
	if (!URL.canParse(text)) return false
	const url = new URL(text)
	return (url.protocol === 'https:' || url.protocol === 'http:') && url.hostname !== ''
}

const htmlEscapes: Readonly<Record<string, string>> = {
	'&': '&amp;',
	'<': '&lt;',
	'>': '&gt;',
	'"': '&quot;',
	"'": '&#39;',
}

/** For putting your names and addresses into the page's HTML. */
export function escapeHTML(text: string): string {
	return text.replace(/[&<>"']/g, (c) => htmlEscapes[c] ?? c)
}
