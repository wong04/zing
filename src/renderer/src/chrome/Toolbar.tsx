import type { MouseEvent, ReactNode } from 'react'
import type { CommandId } from '@shared/commands'
import type { TabView } from '@shared/ipc'
import { zing } from '../api'
import { AddressBar } from './AddressBar'
import { BackIcon, ForwardIcon, MoreIcon, ReloadIcon, StopIcon } from './icons'
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
			<CommandButton command="history.back" disabled={!tab?.canGoBack}>
				<BackIcon />
			</CommandButton>
			<CommandButton command="history.forward" disabled={!tab?.canGoForward}>
				<ForwardIcon />
			</CommandButton>
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
