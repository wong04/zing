import { createContext, useContext } from 'react'
import type { ToolPanelView } from '@shared/shell/shellState'

/** What another part of Zing handed the panel a tool sits in to start from; null when nothing did. */
export const PanelInputContext = createContext<ToolPanelView['input'] | null>(null)

export function usePanelInput(): ToolPanelView['input'] | null {
	return useContext(PanelInputContext)
}
