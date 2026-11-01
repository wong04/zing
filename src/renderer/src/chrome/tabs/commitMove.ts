import type { Slot } from '@shared/tabs/slots'
import { zing } from '../../api'

/** Sends a dropped slot's new place to main: pinned tabs count among the pinned, the rest after them; a stack moves as one. */
export async function commitMove(slot: Slot, to: number, pinnedCount: number, dropdowns: boolean): Promise<void> {
	if (slot.kind === 'stack') return zing.moveStack(slot.key, to)
	if (slot.pinned) return zing.moveTab(slot.id, to)
	if (dropdowns) return zing.moveStack(slot.key, to)
	return zing.moveTab(slot.id, pinnedCount + to)
}
