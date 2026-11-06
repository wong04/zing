import type { PickedFile } from '../../../shared/engines/engineJob'

/** The drag type of a file one tool offers another. It carries the file's token, which only this window's host can turn into a path. */
export const TOOL_FILE_TYPE = 'application/x-zing-tool-file'

interface OutgoingTransfer {
	effectAllowed: string
	setData(type: string, value: string): void
}

interface IncomingTransfer {
	getData(type: string): string
}

// The file being dragged right now. A page can offer any drag type, so a drop is believed only when it names this file;
// a token from anywhere else is ignored.
let inFlight: PickedFile | undefined

export function beginToolDrag(transfer: OutgoingTransfer, file: PickedFile): void {
	inFlight = file
	transfer.effectAllowed = 'copy'
	transfer.setData(TOOL_FILE_TYPE, file.token)
}

export function endToolDrag(): void {
	inFlight = undefined
}

/** Whether a drag over a drop zone is one of our files. The data itself can't be read until the drop, so this goes by the types. */
export function isToolDrag(types: readonly string[]): boolean {
	return inFlight !== undefined && types.includes(TOOL_FILE_TYPE)
}

/** The file dropped, or undefined when the drop isn't the one being dragged. */
export function droppedToolFile(transfer: IncomingTransfer): PickedFile | undefined {
	if (inFlight === undefined || transfer.getData(TOOL_FILE_TYPE) !== inFlight.token) return undefined
	return inFlight
}
