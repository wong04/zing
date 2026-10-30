import type { CSSProperties } from 'react'
import { CHROME_LAYOUT } from '@shared/ipc'
import { TabStrip } from './chrome/TabStrip'
import { LibraryChrome } from './library/LibraryChrome'
import { Toolbar } from './chrome/Toolbar'
import { useTabsState } from './chrome/useTabsState'
import { useAccentTheme } from './settings/useAccentTheme'
import { useSettings } from './settings/useSettings'
import { Sidebar } from './sidebar/Sidebar'

// Heights come from the shared layout contract so main places the page view exactly under them.
const layout = {
	'--tabstrip-height': `${CHROME_LAYOUT.tabStripHeight}px`,
	'--toolbar-height': `${CHROME_LAYOUT.toolbarHeight}px`,
} as CSSProperties

/** The window chrome above the page. The page itself is a separate view main lays out below this. */
export function App() {
	const state = useTabsState()
	const active = state?.tabs.find((t) => t.id === state.activeId)
	const pinned = useSettings()?.settings.sidebarPinned ?? true
	useAccentTheme()
	return (
		<div className="shell">
			{pinned ? (
				<div className="sidebar-slot">
					<Sidebar variant="pinned" />
				</div>
			) : null}
			<div className="chrome" style={layout}>
				<TabStrip state={state} />
				<Toolbar tab={active} />
				<LibraryChrome />
			</div>
		</div>
	)
}
