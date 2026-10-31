import { readFileSync } from 'node:fs'
import { describe, expect, it } from 'vitest'
import { TabStore, UnknownTabError } from './tab-store'
import { zoomInFactor, zoomOutFactor } from './zoom'

interface Op {
	readonly op: string
	readonly id?: string
	readonly anchor?: string
	readonly url?: string
	readonly title?: string
	readonly pinned?: boolean
	readonly index?: number
	readonly n?: number
	readonly patch?: { url?: string; title?: string; faviconUrl?: string }
	readonly returns?: string | null
	readonly returnsUrl?: string
	readonly throws?: string
}

interface Case {
	readonly name: string
	readonly ops: readonly Op[]
	readonly expect: {
		readonly urls?: readonly string[]
		readonly pinned?: readonly string[]
		readonly activeId?: string | null
		readonly canReopen?: boolean
		readonly records?: Record<string, Record<string, unknown>>
	}
}

const { cases } = JSON.parse(readFileSync(new URL('./tab-store.vectors.json', import.meta.url), 'utf8')) as { cases: Case[] }

function clockFrom(start: number): () => number {
	let t = start
	return () => t++
}

function run(c: Case): void {
	const store = new TabStore(clockFrom(1000))
	const ids = new Map<string, string>() // vector id (t1, t2...) -> real id
	const names = new Map<string, string>() // real id -> vector id
	const real = (id: string): string => ids.get(id) ?? id
	const register = (record: { id: string }): string => {
		const name = `t${ids.size + 1}`
		ids.set(name, record.id)
		names.set(record.id, name)
		return name
	}
	const name = (id: string | undefined): string | null => (id === undefined ? null : (names.get(id) ?? id))
	for (const o of c.ops) {
		const perform = (): string | null => {
			switch (o.op) {
				case 'add':
					return register(store.add(o.url!))
				case 'addAfter':
					return register(store.addAfter(real(o.anchor!), o.url!))
				case 'addRestored':
					return register(store.addRestored({ url: o.url!, title: o.title!, pinned: o.pinned! }))
				case 'activate':
					return name(store.activate(real(o.id!)).id)
				case 'touch':
					store.touch(real(o.id!))
					return null
				case 'update':
					return name(store.update(real(o.id!), o.patch!).id)
				case 'close':
					return name(store.close(real(o.id!)).nextActiveId)
				case 'reopenClosed': {
					const record = store.reopenClosed()
					if (!record) return null
					if (o.returnsUrl !== undefined) expect(record.url).toBe(o.returnsUrl)
					return register(record)
				}
				case 'move':
					store.move(real(o.id!), o.index!)
					return null
				case 'pin':
					store.pin(real(o.id!))
					return null
				case 'unpin':
					store.unpin(real(o.id!))
					return null
				case 'nextId':
					return name(store.nextId())
				case 'previousId':
					return name(store.previousId())
				case 'idForShortcut':
					return name(store.idForShortcut(o.n!))
				default:
					throw new Error(`Unknown op ${o.op}`)
			}
		}
		if (o.throws) {
			expect(perform, `${c.name}: ${o.op}`).toThrow(UnknownTabError)
			continue
		}
		const result = perform()
		if (o.returns !== undefined) expect(result, `${c.name}: ${o.op}`).toBe(o.returns)
	}
	const e = c.expect
	if (e.urls) expect(store.tabs.map((t) => t.url)).toEqual(e.urls)
	if (e.pinned) expect(store.tabs.filter((t) => t.pinned).map((t) => t.url)).toEqual(e.pinned)
	if (e.activeId !== undefined) expect(name(store.activeId)).toBe(e.activeId)
	if (e.canReopen !== undefined) expect(store.canReopen).toBe(e.canReopen)
	for (const [id, fields] of Object.entries(e.records ?? {})) expect(store.get(real(id))).toMatchObject(fields)
}

describe('TabStore (shared vectors)', () => {
	it.each(cases.map((c) => [c.name, c] as const))('%s', (_name, c) => run(c))
})

describe('TabStore ids', () => {
	it('creates records with stable UUIDs', () => {
		const store = new TabStore(clockFrom(1000))
		store.add('https://a.test')
		expect(store.tabs[0]?.id).toMatch(/^[0-9a-f-]{36}$/)
	})
})

describe('zoom steps', () => {
	it('steps through Chromium factors and stops at the ends', () => {
		expect(zoomInFactor(1)).toBe(1.1)
		expect(zoomOutFactor(1)).toBe(0.9)
		expect(zoomInFactor(5)).toBe(5)
		expect(zoomOutFactor(0.25)).toBe(0.25)
	})

	it('snaps an off-step factor to the next step', () => {
		expect(zoomInFactor(1.05)).toBe(1.1)
		expect(zoomOutFactor(1.05)).toBe(1)
	})
})
