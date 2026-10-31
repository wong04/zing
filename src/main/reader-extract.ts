import { Readability } from '@mozilla/readability'
import { parseHTML } from 'linkedom'
import tokens from '../renderer/src/theme/tokens.css?raw'
import { MIN_ARTICLE_CHARS, ReaderUnavailableError, readerCss as themedReaderCss, sanitizeInto, type Article, type SanitizableElement } from '@shared/browser/readerArticle'

export { ReaderUnavailableError, readerDocument, type Article } from '@shared/browser/readerArticle'

function sanitize(html: string): string {
	const { document } = parseHTML(`<!doctype html><html><body>${html}</body></html>`)
	return sanitizeInto({ body: document.body, querySelectorAll: (s) => document.querySelectorAll(s) as unknown as Iterable<SanitizableElement> })
}

function withBase(html: string, url: string): string {
	return /<base[\s>]/i.test(html) ? html : html.replace(/<head[^>]*>/i, (head) => `${head}<base href="${url}">`)
}

/** Pulls the article out of a page's HTML; throws when the page has none. */
export function extractArticle(html: string, url: string): Article {
	const { document } = parseHTML(withBase(html, url))
	const parsed = new Readability(document as unknown as Document, { keepClasses: false }).parse()
	if (!parsed?.content || (parsed.length ?? 0) < MIN_ARTICLE_CHARS) throw new ReaderUnavailableError("Reader can't find an article on this page")
	return {
		title: parsed.title || '',
		byline: parsed.byline ?? '',
		content: sanitize(parsed.content),
	}
}

export function readerCss(): string {
	return themedReaderCss(tokens)
}
