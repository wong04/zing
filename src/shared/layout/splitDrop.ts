import { MAX_PANES, type DropEdge, type Rect } from './split'

/** A page on screen a tab can be dropped beside: the tab it shows and where. */
export interface DropPane {
	readonly tabId: string
	readonly rect: Rect
	/** How many panes its split has already (1 for a page on its own). */
	readonly panes: number
}

export interface EdgeDrop {
	readonly tabId: string
	readonly edge: DropEdge
	/** Where the dropped tab would go: the half of the page on that side, highlighted while dragging. */
	readonly zone: Rect
}

/** How near an edge, as a share of the page, counts as aiming at it; the middle splits nothing. */
const EDGE_SHARE = 0.25

/**
 * Where a tab dragged over the pages would land in a split. Near a page's left or right edge it goes beside
 * the page; near its top or bottom, above or below. The middle, a full split, and the tab's own page are no target.
 */
export function edgeDropAt(x: number, y: number, panes: readonly DropPane[], draggedTabId: string): EdgeDrop | undefined {
	const pane = panes.find((p) => x >= p.rect.x && x < p.rect.x + p.rect.width && y >= p.rect.y && y < p.rect.y + p.rect.height)
	if (!pane || pane.tabId === draggedTabId || pane.panes >= MAX_PANES) return undefined
	const { rect } = pane
	const fx = (x - rect.x) / rect.width
	const fy = (y - rect.y) / rect.height
	// The nearest edge wins, so a corner picks the side the pointer is closer to.
	const distances: [DropEdge, number][] = [
		['left', fx],
		['right', 1 - fx],
		['top', fy],
		['bottom', 1 - fy],
	]
	const [edge, distance] = distances.reduce((best, next) => (next[1] < best[1] ? next : best))
	if (distance > EDGE_SHARE) return undefined
	const halfWidth = Math.round(rect.width / 2)
	const halfHeight = Math.round(rect.height / 2)
	const zone: Rect =
		edge === 'left'
			? { ...rect, width: halfWidth }
			: edge === 'right'
				? { ...rect, x: rect.x + rect.width - halfWidth, width: halfWidth }
				: edge === 'top'
					? { ...rect, height: halfHeight }
					: { ...rect, y: rect.y + rect.height - halfHeight, height: halfHeight }
	return { tabId: pane.tabId, edge, zone }
}
