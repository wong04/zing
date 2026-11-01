import { useEffect, useState } from 'react'
import type { LibraryViewState } from '@shared/ipc'
import { followHostState } from '../hostState'
import { zing } from '../api'

/** The shared library plus this window's panel and bar state, live from main. */
export function useLibraryState(): LibraryViewState | undefined {
	const [state, setState] = useState<LibraryViewState>()
	useEffect(() => {
		return followHostState(zing.onLibraryState, zing.getLibrary, setState)
	}, [])
	return state
}
