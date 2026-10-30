/**
 * Runs inside a page just before it sleeps. A form counts as unsaved when any
 * field differs from what the markup gave it, because discarding the page
 * would throw away what was typed.
 */
export const PAGE_PROBE_SOURCE = `(() => {
	const edited = (el) => {
		if (el instanceof HTMLTextAreaElement) return el.value !== el.defaultValue
		if (el instanceof HTMLSelectElement) {
			if (el.multiple) return [...el.options].some((o) => o.selected !== o.defaultSelected)
			const expected = Math.max(0, [...el.options].findIndex((o) => o.defaultSelected))
			return el.selectedIndex !== expected
		}
		if (!(el instanceof HTMLInputElement)) return false
		if (['hidden', 'submit', 'button', 'reset', 'image', 'file'].includes(el.type)) return false
		if (el.type === 'checkbox' || el.type === 'radio') return el.checked !== el.defaultChecked
		return el.value !== el.defaultValue
	}
	const richText = [...document.querySelectorAll('[contenteditable=""], [contenteditable="true"]')]
		.some((el) => (el.textContent || '').trim() !== '')
	const dirty = richText || [...document.querySelectorAll('input, textarea, select')].some(edited)
	return { scrollY: Math.round(window.scrollY), dirty }
})()`

export interface PageProbe {
	readonly scrollY: number
	readonly dirty: boolean
}

export class PageProbeError extends Error {
	constructor(readonly value: unknown) {
		super(`Page probe returned an unexpected value: ${JSON.stringify(value)}`)
		this.name = 'PageProbeError'
	}
}

export function parsePageProbe(value: unknown): PageProbe {
	if (typeof value !== 'object' || value === null) throw new PageProbeError(value)
	const { scrollY, dirty } = value as Record<string, unknown>
	if (typeof scrollY !== 'number' || !Number.isFinite(scrollY) || typeof dirty !== 'boolean') {
		throw new PageProbeError(value)
	}
	return { scrollY: Math.max(0, scrollY), dirty }
}
