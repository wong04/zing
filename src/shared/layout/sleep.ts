// Sleeping tabs: Electron has no built-in tab discarding, and memory is its
// weak spot. A sleeping tab keeps its record (URL, title, favicon, scroll,
// thumbnail) but loses its renderer until it's shown again.

export interface SleepCandidate {
	id: string
	lastActiveAt: number
	pinned: boolean
	isActive: boolean
	visibleInSplit: boolean
	playingAudio: boolean
	hasUnsavedForm: boolean
	asleep: boolean
}

export interface SleepSettings {
	enabled: boolean
	afterMinutes: number
	sleepPinned: boolean
}

export const DEFAULT_SLEEP_SETTINGS: SleepSettings = { enabled: true, afterMinutes: 30, sleepPinned: false }

export type KeepAwakeReason = 'disabled' | 'already-asleep' | 'active' | 'visible-in-split' | 'playing-audio' | 'unsaved-form' | 'pinned' | 'recently-used'

// Why a tab stays awake, or undefined if it may sleep. Returning the reason
// (not just a boolean) lets the tab's tooltip and the tests say *why*.
export function keepAwakeReason(tab: SleepCandidate, now: number, settings: SleepSettings): KeepAwakeReason | undefined {
	if (!settings.enabled) return 'disabled'
	if (tab.asleep) return 'already-asleep'
	if (tab.isActive) return 'active'
	if (tab.visibleInSplit) return 'visible-in-split'
	if (tab.playingAudio) return 'playing-audio'
	// Discarding would throw away what the user typed.
	if (tab.hasUnsavedForm) return 'unsaved-form'
	if (tab.pinned && !settings.sleepPinned) return 'pinned'
	if (now - tab.lastActiveAt < settings.afterMinutes * 60_000) return 'recently-used'
	return undefined
}

export function tabsToSleep(tabs: readonly SleepCandidate[], now: number, settings: SleepSettings): string[] {
	return tabs.filter((tab) => keepAwakeReason(tab, now, settings) === undefined).map((tab) => tab.id)
}

// The soonest moment any awake tab could become sleepable, so the main
// process schedules one timer instead of polling.
export function nextSleepCheck(tabs: readonly SleepCandidate[], now: number, settings: SleepSettings): number | undefined {
	if (!settings.enabled) return undefined
	const waiting = tabs.filter((tab) => keepAwakeReason(tab, now, settings) === 'recently-used')
	if (waiting.length === 0) return undefined
	return Math.min(...waiting.map((tab) => tab.lastActiveAt + settings.afterMinutes * 60_000))
}
