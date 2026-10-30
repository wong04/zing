// Port of Krackn Tests/toolbox. The point is that what people *type* finds the
// right tool first — so each case is a real query and the one answer it must
// lead with.
import { describe, expect, it } from 'vitest'
import { allItems, groupOf, isInToolbox, searchEntries, toolboxGroups } from './catalog'
import { panelContentFromId, panelId, tool, UtilityTool } from './panelContent'
import { keywords, rank, type Entry } from './toolboxSearch'

// Catalog order, as the Toolbox shows it.
const order = [
	'section:Files',
	'section:Convert',
	'section:Download',
	'tool:Background Remover',
	'section:Open a Link',
	'section:Diagnose',
	'section:Network & VPN',
	'tool:Link Cleaner',
	'section:Security',
	'tool:Passwords',
	'tool:Hashes',
	'tool:HMAC',
	'tool:JWT Decoder',
	'tool:JSON / YAML',
	'tool:Base64',
	'tool:URL Encoding',
	'tool:Regex Tester',
	'tool:UUID',
	'tool:Timestamps',
	'tool:Cron Parser',
	'tool:Scientific Calculator',
	'tool:Graphing Calculator',
	'tool:Unit Converter',
	'tool:Number Bases',
	'tool:Text Case',
	'tool:Text Stats',
	'tool:Color Converter',
	'tool:QR Code',
]

const entries: Entry[] = order.map((id) => ({
	id,
	title: id.slice(id.indexOf(':') + 1),
	group: '',
	keywords: keywords[id] ?? [],
}))

function top(q: string): string {
	return rank(q, entries)[0]?.title ?? '(nothing)'
}

describe('every feature has search words', () => {
	it(`all ${order.length} catalog items have keywords`, () => {
		expect(order.filter((id) => (keywords[id] ?? []).length === 0)).toEqual([])
	})
	it("no keywords for things that don't exist", () => {
		expect(Object.keys(keywords).filter((id) => !order.includes(id))).toEqual([])
	})
})

describe('what people type finds the right thing first', () => {
	const cases: [string, string][] = [
		['sha256', 'Hashes'],
		['checksum', 'Hashes'],
		['md5', 'Hashes'],
		['youtube', 'Download'],
		['mp4', 'Convert'],
		['heic', 'Convert'],
		['epoch', 'Timestamps'],
		['unix time', 'Timestamps'],
		['vpn', 'Network & VPN'],
		['wireguard', 'Network & VPN'],
		['unblock', 'Open a Link'],
		['blocked', 'Open a Link'],
		['regex', 'Regex Tester'],
		['colour', 'Color Converter'],
		['rgb', 'Color Converter'],
		['remove background', 'Background Remover'],
		['json', 'JSON / YAML'],
		['yaml', 'JSON / YAML'],
		['plot', 'Graphing Calculator'],
		['graph', 'Graphing Calculator'],
		['calc', 'Scientific Calculator'],
		['celsius', 'Unit Converter'],
		['binary', 'Number Bases'],
		['camelcase', 'Text Case'],
		['word count', 'Text Stats'],
		['malware', 'Security'],
		['phishing', 'Security'],
		['token', 'JWT Decoder'],
		['guid', 'UUID'],
		['utm', 'Link Cleaner'],
		['cron', 'Cron Parser'],
		['password', 'Passwords'],
		['pdf', 'Files'],
		['qr', 'QR Code'],
	]
	it('has all 34 real queries', () => {
		expect(cases).toHaveLength(34)
	})
	it.each(cases)('"%s" → %s', (query, want) => {
		expect(top(query)).toBe(want)
	})
})

describe('abbreviations and partial words', () => {
	it('"gc" finds Graphing Calculator', () => {
		expect(rank('gc', entries).some((e) => e.title === 'Graphing Calculator')).toBe(true)
	})
	it('"jwtd" → JWT Decoder', () => {
		expect(top('jwtd')).toBe('JWT Decoder')
	})
	it('"conv" → Convert (title beats keyword)', () => {
		expect(top('conv')).toBe('Convert')
	})
	it('"url enc" → URL Encoding', () => {
		expect(top('url enc')).toBe('URL Encoding')
	})
})

describe('no false matches', () => {
	it('nonsense matches nothing', () => {
		expect(rank('zzqx', entries)).toEqual([])
	})
	it('every word must match', () => {
		expect(rank('json zzqx', entries)).toEqual([])
	})
	it('empty query returns everything in order', () => {
		expect(rank('  ', entries)).toEqual(entries)
	})
})

// Zing-only: Krackn's test rebuilt the catalog by hand; these pin that the
// real catalog is the same list, so the hand-built entries above stay honest.
describe('the catalog', () => {
	it('has six groups', () => {
		expect(toolboxGroups).toEqual([
			'Files & Media',
			'Web & Network',
			'Security',
			'Developer',
			'Math & Science',
			'Text & Design',
		])
	})
	it('lists exactly the searched items, in the same order', () => {
		expect(allItems().map(panelId)).toEqual(order)
		expect(searchEntries().map((e) => e.id)).toEqual(order)
	})
	it('search entries carry their group and keywords', () => {
		const hashes = searchEntries().find((e) => e.id === 'tool:Hashes')
		expect(hashes?.group).toBe('Security')
		expect(hashes?.keywords).toContain('sha256')
	})
	it('ids round-trip', () => {
		expect(order.map((id) => panelContentFromId(id)).map((content) => content && panelId(content))).toEqual(order)
		expect(panelContentFromId('tool:Nope')).toBeNull()
		expect(panelContentFromId('Hashes')).toBeNull()
	})
	it('engine setup lives in Settings, not the Toolbox', () => {
		expect(isInToolbox(tool(UtilityTool.tools))).toBe(false)
		expect(groupOf(tool(UtilityTool.hash))).toBe('Security')
	})
})
