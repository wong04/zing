import { makeTabSnapshot, type TabSnapshot } from '@shared/browser/tabOrganizer'
import {
	localCalendar,
	scheduleElapsed,
	tabsToClose,
	tabsToCloseOnSchedule,
	type AutoClosePolicy,
	type DayCalendar,
} from '@shared/browser/tabLifecycle'
import type { TabId, TabRecord } from '@shared/model'
import { nextAutoCloseCheck } from '@shared/session/autoCloseSchedule'
import { OffsetClock, systemClock, type Clock } from '@shared/session/clock'
import { OneShotTimer } from '@shared/session/oneShotTimer'
import type { SettingsStore } from '@shared/settings'
import { TabSleeper, type SleepHost, type SleepablePage } from './tab-sleep'

/** What auto-close needs from the tab service. */
export interface AutoCloseHost {
	readonly activeId: TabId | undefined
	readonly records: readonly TabRecord[]
	close(id: TabId): void
}

type LifecycleSettings = Pick<SettingsStore, 'get' | 'subscribe'>

export function autoClosePolicy(settings: LifecycleSettings): AutoClosePolicy {
	return {
		enabled: settings.get('autoCloseEnabled'),
		rule: settings.get('autoCloseRule'),
		customHours: settings.get('autoCloseCustomHours'),
		atTimeEnabled: settings.get('autoCloseAtTimeEnabled'),
		hour: settings.get('autoCloseHour'),
		minute: settings.get('autoCloseMinute'),
	}
}

/**
 * Closes idle tabs and sweeps at a fixed time of day, with one timer aimed at
 * the next moment either could matter. Spared tabs are the policy's business
 * (`tabsToClose`): pinned, active, and the last one standing.
 */
export class TabAutoCloser {
	readonly #host: AutoCloseHost
	readonly #policy: () => AutoClosePolicy
	readonly #clock: Clock
	readonly #calendar: DayCalendar
	readonly #timer: OneShotTimer
	/** Start of the window `scheduleElapsed` asks about; only meaningful while the daily sweep is on. */
	#lastSweep: number

	constructor(
		host: AutoCloseHost,
		policy: () => AutoClosePolicy,
		clock: Clock,
		calendar: DayCalendar = localCalendar,
	) {
		this.#host = host
		this.#policy = policy
		this.#clock = clock
		this.#calendar = calendar
		this.#lastSweep = clock.now()
		this.#timer = new OneShotTimer(clock, () => this.#sweep())
	}

	reschedule(): void {
		const policy = this.#policy()
		const now = this.#clock.now()
		// Turning the daily sweep on must not fire for a time that passed before it was asked for.
		if (!policy.enabled || !policy.atTimeEnabled) this.#lastSweep = now
		this.#timer.armAt(nextAutoCloseCheck(this.#snapshots(), policy, now, this.#calendar))
	}

	rearm(): void {
		this.#timer.cancel()
		this.reschedule()
	}

	cancel(): void {
		this.#timer.cancel()
	}

	#sweep(): void {
		const policy = this.#policy()
		const now = this.#clock.now()
		if (!policy.enabled) return
		const daily = scheduleElapsed(policy, this.#lastSweep, now, this.#calendar)
		this.#lastSweep = now
		this.#closeAll(tabsToClose(this.#snapshots(), policy, now))
		if (daily) this.#closeAll(tabsToCloseOnSchedule(this.#snapshots()))
		this.reschedule()
	}

	#closeAll(ids: readonly TabId[]): void {
		for (const id of ids) this.#host.close(id)
	}

	#snapshots(): TabSnapshot[] {
		return this.#host.records.map((t) =>
			makeTabSnapshot({
				id: t.id,
				title: t.title,
				domain: '',
				lastAccessed: t.lastActiveAt,
				createdAt: t.createdAt,
				isPinned: t.pinned,
				isActive: t.id === this.#host.activeId,
			}),
		)
	}
}

export interface TabLifecycleOptions {
	readonly tabs: AutoCloseHost & SleepHost
	readonly settings: LifecycleSettings
	readonly clock: Clock
}

const WATCHED_SETTINGS: ReadonlySet<string> = new Set([
	'autoCloseEnabled',
	'autoCloseRule',
	'autoCloseCustomHours',
	'autoCloseAtTimeEnabled',
	'autoCloseHour',
	'autoCloseMinute',
	'sleepingTabs',
])

/** A window's auto-close and sleeping-tab timers, kept in step with its tabs and settings. */
export class TabLifecycle {
	readonly #autoCloser: TabAutoCloser
	readonly #sleeper: TabSleeper
	readonly #disposers: (() => void)[]

	constructor({ tabs, settings, clock }: TabLifecycleOptions) {
		this.#autoCloser = new TabAutoCloser(tabs, () => autoClosePolicy(settings), clock)
		this.#sleeper = new TabSleeper(tabs, () => settings.get('sleepingTabs'), clock)
		this.#disposers = [
			settings.subscribe((_, key) => {
				if (WATCHED_SETTINGS.has(key)) this.reschedule()
			}),
			clock.onAdvance(() => {
				this.#autoCloser.rearm()
				this.#sleeper.rearm()
			}),
		]
		this.reschedule()
	}

	thumbnail(id: TabId): string | undefined {
		return this.#sleeper.thumbnail(id)
	}

	reschedule(): void {
		this.#autoCloser.reschedule()
		this.#sleeper.reschedule()
	}

	woke(id: TabId, contents: SleepablePage): void {
		this.#sleeper.restoreOnWake(id, contents)
	}

	dispose(): void {
		for (const dispose of this.#disposers) dispose()
		this.#autoCloser.cancel()
		this.#sleeper.cancel()
	}
}

export interface ZingTestControls {
	advanceClock(ms: number): void
}

/**
 * Real time, except under ZING_TEST_CLOCK=1, where e2e tests move it with
 * `globalThis.__zingTest.advanceClock(ms)` instead of waiting out hours.
 */
export function createClock(): Clock {
	if (process.env.ZING_TEST_CLOCK !== '1') return systemClock
	const clock = new OffsetClock()
	const controls: ZingTestControls = { advanceClock: (ms) => clock.advance(ms) }
	;(globalThis as { __zingTest?: ZingTestControls }).__zingTest = controls
	return clock
}

let shared: Clock | undefined

/** One clock for the whole app, so a test that advances time moves every window together. */
export function appClock(): Clock {
	shared ??= createClock()
	return shared
}
