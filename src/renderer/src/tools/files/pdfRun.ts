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

/**
 * Opens the files in order. `isCancelled` is looked at before each file and after a failure, so nothing after a Cancel starts
 * and a file whose opening Cancel broke off is not counted.
 * @throws the failure of a file, unless Cancel caused it.
 */
export async function openInTurn<T>(files: readonly T[], open: (file: T) => Promise<void>, isCancelled: () => boolean): Promise<{ added: number; stopped: boolean }> {
	let added = 0
	for (const file of files) {
		if (isCancelled()) return { added, stopped: true }
		try {
			await open(file)
		} catch (error) {
			if (isCancelled()) return { added, stopped: true }
			throw error
		}
		added++
	}
	return { added, stopped: false }
}

/** The words after Cancel while files were opening. */
export function describeStoppedOpen(added: number): string {
	if (added === 0) return 'Stopped. No file was added.'
	return `Stopped. ${added} ${added === 1 ? 'file was' : 'files were'} added.`
}

/** The busy label while files are being opened, as opposed to a save or split. */
export const OPENING = 'Opening…'

/** Why a file can't be opened or added right now, or null when it can: doing it would end the session the run is saving into. */
export function whyNotNow(busy: string | null): string | null {
	if (busy === null) return null
	if (busy === OPENING) return 'A file is still opening. Wait for it to finish, then try again.'
	return `A save is running (${busy}). Wait for it to finish, or press Cancel, before opening or adding a file.`
}

/** A refused drop or hand-off has already been taken from where it came, so the person is told by name that it was not added. */
export function notAdded(names: readonly string[], reason: string): string {
	const list = new Intl.ListFormat('en', { type: 'conjunction' }).format(names)
	return `${list} ${names.length === 1 ? 'was' : 'were'} not added. ${reason}`
}

/** The banner after Cancel on Convert's PDF to pictures. */
export function describeCancelledPages(kept: number | undefined): string {
	if (kept === undefined || kept === 0) return 'Cancelled. Nothing was kept.'
	return `Cancelled. The ${kept} ${kept === 1 ? 'page already made is' : 'pages already made are'} in your Downloads folder.`
}
