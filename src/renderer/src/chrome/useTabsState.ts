import { useEffect, useState } from 'react'
import type { TabsState } from '@shared/ipc'
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
		const unsubscribe = zing.onTabsState(apply)
		void zing.getTabs().then(apply)
		return () => {
			unsubscribe()
			cancelAnimationFrame(frame)
		}
	}, [])
	return state
}
