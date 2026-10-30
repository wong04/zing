import { useRef, type MouseEvent, type ReactNode } from 'react'
import type { HistoryDirection } from '@shared/browser/pageUi'
import type { CommandId } from '@shared/commands'
import { zing } from '../api'
import { commandTooltip } from './tooltip'

const HOLD_MS = 450

/** Back or Forward: a click runs the command; press-and-hold or right-click lists the history behind it. */
export function HistoryButton({
	command,
	direction,
	disabled,
	children,
}: {
	command: CommandId
	direction: HistoryDirection
	disabled: boolean
	children: ReactNode
}) {
	const title = commandTooltip(command)
	const timer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined)
	const held = useRef(false)

	const openMenu = (button: HTMLButtonElement): void => {
		const rect = button.getBoundingClientRect()
		void zing.openHistoryMenu(direction, { x: rect.left, y: rect.bottom })
	}
	const cancelHold = (): void => clearTimeout(timer.current)

	return (
		<button
			type="button"
			className="icon-button"
			aria-label={title}
			title={title}
			disabled={disabled}
			onPointerDown={(e) => {
				if (e.button !== 0) return
				held.current = false
				const button = e.currentTarget
				timer.current = setTimeout(() => {
					held.current = true
					openMenu(button)
				}, HOLD_MS)
			}}
			onPointerUp={cancelHold}
			onPointerLeave={cancelHold}
			onContextMenu={(e: MouseEvent<HTMLButtonElement>) => {
				e.preventDefault()
				cancelHold()
				if (!disabled) openMenu(e.currentTarget)
			}}
			onClick={() => {
				if (held.current) return
				void zing.runCommand(command)
			}}
		>
			{children}
		</button>
	)
}
