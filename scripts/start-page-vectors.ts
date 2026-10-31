// The start page's exact output, as test vectors for the C# port (host/Zing.Core/StartPage.cs,
// StartShortcuts.cs, Palette.cs). `src/shared/browser/startPage.vectors.test.ts` fails if the
// committed `startPage.vectors.json` differs from what this renders; regenerate with
// `npm run gen:start-page-vectors`.
import { classify } from '../src/shared/browser/addressInput'
import { SEARCH_URLS } from '../src/shared/browser/searchEngines'
import { editShortcutsURL, restyleScript, startPageHTML, themeCSS, type StartPageInput, type StartPageTheme } from '../src/shared/browser/startPage'
import { decode, defaults, encode, escapeHTML, glyph, isWebAddress, make, name, type StartShortcut } from '../src/shared/browser/startShortcuts'
import { accentThemes, darkTokens, lightTokens, type AccentTheme } from '../src/shared/theme/accent'
import { contrastRatio, hexFromHSL, mix } from '../src/shared/theme/color'
import { accentInk, control, raised } from '../src/shared/theme/palette'
import { SettingsStore, parseSettings, type Settings } from '../src/shared/settings'

type MainStartPage = typeof import('../src/main/start-page')
type Engine = Settings['searchEngine']

/** The error's class name; a failure if nothing was thrown. */
function thrownName(run: () => unknown): string {
	try {
		run()
	} catch (error) {
		if (error instanceof Error) return error.name
		throw error
	}
	throw new Error('expected the call to throw')
}

const engines = Object.keys(SEARCH_URLS) as Engine[]
const google = 'https://www.google.com/search'

const hostile: StartShortcut[] = [
	{ title: `<script>alert("t")</script>`, url: 'https://ok.com/' },
	{ title: `" onmouseover="alert(1)`, url: 'https://x.com/"onmouseover="alert(2)' },
	{ title: `<img src=x onerror=alert(3)>`, url: `https://y.com/?q='><svg/onload=alert(4)>` },
]

const shortcutLists: Record<string, readonly StartShortcut[]> = {
	defaults,
	empty: [],
	hostile,
	'non-web addresses are not tiles': [
		{ title: 'ok', url: 'https://ok.com' },
		{ title: 'js', url: 'javascript:alert(1)' },
		{ title: 'data', url: 'data:text/html,hi' },
		{ title: 'file', url: 'file:///C:/Windows/win.ini' },
		{ title: 'plain', url: 'http://plain.com' },
	],
	'hosts as the browser normalises them': [
		{ title: 'Bücher', url: 'https://bücher.de/Pfad' },
		{ title: 'Port and user', url: 'https://User:pw@Example.COM:8443/Path?Q=1#frag' },
		{ title: 'Mixed case', url: 'HTTPS://MiXeD.Example.org/' },
		{ title: 'Loopback', url: 'http://127.0.0.1:3000/' },
		{ title: 'v6', url: 'http://[::1]:3000/' },
	],
	glyphs: [
		{ title: '  spaced ', url: 'https://a.test' },
		{ title: '', url: 'https://b.test' },
		{ title: '   ', url: 'https://c.test' },
		{ title: 'élan', url: 'https://d.test' },
		{ title: '😀 smile', url: 'https://e.test' },
		{ title: 'e\u0301cole', url: 'https://f.test' },
		{ title: 'ǆ', url: 'https://g.test' },
		{ title: '👨‍👩‍👧 family', url: 'https://h.test' },
		{ title: 'GitHub', url: 'https://github.com' },
		{ title: 'A very long title that the tile has to truncate with an ellipsis somewhere', url: 'https://long.test' },
	],
}

const searchURLs = [
	google,
	'https://duckduckgo.com/',
	'http://localhost:8080/find',
	'https://Search.Example.COM:444/q',
	'https://s.com/search?a="b"&c=<d>',
	'https://bücher.de/s',
]

const badSearchURLs = ['javascript:alert(1)', 'not a url', 'ftp://files.test/x', 'file:///x', '']

// Mirrors src/main/start-page.ts `startPageTheme`; startPageVectors asserts the two agree.
function themeFor(accent: AccentTheme): StartPageTheme {
	const tokens = (p: ReturnType<typeof lightTokens>) => ({
		background: p.background,
		surface: p.surface,
		tint: p.tint,
		well: p.well,
		outline: p.outline,
		accent: p.accent,
		onAccent: p.onAccent,
		text: p.text,
		textSecondary: p.textSecondary,
		control: control(p),
		raised: raised(p),
		accentInk: accentInk(p),
	})
	return { light: tokens(lightTokens(accent)), dark: tokens(darkTokens(accent)) }
}

function pageInputs(): { name: string; input: StartPageInput }[] {
	const theme = themeFor('yellow')
	return [
		...Object.entries(shortcutLists).map(([listName, shortcuts]) => ({ name: `${listName} / google / yellow`, input: { searchURL: google, shortcuts, theme } })),
		...searchURLs.map((searchURL) => ({ name: `defaults / ${searchURL}`, input: { searchURL, shortcuts: defaults, theme } })),
		...accentThemes.map((accent) => ({ name: `accent ${accent}`, input: { searchURL: google, shortcuts: defaults, theme: themeFor(accent) } })),
	]
}

function badThemes(): { name: string; theme: StartPageTheme }[] {
	const base = themeFor('yellow')
	const withLight = (patch: Partial<StartPageTheme['light']>): StartPageTheme => ({ ...base, light: { ...base.light, ...patch } })
	return [
		{ name: 'semicolon', theme: withLight({ accent: 'red;}</style><script>alert(1)</script>' }) },
		{ name: 'brace', theme: withLight({ text: 'red}' }) },
		{ name: 'angle', theme: withLight({ text: '<red' }) },
		{ name: 'backslash', theme: withLight({ text: 'a\\b' }) },
		{ name: 'comment', theme: withLight({ text: 'a/*b' }) },
		{ name: 'blank', theme: { ...base, dark: { ...base.dark, text: ' ' } } },
		{ name: 'empty', theme: withLight({ surface: '' }) },
	]
}

function functionalTheme(): StartPageTheme {
	const base = themeFor('blue')
	return { ...base, light: { ...base.light, accent: 'rgb(217 184 114 / 90%)', background: '#000000' } }
}

const manyShortcuts = (count: number, host: string): StartShortcut[] =>
	Array.from({ length: count }, (_, i) => ({ title: `s${i}`, url: `https://s${i}.${host}` }))

const decodeRaws: (string | null)[] = [
	null,
	'[]',
	'{nope',
	'',
	'null',
	'{}',
	'[{"title":1,"url":"https://ok.com"}]',
	'[{"title":"ok","url":"https://ok.com"},{"title":"x","url":"javascript:alert(1)"},{"title":"y","url":"data:text/html,hi"}]',
	'[{"title":"a","url":"https://a.test","extra":true}]',
	'[null]',
	'[[]]',
	'[{"title":"é\\u00e9 \\ud83d\\ude00","url":"https://u.test/\\u00fc"}]',
	JSON.stringify(manyShortcuts(30, 'com')),
]

const encodeLists: StartShortcut[][] = [
	[],
	[...defaults],
	[{ title: 'quote " and \\ slash', url: 'https://q.test/a/b' }],
	[{ title: 'control \u0001\u001f\b\f\n\r\t and <>&\'+', url: 'https://c.test/' }],
	[{ title: 'ünï 😀 \u2028\u2029\u007f', url: 'https://u.test/ü' }],
	manyShortcuts(20, 'com'),
]

const makeCases: [string, string][] = [
	['', 'apple.com'],
	['  My Docs ', 'docs.google.com/document/u/0/'],
	['x', 'best pizza'],
	['x', 'javascript:alert(1)'],
	['x', 'file:///etc/passwd'],
	['', 'bücher.de'],
	['', 'www.apple.com'],
	['', 'http://localhost:3000/app'],
	['', '192.168.0.1:8080'],
	['', 'https://user:pw@www.host.test:99/p'],
	['', 'https://[::1]:3000/'],
	['', 'ÉCOLE.fr'],
	['', 'https://émile.fr'],
	['', '  spaced.com  '],
	['', ''],
	['', 'mailto:me@x.test'],
	['', 'about:blank'],
	['a'.repeat(50), 'a.test'],
	['😀'.repeat(45), 'a.test'],
	['e\u0301'.repeat(45), 'a.test'],
	['\u00a0nbsp\u00a0', 'a.test'],
]

const isWebAddressCases = ['https://a.test', 'http://a.test', 'ftp://a.test', 'javascript:alert(1)', 'https://', 'not a url', '', 'HTTPS://A.TEST', 'http://[::1]/', 'https://a b.test']

const navigationCases: [string, string, Engine][] = [
	['zing://home', 'zing://edit-shortcuts', 'google'],
	['https://evil.test/', 'zing://edit-shortcuts', 'google'],
	['zing://home', 'https://www.google.com/search?q=apple.com', 'google'],
	['zing://home', 'https://duckduckgo.com/?q=two+words', 'duckduckgo'],
	['zing://home', 'https://www.google.com/search?q=a%20b%2Bc&x=1', 'google'],
	['zing://home', 'https://www.google.com/search?x=1&q=first&q=second', 'google'],
	['zing://home', 'https://www.google.com/search?q=', 'google'],
	['zing://home', 'https://www.google.com/search?q', 'google'],
	['zing://home', 'https://www.google.com/search?q=%E2%82%AC+%F0%9F%98%80', 'google'],
	['zing://home', 'https://www.google.com/search?q=%ZZ%E2', 'google'],
	['zing://home', 'https://www.google.com/search?x=1', 'google'],
	['zing://home', 'https://www.google.com/search?q=x#frag', 'google'],
	['zing://home', 'https://www.google.com/other?q=x', 'google'],
	['zing://home', 'https://www.google.com:8443/search?q=x', 'google'],
	['zing://home', 'http://www.google.com/search?q=x', 'google'],
	['zing://home', 'https://github.com/', 'google'],
	['https://example.com/', 'https://www.google.com/search?q=x', 'google'],
	['zing://home', 'https://www.bing.com/search?q=x', 'google'],
	['zing://home/', 'zing://edit-shortcuts', 'google'],
	['zing://home', 'zing://edit-shortcuts/', 'google'],
	['zing://home', 'zing://other', 'google'],
	['zing://home', 'not a url', 'google'],
	['zing://home', '', 'google'],
	['not a url', 'zing://edit-shortcuts', 'google'],
	['', 'zing://edit-shortcuts', 'google'],
	['zing://home', 'https://kagi.com/search?q=hi', 'kagi'],
	['zing://home', 'https://www.startpage.com/sp/search?q=hi', 'startpage'],
	['zing://home', 'https://search.brave.com/search?q=hi', 'brave'],
]

const isStartPageCases = ['zing://home', 'zing://home/', 'zing://edit-shortcuts', 'https://home', 'not a url', '', 'zing://HOME', 'zing://home/x', 'zing://home?x=1', 'zing://home#x']

const settingsCases: { name: string; stored: Record<string, unknown> }[] = [
	{ name: 'defaults', stored: {} },
	{ name: 'edited shortcuts', stored: { startShortcuts: [{ title: 'Mine', url: 'https://mine.test' }] } },
	{ name: 'no shortcuts', stored: { startShortcuts: [] } },
	{ name: 'hostile shortcuts', stored: { startShortcuts: hostile } },
	{ name: 'non-web shortcuts', stored: { startShortcuts: [{ title: 'js', url: 'javascript:alert(1)' }, { title: 'ok', url: 'https://ok.test' }] } },
	{ name: 'over the limit', stored: { startShortcuts: manyShortcuts(20, 'test') } },
	...engines.map((searchEngine) => ({ name: `engine ${searchEngine}`, stored: { searchEngine } })),
	...accentThemes.map((accent) => ({ name: `accent ${accent}`, stored: { accent } })),
]

const hslCases: [number, number, number][] = [
	[0, 0, 1], [0, 0, 0], [0, 0, 0.5], [0, 1, 0.5], [120, 1, 0.5], [240, 1, 0.5], [480, 1, 0.5], [-120, 1, 0.5],
	[38, 0.47, 0.941], [211, 0.88 * 0.575, 0.649], [272, 0.82 * 0.498, 0.578], [142, 1.2, -0.1], [359.5, 0.3, 0.3], [-0.0001, 0.5, 0.25],
]

const mixCases: [string, string, number][] = [
	['#F7F2E9', '#FFFDF8', 0.45], ['#2C251E', '#F0E7D8', 0.13], ['#000000', '#FFFFFF', 0.5], ['#000000', '#FFFFFF', 0.25],
	['#102030', '#FFEEDD', 0], ['#102030', '#FFEEDD', 1], ['#102030', '#FFEEDD', 1.5], ['#102030', '#FFEEDD', -1], ['112233', 'AABBCC', 0.35],
]

const contrastCases: [string, string][] = [['#000000', '#FFFFFF'], ['#F7F2E9', '#241F19'], ['#D9B872', '#2A2118'], ['#777777', '#777777']]

function paletteVectors(main: MainStartPage) {
	return {
		hsl: hslCases.map(([hue, saturation, lightness]) => ({ hue, saturation, lightness, hex: hexFromHSL(hue, saturation, lightness) })),
		mix: mixCases.map(([a, b, amount]) => ({ a, b, amount, hex: mix(a, b, amount) })),
		contrast: contrastCases.map(([a, b]) => ({ a, b, ratio: contrastRatio(a, b) })),
		badHex: ['not-a-colour', '#FFF', '#GGGGGG', '#F7F2E9\n', '', '##F7F2E9', '#F7F2E9F'],
		accents: accentThemes.map((accent) => {
			const light = lightTokens(accent)
			const dark = darkTokens(accent)
			return {
				accent,
				light,
				dark,
				lightDerived: { control: control(light), raised: raised(light), accentInk: accentInk(light) },
				darkDerived: { control: control(dark), raised: raised(dark), accentInk: accentInk(dark) },
				startPageTheme: main.startPageTheme(accent),
			}
		}),
	}
}

export function startPageVectors(main: MainStartPage) {
	const theme = themeFor('yellow')
	for (const accent of accentThemes) {
		if (JSON.stringify(themeFor(accent)) !== JSON.stringify(main.startPageTheme(accent))) {
			throw new Error(`scripts/start-page-vectors.ts themeFor(${accent}) has drifted from src/main/start-page.ts startPageTheme`)
		}
	}
	return {
		editShortcutsURL,
		palette: paletteVectors(main),
		shortcuts: {
			limit: 16,
			defaults,
			decode: decodeRaws.map((raw) => ({ raw, list: decode(raw) })),
			encode: encodeLists.map((list) => ({ list, encoded: encode(list) })),
			make: makeCases.map(([title, address]) => ({ title, address, result: make(title, address) })),
			name: makeCases
				.flatMap(([, address]) => {
					const result = classify(address)
					return result?.kind === 'url' ? [result.url] : []
				})
				.map((url) => ({ url, name: name(url) })),
			glyph: Object.values(shortcutLists)
				.flat()
				.map((shortcut) => ({ shortcut, glyph: glyph(shortcut) })),
			isWebAddress: isWebAddressCases.map((text) => ({ text, result: isWebAddress(text) })),
			escape: [`<script>"x" & 'y'</script>`, '', 'plain', '&amp;', `a'b"c<d>e&f`].map((text) => ({ text, escaped: escapeHTML(text) })),
		},
		pages: pageInputs().map(({ name: pageName, input }) => ({ name: pageName, input, html: startPageHTML(input) })),
		pageErrors: [
			...badSearchURLs.map((searchURL) => ({ name: `search url ${JSON.stringify(searchURL)}`, input: { searchURL, shortcuts: defaults, theme } })),
			...badThemes().map((bad) => ({ name: `token ${bad.name}`, input: { searchURL: google, shortcuts: defaults, theme: bad.theme } })),
		].map((c) => ({ ...c, error: thrownName(() => startPageHTML(c.input)) })),
		themes: [...accentThemes.map((accent) => ({ name: accent, theme: themeFor(accent) })), { name: 'functional colours', theme: functionalTheme() }].map((t) => ({
			...t,
			css: themeCSS(t.theme),
			restyle: restyleScript(t.theme),
		})),
		isStartPage: isStartPageCases.map((url) => ({ url, result: main.isStartPage(url) })),
		navigation: navigationCases.map(([from, to, engine]) => ({ from, to, engine, result: main.classifyNavigation(from, to, engine) })),
		handle: settingsCases.map(({ name: caseName, stored }) => ({
			name: caseName,
			stored,
			html: new main.StartPage(new SettingsStore(parseSettings(stored))).html(),
		})),
		searchFormAction: engines.map((engine) => ({ engine, action: SEARCH_URLS[engine].replace(/\?q=$/, '') })),
	}
}
