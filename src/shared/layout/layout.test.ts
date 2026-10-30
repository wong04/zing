import { describe, expect, it } from 'vitest'
import { DEFAULT_SLEEP_SETTINGS, keepAwakeReason, nextSleepCheck, tabsToSleep, type SleepCandidate } from './sleep'
import { addPane, createSplit, dragDivider, equalize, paneRects, removePane, splitFromDrop, SplitError, stripUnits, type SplitLayout } from './split'

const container = { x: 0, y: 80, width: 1281, height: 720 }

function totalSpan(rects: { x: number; width: number }[]): number {
	return rects.at(-1)!.x + rects.at(-1)!.width - rects[0]!.x
}

describe('creating splits', () => {
	it('needs 2–4 distinct tabs', () => {
		expect(() => createSplit(['a'], 'row')).toThrow(SplitError)
		expect(() => createSplit(['a', 'b', 'c', 'd', 'e'], 'row')).toThrow(SplitError)
		expect(() => createSplit(['a', 'a'], 'row')).toThrow(SplitError)
	})
	it('starts with equal panes', () => expect(createSplit(['a', 'b', 'c'], 'row').ratios).toEqual([1 / 3, 1 / 3, 1 / 3]))
	it('dropping on the left edge puts the dropped tab first, side by side', () => {
		const split = splitFromDrop('target', 'dropped', 'left')
		expect(split.orientation).toBe('row')
		expect(split.tabIds).toEqual(['dropped', 'target'])
	})
	it('dropping on the bottom edge stacks it underneath', () => {
		const split = splitFromDrop('target', 'dropped', 'bottom')
		expect(split.orientation).toBe('column')
		expect(split.tabIds).toEqual(['target', 'dropped'])
	})
})

describe('pane rectangles', () => {
	it('are whole pixels and fill the container exactly, with dividers between', () => {
		for (const n of [2, 3, 4]) {
			const split = createSplit(['a', 'b', 'c', 'd'].slice(0, n), 'row')
			const rects = paneRects(container, split)
			expect(rects.every((r) => Number.isInteger(r.x) && Number.isInteger(r.width))).toBe(true)
			expect(totalSpan(rects)).toBe(container.width)
			for (let i = 1; i < rects.length; i++) expect(rects[i]!.x - (rects[i - 1]!.x + rects[i - 1]!.width)).toBe(4)
		}
	})
	it('fractional containers still produce whole pixels', () => {
		const rects = paneRects({ x: 0.5, y: 10.4, width: 999.7, height: 500.2 }, createSplit(['a', 'b', 'c'], 'column'))
		expect(rects.every((r) => [r.x, r.y, r.width, r.height].every(Number.isInteger))).toBe(true)
	})
	it('a column split keeps the full width', () => {
		const rects = paneRects(container, createSplit(['a', 'b'], 'column'))
		expect(rects.map((r) => r.width)).toEqual([1281, 1281])
		expect(rects[0]!.y).toBe(80)
		expect(rects[1]!.y + rects[1]!.height).toBe(800)
	})
})

describe('dragging a divider', () => {
	const split = createSplit(['a', 'b', 'c'], 'row')
	it('only moves its two neighbours', () => {
		const moved = dragDivider(split, 0, 100, 1208)
		expect(moved.ratios[2]).toBeCloseTo(split.ratios[2]!)
		expect(moved.ratios.reduce((a, b) => a + b)).toBeCloseTo(1)
		expect(moved.ratios[0]! * 1200).toBeCloseTo(500)
	})
	it('stops at the minimum pane size', () => {
		const moved = dragDivider(split, 0, 10_000, 1208)
		expect(moved.ratios[1]! * 1200).toBeCloseTo(240)
	})
	it('refuses a divider that does not exist', () => expect(() => dragDivider(split, 2, 10, 1208)).toThrow(SplitError))
	it('equalize resets', () => expect(equalize(dragDivider(split, 0, 50, 1208)).ratios).toEqual(split.ratios))
})

describe('adding and removing panes', () => {
	const two = createSplit(['a', 'b'], 'row')
	it('a new pane takes an equal share', () => {
		const three = addPane(two, 'c')
		expect(three.tabIds).toEqual(['a', 'b', 'c'])
		expect(three.ratios.map((r) => +r.toFixed(4))).toEqual([0.3333, 0.3333, 0.3333])
	})
	it('no more than four', () => expect(() => addPane(addPane(addPane(two, 'c'), 'd'), 'e')).toThrow(SplitError))
	it('removing renormalises the rest', () => {
		const three: SplitLayout = { ...addPane(two, 'c'), ratios: [0.5, 0.25, 0.25] }
		expect(removePane(three, 'a')?.ratios).toEqual([0.5, 0.5])
	})
	it('removing down to one tab dissolves the split', () => expect(removePane(two, 'a')).toBeUndefined())
})

describe('the tab strip shows each split once', () => {
	it('at the position of its first member', () => {
		const split = { ...createSplit(['b', 'd'], 'row'), id: 's1' }
		expect(stripUnits(['a', 'b', 'c', 'd', 'e'], [split])).toEqual([
			{ kind: 'tab', tabId: 'a' },
			{ kind: 'split', splitId: 's1', tabIds: ['b', 'd'] },
			{ kind: 'tab', tabId: 'c' },
			{ kind: 'tab', tabId: 'e' },
		])
	})
})

describe('sleeping tabs', () => {
	const now = 10 * 60 * 60_000
	const idle: SleepCandidate = { id: 't', lastActiveAt: now - 31 * 60_000, pinned: false, isActive: false, visibleInSplit: false, playingAudio: false, hasUnsavedForm: false, asleep: false }

	it('an idle background tab sleeps after 30 minutes', () => expect(keepAwakeReason(idle, now, DEFAULT_SLEEP_SETTINGS)).toBeUndefined())
	it.each([
		['active', { isActive: true }],
		['visible-in-split', { visibleInSplit: true }],
		['playing-audio', { playingAudio: true }],
		['unsaved-form', { hasUnsavedForm: true }],
		['pinned', { pinned: true }],
		['recently-used', { lastActiveAt: now - 5 * 60_000 }],
		['already-asleep', { asleep: true }],
	] as const)('stays awake when %s', (reason, patch) => {
		expect(keepAwakeReason({ ...idle, ...patch }, now, DEFAULT_SLEEP_SETTINGS)).toBe(reason)
	})
	it('pinned tabs can sleep if the user allows it', () => {
		expect(keepAwakeReason({ ...idle, pinned: true }, now, { ...DEFAULT_SLEEP_SETTINGS, sleepPinned: true })).toBeUndefined()
	})
	it('nothing sleeps when the feature is off', () => expect(tabsToSleep([idle], now, { ...DEFAULT_SLEEP_SETTINGS, enabled: false })).toEqual([]))
	it('schedules one timer for the soonest tab', () => {
		const soon = { ...idle, id: 'soon', lastActiveAt: now - 25 * 60_000 }
		const later = { ...idle, id: 'later', lastActiveAt: now - 10 * 60_000 }
		expect(nextSleepCheck([idle, soon, later], now, DEFAULT_SLEEP_SETTINGS)).toBe(now + 5 * 60_000)
	})
})
