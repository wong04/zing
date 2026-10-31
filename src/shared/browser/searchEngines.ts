import type { Settings } from '../settings'

/** Each engine's search URL, the query appended. Shared with the C# host via host/Zing.Core/Defaults. */
export const SEARCH_URLS: Readonly<Record<Settings['searchEngine'], string>> = {
	google: 'https://www.google.com/search?q=',
	duckduckgo: 'https://duckduckgo.com/?q=',
	bing: 'https://www.bing.com/search?q=',
	brave: 'https://search.brave.com/search?q=',
	kagi: 'https://kagi.com/search?q=',
	startpage: 'https://www.startpage.com/sp/search?q=',
}
