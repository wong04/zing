import { useSyncExternalStore } from 'react'
import type { AiTabsState } from '@shared/ipc'
import { zing } from '../api'
import { followHostState } from '../hostState'
import './aiTabs.css'

const EMPTY: AiTabsState = { shared: [], waiting: [] }

let current = EMPTY
const listeners = new Set<() => void>()
let stop: (() => void) | null = null

// One subscription per view, however many tabs read it: started by the first reader, ended with the last.
function subscribe(listener: () => void): () => void {
	listeners.add(listener)
	stop ??= followHostState(zing.onAiState, zing.getAiState, (state) => {
		current = state
		for (const l of listeners) l()
	})
	return () => {
		listeners.delete(listener)
		if (listeners.size > 0 || !stop) return
		stop()
		stop = null
	}
}

/** This window's tabs as AI control sees them: which are shared, which wait for the owner, and the bars of the tab in front. */
export function useAiTabs(): AiTabsState {
	return useSyncExternalStore(subscribe, () => current)
}

/** The class names a tab gets: an outline while shared, and a badge while something waits for the owner on it. */
export function aiTabClasses(state: AiTabsState, id: string): string[] {
	return [state.shared.includes(id) && 'ai-shared', state.waiting.includes(id) && 'ai-waiting'].filter((c): c is string => typeof c === 'string')
}
