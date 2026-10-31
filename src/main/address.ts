import type { Settings } from '@shared/settings'
import { classify } from '@shared/browser/addressInput'
import { stripTrackers } from '@shared/browser/linkCleaner'
import { SEARCH_URLS } from '@shared/browser/searchEngines'

/** The engine's search endpoint without the query, for a form that adds `?q=` itself. */
export function searchFormAction(engine: Settings['searchEngine']): string {
	return SEARCH_URLS[engine].replace(/\?q=$/, '')
}

/** Typed text to the URL to load: a place to go (tracker-free) or a search on the chosen engine. Undefined for blank input. */
export function resolveAddress(input: string, engine: Settings['searchEngine']): string | undefined {
	const result = classify(input)
	if (!result) return undefined
	if (result.kind === 'url') return stripTrackers(result.url)
	return SEARCH_URLS[engine] + encodeURIComponent(result.text)
}
