/** Nothing has come for the whole limit: the thing being waited for is not going to arrive. */
export class StallError extends Error {
	override name = 'StallError'
}

/** Counts how long a wait has gone without progress, so a poll that never gets an answer ends with an error. */
export class Stall {
	private last: number

	constructor(
		private readonly limitMs: number,
		private readonly now: () => number = () => Date.now(),
	) {
		this.last = now()
	}

	progress(): void {
		this.last = this.now()
	}

	check(): void {
		if (this.now() - this.last > this.limitMs) throw new StallError(`The conversion stopped making progress: nothing new for ${Math.round(this.limitMs / 1000)} seconds`)
	}
}
