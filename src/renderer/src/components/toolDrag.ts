import type { PickedFile } from '../../../shared/engines/engineJob'

/** The drag type of a file one tool offers another. Its value is fixed: the file itself is held only in this module. */
export const TOOL_FILE_TYPE = 'application/x-zing-tool-file'

interface OutgoingTransfer {
	effectAllowed: string
	setData(type: string, value: string): void
}

// The file being dragged right now. The token never goes into the drag data, where a page or another app the row is
// dragged over could read it; a drop is matched by type and by this slot, so a drag from anywhere else gets nothing.
let inFlight: PickedFile | undefined

export function beginToolDrag(transfer: OutgoingTransfer, file: PickedFile): void {
	inFlight = file
	transfer.effectAllowed = 'copy'
	transfer.setData(TOOL_FILE_TYPE, 'tool-file')
}

/** Ends the drag; given a file, only if that is the one being dragged (its row went away). */
export function endToolDrag(file?: PickedFile): void {
	if (file !== undefined && inFlight?.token !== file.token) return
	inFlight = undefined
}

/** Clears the slot when a drag ends or drops anywhere in `target`, for a source row that is gone and can't say so. */
export function watchDragEnd(target: EventTarget): () => void {
	const end = (): void => endToolDrag()
	target.addEventListener('dragend', end)
	target.addEventListener('drop', end)
	return () => {
		target.removeEventListener('dragend', end)
		target.removeEventListener('drop', end)
	}
}

if (typeof window !== 'undefined') watchDragEnd(window)

/** Whether a drag over a drop zone is one of our files. */
export function isToolDrag(types: readonly string[]): boolean {
	return inFlight !== undefined && types.includes(TOOL_FILE_TYPE)
}

/** The file dropped, or undefined when the drop isn't one of ours. */
export function droppedToolFile(types: readonly string[]): PickedFile | undefined {
	return isToolDrag(types) ? inFlight : undefined
}
