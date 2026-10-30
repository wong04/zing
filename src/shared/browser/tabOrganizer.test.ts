// Port of Krackn `Tests/taborg`: organisation, pinning, stacks, reordering.
import { describe, expect, it } from 'vitest'
import { siteGroupOfURL } from './siteGroup'
import { registrableDomain } from './registrableDomain'
import {
	TabArrangementError,
	arrange,
	freezeDisplayedOrder,
	makeTabSnapshot,
	moveTab,
	moveWithin,
	moving,
	movingStack,
	order,
	stacks,
	stacksByName,
	type TabSnapshot,
} from './tabOrganizer'
import { mixed, tab } from './taborgFixtures'

describe('organisation', () => {
	it('manual leaves order alone', () => {
		expect(order(mixed, 'manual')).toEqual(['gmail', 'bbc', 'docs', 'apple', 'drive'])
	})

	// Same-site tabs clump, and sites keep first-appearance order — so opening a
	// new site never shuffles the tabs already on screen.
	it('by domain clumps same site', () => {
		expect(order(mixed, 'domain')).toEqual(['gmail', 'docs', 'drive', 'bbc', 'apple'])
	})

	it('alphabetical', () => {
		expect(order(mixed, 'alphabetical')).toEqual(['apple', 'bbc', 'docs', 'drive', 'gmail'])
	})

	// createdMinutesAgo counts backwards, so "drive" (10 min ago) is newest.
	it('newest first', () => {
		expect(order(mixed, 'newest')).toEqual(['drive', 'apple', 'docs', 'bbc', 'gmail'])
	})

	it('recently used', () => {
		const byUse = [
			tab('a', 'a.com', { accessedMinutesAgo: 30 }),
			tab('b', 'b.com', { accessedMinutesAgo: 5 }),
			tab('c', 'c.com', { accessedMinutesAgo: 60 }),
		]
		expect(order(byUse, 'recentlyUsed')).toEqual(['b', 'a', 'c'])
	})

	it('alphabetical ignores case and accents', () => {
		const cased = [tab('zebra', 'z.com'), tab('Álvaro', 'a.com'), tab('apple', 'b.com')]
		expect(order(cased, 'alphabetical')).toEqual(['Álvaro', 'apple', 'zebra'])
	})
})

describe('pinning', () => {
	const withPins = [
		tab('loose1', 'z.com', { createdMinutesAgo: 10 }),
		tab('pin1', 'm.com', { createdMinutesAgo: 50, pinned: true }),
		tab('pin2', 'n.com', { createdMinutesAgo: 40, pinned: true }),
		tab('loose2', 'a.com', { createdMinutesAgo: 20 }),
	]

	// Pinned first, in their own order, whatever the strategy says. The loose
	// tabs still sort normally around them — titles "loose1" < "loose2".
	it('pinned stay first under alphabetical', () => {
		expect(order(withPins, 'alphabetical')).toEqual(['pin1', 'pin2', 'loose1', 'loose2'])
	})
	it('pinned stay first under domain', () => {
		expect(order(withPins, 'domain')).toEqual(['pin1', 'pin2', 'loose1', 'loose2'])
	})
	// pin1 was created *before* pin2, so "newest first" would put pin2 ahead of it
	// if pinned tabs were being sorted. They keep insertion order instead.
	it('pinned are exempt from sorting', () => {
		expect(order(withPins, 'newest')).toEqual(['pin1', 'pin2', 'loose1', 'loose2'])
	})

	// Blank new tabs bucket together rather than piling under the first real site,
	// and the bucket takes its place by first appearance like any other site.
	it('empty domains form their own group', () => {
		const withBlank = [tab('real', 'x.com'), tab('blank1', ''), tab('other', 'y.com'), tab('blank2', '')]
		expect(order(withBlank, 'domain')).toEqual(['real', 'blank1', 'blank2', 'other'])
	})
})

describe('dropdown style: one entry per site, in the arrangement\'s order', () => {
	const st = stacks(mixed)

	it('one stack per site', () => {
		expect(st.length).toBe(3)
	})
	it('sites in the order they first appeared', () => {
		expect(st.map((s) => s.domain)).toEqual(['google.com', 'bbc.co.uk', 'apple.com'])
	})
	it('a site\'s pages keep their order inside its stack', () => {
		expect(st[0]?.ids).toEqual(['gmail', 'docs', 'drive'])
	})
	// The arrangement decides the order: tabs arrive already arranged.
	it('Newest + Dropdown: the site with the newest page comes first', () => {
		const byId = new Map(mixed.map((t) => [t.id, t]))
		const recentFirst = order(mixed, 'newest').map((id) => byId.get(id) as TabSnapshot)
		expect(stacks(recentFirst)[0]?.domain).toBe('google.com')
	})
	it('A–Z + Dropdown: sites sort by name', () => {
		expect(stacksByName(mixed).map((s) => s.domain)).toEqual(['apple.com', 'bbc.co.uk', 'google.com'])
	})
	it('A–Z keeps new tabs after the sites', () => {
		expect(stacksByName([...mixed, tab('blank', '')]).at(-1)?.domain).toBe('')
	})
	it('blank new tabs are each their own stack', () => {
		expect(stacks([...mixed, tab('new1', ''), tab('new2', '')]).length).toBe(5)
	})
	it('pinned tabs aren\'t stacked', () => {
		expect(stacks([tab('pin', 'google.com', { pinned: true }), ...mixed])[0]?.ids.length).toBe(3)
	})
})

describe('Dropdown style: Google\'s products are separate dropdowns', () => {
	const group = (url: string) => siteGroupOfURL(new URL(url))
	const named: [string, string][] = [
		['https://docs.google.com/document/d/abc/edit', 'Google Docs'],
		['https://docs.google.com/document/u/0/', 'Google Docs'],
		['https://docs.google.com/spreadsheets/d/abc/edit', 'Google Sheets'],
		['https://docs.google.com/presentation/d/abc/edit', 'Google Slides'],
		['https://docs.google.com/forms/d/abc/viewform', 'Google Forms'],
		['https://gemini.google.com/app/123', 'Gemini'],
		['https://mail.google.com/mail/u/0/#inbox', 'Gmail'],
		['https://drive.google.com/drive/my-drive', 'Google Drive'],
		['https://www.google.com/search?q=krackn', 'Google'],
		['https://www.google.com/url?q=https://example.com', 'Google'],
		['https://www.google.com/maps/place/x', 'Google Maps'],
		['https://www.google.co.uk/search?q=x', 'Google'],
		['https://accounts.google.com/signin', 'Google Account'],
		['https://unknownthing.google.com/', 'unknownthing.google.com'],
		// Everything else is still one dropdown per site.
		['https://en.wikipedia.org/wiki/Kraken', 'wikipedia.org'],
		['https://github.com/apple/swift', 'github.com'],
		['https://www.youtube.com/watch?v=1', 'youtube.com'],
		// Look-alikes aren't Google.
		['https://google.xyz/', 'google.xyz'],
		['https://google.com.evil.com/', 'evil.com'],
	]
	it.each(named)('%s → %s', (url, want) => {
		expect(group(url).name).toBe(want)
	})

	it('two Docs share a dropdown', () => {
		expect(group('https://docs.google.com/document/d/a').key).toBe(group('https://docs.google.com/document/d/b').key)
	})
	it('a Doc and a Sheet don\'t', () => {
		expect(group('https://docs.google.com/document/d/a').key).not.toBe(
			group('https://docs.google.com/spreadsheets/d/a').key,
		)
	})
	it('two Wikipedia languages still share one', () => {
		expect(group('https://en.wikipedia.org/').key).toBe(group('https://de.wikipedia.org/').key)
	})

	const snap = (url: string): TabSnapshot => {
		const parsed = new URL(url)
		const g = siteGroupOfURL(parsed)
		return makeTabSnapshot({
			id: url,
			title: url,
			domain: registrableDomain(parsed.hostname),
			group: g.key,
			groupName: g.name,
			lastAccessed: 0,
			createdAt: 0,
		})
	}
	const google = [
		'https://docs.google.com/document/d/1',
		'https://gemini.google.com/app',
		'https://docs.google.com/presentation/d/1',
		'https://docs.google.com/document/d/2',
		'https://www.google.com/search?q=a',
		'https://github.com/x',
	].map(snap)

	it('stacks: Docs ×2, Gemini, Slides, Google, GitHub', () => {
		expect(stacks(google).map((s) => `${s.name} ${s.ids.length}`)).toEqual([
			'Google Docs 2',
			'Gemini 1',
			'Google Slides 1',
			'Google 1',
			'github.com 1',
		])
	})
	it('A–Z sorts by the name shown', () => {
		expect(stacksByName(google).map((s) => s.name)).toEqual([
			'Gemini',
			'github.com',
			'Google',
			'Google Docs',
			'Google Slides',
		])
	})
})

describe('Dropdown style: reordering stacks and the pages inside one', () => {
	const groups = [['a1', 'a2'], ['b1'], ['c1', 'c2', 'c3']]
	const c = ['c1', 'c2', 'c3']

	it('stack dragged right takes all its pages along', () => {
		expect(movingStack(groups, 0, 2).flat()).toEqual(['b1', 'c1', 'c2', 'c3', 'a1', 'a2'])
	})
	it('stack dragged left', () => {
		expect(movingStack(groups, 2, 0).flat()).toEqual(['c1', 'c2', 'c3', 'a1', 'a2', 'b1'])
	})
	it('out-of-range move changes nothing', () => {
		expect(movingStack(groups, 0, 9).flat()).toEqual(['a1', 'a2', 'b1', 'c1', 'c2', 'c3'])
	})
	it('page dragged to the bottom of its list', () => {
		expect(moving('c1', c, 2)).toEqual(['c2', 'c3', 'c1'])
	})
	it('page dragged to the top', () => {
		expect(moving('c3', c, 0)).toEqual(['c3', 'c1', 'c2'])
	})
	it('past the end clamps to the end', () => {
		expect(moving('c1', c, 99)).toEqual(['c2', 'c3', 'c1'])
	})
	it('an id not in the list changes nothing', () => {
		expect(moving('a1', c, 0)).toEqual(['c1', 'c2', 'c3'])
	})
})

// Not in Tests/taborg: `freezeDisplayedOrder` lived on BrowserModel there, untested.
describe('freezing the displayed order', () => {
	it('makes the A–Z dropdown order the manual order, pinned first', () => {
		const tabs = [tab('pin', 'z.com', { pinned: true }), ...mixed]
		expect(freezeDisplayedOrder(tabs, 'alphabetical')).toEqual([
			'pin',
			'apple',
			'bbc',
			'gmail',
			'docs',
			'drive',
		])
	})
	it('groups a manual strip by site, as the dropdowns show it', () => {
		expect(freezeDisplayedOrder(mixed, 'manual')).toEqual(['gmail', 'docs', 'drive', 'bbc', 'apple'])
	})
	it('refuses an arrangement that loses a tab', () => {
		expect(() => arrange(mixed, [['gmail']])).toThrow(TabArrangementError)
	})
})

// Not in Tests/: `BrowserModel.moveTab` and `move(_:within:to:)` were model code with no
// checks of their own, so these follow the Swift behaviour directly.
describe('dragging a tab along the strip keeps pinned and unpinned apart', () => {
	const strip = [
		tab('p1', 'a.com', { pinned: true }),
		tab('p2', 'b.com', { pinned: true }),
		tab('l1', 'c.com'),
		tab('l2', 'd.com'),
		tab('l3', 'e.com'),
	]

	it('an unpinned tab moves among the unpinned', () => {
		expect(moveTab(strip, 2, 4)).toEqual(['p1', 'p2', 'l2', 'l3', 'l1'])
		expect(moveTab(strip, 4, 2)).toEqual(['p1', 'p2', 'l3', 'l1', 'l2'])
	})
	it('a pinned tab moves among the pinned', () => {
		expect(moveTab(strip, 0, 1)).toEqual(['p2', 'p1', 'l1', 'l2', 'l3'])
	})
	it('an unpinned tab dropped in the pinned run stops at its edge', () => {
		expect(moveTab(strip, 4, 0)).toEqual(['p1', 'p2', 'l3', 'l1', 'l2'])
	})
	it('a pinned tab dropped among the unpinned stops at the end of the pinned run', () => {
		expect(moveTab(strip, 0, 4)).toEqual(['p2', 'p1', 'l1', 'l2', 'l3'])
	})
	it('a move clamped back onto itself changes nothing', () => {
		expect(moveTab(strip, 2, 0)).toEqual(['p1', 'p2', 'l1', 'l2', 'l3'])
		expect(moveTab(strip, 1, 3)).toEqual(['p1', 'p2', 'l1', 'l2', 'l3'])
	})
	it('the same slot or an index off the strip changes nothing', () => {
		expect(moveTab(strip, 3, 3)).toEqual(['p1', 'p2', 'l1', 'l2', 'l3'])
		expect(moveTab(strip, 9, 0)).toEqual(['p1', 'p2', 'l1', 'l2', 'l3'])
		expect(moveTab(strip, 2, -1)).toEqual(['p1', 'p2', 'l1', 'l2', 'l3'])
	})
	it('with nothing pinned, any slot is fair', () => {
		const loose = [tab('a', 'a.com'), tab('b', 'b.com'), tab('c', 'c.com')]
		expect(moveTab(loose, 2, 0)).toEqual(['c', 'a', 'b'])
	})
})

describe('dragging a page inside its dropdown', () => {
	// Manual order can leave a site's pages apart: google's are at slots 1 and 3.
	const strip = [
		tab('pin', 'z.com', { pinned: true }),
		tab('g1', 'google.com'),
		tab('b1', 'bbc.co.uk'),
		tab('g2', 'google.com'),
		tab('g3', 'google.com'),
	]
	const google = ['g1', 'g2', 'g3']

	it('the pages swap among the slots the stack already holds', () => {
		expect(moveWithin(strip, 'g3', google, 0)).toEqual(['pin', 'g3', 'b1', 'g1', 'g2'])
	})
	it('to the bottom of the list', () => {
		expect(moveWithin(strip, 'g1', google, 2)).toEqual(['pin', 'g2', 'b1', 'g3', 'g1'])
	})
	it('past the end clamps to the end', () => {
		expect(moveWithin(strip, 'g1', google, 99)).toEqual(['pin', 'g2', 'b1', 'g3', 'g1'])
	})
	it('the stack order comes from the strip, not the list passed in', () => {
		expect(moveWithin(strip, 'g3', ['g3', 'g2', 'g1'], 0)).toEqual(['pin', 'g3', 'b1', 'g1', 'g2'])
	})
	it('a page not in the stack changes nothing', () => {
		expect(moveWithin(strip, 'b1', google, 0)).toEqual(['pin', 'g1', 'b1', 'g2', 'g3'])
	})
})
