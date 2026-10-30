import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { SleepSettings } from '@shared/layout/sleep'
import type { TabRecord } from '@shared/model'
import type { Clock } from '@shared/session/clock'
import { SLEEP_RETRY_MS, TabSleeper, type SleepHost, type SleepablePage } from './tab-sleep'

const MIN = 60_000
const clock: Clock = { now: () => Date.now(), onAdvance: () => () => undefined }
const settings: SleepSettings = { enabled: true, afterMinutes: 30, sleepPinned: false }

function record(id: string, changes: Partial<TabRecord> = {}): TabRecord {
	const at = Date.now()
	return { id, url: `https://${id}.test/`, title: id, pinned: false, createdAt: at, lastActiveAt: at, updatedAt: at, ...changes }
}

interface PageState {
	audible: boolean
	dirty: boolean
	scrollY: number
	onProbe?: () => void
}

class FakeHost implements SleepHost {
	readonly slept: string[] = []
	readonly pages = new Map<string, PageState>()
	sleeper: TabSleeper | undefined

	constructor(
		public records: TabRecord[],
		public activeId: string | undefined,
	) {
		for (const t of records) this.pages.set(t.id, { audible: false, dirty: false, scrollY: 0 })
	}

	hasView(id: string): boolean {
		return this.pages.has(id)
	}

	contentsFor(id: string): SleepablePage | undefined {
		const state = this.pages.get(id)
		if (!state) return undefined
		return {
			isDestroyed: () => false,
			isCurrentlyAudible: () => state.audible,
			executeJavaScript: () => {
				state.onProbe?.()
				return Promise.resolve({ scrollY: state.scrollY, dirty: state.dirty })
			},
			capturePage: () => Promise.resolve({ isEmpty: () => true }),
			once: () => undefined,
		} as unknown as SleepablePage
	}

	sleep(id: string): void {
		this.slept.push(id)
		this.pages.delete(id)
		this.sleeper?.reschedule()
	}
}

function arm(host: FakeHost): TabSleeper {
	const sleeper = new TabSleeper(host, () => settings, clock)
	host.sleeper = sleeper
	sleeper.reschedule()
	return sleeper
}

beforeEach(() => {
	vi.useFakeTimers()
	vi.setSystemTime(Date.UTC(2026, 0, 10, 12))
})
afterEach(() => vi.useRealTimers())

describe('TabSleeper', () => {
	it('sleeps an inactive tab after the idle period, never the active or a pinned one', async () => {
		const host = new FakeHost([record('active'), record('idle'), record('pin', { pinned: true })], 'active')
		arm(host)
		await vi.advanceTimersByTimeAsync(29 * MIN)
		expect(host.slept).toEqual([])
		await vi.advanceTimersByTimeAsync(2 * MIN)
		expect(host.slept).toEqual(['idle'])
	})

	it('leaves a tab playing audio awake, then sleeps it once it falls silent', async () => {
		const host = new FakeHost([record('active'), record('radio')], 'active')
		host.pages.get('radio')!.audible = true
		arm(host)
		await vi.advanceTimersByTimeAsync(31 * MIN)
		expect(host.slept).toEqual([])
		host.pages.get('radio')!.audible = false
		await vi.advanceTimersByTimeAsync(SLEEP_RETRY_MS + MIN)
		expect(host.slept).toEqual(['radio'])
	})

	it('leaves a tab with unsaved input awake, then sleeps it once the input is gone', async () => {
		const host = new FakeHost([record('active'), record('form')], 'active')
		host.pages.get('form')!.dirty = true
		arm(host)
		await vi.advanceTimersByTimeAsync(31 * MIN)
		expect(host.slept).toEqual([])
		host.pages.get('form')!.dirty = false
		await vi.advanceTimersByTimeAsync(SLEEP_RETRY_MS + MIN)
		expect(host.slept).toEqual(['form'])
	})

	it('does not sleep a tab the user returned to while it was being inspected', async () => {
		const host = new FakeHost([record('active'), record('back')], 'active')
		host.pages.get('back')!.onProbe = () => {
			host.records = host.records.map((t) => (t.id === 'back' ? { ...t, lastActiveAt: Date.now() } : t))
		}
		arm(host)
		await vi.advanceTimersByTimeAsync(31 * MIN)
		expect(host.slept).toEqual([])
	})

	it('sleeps a tab that is already past its idle time when scheduling starts', async () => {
		const host = new FakeHost([record('active'), record('stale', { lastActiveAt: Date.now() - 2 * 60 * MIN })], 'active')
		arm(host)
		await vi.advanceTimersByTimeAsync(200)
		expect(host.slept).toEqual(['stale'])
	})

	it('keeps one timer armed, and none when nothing can sleep', () => {
		const host = new FakeHost([record('active'), record('a'), record('b')], 'active')
		const sleeper = arm(host)
		for (let i = 0; i < 10; i++) sleeper.reschedule()
		expect(vi.getTimerCount()).toBe(1)
		vi.clearAllTimers()
		arm(new FakeHost([record('only')], 'only'))
		expect(vi.getTimerCount()).toBe(0)
	})
})
