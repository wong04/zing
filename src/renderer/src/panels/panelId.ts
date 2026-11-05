import { createContext, useContext } from 'react'

/** The id of the floating panel a tool body is drawn in. A tool that runs an engine job names its panel, so closing the panel ends the job. */
export const PanelIdContext = createContext<string | null>(null)

export function usePanelId(): string {
	const id = useContext(PanelIdContext)
	if (id === null) throw new Error('This tool must be drawn inside a panel')
	return id
}
