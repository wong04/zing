// The scenarios live in tabLifecycle.vectors.json ("sleeper"), which the C# port's tests play too.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import raw from '@shared/browser/tabLifecycle.vectors.json'
import type { SleepSettings } from '@shared/layout/sleep'
import type { TabRecord } from '@shared/model'
import type { Clock } from '@shared/session/clock'
import { TabSleeper, type SleepHost, type SleepablePage } from './tab-sleep'

type Step =
	| { advanceMinutes: number }
	| { advanceMs: number }
	| { reschedule: number }
	| { setAudible: string; value: boolean }
	| { setDirty: string; value: boolean }
	| { setProbeFails: string; value: boolean }
	| { expectSlept: string[] }
	| { expectTimers: number }
	| { expectErrors: number }
	| { expectWakeScroll: string; value: number | null }

interface TabSpec {
	id: string
	pinned?: boolean
	audible?: boolean
	dirty?: boolean
	scrollY?: number
	touchOnProbe?: boolean
	probeFails?: boolean
	idleMinutes?: number
}

interface Scenario {
	name: string
	active: string
	tabs: TabSpec[]
	steps: Step[]
}

const scenarios = (raw as unknown as { sleeper: Scenario[] }).sleeper
const MIN = 60_000
const clock: Clock = { now: () => Date.now(), onAdvance: () => () => undefined }
const settings: SleepSettings = { enabled: true, afterMinutes: 30, sleepPinned: false }

interface PageState {
	audible: boolean
	dirty: boolean
	scrollY: number
	probeFails: boolean
	onProbe?: () => void
}

class FakeHost implements SleepHost {
	readonly slept: string[] = []
	readonly pages = new Map<string, PageState>()
	sleeper: TabSleeper | undefined

	constructor(
		public records: TabRecord[],
		public activeId: string | undefined,
	) {}

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
				if (state.probeFails) return Promise.reject(new Error('page navigated'))
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

function build(scenario: Scenario): FakeHost {
	const at = Date.now()
	const records = scenario.tabs.map<TabRecord>((t) => ({
		id: t.id,
		url: `https://${t.id}.test/`,
		title: t.id,
		pinned: t.pinned ?? false,
		createdAt: at,
		lastActiveAt: at - (t.idleMinutes ?? 0) * MIN,
		updatedAt: at,
	}))
	const host = new FakeHost(records, scenario.active)
	for (const t of scenario.tabs) {
		host.pages.set(t.id, {
			audible: t.audible ?? false,
			dirty: t.dirty ?? false,
			scrollY: t.scrollY ?? 0,
			probeFails: t.probeFails ?? false,
			onProbe: t.touchOnProbe
				? () => {
						host.records = host.records.map((r) => (r.id === t.id ? { ...r, lastActiveAt: Date.now() } : r))
					}
				: undefined,
		})
	}
	return host
}

function wakeScroll(sleeper: TabSleeper, id: string): number | null {
	const scripts: string[] = []
	const loaded: (() => void)[] = []
	const contents = {
		isDestroyed: () => false,
		once: (_event: string, handler: () => void) => loaded.push(handler),
		executeJavaScript: (script: string) => {
			scripts.push(script)
			return Promise.resolve()
		},
	} as unknown as SleepablePage
	sleeper.restoreOnWake(id, contents)
	for (const handler of loaded) handler()
	if (scripts.length === 0) return null
	return Number(/scrollTo\(0, ([\d.]+)\)/.exec(scripts[0] ?? '')![1])
}

beforeEach(() => {
	vi.useFakeTimers()
	vi.setSystemTime(Date.parse(raw.scenarioStart))
})
afterEach(() => vi.useRealTimers())

describe('TabSleeper', () => {
	it.each(scenarios)('$name', async (scenario) => {
		const host = build(scenario)
		const warnings = vi.spyOn(console, 'warn').mockImplementation(() => undefined)
		const sleeper = new TabSleeper(host, () => settings, clock)
		host.sleeper = sleeper
		sleeper.reschedule()

		for (const step of scenario.steps) {
			if ('advanceMinutes' in step) await vi.advanceTimersByTimeAsync(step.advanceMinutes * MIN)
			else if ('advanceMs' in step) await vi.advanceTimersByTimeAsync(step.advanceMs)
			else if ('reschedule' in step) for (let i = 0; i < step.reschedule; i++) sleeper.reschedule()
			else if ('setAudible' in step) host.pages.get(step.setAudible)!.audible = step.value
			else if ('setDirty' in step) host.pages.get(step.setDirty)!.dirty = step.value
			else if ('setProbeFails' in step) host.pages.get(step.setProbeFails)!.probeFails = step.value
			else if ('expectSlept' in step) expect(host.slept).toEqual(step.expectSlept)
			else if ('expectErrors' in step) expect(warnings).toHaveBeenCalledTimes(step.expectErrors)
			else if ('expectWakeScroll' in step) expect(wakeScroll(sleeper, step.expectWakeScroll)).toBe(step.value)
			else expect(vi.getTimerCount()).toBe(step.expectTimers)
		}
		warnings.mockRestore()
	})
})
