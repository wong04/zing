// Port of Krackn's `FaviconChoice` (Features/Browser/FaviconChoice.swift) and the icon probe
// from `BrowserTab.refreshIcon` (BrowserTabActions.swift).
//
// Icons used to be looked up per *site* — the registrable domain — so every
// tab on google.com showed whichever Google icon loaded first: Gmail's on
// Docs, Slides, Drive and Sites alike. Docs, Sheets and Slides even share
// one host, docs.google.com. The page's own `<link rel="icon">` is the only
// thing that says which product it is, so that's what decides the icon now.

export interface FaviconLink {
	href: string
	/** The `rel` attribute, lowercased: "icon", "shortcut icon", "apple-touch-icon"… */
	rel: string
	/** The `sizes` attribute, lowercased, e.g. "32x32" or "16x16 32x32" or "any". */
	sizes: string
	type: string
	/** Whether the link's `media` query (if any) matches the page right now. */
	mediaMatches: boolean
}

export interface FaviconLinkInit {
	href: string
	rel: string
	sizes?: string
	type?: string
	mediaMatches?: boolean
}

export function makeFaviconLink(init: FaviconLinkInit): FaviconLink {
	return {
		href: init.href,
		rel: init.rel.toLowerCase(),
		sizes: (init.sizes ?? '').toLowerCase(),
		type: (init.type ?? '').toLowerCase(),
		mediaMatches: init.mediaMatches ?? true,
	}
}

/**
 * Run in the page (`webContents.executeJavaScript`) to read the icons it declares,
 * as `FaviconLinkInit`s. The `media` query has to be evaluated there, against the
 * page's own colour scheme: GitHub, among others, declares a white icon for dark
 * mode and a black one for light, and taking the wrong one leaves the tab with an
 * invisible icon. Capped so a page can't hand back an enormous list.
 */
export const faviconProbeScript = `Array.from(document.querySelectorAll(
	"link[rel~='icon' i], link[rel='apple-touch-icon' i], link[rel='apple-touch-icon-precomposed' i]"
)).slice(0, 32).map(l => ({ href: l.href || '', rel: l.rel || '', sizes: l.getAttribute('sizes') || '',
	type: l.type || '', mediaMatches: !l.media || matchMedia(l.media).matches }))`

const swiftInt = /^[+-]?[0-9]+$/

/** "16x16 32x32" → 32. */
export function largestSize(sizes: string): number | null {
	const widths = sizes
		.split(' ')
		.filter((token) => token.length > 0)
		.flatMap((token) => {
			const parts = token.split('x').filter((part) => part.length > 0)
			const width = parts[0] ?? ''
			return parts.length === 2 && swiftInt.test(width) ? [Number(width)] : []
		})
	return widths.length === 0 ? null : Math.max(...widths)
}

function isUsable(link: FaviconLink): boolean {
	if (link.href === '' || !link.mediaMatches) return false
	const lower = link.href.toLowerCase()
	return lower.startsWith('https://') || lower.startsWith('http://') || lower.startsWith('data:image/')
}

function score(link: FaviconLink): number {
	let s = 0
	const isTouch = link.rel.includes('apple-touch')
	if (!isTouch && link.rel.includes('icon')) s += 1000
	const px = largestSize(link.sizes)
	if (link.sizes === 'any' || link.type.includes('svg')) {
		s += 300
	} else if (px !== null) {
		// Closest to 32 wins; bigger than needed beats too small.
		s += px >= 32 ? 400 - Math.min(px - 32, 300) : 200 - (32 - px) * 4
	} else {
		s += 250
	}
	return s
}

/**
 * Tab icons draw at 14–16px, so 32px (2x) is ideal. Prefers a real
 * `icon` over an apple-touch-icon (a big, padded, often differently
 * styled tile), the size nearest 32 among those, and an SVG or "any"
 * size when nothing is labelled — then the page's own order.
 */
export function best(links: readonly FaviconLink[]): string | null {
	let chosen: FaviconLink | null = null
	let chosenScore = -Infinity
	for (const link of links.filter(isUsable)) {
		const s = score(link)
		if (s <= chosenScore) continue
		chosen = link
		chosenScore = s
	}
	return chosen?.href ?? null
}
