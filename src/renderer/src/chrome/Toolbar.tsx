import type { MouseEvent, ReactNode } from 'react'
import type { CommandId } from '@shared/commands'
import type { TabView } from '@shared/ipc'
import { zing } from '../api'
import { AddressBar } from './AddressBar'
import { BackIcon, ForwardIcon, MoreIcon, ReloadIcon, StopIcon } from './icons'
import { HistoryButton } from './HistoryButton'
import { commandTooltip } from './tooltip'

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

function openMenu(e: MouseEvent<HTMLButtonElement>): void {
	const rect = e.currentTarget.getBoundingClientRect()
	void zing.openAppMenu({ x: rect.left, y: rect.bottom })
}

export function Toolbar({ tab }: { tab: TabView | undefined }) {
	const loading = tab?.loading ?? false
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
			<button type="button" className="icon-button" aria-label="Menu" title="Menu" onClick={openMenu}>
				<MoreIcon />
			</button>
			<div className={loading ? 'loading-lane active' : 'loading-lane'} aria-hidden="true" />
		</div>
	)
}
