import type { WebContents } from 'electron'
import { ReaderUnavailableError, extractArticle, readerCss, readerDocument } from './reader-extract'

// Isolated world, so page scripts can't see or tamper with the reader's DOM work.
const READER_WORLD = 1001
const READABLE_PROTOCOLS = new Set(['http:', 'https:', 'file:'])

/** Replaces the page's document with its extracted article; leaving reloads the page. */
export class ReaderController {
	readonly #active = new WeakSet<WebContents>()

	attach(contents: WebContents): void {
		contents.on('did-start-navigation', (details) => {
			if (details.isMainFrame && !details.isSameDocument) this.#active.delete(contents)
		})
	}

	async toggle(contents: WebContents): Promise<void> {
		if (this.#active.has(contents)) {
			this.#active.delete(contents)
			contents.reload()
			return
		}
		const url = contents.getURL()
		if (!URL.canParse(url) || !READABLE_PROTOCOLS.has(new URL(url).protocol)) {
			throw new ReaderUnavailableError("Reader isn't available for this page")
		}
		const html: string = await contents.executeJavaScriptInIsolatedWorld(READER_WORLD, [
			{ code: 'document.documentElement.outerHTML' },
		])
		const article = extractArticle(html, url)
		const markup = JSON.stringify(readerDocument(article))
		await contents.executeJavaScriptInIsolatedWorld(READER_WORLD, [
			{
				// DOMParser rather than innerHTML so a Trusted Types page can't block the swap.
				code: `(() => {
					const parsed = new DOMParser().parseFromString(${markup}, 'text/html')
					document.replaceChild(document.importNode(parsed.documentElement, true), document.documentElement)
				})()`,
			},
		])
		await contents.insertCSS(readerCss())
		this.#active.add(contents)
	}
}
