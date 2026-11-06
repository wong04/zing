import type { PickedFile } from '@shared/engines/engineJob'

// Convert's "Open in PDF Tools" passes a PDF it made to the PDF panel. Both live in the one interface, so this is a plain
// hand-over in memory: the token is the same grant either would use, and nothing is stored.
let waiting: PickedFile | undefined
const listeners = new Set<(file: PickedFile) => void>()

/** Gives the file to a PDF panel that is open, or keeps it for the one about to open. */
export function handOffPdf(file: PickedFile): void {
	if (listeners.size === 0) {
		waiting = file
		return
	}
	for (const listener of listeners) listener(file)
}

/** A file waiting for a panel that has not opened (and is not coming, if the panel could not open). */
export function takeHandedPdf(): PickedFile | undefined {
	const file = waiting
	waiting = undefined
	return file
}

export function onHandedPdf(listener: (file: PickedFile) => void): () => void {
	listeners.add(listener)
	return () => listeners.delete(listener)
}
