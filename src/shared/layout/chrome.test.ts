import { describe, expect, it } from 'vitest'
import { chromeInsets, clampRailWidth, VERTICAL_TABS } from './chrome'

const base = { favouritesBar: false, railWidth: 240, leftDock: 0 }

describe('chrome insets', () => {
	it('top tabs: tab strip + toolbar above the page', () => expect(chromeInsets({ ...base, position: 'top' })).toEqual({ top: 80, left: 0, right: 0 }))
	it('the Favourites bar adds its height in every position', () => {
		expect(chromeInsets({ ...base, position: 'top', favouritesBar: true }).top).toBe(108)
		expect(chromeInsets({ ...base, position: 'left', favouritesBar: true }).top).toBe(68)
	})
	it('left tabs: only the toolbar on top, the rail on the left', () => expect(chromeInsets({ ...base, position: 'left' })).toEqual({ top: 40, left: 240, right: 0 }))
	it('right tabs: the rail on the right', () => expect(chromeInsets({ ...base, position: 'right' })).toEqual({ top: 40, left: 0, right: 240 }))
	it('a docked panel adds to the left in every position', () => {
		expect(chromeInsets({ ...base, position: 'left', leftDock: 320 }).left).toBe(560)
		expect(chromeInsets({ ...base, position: 'right', leftDock: 320 }).left).toBe(320)
	})
	it('every inset stays on a multiple of 4', () => {
		for (const position of ['top', 'left', 'right'] as const) {
			for (const favouritesBar of [false, true]) {
				const i = chromeInsets({ position, favouritesBar, railWidth: VERTICAL_TABS.collapsedWidth, leftDock: 204 })
				expect([i.top % 4, i.left % 4, i.right % 4]).toEqual([0, 0, 0])
			}
		}
	})
})

describe('rail width', () => {
	it('clamps and snaps to 4px', () => {
		expect(clampRailWidth(10)).toBe(VERTICAL_TABS.minWidth)
		expect(clampRailWidth(9999)).toBe(VERTICAL_TABS.maxWidth)
		expect(clampRailWidth(251)).toBe(252)
	})
})
