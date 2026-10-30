/** Wall-clock time as a dependency, so lifecycle timers can be driven quickly in tests. */
export interface Clock {
	now(): number
	/** Fires after the clock jumps forward, so timers armed against the old time can be recomputed. */
	onAdvance(listener: () => void): () => void
}

export const systemClock: Clock = {
	now: () => Date.now(),
	onAdvance: () => () => undefined,
}

/** Real time plus a movable offset. Only ever constructed under the test env var. */
export class OffsetClock implements Clock {
	#offset = 0
	readonly #listeners = new Set<() => void>()

	now(): number {
		return Date.now() + this.#offset
	}

	advance(ms: number): void {
		this.#offset += ms
		for (const listener of this.#listeners) listener()
	}

	onAdvance(listener: () => void): () => void {
		this.#listeners.add(listener)
		return () => this.#listeners.delete(listener)
	}
}
