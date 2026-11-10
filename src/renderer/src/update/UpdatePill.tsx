import { useLayoutEffect, useRef } from 'react'
import type { UpdateState } from '@shared/ipc'
import { zing } from '../api'
import { PillButton } from '../components'
import { useAccentTheme } from '../settings/useAccentTheme'
import { useUpdateState } from './useUpdateState'
import './update.css'

/** The pill itself, only while an update is downloaded and waiting. Every click asks; the host restarts once. */
export function UpdatePillButton({ state }: { state: UpdateState | null }) {
	if (state?.status !== 'ready') return null
	return <PillButton onClick={() => void zing.relaunchToUpdate()}>Relaunch to update</PillButton>
}

/** Root of the #update-pill view: the host shows it at the content's bottom right, sized to what this measures. */
export function UpdatePill() {
	useAccentTheme()
	const state = useUpdateState()
	const root = useRef<HTMLDivElement>(null)
	const ready = state?.status === 'ready'
	useLayoutEffect(() => {
		const element = root.current
		if (!element || !ready) return
		const report = (): void => {
			const { width, height } = element.getBoundingClientRect()
			void zing.setUpdatePillSize(width, height)
		}
		report()
		const observer = new ResizeObserver(report)
		observer.observe(element)
		return () => observer.disconnect()
	}, [ready])
	return (
		<div ref={root} className="update-pill">
			<UpdatePillButton state={state} />
		</div>
	)
}
