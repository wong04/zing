import { describe, expect, it, vi } from 'vitest'
// Vitest blanks stylesheet imports; the e2e reader test checks the real tokens.
vi.mock('../renderer/src/theme/tokens.css?raw', () => ({
	default: "@font-face { font-family: 'Inter'; src: url('x.ttf'); } :root { --bg: #f7f2e9; }",
}))

import { ReaderUnavailableError, extractArticle, readerCss, readerDocument } from './reader-extract'

const PARAGRAPH =
	'Zing keeps nothing by default, which means a reader view has to be built from the page that is open right now. '.repeat(4)

const ARTICLE = `<!doctype html><html><head><title>Fixture</title><style>body{color:red}</style></head><body>
<nav><a href="/home">Home</a><a href="/about">About</a></nav>
<article><h1>The Fixture Article</h1><p class="byline">By Test Author</p>
<p>${PARAGRAPH}</p><p>${PARAGRAPH}</p>
<p onclick="steal()">Second ${PARAGRAPH} <a href="javascript:alert(1)">bad</a> <a href="/rel">good</a></p>
<script>alert(1)</script></article>
<footer>Copyright</footer></body></html>`

describe('extractArticle', () => {
	it('extracts the article and drops the page chrome', () => {
		const article = extractArticle(ARTICLE, 'https://example.test/post')
		expect(article.title).toBeTruthy()
		expect(article.content).toContain('reader view has to be built')
		expect(article.content).not.toContain('Copyright')
	})

	it('removes scripts, handlers and script URLs', () => {
		const { content } = extractArticle(ARTICLE, 'https://example.test/post')
		expect(content).not.toMatch(/<script|onclick|javascript:/i)
	})

	it('resolves relative links against the page URL', () => {
		const { content } = extractArticle(ARTICLE, 'https://example.test/post')
		expect(content).toContain('https://example.test/rel')
	})

	it('throws when there is no article', () => {
		expect(() => extractArticle('<html><body><p>hi</p></body></html>', 'https://example.test/')).toThrow(ReaderUnavailableError)
	})
})

describe('reader page', () => {
	it('themes the page with the shared tokens and no font-face', () => {
		const css = readerCss()
		expect(css).toContain('--bg:')
		expect(css).not.toContain('@font-face')
		expect(css).toContain('var(--bg)')
	})

	it('escapes the title', () => {
		const html = readerDocument({ title: '<b>x</b>', byline: '', content: '<p>y</p>' })
		expect(html).toContain('&lt;b&gt;x&lt;/b&gt;')
	})
})
