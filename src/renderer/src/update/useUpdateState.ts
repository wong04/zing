import { useEffect, useState } from 'react'
import type { UpdateState } from '@shared/ipc'
import { zing } from '../api'
import { followHostState } from '../hostState'

/** The app's update state, null until the host has answered. */
export function useUpdateState(): UpdateState | null {
	const [state, setState] = useState<UpdateState | null>(null)
	useEffect(() => followHostState(zing.onUpdateState, zing.getUpdateState, setState), [])
	return state
}
