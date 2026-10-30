import { describe, expect, it } from 'vitest'
import { iconWidth, pinnedWidth } from '@shared/browser/tabReorder'
import type { Slot } from '@shared/tabs/slots'
import { MAX_TAB_WIDTH, slotMetrics } from './layout'

const pinned = (id: string): Slot => ({ kind: 'tab', key: `pin:${id}`, id, pinned: true })
const loose = (id: string): Slot => ({ kind: 'tab', key: id, id, pinned: false })

describe('slotMetrics', () => {
	it('draws pinned tabs as fixed-width icons', () => {
		const metrics = slotMetrics([pinned('p'), loose('a')], 'a', 800)
		expect(metrics.get('pin:p')).toEqual({ width: pinnedWidth, compact: true })
	})

	it('caps a lone tab instead of spanning the window', () => {
		expect(slotMetrics([loose('a')], 'a', 1400).get('a')?.width).toBe(MAX_TAB_WIDTH)
	})

	it('shrinks crowded tabs to icons and keeps the active one readable', () => {
		const slots = Array.from({ length: 12 }, (_, i) => loose(`t${i}`))
		const metrics = slotMetrics(slots, 't3', 900)
		expect(metrics.get('t0')?.compact).toBe(true)
		expect(metrics.get('t0')?.width).toBeGreaterThanOrEqual(iconWidth)
		expect(metrics.get('t3')?.compact).toBe(false)
	})

	it('finds the active tab inside a stack', () => {
		const stack: Slot = { kind: 'stack', key: 'a.test', name: 'a.test', ids: ['x', 'y'] }
		const slots = Array.from({ length: 10 }, (_, i) => loose(`t${i}`))
		const metrics = slotMetrics([stack, ...slots], 'y', 900)
		expect(metrics.get('a.test')?.compact).toBe(false)
	})
})
