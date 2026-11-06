// Pictures of PDF pages, drawn by pdf.js (Apache-2.0, 6.4.299) in its own worker. The PDF is untrusted, so what pdf.js is
// allowed to do is fixed here and nowhere else: see OPEN_OPTIONS.
import type { PDFDocumentProxy, RenderTask } from 'pdfjs-dist'
import { PdfEditError } from '@shared/pdf/pdfCore'

type PdfJs = typeof import('pdfjs-dist')

let loading: Promise<PdfJs> | undefined

/** Loads pdf.js and its worker on first use only; the panel and Convert's PDF path are the only callers. */
function loadPdfJs(): Promise<PdfJs> {
	loading ??= (async () => {
		const [lib, worker] = await Promise.all([import('pdfjs-dist'), import('pdfjs-dist/build/pdf.worker.min.mjs?worker')])
		// The worker is bundled with the rest of the interface (a .js file of Zing's own origin, not an address from anywhere else).
		lib.GlobalWorkerOptions.workerPort = new worker.default()
		return lib
	})()
	return loading
}

/** Where the bundle keeps the fonts and character maps pdf.js reads (copied there at build time by vite.config.ts). */
const bundleFolder = (name: string): string => new URL(`pdfjs/${name}/`, document.baseURI).href

/**
 * How every PDF is opened. A PDF can carry scripts, links, XFA forms and addresses to fetch; none of that is wanted to draw
 * a page picture, so:
 * - no eval (`isEvalSupported`, the setting behind CVE-2024-4367) and no XFA forms;
 * - the file is given as bytes and never fetched, and nothing is read ahead, in ranges or as a stream;
 * - fonts and character maps are read only from the bundle, never an address from the file;
 * - no wasm (`wasmUrl` is left out, so pdf.js never loads a decoder and the page's content security policy needs no `wasm-unsafe-eval`);
 * - fonts are drawn as shapes, not installed in the page as web fonts;
 * - only the drawing is used: no annotation layer, so a link in the file is not made clickable, and no scripting sandbox.
 */
export const OPEN_OPTIONS = {
	isEvalSupported: false,
	enableXfa: false,
	disableAutoFetch: true,
	disableStream: true,
	disableRange: true,
	disableFontFace: true,
	useSystemFonts: false,
	maxImageSize: 32 * 1024 * 1024,
	verbosity: 0,
} as const

export interface PagePictureSize {
	readonly width: number
	readonly height: number
}

export interface PdfPreview {
	readonly pageCount: number
	/** The page's size in points, as it is shown (its own turn included), before any extra turn. */
	pageSize(index: number): Promise<PagePictureSize>
	/**
	 * Draws page `index` (from 0) turned `turn` degrees more, `width` CSS pixels wide, on a white ground, into `canvas`.
	 * `cancel` stops it; the promise then resolves false. Resolves true once drawn.
	 */
	draw(index: number, canvas: HTMLCanvasElement, width: number, turn: number): { readonly done: Promise<boolean>; readonly cancel: () => void }
	/** A picture of the page as PNG or JPEG bytes, at 150 pixels to the inch but never more than 4096 on a side. */
	toImage(index: number, kind: 'png' | 'jpg', quality: number): Promise<Uint8Array>
	destroy(): Promise<void>
}

const isCancelled = (error: unknown): boolean => error instanceof Error && error.name === 'RenderingCancelledException'

export async function openPreview(name: string, bytes: Uint8Array): Promise<PdfPreview> {
	const lib = await loadPdfJs()
	// pdf.js takes the buffer it is given, so it gets a copy and the panel keeps its bytes.
	const task = lib.getDocument({
		...OPEN_OPTIONS,
		data: bytes.slice(),
		cMapUrl: bundleFolder('cmaps'),
		cMapPacked: true,
		standardFontDataUrl: bundleFolder('standard_fonts'),
	})
	let doc: PDFDocumentProxy
	try {
		doc = await task.promise
	} catch (error) {
		throw new PdfEditError(`${name} can't be shown: ${error instanceof Error ? error.message : String(error)}`)
	}
	return {
		pageCount: doc.numPages,
		async pageSize(index) {
			const page = await doc.getPage(index + 1)
			const view = page.getViewport({ scale: 1 })
			return { width: view.width, height: view.height }
		},
		draw(index, canvas, width, turn) {
			let render: RenderTask | undefined
			let stopped = false
			const done = (async () => {
				const page = await doc.getPage(index + 1)
				if (stopped) return false
				const base = page.getViewport({ scale: 1, rotation: (page.rotate + turn) % 360 })
				const scale = (width / base.width) * (globalThis.devicePixelRatio || 1)
				const viewport = page.getViewport({ scale, rotation: (page.rotate + turn) % 360 })
				canvas.width = Math.max(1, Math.floor(viewport.width))
				canvas.height = Math.max(1, Math.floor(viewport.height))
				render = page.render({ canvas, viewport, background: '#ffffff' })
				try {
					await render.promise
					return true
				} catch (error) {
					if (isCancelled(error)) return false
					throw error
				} finally {
					page.cleanup()
				}
			})()
			return {
				done,
				cancel: () => {
					stopped = true
					render?.cancel()
				},
			}
		},
		async toImage(index, kind, quality) {
			const page = await doc.getPage(index + 1)
			const base = page.getViewport({ scale: 1 })
			const scale = Math.min(150 / 72, 4096 / Math.max(base.width, base.height))
			const viewport = page.getViewport({ scale })
			const canvas = document.createElement('canvas')
			canvas.width = Math.max(1, Math.floor(viewport.width))
			canvas.height = Math.max(1, Math.floor(viewport.height))
			try {
				await page.render({ canvas, viewport, background: '#ffffff' }).promise
			} finally {
				page.cleanup()
			}
			const blob = await new Promise<Blob | null>((resolve) => canvas.toBlob(resolve, kind === 'png' ? 'image/png' : 'image/jpeg', quality))
			// A cleared canvas size frees its pixels before the garbage collector gets to it.
			canvas.width = 0
			canvas.height = 0
			if (blob === null) throw new PdfEditError(`Page ${index + 1} of ${name} is too large to turn into a picture.`)
			return new Uint8Array(await blob.arrayBuffer())
		},
		destroy: () => doc.destroy(),
	}
}
