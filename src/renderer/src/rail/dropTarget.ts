/**
 * Where something dragged in the rail would land. The rail's sections mark their places in the DOM:
 * - `data-drop-folder`: a folder of Favourites or Pinned (the tile grid, the Pinned section, a pinned folder); a drop goes at its end
 * - `data-drop-item` with `data-drop-parent`, `data-drop-index` and `data-drop-axis`: an item, a drop goes before or after it
 * - `data-drop="today"`: today's tabs, where a saved item becomes an ordinary tab again
 */
export type DropTarget = { readonly kind: 'folder'; readonly folder: string; readonly at: number } | { readonly kind: 'today' }

/** "At the end": the host clamps a place to the folder's children. */
export const AT_END = 2 ** 31 - 1

const MARK = 'drop-over'
let marked: Element | undefined

function targetElement(x: number, y: number, dragged: Element | null): HTMLElement | undefined {
	// The held element follows the pointer, so it is under it: look past it.
	const hit = document.elementsFromPoint(x, y).find((el) => !dragged?.contains(el))
	return hit?.closest<HTMLElement>('[data-drop-item], [data-drop-folder], [data-drop="today"]') ?? undefined
}

export function dropTargetAt(x: number, y: number, dragged: Element | null): DropTarget | undefined {
	const element = targetElement(x, y, dragged)
	if (!element) return undefined
	const { dropItem, dropParent, dropIndex, dropAxis, dropFolder } = element.dataset
	if (dropItem !== undefined && dropParent !== undefined && dropIndex !== undefined) {
		const rect = element.getBoundingClientRect()
		const after = dropAxis === 'x' ? x > rect.left + rect.width / 2 : y > rect.top + rect.height / 2
		return { kind: 'folder', folder: dropParent, at: Number(dropIndex) + (after ? 1 : 0) }
	}
	if (dropFolder !== undefined) return { kind: 'folder', folder: dropFolder, at: AT_END }
	return { kind: 'today' }
}

/** Highlights the place under the pointer while something is dragged. */
export function markDropTarget(x: number, y: number, dragged: Element | null): void {
	const element = targetElement(x, y, dragged)
	if (element === marked) return
	marked?.classList.remove(MARK)
	element?.classList.add(MARK)
	marked = element
}

export function clearDropMark(): void {
	marked?.classList.remove(MARK)
	marked = undefined
}
