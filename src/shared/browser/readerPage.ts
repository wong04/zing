// Reader mode inside the page (WebView2 host): Readability runs on a copy of the live
// document, and the article replaces it. The host runs this in an isolated world, so
// page scripts can neither see nor tamper with the work.
import { Readability } from '@mozilla/readability'
import { MIN_ARTICLE_CHARS, readerDocument, sanitizeInto, type SanitizableElement } from './readerArticle'

export type ReaderOutcome = { readonly ok: true; readonly title: string } | { readonly ok: false; readonly reason: string }

const READABLE_PROTOCOLS = new Set(['http:', 'https:', 'file:'])

export function showReader(document: Document, css: string): ReaderOutcome {
	if (!READABLE_PROTOCOLS.has(document.location.protocol)) return { ok: false, reason: "Reader isn't available for this page" }
	const parsed = new Readability(document.cloneNode(true) as Document, { keepClasses: false }).parse()
	if (!parsed?.content || (parsed.length ?? 0) < MIN_ARTICLE_CHARS) return { ok: false, reason: "Reader can't find an article on this page" }
	const holder = new DOMParser().parseFromString(`<!doctype html><html><body>${parsed.content}</body></html>`, 'text/html')
	const content = sanitizeInto({ body: holder.body, querySelectorAll: (s) => holder.querySelectorAll(s) as unknown as Iterable<SanitizableElement> })
	const markup = readerDocument({ title: parsed.title || '', byline: parsed.byline ?? '', content })
	// DOMParser rather than innerHTML so a Trusted Types page can't block the swap.
	const reader = new DOMParser().parseFromString(markup, 'text/html')
	document.replaceChild(document.importNode(reader.documentElement, true), document.documentElement)
	const style = document.createElement('style')
	style.textContent = css
	document.head.appendChild(style)
	return { ok: true, title: parsed.title || '' }
}
