import type { WebContents } from 'electron'
import type { FindDirection } from '@shared/browser/pageUi'
import type { PageUiHost } from './pageui-layer'

/**
 * Find in page for the active tab. The bar lives in the panels view; the
 * search runs in the page's own WebContents, so it stops when the tab changes.
 */
export class FindController {
	#target: WebContents | undefined
	#query = ''
	#focusToken = 0

	constructor(
		private readonly panels: PageUiHost,
		private readonly activeContents: () => WebContents | undefined,
	) {}

	/** Listens for results on one page; every page WebContents is attached once. */
	attach(contents: WebContents): void {
		contents.on('found-in-page', (_event, result) => {
			if (contents !== this.#target || this.panels.state().find === undefined) return
			this.panels.set({
				find: { query: this.#query, active: result.matches === 0 ? 0 : result.activeMatchOrdinal, matches: result.matches },
			})
		})
	}

	open(): void {
		const contents = this.activeContents()
		if (!contents) return
		if (this.#target !== contents) this.#stop()
		this.#target = contents
		this.panels.set({
			find: this.panels.state().find ?? { query: this.#query, active: 0, matches: 0 },
			findFocus: ++this.#focusToken,
		})
		this.panels.focus()
		if (this.#query) this.#search(contents, 'next', 'restart')
	}

	/** Find Next / Previous with a remembered query; without one it opens the bar. */
	step(direction: FindDirection): void {
		if (this.#query === '' || this.panels.state().find === undefined) {
			this.open()
			return
		}
		const contents = this.#target
		if (!contents || contents.isDestroyed()) return
		this.#search(contents, direction, 'continue')
	}

	update(text: string): void {
		this.#query = text
		const contents = this.#target
		if (!contents || contents.isDestroyed()) return
		if (text === '') {
			contents.stopFindInPage('clearSelection')
			this.panels.set({ find: { query: '', active: 0, matches: 0 } })
			return
		}
		this.#search(contents, 'next', 'restart')
	}

	close(): void {
		this.#stop()
		this.#target = undefined
		this.panels.set({ find: undefined })
		this.activeContents()?.focus()
	}

	/** The tab changed under an open bar: the old page's highlights go with it. */
	activeChanged(): void {
		if (this.panels.state().find === undefined || this.activeContents() === this.#target) return
		this.close()
	}

	#search(contents: WebContents, direction: FindDirection, mode: 'restart' | 'continue'): void {
		contents.findInPage(this.#query, { forward: direction === 'next', findNext: mode === 'continue' })
	}

	#stop(): void {
		if (this.#target && !this.#target.isDestroyed()) this.#target.stopFindInPage('clearSelection')
	}
}
