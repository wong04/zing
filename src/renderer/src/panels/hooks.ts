import { useEffect, useState } from 'react'
import { EMPTY_TOOL_PANELS, type ShellState, type ToolPanelsState } from '@shared/shell/shellState'
import { zing } from '../api'

export function useToolPanels(): ToolPanelsState {
	const [state, setState] = useState<ToolPanelsState>(EMPTY_TOOL_PANELS)
	useEffect(() => {
		const off = zing.onToolPanelsState(setState)
		void zing.getToolPanels().then(setState)
		return off
	}, [])
	return state
}

export function useShellState(): ShellState | undefined {
	const [state, setState] = useState<ShellState>()
	useEffect(() => {
		const off = zing.onShellState(setState)
		void zing.getShell().then(setState)
		return off
	}, [])
	return state
}
