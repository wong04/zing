import { useEffect, useState } from 'react'
import type { LibraryViewState } from '@shared/ipc'
import { zing } from '../api'

/** The shared library plus this window's panel and bar state, live from main. */
export function useLibraryState(): LibraryViewState | undefined {
	const [state, setState] = useState<LibraryViewState>()
	useEffect(() => {
		const unsubscribe = zing.onLibraryState(setState)
		void zing.getLibrary().then(setState)
		return unsubscribe
	}, [])
	return state
}
