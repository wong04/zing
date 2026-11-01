import { useEffect, useState } from 'react'
import type { TabsState } from '@shared/ipc'
import { followHostState } from '../hostState'
import { zing } from '../api'

/** Live tab state from main, applied at most once per animation frame. */
export function useTabsState(): TabsState | undefined {
	const [state, setState] = useState<TabsState>()
	useEffect(() => {
		let latest: TabsState | undefined
		let frame = 0
		const apply = (next: TabsState): void => {
			latest = next
			if (frame) return
			frame = requestAnimationFrame(() => {
				frame = 0
				setState(latest)
			})
		}
		const unsubscribe = followHostState(zing.onTabsState, zing.getTabs, apply)
		return () => {
			unsubscribe()
			cancelAnimationFrame(frame)
		}
	}, [])
	return state
}
