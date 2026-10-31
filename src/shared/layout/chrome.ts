import { CHROME_LAYOUT } from '../ipc'

export type TabBarPosition = 'top' | 'left' | 'right'

/** Default and limits for the vertical tab rail, in DIPs. Multiples of 4 keep the page edge on a device pixel. */
export const VERTICAL_TABS = { defaultWidth: 240, minWidth: 160, maxWidth: 480, collapsedWidth: 48 } as const

export interface ChromeInsets {
	readonly top: number
	readonly left: number
	readonly right: number
}

export interface ChromeShape {
	readonly position: TabBarPosition
	readonly favouritesBar: boolean
	/** The vertical rail's current width (collapsed or not); ignored when tabs are on top. */
	readonly railWidth: number
	/** Anything else docked on the left: the pinned app sidebar plus the bookmarks panel. */
	readonly leftDock: number
}

// With tabs on the side the tab strip row is gone: the toolbar becomes the
// top band and the rail takes a column on its side. The app sidebar folds
// into a left rail, so leftDock only applies when the rail isn't there.
export function chromeInsets(shape: ChromeShape): ChromeInsets {
	const bar = shape.favouritesBar ? CHROME_LAYOUT.favouritesBarHeight : 0
	if (shape.position === 'top') {
		return { top: CHROME_LAYOUT.tabStripHeight + CHROME_LAYOUT.toolbarHeight + bar, left: shape.leftDock, right: 0 }
	}
	const top = CHROME_LAYOUT.toolbarHeight + bar
	if (shape.position === 'left') return { top, left: shape.railWidth + shape.leftDock, right: 0 }
	return { top, left: shape.leftDock, right: shape.railWidth }
}

export function clampRailWidth(width: number): number {
	const snapped = Math.round(width / 4) * 4
	return Math.min(VERTICAL_TABS.maxWidth, Math.max(VERTICAL_TABS.minWidth, snapped))
}
