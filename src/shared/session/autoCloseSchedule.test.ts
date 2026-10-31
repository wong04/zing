// The cases live in autoCloseSchedule.vectors.json, which the C# port's tests read too.
import { describe, expect, it } from 'vitest'
import { defaultAutoClosePolicy, utcCalendar, type AutoClosePolicy } from '../browser/tabLifecycle'
import { makeTabSnapshot, type TabSnapshot } from '../browser/tabOrganizer'
import { nextAutoCloseCheck, nextDailyFire } from './autoCloseSchedule'
import raw from './autoCloseSchedule.vectors.json'

interface TabSpec {
	id: string
	agoMs: number
	isPinned?: boolean
	isActive?: boolean
}

interface Vectors {
	now: string
	nextDailyFire: { name: string; policy: Partial<AutoClosePolicy>; expectOffsetMs: number }[]
	nextAutoCloseCheck: { name: string; policy: Partial<AutoClosePolicy>; tabs: string | TabSpec[]; expectOffsetMs: number | null }[]
	tabSets: Record<string, TabSpec[]>
}

const vectors = raw as unknown as Vectors
const now = Date.parse(vectors.now)

function policyOf(changes: Partial<AutoClosePolicy>): AutoClosePolicy {
	return { ...defaultAutoClosePolicy, enabled: true, ...changes }
}

function snapshots(specs: string | TabSpec[]): TabSnapshot[] {
	const list = typeof specs === 'string' ? vectors.tabSets[specs]! : specs
	return list.map((t) => ({
		...makeTabSnapshot({ id: t.id, title: t.id, domain: '', lastAccessed: now - t.agoMs, createdAt: now - t.agoMs }),
		isPinned: t.isPinned ?? false,
		isActive: t.isActive ?? false,
	}))
}

describe('nextDailyFire', () => {
	it.each(vectors.nextDailyFire)('$name', ({ policy, expectOffsetMs }) => {
		expect(nextDailyFire(policyOf(policy), now, utcCalendar)).toBe(now + expectOffsetMs)
	})
})

describe('nextAutoCloseCheck', () => {
	it.each(vectors.nextAutoCloseCheck)('$name', ({ policy, tabs, expectOffsetMs }) => {
		const next = nextAutoCloseCheck(snapshots(tabs), policyOf(policy), now, utcCalendar)
		expect(next === undefined ? null : next - now).toBe(expectOffsetMs)
	})
})
