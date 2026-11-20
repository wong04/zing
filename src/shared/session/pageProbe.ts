/**
 * Runs inside a page just before it sleeps, in the page and each same-origin frame. A form counts as unsaved when any
 * field differs from what the markup gave it, or when the person typed into a field that still holds text (the host's
 * SleepGuards script notes typing, capture and uploads from the document's start). Mirrors PageProbe.Source.
 */
export const PAGE_PROBE_SOURCE = `(() => {
	const key = Symbol.for('zing.sleepGuards')
	const windows = []
	const collect = (w) => {
		// A cross-origin frame's document can't be read (SecurityError); its own frames may still be same-origin.
		try {
			if (w.document) windows.push(w)
		} catch {}
		for (let i = 0; i < w.frames.length; i++) collect(w.frames[i])
	}
	collect(window)
	const edited = (w, el) => {
		if (el instanceof w.HTMLTextAreaElement) return el.value !== el.defaultValue
		if (el instanceof w.HTMLSelectElement) {
			if (el.multiple) return [...el.options].some((o) => o.selected !== o.defaultSelected)
			const expected = Math.max(0, [...el.options].findIndex((o) => o.defaultSelected))
			return el.selectedIndex !== expected
		}
		if (!(el instanceof w.HTMLInputElement)) return false
		if (['hidden', 'submit', 'button', 'reset', 'image', 'file'].includes(el.type)) return false
		if (el.type === 'checkbox' || el.type === 'radio') return el.checked !== el.defaultChecked
		return el.value !== el.defaultValue
	}
	const dirty = (w) => {
		const doc = w.document
		const richText = [...doc.querySelectorAll('[contenteditable=""], [contenteditable="true"]')]
			.some((el) => (el.textContent || '').trim() !== '')
		return richText || [...doc.querySelectorAll('input, textarea, select')].some((el) => edited(w, el)) || !!w[key]?.typed()
	}
	return {
		scrollY: Math.round(window.scrollY),
		dirty: windows.some(dirty),
		capturing: windows.some((w) => !!w[key]?.capturing()),
		pictureInPicture: !!window.documentPictureInPicture?.window || windows.some((w) => w.document.pictureInPictureElement !== null),
		uploading: windows.some((w) => !!w[key]?.uploading()),
	}
})()`

export interface PageProbe {
	readonly scrollY: number
	readonly dirty: boolean
	readonly capturing: boolean
	readonly pictureInPicture: boolean
	readonly uploading: boolean
}

export class PageProbeError extends Error {
	constructor(readonly value: unknown) {
		super(`Page probe returned an unexpected value: ${JSON.stringify(value)}`)
		this.name = 'PageProbeError'
	}
}

export function parsePageProbe(value: unknown): PageProbe {
	if (typeof value !== 'object' || value === null) throw new PageProbeError(value)
	const { scrollY, dirty, capturing, pictureInPicture, uploading } = value as Record<string, unknown>
	if (typeof scrollY !== 'number' || !Number.isFinite(scrollY)) throw new PageProbeError(value)
	if (typeof dirty !== 'boolean' || typeof capturing !== 'boolean' || typeof pictureInPicture !== 'boolean' || typeof uploading !== 'boolean') {
		throw new PageProbeError(value)
	}
	return { scrollY: Math.max(0, scrollY), dirty, capturing, pictureInPicture, uploading }
}
