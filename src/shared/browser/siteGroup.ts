// Port of Krackn's `SiteGroup` (Features/Browser/TabOrganizer.swift).
import { registrableDomain } from './registrableDomain'

export interface SiteGroup {
	/** What tabs are grouped by. */
	key: string
	/** What the dropdown is called. */
	name: string
}

const docsEditors: Readonly<Record<string, string>> = {
	document: 'Google Docs',
	spreadsheets: 'Google Sheets',
	presentation: 'Google Slides',
	forms: 'Google Forms',
	drawings: 'Google Drawings',
}

const products: Readonly<Record<string, string>> = {
	mail: 'Gmail', drive: 'Google Drive', gemini: 'Gemini',
	calendar: 'Google Calendar', meet: 'Google Meet', maps: 'Google Maps',
	photos: 'Google Photos', keep: 'Google Keep', classroom: 'Google Classroom',
	translate: 'Google Translate', sites: 'Google Sites', chat: 'Google Chat',
	contacts: 'Google Contacts', news: 'Google News', scholar: 'Google Scholar',
	accounts: 'Google Account', myaccount: 'Google Account', notebooklm: 'NotebookLM',
	play: 'Google Play', earth: 'Google Earth', books: 'Google Books',
	jamboard: 'Jamboard', voice: 'Google Voice', groups: 'Google Groups',
	analytics: 'Google Analytics', ads: 'Google Ads', 'console.cloud': 'Google Cloud',
	cloud: 'Google Cloud', 'colab.research': 'Colab', aistudio: 'Google AI Studio',
}

function own(table: Readonly<Record<string, string>>, key: string): string | undefined {
	return Object.hasOwn(table, key) ? table[key] : undefined
}

const twoLetters = /^\p{L}{2}$/u

/**
 * google.com and its country domains — google.de, google.co.uk,
 * google.com.au — and nothing else that merely starts with "google.".
 */
export function isGoogle(domain: string): boolean {
	if (!domain.startsWith('google.')) return false
	const rest = domain.slice('google.'.length).split('.')
	const [first = '', second = ''] = rest
	if (rest.length === 1) return first === 'com' || twoLetters.test(first)
	if (rest.length === 2) return (first === 'co' || first === 'com') && twoLetters.test(second)
	return false
}

/**
 * Which dropdown a page belongs to.
 *
 * One per domain — except Google, where one domain is a dozen unrelated
 * products: a Doc, a Gmail inbox, a Gemini chat and a search all report
 * `google.com`, so they piled into one "google.com" dropdown. Google's
 * products each get their own, named for the product. Docs, Sheets and
 * Slides even share a host (`docs.google.com`), so the path decides those.
 *
 * `domain` is the host's registrable domain (`registrableDomain`).
 */
export function siteGroupOf(rawHost: string, path: string, domain: string): SiteGroup {
	const host = rawHost.toLowerCase()
	if (!isGoogle(domain)) return { key: domain, name: domain }
	const sub = host.endsWith('.' + domain) ? host.slice(0, -(domain.length + 1)) : ''
	const first = (path.split('/').find((segment) => segment.length > 0) ?? '').toLowerCase()

	if (sub === 'docs') {
		const editor = own(docsEditors, first)
		return { key: `docs.${domain}/${editor === undefined ? '' : first}`, name: editor ?? 'Google Docs' }
	}
	if (sub === '' || sub === 'www') {
		if (first === 'maps') return { key: `${domain}/maps`, name: 'Google Maps' }
		return { key: domain, name: 'Google' }
	}
	// The product's own subdomain; an unknown one is named by its host.
	return { key: `${sub}.${domain}`, name: own(products, sub) ?? host }
}

/** The group for a page's address, as `BrowserTab.snapshot` derives it. */
export function siteGroupOfURL(url: URL): SiteGroup {
	return siteGroupOf(url.hostname, url.pathname, registrableDomain(url.hostname))
}
