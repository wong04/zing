import { readFileSync } from 'node:fs'
import { describe, expect, it } from 'vitest'
import { buildSlots, findStack, pinnedSlotCount, snapshotOf, type SnapshotSource } from './slots'

function source(id: string, url: string, extra: Partial<SnapshotSource> = {}): SnapshotSource {
	return { id, url, title: id, pinned: false, createdAt: 1, lastActiveAt: 1, ...extra }
}

// The snapshotOf cases are shared with the C# port (TabOrganizerTests.cs).
const { snapshotOf: snapshotCases } = JSON.parse(
	readFileSync(new URL('../browser/tabOrganizer.vectors.json', import.meta.url), 'utf8'),
) as { snapshotOf: { name: string; url: string; active: boolean; want: Record<string, unknown> }[] }

describe('snapshotOf (shared vectors)', () => {
	it.each(snapshotCases.map((c) => [c.name, c] as const))('%s', (_name, c) => {
		expect(snapshotOf(source('a', c.url), c.active)).toMatchObject(c.want)
	})
})

describe('buildSlots', () => {
	const snaps = [
		snapshotOf(source('p', 'https://pinned.test/', { pinned: true }), false),
		snapshotOf(source('a1', 'https://a.test/1'), false),
		snapshotOf(source('b1', 'https://b.test/'), false),
		snapshotOf(source('a2', 'https://a.test/2'), false),
	]

	it('makes one slot per tab in Separate mode', () => {
		const slots = buildSlots(snaps, 'manual', false)
		expect(slots.map((s) => s.key)).toEqual(['pin:p', 'a1', 'b1', 'a2'])
		expect(pinnedSlotCount(slots)).toBe(1)
	})

	it('stacks sites with several pages in Dropdown mode and leaves one-page sites as tabs', () => {
		const slots = buildSlots(snaps, 'manual', true)
		expect(slots.map((s) => s.kind)).toEqual(['tab', 'stack', 'tab'])
		expect(slots[1]).toMatchObject({ key: 'a.test', ids: ['a1', 'a2'] })
		expect(slots[0]).toMatchObject({ pinned: true })
	})

	it('finds a stack only when it holds more than one page', () => {
		expect(findStack(snaps, 'manual', 'a.test')?.ids).toEqual(['a1', 'a2'])
		expect(findStack(snaps, 'manual', 'b.test')).toBeUndefined()
	})
})
