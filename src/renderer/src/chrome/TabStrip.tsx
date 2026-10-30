import type { KeyboardEvent, MouseEvent } from 'react'
import type { TabView } from '@shared/ipc'
import { zing } from '../api'
import { monogram, tabTitle } from './display'
import { CloseIcon, PlusIcon } from './icons'
import { commandTooltip } from './tooltip'

const MIDDLE_BUTTON = 1

function TabChip({ tab, active }: { tab: TabView; active: boolean }) {
	const activate = (): void => void zing.activateTab(tab.id)
	const close = (): void => void zing.closeTab(tab.id)

	const onMouseDown = (e: MouseEvent): void => {
		if (e.button === 0) activate()
	}
	const onAuxClick = (e: MouseEvent): void => {
		if (e.button === MIDDLE_BUTTON) close()
	}
	const onKeyDown = (e: KeyboardEvent): void => {
		if (e.key === 'Enter' || e.key === ' ') activate()
	}
	const onContextMenu = (e: MouseEvent): void => {
		e.preventDefault()
		void zing.openTabMenu(tab.id, { x: e.clientX, y: e.clientY })
	}
	const onClose = (e: MouseEvent): void => {
		e.stopPropagation()
		close()
	}

	const title = tabTitle(tab)
	const classes = ['tab', active && 'active', tab.pinned && 'pinned'].filter(Boolean).join(' ')
	return (
		<div
			role="tab"
			aria-selected={active}
			tabIndex={active ? 0 : -1}
			className={classes}
			title={title}
			onMouseDown={onMouseDown}
			onAuxClick={onAuxClick}
			onKeyDown={onKeyDown}
			onContextMenu={onContextMenu}
		>
			{/* Close sits on the left, as in Krackn, so it never moves when a title changes width. */}
			<button
				type="button"
				className="tab-close"
				aria-label={`Close ${title}`}
				onMouseDown={(e) => e.stopPropagation()}
				onClick={onClose}
			>
				<CloseIcon />
			</button>
			<span className={tab.loading ? 'tab-icon loading' : 'tab-icon'} aria-hidden="true">
				{tab.loading ? null : monogram(tab)}
			</span>
			{tab.pinned ? null : <span className="tab-title">{title}</span>}
		</div>
	)
}

export function TabStrip({ tabs, activeId }: { tabs: readonly TabView[]; activeId: string | undefined }) {
	return (
		<div className="tabstrip">
			<div className="tabs" role="tablist" aria-label="Tabs">
				{tabs.map((tab) => (
					<TabChip key={tab.id} tab={tab} active={tab.id === activeId} />
				))}
			</div>
			<button
				type="button"
				className="icon-button new-tab"
				aria-label="New Tab"
				title={commandTooltip('tab.new')}
				onClick={() => void zing.runCommand('tab.new')}
			>
				<PlusIcon />
			</button>
		</div>
	)
}
