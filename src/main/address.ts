import type { Settings } from '@shared/settings'
import { classify } from '@shared/browser/addressInput'
import { stripTrackers } from '@shared/browser/linkCleaner'

const SEARCH_URLS: Record<Settings['searchEngine'], string> = {
	google: 'https://www.google.com/search?q=',
	duckduckgo: 'https://duckduckgo.com/?q=',
	bing: 'https://www.bing.com/search?q=',
	brave: 'https://search.brave.com/search?q=',
	kagi: 'https://kagi.com/search?q=',
	startpage: 'https://www.startpage.com/sp/search?q=',
}

/** Typed text to the URL to load: a place to go (tracker-free) or a search on the chosen engine. Undefined for blank input. */
export function resolveAddress(input: string, engine: Settings['searchEngine']): string | undefined {
	const result = classify(input)
	if (!result) return undefined
	if (result.kind === 'url') return stripTrackers(result.url)
	return SEARCH_URLS[engine] + encodeURIComponent(result.text)
}
