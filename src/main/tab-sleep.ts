import type { WebContents } from 'electron'
import {
	keepAwakeReason,
	nextSleepCheck,
	tabsToSleep,
	type SleepCandidate,
	type SleepSettings,
} from '@shared/layout/sleep'
import type { TabId, TabRecord } from '@shared/model'
import type { Clock } from '@shared/session/clock'
import { OneShotTimer } from '@shared/session/oneShotTimer'
import { PAGE_PROBE_SOURCE, parsePageProbe } from '@shared/session/pageProbe'

export type SleepablePage = Pick<
	WebContents,
	'isCurrentlyAudible' | 'executeJavaScript' | 'capturePage' | 'once' | 'isDestroyed'
>

/** What the sleeper needs from the tab service. */
export interface SleepHost {
	readonly activeId: TabId | undefined
	readonly records: readonly TabRecord[]
	hasView(id: TabId): boolean
	contentsFor(id: TabId): SleepablePage | undefined
	sleep(id: TabId): void
}

/** Audio and unsaved input have no event that says "now safe", so blocked tabs are looked at again after this. */
export const SLEEP_RETRY_MS = 5 * 60_000
const CAPTURE_TIMEOUT_MS = 2_000
const THUMBNAIL_WIDTH = 160
const MAX_THUMBNAILS = 64

function withTimeout<T>(promise: Promise<T>, ms: number): Promise<T | undefined> {
	return new Promise((resolve, reject) => {
		const timer = setTimeout(() => resolve(undefined), ms)
		promise.then(
			(value) => {
				clearTimeout(timer)
				resolve(value)
			},
			(error: unknown) => {
				clearTimeout(timer)
				reject(error)
			},
		)
	})
}

/**
 * Sleeps tabs that have sat idle: schedules one timer for the soonest moment a
 * tab could qualify, then drops that tab's renderer while the record, scroll
 * position and a small thumbnail stay behind.
 */
export class TabSleeper {
	readonly #host: SleepHost
	readonly #settings: () => SleepSettings
	readonly #clock: Clock
	readonly #timer: OneShotTimer
	readonly #scroll = new Map<TabId, number>()
	readonly #thumbnails = new Map<TabId, string>()
	/** Tabs held awake last sweep by unsaved input or a page that couldn't be inspected. */
	readonly #held = new Set<TabId>()
	#retryAt: number | undefined
	#sweeping = false

	constructor(host: SleepHost, settings: () => SleepSettings, clock: Clock) {
		this.#host = host
		this.#settings = settings
		this.#clock = clock
		this.#timer = new OneShotTimer(clock, () => this.#onTimer())
	}

	thumbnail(id: TabId): string | undefined {
		return this.#thumbnails.get(id)
	}

	/** Recomputes the single wake-up. Cheap enough to call on every tab change. */
	reschedule(): void {
		this.#prune()
		if (this.#sweeping) return
		this.#timer.armAt(this.#nextTarget())
	}

	/** After the clock jumps: a target computed against the old time is stale. */
	rearm(): void {
		this.#timer.cancel()
		this.reschedule()
	}

	cancel(): void {
		this.#timer.cancel()
	}

	/** A sleeping tab has a fresh page; put its scroll position back once it has loaded. */
	restoreOnWake(id: TabId, contents: SleepablePage): void {
		const scrollY = this.#scroll.get(id)
		this.#scroll.delete(id)
		this.#thumbnails.delete(id)
		if (!scrollY) return
		contents.once('did-finish-load', () => {
			if (contents.isDestroyed()) return
			contents.executeJavaScript(`window.scrollTo(0, ${scrollY})`).catch(reportError)
		})
	}

	#onTimer(): void {
		this.#sweep().catch(reportError)
	}

	async #sweep(): Promise<void> {
		this.#sweeping = true
		try {
			this.#retryAt = undefined
			this.#held.clear()
			const ids = tabsToSleep(this.#candidates(), this.#clock.now(), this.#settings())
			for (const id of ids) {
				await this.#trySleep(id)
				// Anything still awake was spared for a reason; not re-sweeping it right away avoids a spin.
				if (this.#host.hasView(id)) this.#held.add(id)
			}
		} finally {
			this.#sweeping = false
			this.reschedule()
		}
	}

	async #trySleep(id: TabId): Promise<void> {
		const before = this.#find(id)
		const page = this.#host.contentsFor(id)
		if (!before || !page) return
		let raw: unknown
		try {
			raw = await page.executeJavaScript(PAGE_PROBE_SOURCE)
		} catch (error) {
			// The page navigated or crashed mid-probe; its state is unknown, so it stays awake.
			console.warn(`Not sleeping tab ${id}: ${(error as Error).message}`)
			this.#held.add(id)
			return
		}
		const probe = parsePageProbe(raw)
		if (probe.dirty) {
			this.#held.add(id)
			return
		}
		const thumbnail = await this.#capture(page)
		// Awaits above let the user come back to the tab, or the page die.
		const after = this.#find(id)
		if (!after || after.lastActiveAt !== before.lastActiveAt) return
		if (id === this.#host.activeId || !this.#host.hasView(id) || page.isDestroyed() || page.isCurrentlyAudible()) return
		this.#scroll.set(id, probe.scrollY)
		if (thumbnail) this.#remember(id, thumbnail)
		this.#host.sleep(id)
	}

	/** The thumbnail is garnish: a page that can't be captured in time still sleeps without one. */
	async #capture(page: SleepablePage): Promise<string | undefined> {
		const image = await withTimeout(page.capturePage(undefined, { stayHidden: true }), CAPTURE_TIMEOUT_MS)
		if (!image || image.isEmpty()) return undefined
		const jpeg = image.resize({ width: THUMBNAIL_WIDTH }).toJPEG(60)
		return `data:image/jpeg;base64,${jpeg.toString('base64')}`
	}

	#remember(id: TabId, thumbnail: string): void {
		this.#thumbnails.delete(id)
		this.#thumbnails.set(id, thumbnail)
		const oldest = this.#thumbnails.keys().next().value
		if (this.#thumbnails.size > MAX_THUMBNAILS && oldest !== undefined) this.#thumbnails.delete(oldest)
	}

	#find(id: TabId): TabRecord | undefined {
		return this.#host.records.find((t) => t.id === id)
	}

	/** Only tabs with a renderer: one without is already asleep, or was never loaded. */
	#candidates(): SleepCandidate[] {
		return this.#host.records.flatMap((t) => {
			const page = this.#host.contentsFor(t.id)
			if (!page || page.isDestroyed()) return []
			return [
				{
					id: t.id,
					lastActiveAt: t.lastActiveAt,
					pinned: t.pinned,
					isActive: t.id === this.#host.activeId,
					// No split view yet; its owner flips this when splits land.
					visibleInSplit: false,
					playingAudio: page.isCurrentlyAudible(),
					hasUnsavedForm: false,
					asleep: false,
				},
			]
		})
	}

	#nextTarget(): number | undefined {
		const settings = this.#settings()
		const now = this.#clock.now()
		const candidates = this.#candidates()
		const blocked =
			this.#held.size > 0 || candidates.some((c) => keepAwakeReason(c, now, settings) === 'playing-audio')
		if (!blocked) this.#retryAt = undefined
		else this.#retryAt ??= now + SLEEP_RETRY_MS
		// nextSleepCheck only looks ahead; a tab already past its idle time (the clock jumped, or the
		// machine slept) is due now.
		const due = tabsToSleep(candidates, now, settings).some((id) => !this.#held.has(id))
		const targets = [due ? now : undefined, nextSleepCheck(candidates, now, settings), this.#retryAt].filter(
			(t) => t !== undefined,
		)
		return targets.length === 0 ? undefined : Math.min(...targets)
	}

	#prune(): void {
		if (this.#scroll.size === 0 && this.#thumbnails.size === 0 && this.#held.size === 0) return
		const open = new Set(this.#host.records.map((t) => t.id))
		for (const map of [this.#scroll, this.#thumbnails]) {
			for (const id of map.keys()) if (!open.has(id)) map.delete(id)
		}
		for (const id of this.#held) if (!open.has(id)) this.#held.delete(id)
	}
}

function reportError(error: unknown): void {
	console.error('Tab sleep failed:', error)
}
