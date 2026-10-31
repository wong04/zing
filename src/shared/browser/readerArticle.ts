// What reader mode shows, independent of where the article was parsed: Electron parsed the page's
// HTML in main with linkedom; the WebView2 host runs Readability in the page itself (readerPage.ts).

export class ReaderUnavailableError extends Error {
	constructor(reason: string) {
		super(reason)
		this.name = 'ReaderUnavailableError'
	}
}

export interface Article {
	readonly title: string
	readonly byline: string
	/** Sanitized HTML, safe to inject into a page. */
	readonly content: string
}

// Readability falls back to whatever text it can find; a login form or a stub isn't an article.
export const MIN_ARTICLE_CHARS = 250

const FORBIDDEN_TAGS = 'script, style, iframe, frame, object, embed, form, link, meta, base, noscript, template'

/** Event handlers and script URLs would run in the page's own world once injected. */
export function sanitizeInto(document: { body: { innerHTML: string }; querySelectorAll(selector: string): Iterable<SanitizableElement> }): string {
	for (const element of document.querySelectorAll(FORBIDDEN_TAGS)) element.remove()
	for (const element of document.querySelectorAll('*')) {
		for (const name of element.getAttributeNames()) {
			const value = element.getAttribute(name) ?? ''
			const scriptUrl = /^\s*(javascript|vbscript):/i.test(value) && (name === 'href' || name === 'src' || name === 'xlink:href')
			if (name.toLowerCase().startsWith('on') || scriptUrl) element.removeAttribute(name)
		}
	}
	return document.body.innerHTML
}

export interface SanitizableElement {
	remove(): void
	getAttributeNames(): string[]
	getAttribute(name: string): string | null
	removeAttribute(name: string): void
}

function escapeHtml(text: string): string {
	return text.replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c] ?? c)
}

/** Zing's own tokens, minus the @font-face that only resolves inside the app bundle. */
export function readerCss(tokens: string): string {
	const theme = tokens.replace(/@font-face\s*\{[^}]*\}/g, '')
	return `${theme}
html { background: var(--bg); }
body { margin: 0; background: var(--bg); color: var(--text); font: 1.125rem/1.7 var(--font); }
main[data-zing-reader] { max-width: 42rem; margin: 0 auto; padding: 3.5rem 1.5rem 6rem; }
main[data-zing-reader] h1.zing-title { font-size: 2.1rem; line-height: 1.2; margin: 0 0 0.5rem; }
main[data-zing-reader] .zing-byline { color: var(--muted); font-size: 0.9rem; margin: 0 0 2rem; }
main[data-zing-reader] a { color: var(--accent-ink); }
main[data-zing-reader] img, main[data-zing-reader] video { max-width: 100%; height: auto; border-radius: var(--control-radius); }
main[data-zing-reader] pre, main[data-zing-reader] code { background: var(--well); border-radius: 4px; }
main[data-zing-reader] pre { padding: 0.8rem 1rem; overflow-x: auto; }
main[data-zing-reader] blockquote { margin: 1.2rem 0; padding-left: 1rem; border-left: 3px solid var(--accent); color: var(--muted); }
main[data-zing-reader] hr { border: 0; border-top: 1px solid var(--separator); }
`
}

export function readerDocument(article: Article): string {
	const byline = article.byline ? `<p class="zing-byline">${escapeHtml(article.byline)}</p>` : ''
	return `<!doctype html><html><head><meta charset="utf-8"><title>${escapeHtml(article.title)}</title></head><body><main data-zing-reader><h1 class="zing-title">${escapeHtml(article.title)}</h1>${byline}${article.content}</main></body></html>`
}
