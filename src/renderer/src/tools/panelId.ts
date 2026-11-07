import { createContext, useContext } from 'react'

/** The id of the panel a tool body is drawn in; an engine job belongs to it, so closing the panel stops the job. */
export const PanelIdContext = createContext<string | null>(null)

export class NoPanelError extends Error {
	override name = 'NoPanelError'
	constructor() {
		super('This tool must be drawn inside a panel')
	}
}

export function usePanelId(): string {
	const id = useContext(PanelIdContext)
	if (id === null) throw new NoPanelError()
	return id
}
