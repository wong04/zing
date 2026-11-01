import type { MouseEvent, ReactNode } from 'react'
import type { CommandId } from '@shared/commands'
import type { TabView } from '@shared/ipc'
import { zing } from '../api'
import { AddressBar } from './AddressBar'
import { BackIcon, ForwardIcon, MoreIcon, ReloadIcon, StopIcon } from './icons'
import { HistoryButton } from './HistoryButton'
import { commandTooltip } from './tooltip'
import { pageFaultText } from './display'
import './pageFault.css'

function CommandButton({ command, disabled, children }: { command: CommandId; disabled?: boolean; children: ReactNode }) {
	const title = commandTooltip(command)
	return (
		<button
			type="button"
			className="icon-button"
			aria-label={title}
			title={title}
			disabled={disabled}
			onClick={() => void zing.runCommand(command)}
		>
			{children}
		</button>
	)
}

/** The active tab's page crashed or hung: say so, and offer the reload that starts it afresh. */
function PageFault({ tab }: { tab: TabView | undefined }) {
	const text = pageFaultText(tab)
	if (text === undefined) return null
	return (
		<div className="page-fault" role="status" data-page-state={tab?.pageState}>
			<span>{text}</span>
			<button type="button" className="page-fault-reload" onClick={() => void zing.runCommand('page.reload')}>
				Reload
			</button>
		</div>
	)
}

function openMenu(e: MouseEvent<HTMLButtonElement>): void {
	const rect = e.currentTarget.getBoundingClientRect()
	void zing.openAppMenu({ x: rect.left, y: rect.bottom })
}

export function Toolbar({ tab }: { tab: TabView | undefined }) {
	// A crashed page can be left mid-load; Stop would do nothing for it, Reload brings it back.
	const loading = (tab?.loading ?? false) && tab?.pageState === undefined
	return (
		<div className="toolbar">
			<HistoryButton command="history.back" direction="back" disabled={!tab?.canGoBack}>
				<BackIcon />
			</HistoryButton>
			<HistoryButton command="history.forward" direction="forward" disabled={!tab?.canGoForward}>
				<ForwardIcon />
			</HistoryButton>
			{loading ? (
				<CommandButton command="page.stop">
					<StopIcon />
				</CommandButton>
			) : (
				<CommandButton command="page.reload">
					<ReloadIcon />
				</CommandButton>
			)}
			<AddressBar tab={tab} />
			<PageFault tab={tab} />
			<button type="button" className="icon-button" aria-label="Menu" title="Menu" onClick={openMenu}>
				<MoreIcon />
			</button>
			<div className={loading ? 'loading-lane active' : 'loading-lane'} aria-hidden="true" />
		</div>
	)
}
