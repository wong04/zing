import type { ReactNode } from 'react'
import { panelContentFromId, panelTitle } from '@shared/toolbox/panelContent'
import type { Point, ToolPanelView } from '@shared/shell/shellState'
import { zing } from '../api'
import { GearIcon, GlobeIcon, GridIcon, SidebarIcon, WindowIcon } from '../components/icons'
import { useSettings } from '../settings/useSettings'
import { useToolPanels } from '../panels/hooks'
import { PanelIcon } from '../panels/panelIcon'
import { useTearOff } from '../panels/useTearOff'
import './sidebar.css'

interface RowProps {
	readonly icon: ReactNode
	readonly title: string
	readonly selected?: boolean
	readonly trailing?: ReactNode
	readonly onClick: () => void
}

function Row({ icon, title, selected = false, trailing, onClick }: RowProps) {
	return (
		<button type="button" className={selected ? 'side-row selected' : 'side-row'} aria-current={selected ? 'page' : undefined} onClick={onClick}>
			<span className="side-row-icon">{icon}</span>
			<span className="side-row-title">{title}</span>
			{trailing}
		</button>
	)
}

/** A recent tool: click opens it, hold and release elsewhere tears it off into a floating panel. */
function RecentRow({ content }: { content: string }) {
	const parsed = panelContentFromId(content)
	const title = parsed ? panelTitle(parsed) : content
	const open = (at: Point): void => void zing.openPanel(content, at)
	const { armed, handlers } = useTearOff(
		() => open({ x: window.screenX + 560, y: window.screenY + 160 }),
		open,
		(anchor) => void zing.openShellMenu('recent', content, anchor),
	)
	return (
		<div
			role="button"
			tabIndex={0}
			className={armed ? 'side-row armed' : 'side-row'}
			title={`${title} (hold and drag to open in a window)`}
			data-testid="recent-row"
			{...handlers}
		>
			<span className="side-row-icon">
				<PanelIcon content={content} />
			</span>
			<span className="side-row-title">{title}</span>
		</div>
	)
}

function MinimizedRow({ panel }: { panel: ToolPanelView }) {
	return (
		<button
			type="button"
			className="side-row"
			title={`Restore ${panel.title}`}
			data-testid="minimized-row"
			onClick={() => void zing.restorePanel(panel.id)}
			onContextMenu={(e) => {
				e.preventDefault()
				void zing.openShellMenu('minimized', panel.id, { x: e.clientX, y: e.clientY })
			}}
		>
			<span className="side-row-icon">
				<PanelIcon content={panel.content} />
			</span>
			<span className="side-row-title">{panel.title}</span>
			<span className="side-row-trailing">
				<WindowIcon size={12} />
			</span>
		</button>
	)
}

export type SidebarVariant = 'pinned' | 'floating'

/**
 * The app sidebar (Krackn Sidebar): Browser, Toolbox, the three most recent
 * tools, minimized panels, and Settings at the foot. Pinned it sits in the
 * chrome and takes layout space; unpinned it is drawn over the page by the
 * surface view when the cursor reaches the left edge.
 */
export function Sidebar({ variant }: { variant: SidebarVariant }) {
	const handle = useSettings()
	const panels = useToolPanels()
	const pinned = handle?.settings.sidebarPinned ?? true
	const recents = handle?.settings.recentFeatures ?? []
	const minimized = panels.panels.filter((p) => p.minimized)
	const label = pinned ? 'Hide sidebar (Ctrl+Shift+S)' : 'Keep sidebar open (Ctrl+Shift+S)'

	return (
		<nav className={`sidebar ${variant}`} aria-label="App sidebar">
			<div className="sidebar-header">
				<div className="sidebar-drag" />
				<button type="button" className="side-icon-button" aria-label={label} title={label} aria-pressed={pinned} onClick={() => handle?.set('sidebarPinned', !pinned)}>
					<SidebarIcon size={15} />
				</button>
			</div>
			<div className="sidebar-brand">
				<span className="sidebar-mark" aria-hidden="true">
					Z
				</span>
				<span>Zing</span>
			</div>
			<div className="sidebar-scroll">
				<Row icon={<GlobeIcon size={15} />} title="Browser" selected onClick={() => undefined} />
				<Row icon={<GridIcon size={15} />} title="Toolbox" onClick={() => void zing.openToolbox()} />
				{recents.length > 0 ? (
					<>
						<h3 className="side-heading">Recent</h3>
						{recents.map((id) => (
							<RecentRow key={id} content={id} />
						))}
					</>
				) : null}
				{minimized.length > 0 ? (
					<>
						<h3 className="side-heading">Minimized</h3>
						{minimized.map((p) => (
							<MinimizedRow key={p.id} panel={p} />
						))}
					</>
				) : null}
			</div>
			<div className="sidebar-foot">
				<Row icon={<GearIcon size={15} />} title="Settings" onClick={() => void zing.openSettings()} />
			</div>
		</nav>
	)
}
