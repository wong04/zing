import { startPageScheme } from '@shared/browser/startPage'
import { NEW_TAB_TITLE, NEW_TAB_URL } from '@shared/model'
import type { TabView } from '@shared/ipc'

/** The start page's URL comes back from Chromium with a trailing slash. */
function isStartPageUrl(url: string): boolean {
	return url.startsWith(`${startPageScheme}://home`)
}

export function tabTitle(tab: TabView): string {
	if (tab.url === NEW_TAB_URL || isStartPageUrl(tab.url)) return NEW_TAB_TITLE
	return tab.title || tab.url
}

/** The address bar shows nothing for a blank tab, so typing starts clean. */
export function addressText(url: string | undefined): string {
	if (url === undefined || url === NEW_TAB_URL || isStartPageUrl(url)) return ''
	return url
}

/** One letter standing in for the favicon until memory-only favicons land (B22). */
export function monogram(tab: TabView): string {
	if (!URL.canParse(tab.url)) return '•'
	const host = new URL(tab.url).hostname.replace(/^www\./, '')
	return host ? host[0]!.toUpperCase() : '•'
}

/** What the chrome says about a tab whose page isn't working; undefined while it is. */
export function pageFaultText(tab: TabView | undefined): string | undefined {
	switch (tab?.pageState) {
		case 'crashed':
			return 'This page crashed'
		case 'unresponsive':
			return 'This page isn’t responding'
		default:
			return undefined
	}
}

/** The chip's tooltip: its title, plus why its page isn't working. */
export function tabTooltip(tab: TabView): string {
	const title = tabTitle(tab)
	if (tab.pageState === 'crashed') return `${title} (crashed)`
	if (tab.pageState === 'unresponsive') return `${title} (not responding)`
	return title
}
