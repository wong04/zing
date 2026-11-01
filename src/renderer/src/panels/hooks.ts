import { useEffect, useState } from 'react'
import { EMPTY_TOOL_PANELS, type ShellState, type ToolPanelsState } from '@shared/shell/shellState'
import { followHostState } from '../hostState'
import { zing } from '../api'

export function useToolPanels(): ToolPanelsState {
	const [state, setState] = useState<ToolPanelsState>(EMPTY_TOOL_PANELS)
	useEffect(() => {
		return followHostState(zing.onToolPanelsState, zing.getToolPanels, setState)
	}, [])
	return state
}

export function useShellState(): ShellState | undefined {
	const [state, setState] = useState<ShellState>()
	useEffect(() => {
		return followHostState(zing.onShellState, zing.getShell, setState)
	}, [])
	return state
}
