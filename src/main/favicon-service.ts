import { nativeImage, type Session, type WebContents } from 'electron'
import { best, faviconProbeScript, makeFaviconLink, type FaviconLinkInit } from '@shared/browser/faviconChoice'
import type { TabId, TabRecord } from '@shared/model'
import { FaviconStore, type FetchIcon } from './favicons'

const FETCH_TIMEOUT_MS = 6000
const PROBE_DEBOUNCE_MS = 250
// A "not found" page served as HTML is not an icon.
const MIN_BYTES = 60
const MAX_BYTES = 2_000_000
const MAX_DATA_URL_CHARS = 200_000
const ICON_PIXELS = 32

class IconRejectedError extends Error {
	constructor(readonly url: string, detail: string) {
		super(`not using ${url} as an icon: ${detail}`)
		this.name = 'IconRejectedError'
	}
}

/** A tab strip chip is tiny; a 512px touch icon held across twenty tabs is memory for nothing. */
function scaled(bytes: Buffer): string {
	const image = nativeImage.createFromBuffer(bytes)
	if (image.isEmpty()) throw new IconRejectedError('(bytes)', 'not a decodable image')
	return image.resize({ width: ICON_PIXELS, height: ICON_PIXELS, quality: 'best' }).toDataURL()
}

async function download(session: Session, url: string): Promise<string> {
	if (url.startsWith('data:image/')) {
		if (url.length > MAX_DATA_URL_CHARS) throw new IconRejectedError('(data URL)', 'too large')
		return url
	}
	const response = await session.fetch(url, { signal: AbortSignal.timeout(FETCH_TIMEOUT_MS) })
	if (!response.ok) throw new IconRejectedError(url, `HTTP ${response.status}`)
	const bytes = Buffer.from(await response.arrayBuffer())
	if (bytes.length < MIN_BYTES || bytes.length > MAX_BYTES) throw new IconRejectedError(url, `${bytes.length} bytes`)
	// nativeImage cannot decode SVG, but an <img> can draw one.
	if (response.headers.get('content-type')?.includes('svg')) return `data:image/svg+xml;base64,${bytes.toString('base64')}`
	return scaled(bytes)
}

/** Takes the first candidate that loads; an unreachable or invalid one just means "try the next". */
function fetchFrom(session: Session): FetchIcon {
	return async (candidates) => {
		for (const candidate of candidates) {
			if (!/^(https?:|data:image\/)/i.test(candidate)) continue
			try {
				return await download(session, candidate)
			} catch (error) {
				// A dead or invalid candidate means try the next conventional location.
				if (!(error instanceof Error)) throw error
			}
		}
		return null
	}
}

function hostOf(url: string): string {
	if (!URL.canParse(url)) return ''
	const parsed = new URL(url)
	return /^https?:$/.test(parsed.protocol) ? parsed.hostname : ''
}

function asLinkInits(value: unknown): FaviconLinkInit[] {
	if (!Array.isArray(value)) return []
	return value.filter((v): v is FaviconLinkInit => typeof v === 'object' && v !== null && typeof (v as FaviconLinkInit).href === 'string')
}

export interface FaviconTabs {
	idOf(contents: WebContents): TabId | undefined
	setFaviconUrl(id: TabId, url: string | undefined): void
}

/** Finds each page's declared icon and keeps the images in memory only. */
export class FaviconService {
	readonly store: FaviconStore

	constructor(session: Session, onChange: () => void) {
		this.store = new FaviconStore(fetchFrom(session), onChange)
	}

	iconFor(record: TabRecord): string | undefined {
		return this.store.icon(record.faviconUrl, hostOf(record.url))
	}

	attach(contents: WebContents, tabs: FaviconTabs): void {
		let timer: ReturnType<typeof setTimeout> | undefined
		const schedule = (): void => {
			clearTimeout(timer)
			timer = setTimeout(() => void this.#probe(contents, tabs), PROBE_DEBOUNCE_MS)
		}
		contents.on('page-favicon-updated', (_e, favicons) => {
			const first = favicons[0]
			if (first) void this.store.loadIcon(first)
		})
		contents.on('did-finish-load', schedule)
		contents.on('page-title-updated', schedule)
		contents.once('destroyed', () => clearTimeout(timer))
	}

	async #probe(contents: WebContents, tabs: FaviconTabs): Promise<void> {
		if (contents.isDestroyed()) return
		const host = hostOf(contents.getURL())
		if (!host) return
		const found: unknown = await contents.executeJavaScript(faviconProbeScript)
		if (contents.isDestroyed()) return
		const chosen = best(asLinkInits(found).map(makeFaviconLink))
		const id = tabs.idOf(contents)
		if (chosen && id !== undefined) {
			tabs.setFaviconUrl(id, chosen)
			void this.store.loadIcon(chosen)
			return
		}
		void this.store.loadHost(host)
	}
}
