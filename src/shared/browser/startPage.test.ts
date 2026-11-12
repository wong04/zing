// Krackn has no StartPage checks of its own; its StartShortcuts checks cover the escaping
// rule ("names are escaped for the page"), and these pin how the page applies it.
import { readFileSync } from 'node:fs'
import { describe, expect, it } from 'vitest'
import { defaults, type StartShortcut } from './startShortcuts'
import {
	SearchURLError,
	ThemeTokenError,
	classifyImportNavigation,
	editShortcutsURL,
	restyleScript,
	startPageHTML,
	themeCSS,
	themeStyleId,
	type ImportNudge,
	type StartPageInput,
	type StartPageTheme,
	type StartPageTokens,
} from './startPage'

const light: StartPageTokens = {
	background: '#F7F2E9', surface: '#FFFDF8', tint: '#EFE6D6', well: '#EBE1CF', outline: '#2A2420',
	accent: '#D9B872', onAccent: '#2A2118', text: '#241F19', textSecondary: '#6E6355',
	control: '#FFFDF8', raised: '#FFFFFF', accentInk: '#8A6D2C',
}
const dark: StartPageTokens = {
	background: '#1A1714', surface: '#241F1A', tint: '#201B16', well: '#2C251E', outline: '#CDBFA6',
	accent: '#C9A45E', onAccent: '#1A1410', text: '#F0E7D8', textSecondary: '#A69884',
	control: '#2C251E', raised: '#332B23', accentInk: '#C9A45E',
}
const theme: StartPageTheme = { light, dark }
const searchURL = 'https://www.google.com/search'

function page(shortcuts: readonly StartShortcut[] = defaults, search = searchURL): string {
	return startPageHTML({ searchURL: search, shortcuts, theme })
}

const hostile: StartShortcut[] = [
	{ title: `<script>alert("t")</script>`, url: 'https://ok.com/' },
	{ title: `" onmouseover="alert(1)`, url: 'https://x.com/"onmouseover="alert(2)' },
	{ title: `<img src=x onerror=alert(3)>`, url: `https://y.com/?q='><svg/onload=alert(4)>` },
]

describe('the new-tab page', () => {
	it('has the clock, the search form, the tiles and the edit link', () => {
		const html = page()
		expect(html).toContain('<div class="clock">')
		expect(html).toContain(`<form action="${searchURL}" method="GET"`)
		expect(html).toContain('<input type="text" name="q"')
		expect(html.match(/<a class="tile" /g)).toHaveLength(8)
		expect(html).toContain(`<a class="edit" href="${editShortcutsURL}">Edit shortcuts</a>`)
		expect(editShortcutsURL).toBe('zing://edit-shortcuts')
	})
	it('built-in tiles carry their symbols', () => {
		expect(page()).toContain('<span class="glyph">◱</span>')
	})
	it('a tile is its address, name and symbol', () => {
		const html = page([{ title: 'Apple', url: 'https://apple.com' }])
		expect(html).toContain('<a class="tile" href="https://apple.com" title="Apple">')
		expect(html).toContain('<span class="glyph">A</span>')
		expect(html).toContain('<span class="label">Apple</span>')
	})
	it('warms the search engine and each tile site', () => {
		const html = page([{ title: 'Apple', url: 'https://apple.com/iphone' }])
		expect(html).toContain('<link rel="preconnect" href="https://www.google.com" crossorigin>')
		expect(html).toContain('<link rel="dns-prefetch" href="https://apple.com">')
	})
	it('colours are custom properties, in a style that can be swapped', () => {
		const html = page()
		expect(html).toContain(`<style id="${themeStyleId}">`)
		expect(html).toContain('--bg: #F7F2E9;')
		expect(html).toContain('@media (prefers-color-scheme: dark)')
		expect(html).toContain('--bg: #1A1714;')
		expect(html).toContain('background: var(--bg);')
		expect(themeStyleId).toBe('zing-theme')
	})
})

describe('only http(s) tiles', () => {
	it('javascript:, data: and file: shortcuts never become tiles', () => {
		const html = page([
			{ title: 'ok', url: 'https://ok.com' },
			{ title: 'js', url: 'javascript:alert(1)' },
			{ title: 'data', url: 'data:text/html,hi' },
			{ title: 'file', url: 'file:///C:/Windows/win.ini' },
			{ title: 'plain', url: 'http://plain.com' },
		])
		expect(html.match(/<a class="tile" /g)).toHaveLength(2)
		expect(html).not.toMatch(/javascript:|data:text|file:/)
	})
	it('the search engine must be http(s) too', () => {
		expect(() => page(defaults, 'javascript:alert(1)')).toThrow(SearchURLError)
		expect(() => page(defaults, 'not a url')).toThrow(SearchURLError)
	})
})

describe('no user text reaches the page unescaped', () => {
	const html = page(hostile)

	it('the only script is the page\'s own', () => {
		expect(html.match(/<script/g)).toHaveLength(1)
	})
	it('no element can be smuggled in', () => {
		expect(html).not.toMatch(/<img|<svg\/onload/)
	})
	it('no attribute can be broken out of', () => {
		expect(html).not.toMatch(/"\s*onmouseover=/)
		expect(html).not.toContain(`'><svg`)
	})
	it('names and addresses appear escaped', () => {
		expect(html).toContain('title="&lt;script&gt;alert(&quot;t&quot;)&lt;/script&gt;"')
		expect(html).toContain('<span class="label">&quot; onmouseover=&quot;alert(1)</span>')
		expect(html).toContain('href="https://x.com/&quot;onmouseover=&quot;alert(2)"')
		expect(html).toContain('href="https://y.com/?q=&#39;&gt;&lt;svg/onload=alert(4)&gt;"')
	})
	it('a symbol made from a name is escaped', () => {
		expect(page([{ title: '<b>', url: 'https://b.com' }])).toContain('<span class="glyph">&lt;</span>')
	})
	it('the search address is escaped into the form', () => {
		expect(page(defaults, 'https://s.com/search?a="b"')).toContain('action="https://s.com/search?a=&quot;b&quot;"')
	})
})

describe('theme tokens and swapping', () => {
	it('a token that could end the style is refused', () => {
		const bad = { light: { ...light, accent: 'red;}</style><script>alert(1)</script>' }, dark }
		expect(() => themeCSS(bad)).toThrow(ThemeTokenError)
	})
	it('an empty token is refused', () => {
		expect(() => themeCSS({ light, dark: { ...dark, text: ' ' } })).toThrow(ThemeTokenError)
	})
	it('functional colours are fine', () => {
		expect(themeCSS({ light: { ...light, accent: 'rgb(217 184 114 / 90%)' }, dark })).toContain(
			'--accent: rgb(217 184 114 / 90%);',
		)
	})
	it('the restyle script swaps the theme style with the new CSS', () => {
		const next = { light: { ...light, background: '#000000' }, dark }
		const script = restyleScript(next)
		expect(script).toContain(`document.getElementById('${themeStyleId}')`)
		const literal = script.slice(script.indexOf('s.textContent = ') + 16, script.lastIndexOf('; })()'))
		expect(JSON.parse(literal)).toBe(themeCSS(next))
	})
	it('the page and the restyle script carry the same CSS', () => {
		expect(page()).toContain(`<style id="${themeStyleId}">\n${themeCSS(theme)}\n</style>`)
	})
})

// The C# page is held to these through the same file (StartPageTests.cs).
interface Vectors {
	pages: { name: string; input: StartPageInput; html: string }[]
	navigation: { from: string; to: string; result: { kind: string; action?: string; source?: string } }[]
}
const vectors = JSON.parse(readFileSync('host/Zing.Core.Tests/vectors/startPage.vectors.json', 'utf8')) as Vectors

describe('the import nudge', () => {
	it('every page vector matches, so a page without a nudge is unchanged', () => {
		for (const v of vectors.pages) expect(startPageHTML(v.input), v.name).toBe(v.html)
	})
	it('without a nudge there is no card and no card CSS', () => {
		const html = page()
		expect(html).not.toContain('nudge')
		expect(html).not.toContain('zing://import')
	})
	it('the card shows only what it was given', () => {
		const offer: ImportNudge = { kind: 'offer', browser: 'Chrome', profile: 'Work', bookmarks: 214, sourceId: 'chrome', passwords: true }
		const html = startPageHTML({ searchURL, shortcuts: [], theme, nudge: offer })
		expect(html).toContain('<h2>Bring your stuff from Chrome?</h2>')
		expect(html).toContain('214 bookmarks and your favourites bar in "Work". Passwords need a quick export')
		expect(html).toContain('href="zing://import/bookmarks?source=chrome"')
		const arc = startPageHTML({ searchURL, shortcuts: [], theme, nudge: { ...offer, profile: undefined, passwords: false } })
		expect(arc).toContain('214 bookmarks and your favourites bar.</p>')
	})
	it('import links are decided as the vectors say', () => {
		for (const v of vectors.navigation.filter((n) => n.to.toLowerCase().startsWith('zing://import'))) {
			const { result } = v
			if (result.kind === 'allow') {
				expect(classifyImportNavigation(v.from, v.to), v.to).toBeUndefined()
				continue
			}
			const expected = result.kind === 'block' ? { kind: 'block' } : { kind: 'import', action: result.action, ...(result.source === undefined ? {} : { sourceId: result.source }) }
			expect(classifyImportNavigation(v.from, v.to), `${v.from} -> ${v.to}`).toEqual(expected)
		}
	})
	it('a count of one is singular', () => {
		const html = startPageHTML({ searchURL, shortcuts: [], theme, nudge: { kind: 'result', browser: 'Arc', added: 1, sourceId: 'arc', passwords: false } })
		expect(html).toContain('<h2>Imported 1 bookmark.</h2>')
	})
	it('other addresses are not its business', () => {
		expect(classifyImportNavigation('zing://home', 'zing://edit-shortcuts')).toBeUndefined()
		expect(classifyImportNavigation('zing://home', 'https://import/bookmarks')).toBeUndefined()
	})
})
