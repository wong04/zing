import { describe, expect, it } from 'vitest'
import { TabStore } from '@shared/model'
import { SettingsStore } from '@shared/settings'
import { TabOrdering, UnknownStackError, type OrderedTabs } from './tab-ordering'

function setup(urls: readonly string[]) {
	let clock = 1000
	const store = new TabStore(() => clock++)
	const ids = urls.map((url) => store.add(url).id)
	store.activate(ids[0]!)
	const tabs: OrderedTabs = {
		get records() {
			return store.tabs
		},
		get activeId() {
			return store.activeId
		},
		applyOrder(order) {
			order.forEach((id, i) => store.move(id, i))
		},
	}
	const settings = new SettingsStore()
	const notices: string[] = []
	const ordering = new TabOrdering(tabs, settings, { notify: (t) => notices.push(t) })
	const order = (): string[] => store.tabs.map((t) => t.id)
	return { store, ids, settings, ordering, notices, order }
}

const mixed = ['https://a.test/1', 'https://b.test/', 'https://a.test/2', 'https://c.test/']

describe('TabOrdering.reorganize', () => {
	it('leaves a manual strip alone', () => {
		const { ordering, ids, order } = setup(mixed)
		ordering.reorganize()
		expect(order()).toEqual(ids)
	})

	it('groups by site and keeps pinned tabs in front', () => {
		const { ordering, ids, order, store } = setup(mixed)
		store.pin(ids[3]!)
		ordering.setOrganization('domain')
		expect(order()).toEqual([ids[3], ids[0], ids[2], ids[1]])
	})

	it('sorts A to Z by title', () => {
		const { ordering, ids, order, store } = setup(['https://x.test/', 'https://y.test/'])
		store.update(ids[0]!, { title: 'Zebra' })
		store.update(ids[1]!, { title: 'Apple' })
		ordering.setOrganization('alphabetical')
		expect(order()).toEqual([ids[1], ids[0]])
	})

	it('puts the newest tab first', () => {
		const { ordering, ids, order } = setup(mixed)
		ordering.setOrganization('newest')
		expect(order()).toEqual([...ids].reverse())
	})
})

describe('TabOrdering drags', () => {
	it('moves a loose tab but not into the pinned run', () => {
		const { ordering, ids, order, store } = setup(mixed)
		store.pin(ids[0]!)
		ordering.move(ids[3]!, 0)
		expect(order()).toEqual([ids[0], ids[3], ids[1], ids[2]])
	})

	it('switches a sorted arrangement to manual, says so, and keeps the dragged order', () => {
		const { ordering, order, settings, notices } = setup(mixed)
		ordering.setOrganization('newest')
		const before = order()
		ordering.move(before[0]!, 2)
		expect(settings.get('tabOrganization')).toBe('manual')
		expect(notices).toHaveLength(1)
		expect(order()).toEqual([before[1], before[2], before[0], before[3]])
	})

	it('moves a stack with all its pages', () => {
		const { ordering, ids, order } = setup(mixed)
		ordering.setDropdowns(true)
		ordering.setOrganization('domain')
		expect(order()).toEqual([ids[0], ids[2], ids[1], ids[3]])
		ordering.moveStack('a.test', 2)
		expect(order()).toEqual([ids[1], ids[3], ids[0], ids[2]])
	})

	it('freezes the shown order before a stack drag under A to Z', () => {
		const { ordering, ids, order, store, settings } = setup(mixed)
		ids.forEach((id, i) => store.update(id, { title: ['m', 'b', 'a', 'z'][i]! }))
		ordering.setDropdowns(true)
		ordering.setOrganization('alphabetical')
		ordering.moveStack('c.test', 0)
		expect(settings.get('tabOrganization')).toBe('manual')
		expect(order()[0]).toBe(ids[3])
	})

	it('reorders pages inside a stack without moving the rest of the strip', () => {
		const { ordering, ids, order } = setup(mixed)
		ordering.moveWithin(ids[2]!, 'a.test', 0)
		expect(order()).toEqual([ids[2], ids[1], ids[0], ids[3]])
	})

	it('refuses an unknown stack', () => {
		const { ordering } = setup(mixed)
		expect(() => ordering.moveStack('nope.test', 0)).toThrow(UnknownStackError)
	})
})
