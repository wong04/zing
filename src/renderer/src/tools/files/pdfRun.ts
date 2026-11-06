// Building and saving several files in turn (a split, or one save) with a Cancel that stops the next one from starting.

export interface RunOutcome {
	/** The names the host gave the files that were saved. */
	readonly saved: string[]
	readonly total: number
	readonly stopped: boolean
}

/**
 * Builds and saves parts 0 to count-1 in order. `isCancelled` is looked at before each part and after a failure: a part that
 * was already being saved when Cancel was pressed finishes and is listed, and nothing after it starts.
 * @throws the failure of a part, with `saved` set to what was saved before it, unless Cancel caused it.
 */
export async function runParts(count: number, build: (index: number) => Promise<Uint8Array>, save: (index: number, bytes: Uint8Array) => Promise<string>, isCancelled: () => boolean): Promise<RunOutcome> {
	const saved: string[] = []
	for (let index = 0; index < count; index++) {
		if (isCancelled()) return { saved, total: count, stopped: true }
		try {
			saved.push(await save(index, await build(index)))
		} catch (error) {
			if (isCancelled()) return { saved, total: count, stopped: true }
			throw Object.assign(error instanceof Error ? error : new Error(String(error)), { saved })
		}
	}
	return { saved, total: count, stopped: false }
}

/** The words for a run that was stopped; null when it ran to the end. */
export function describeRun(outcome: RunOutcome): string | null {
	if (!outcome.stopped) return null
	const { saved, total } = outcome
	if (saved.length === 0) return 'Stopped. Nothing was saved.'
	if (total === 1) return 'Stopped. The file had already been saved to your Downloads folder.'
	return `Stopped. ${saved.length} of ${total} files were saved to your Downloads folder; the other ${total - saved.length} were not made.`
}
