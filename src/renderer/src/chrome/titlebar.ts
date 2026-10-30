import { zing } from '../api'

function readToken(name: string): string {
	return getComputedStyle(document.documentElement).getPropertyValue(name).trim()
}

function sendColors(): void {
	void zing.setTitleBarColors({ symbolColor: readToken('--text') })
}

/**
 * The native caption buttons are drawn by Windows, not CSS, so their glyph
 * colour is pushed from the theme tokens: CSS stays the one source of colour.
 */
export function syncTitleBarColors(): void {
	sendColors()
	matchMedia('(prefers-color-scheme: dark)').addEventListener('change', sendColors)
}
