import type { CSSProperties } from 'react'
import { CHROME_LAYOUT } from '@shared/ipc'
import { captionSurface } from '@shared/theme/accentVariables'
import { SplitDividers } from './chrome/SplitDividers'
import { TabStrip } from './chrome/TabStrip'
import { LibraryChrome } from './library/LibraryChrome'
import { Toolbar } from './chrome/Toolbar'
import { useTabsState } from './chrome/useTabsState'
import { TabRail } from './rail/TabRail'
import { useTitleBarColors } from './chrome/titlebar'
import { useAccentTheme } from './settings/useAccentTheme'
import { useSettings } from './settings/useSettings'
import { Sidebar } from './sidebar/Sidebar'

// Heights come from the shared layout contract so main places the page view exactly under them.
const layout = {
	'--tabstrip-height': `${CHROME_LAYOUT.tabStripHeight}px`,
	'--toolbar-height': `${CHROME_LAYOUT.toolbarHeight}px`,
} as CSSProperties

/**
 * The window chrome around the page. The page itself is a separate view main lays out beside this. Tabs on
 * top: the app sidebar, then the tab strip over the toolbar. Tabs on the left: the rail, which is also the
 * app sidebar, then the toolbar as the top band. Tabs on the right: the app sidebar, the toolbar, the rail.
 */
export function App() {
	const state = useTabsState()
	const active = state?.tabs.find((t) => t.id === state.activeId)
	const settings = useSettings()?.settings
	const sidebarPinned = settings?.sidebarPinned ?? true
	const position = settings?.tabBarPosition ?? 'left'
	useAccentTheme()
	useTitleBarColors(captionSurface(position, settings?.verticalTabsCollapsed ?? false), settings?.accent, settings?.customColor)
	const shownSplit = state?.shownSplit
	const split = state?.splits?.find((s) => s.id === shownSplit?.id)
	const appSidebar =
		sidebarPinned && position !== 'left' ? (
			<div className="sidebar-slot">
				<Sidebar variant="pinned" />
			</div>
		) : null
	return (
		<div className="shell" style={layout}>
			{position === 'left' && sidebarPinned ? (
				<div className="rail-slot">
					<TabRail state={state} variant="pinned" side="left" />
				</div>
			) : (
				appSidebar
			)}
			<div className={position === 'top' ? 'chrome' : 'chrome side'} style={layout}>
				{position === 'top' ? <TabStrip state={state} /> : null}
				<Toolbar tab={active} />
				<LibraryChrome position={position} />
			</div>
			{position === 'right' ? (
				<div className="rail-slot">
					<TabRail state={state} variant="pinned" side="right" />
				</div>
			) : null}
			{split && shownSplit ? <SplitDividers split={split} shown={shownSplit} /> : null}
		</div>
	)
}
