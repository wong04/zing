import { NEW_TAB_TITLE, NEW_TAB_URL } from '@shared/model'
import type { TabView } from '@shared/ipc'

export function tabTitle(tab: TabView): string {
	if (tab.url === NEW_TAB_URL) return NEW_TAB_TITLE
	return tab.title || tab.url
}

/** The address bar shows nothing for a blank tab, so typing starts clean. */
export function addressText(url: string | undefined): string {
	if (url === undefined || url === NEW_TAB_URL) return ''
	return url
}

/** One letter standing in for the favicon until memory-only favicons land (B22). */
export function monogram(tab: TabView): string {
	if (!URL.canParse(tab.url)) return '•'
	const host = new URL(tab.url).hostname.replace(/^www\./, '')
	return host ? host[0]!.toUpperCase() : '•'
}
