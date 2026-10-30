import { describe, expect, it } from 'vitest'
import { buildSlots, findStack, pinnedSlotCount, snapshotOf, type SnapshotSource } from './slots'

function source(id: string, url: string, extra: Partial<SnapshotSource> = {}): SnapshotSource {
	return { id, url, title: id, pinned: false, createdAt: 1, lastActiveAt: 1, ...extra }
}

describe('snapshotOf', () => {
	it('gives a blank tab no site', () => {
		const snap = snapshotOf(source('a', 'about:blank'), false)
		expect(snap.domain).toBe('')
		expect(snap.group).toBe('')
	})

	it('groups subdomains under the registrable domain', () => {
		const snap = snapshotOf(source('a', 'https://docs.example.co.uk/x'), true)
		expect(snap.domain).toBe('example.co.uk')
		expect(snap.isActive).toBe(true)
	})

	it('splits Google products into their own groups', () => {
		const mail = snapshotOf(source('m', 'https://mail.google.com/'), false)
		const docs = snapshotOf(source('d', 'https://docs.google.com/document/d/1'), false)
		expect(mail.group).not.toBe(docs.group)
		expect(docs.groupName).toBe('Google Docs')
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
