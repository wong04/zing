import type { CSSProperties } from 'react'
import { CHROME_LAYOUT } from '@shared/ipc'
import { TabStrip } from './chrome/TabStrip'
import { Toolbar } from './chrome/Toolbar'
import { useTabsState } from './chrome/useTabsState'

// Heights come from the shared layout contract so main places the page view exactly under them.
const layout = {
	'--tabstrip-height': `${CHROME_LAYOUT.tabStripHeight}px`,
	'--toolbar-height': `${CHROME_LAYOUT.toolbarHeight}px`,
} as CSSProperties

/** The window chrome above the page. The page itself is a separate view main lays out below this. */
export function App() {
	const state = useTabsState()
	const active = state?.tabs.find((t) => t.id === state.activeId)
	return (
		<div className="chrome" style={layout}>
			<TabStrip tabs={state?.tabs ?? []} activeId={state?.activeId} />
			<Toolbar tab={active} />
		</div>
	)
}
