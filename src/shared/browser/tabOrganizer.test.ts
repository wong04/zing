// Port of Krackn `Tests/taborg`: organisation, pinning, stacks, reordering.
// The cases live in tabOrganizer.vectors.json, which the C# port (TabOrganizerTests.cs) reads too.
import { readFileSync } from 'node:fs'
import { describe, expect, it } from 'vitest'
import { registrableDomain } from './registrableDomain'
import { siteGroupOfURL } from './siteGroup'
import {
	TabArrangementError,
	arrange,
	displayedStacks,
	freezeDisplayedOrder,
	makeTabSnapshot,
	moveTab,
	moveWithin,
	moving,
	movingStack,
	order,
	stacks,
	stacksByName,
	type Stack,
	type TabOrganization,
	type TabSnapshot,
} from './tabOrganizer'

interface TabSpec {
	readonly id?: string
	readonly domain?: string
	readonly url?: string
	readonly pinned?: boolean
	readonly createdMinutesAgo?: number
	readonly accessedMinutesAgo?: number
}

interface Case {
	readonly name: string
	readonly op: string
	readonly tabs?: string | readonly TabSpec[]
	readonly prepend?: readonly TabSpec[]
	readonly append?: readonly TabSpec[]
	readonly organization?: TabOrganization
	readonly project?: string
	readonly groups?: string[][]
	readonly from?: number
	readonly to?: number
	readonly id?: string
	readonly ids?: string[]
	readonly stackIds?: string[]
	readonly index?: number
	readonly url?: string
	readonly urls?: string[]
	readonly want: unknown
}

const vectors = JSON.parse(readFileSync(new URL('./tabOrganizer.vectors.json', import.meta.url), 'utf8')) as {
	base: number
	fixtures: Record<string, TabSpec[]>
	cases: Case[]
}

const minuteMs = 60_000

function snapshotOfSpec(spec: TabSpec): TabSnapshot {
	if (spec.url !== undefined) {
		const parsed = new URL(spec.url)
		const group = siteGroupOfURL(parsed)
		return makeTabSnapshot({
			id: spec.url,
			title: spec.url,
			domain: registrableDomain(parsed.hostname),
			group: group.key,
			groupName: group.name,
			lastAccessed: 0,
			createdAt: 0,
		})
	}
	return makeTabSnapshot({
		id: spec.id!,
		title: spec.id!,
		domain: spec.domain!,
		lastAccessed: vectors.base - (spec.accessedMinutesAgo ?? 0) * minuteMs,
		createdAt: vectors.base - (spec.createdMinutesAgo ?? 0) * minuteMs,
		isPinned: spec.pinned ?? false,
	})
}

function tabsOf(c: Case): TabSnapshot[] {
	const specs = typeof c.tabs === 'string' ? vectors.fixtures[c.tabs]! : (c.tabs ?? [])
	return [...(c.prepend ?? []), ...specs, ...(c.append ?? [])].map(snapshotOfSpec)
}

function project(list: readonly Stack[], how: string): unknown {
	switch (how) {
		case 'count':
			return list.length
		case 'domain':
			return list.map((s) => s.domain)
		case 'name':
			return list.map((s) => s.name)
		case 'nameCount':
			return list.map((s) => `${s.name} ${s.ids.length}`)
		case 'firstIds':
			return list[0]?.ids
		case 'firstIdsLength':
			return list[0]?.ids.length
		case 'firstDomain':
			return list[0]?.domain
		case 'lastDomain':
			return list.at(-1)?.domain
		case 'full':
			return list
		default:
			throw new Error(`Unknown projection ${how}`)
	}
}

function perform(c: Case): unknown {
	switch (c.op) {
		case 'order':
			return order(tabsOf(c), c.organization!)
		case 'stacks':
			return project(stacks(tabsOf(c)), c.project!)
		case 'stacksByName':
			return project(stacksByName(tabsOf(c)), c.project!)
		case 'orderedStacks': {
			const tabs = tabsOf(c)
			const byId = new Map(tabs.map((t) => [t.id, t]))
			const arranged = order(tabs, c.organization!).map((id) => byId.get(id) as TabSnapshot)
			return project(displayedStacks(arranged, 'manual'), c.project!)
		}
		case 'movingStack':
			return movingStack(c.groups!, c.from!, c.to!).flat()
		case 'moving':
			return moving(c.id!, c.ids!, c.index!)
		case 'freeze':
			return freezeDisplayedOrder(tabsOf(c), c.organization!)
		case 'arrange':
			try {
				return arrange(tabsOf(c), c.groups!)
			} catch (error) {
				if (error instanceof TabArrangementError) return { throws: 'TabArrangementError' }
				throw error
			}
		case 'moveTab':
			return moveTab(tabsOf(c), c.from!, c.to!)
		case 'moveWithin':
			return moveWithin(tabsOf(c), c.id!, c.stackIds!, c.index!)
		case 'siteGroup':
			return { name: siteGroupOfURL(new URL(c.url!)).name }
		case 'siteGroupKeys':
			return c.urls!.map((url) => siteGroupOfURL(new URL(url)).key)
		case 'sameKey': {
			const [a, b] = c.urls!.map((url) => siteGroupOfURL(new URL(url)).key)
			return a === b
		}
		default:
			throw new Error(`Unknown op ${c.op}`)
	}
}

describe('tab organizer (shared vectors)', () => {
	it.each(vectors.cases.map((c) => [c.name, c] as const))('%s', (_name, c) => {
		expect(perform(c)).toEqual(c.want)
	})
})
