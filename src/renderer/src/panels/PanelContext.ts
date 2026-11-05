import { createContext, useContext } from 'react'

/** The id of the panel a tool is shown in, which is what the host's jobs and viewer files belong to. */
export const PanelIdContext = createContext<string | null>(null)

export class NoPanelError extends Error {
	override name = 'NoPanelError'
	constructor() {
		super('This tool was rendered outside a panel, so it has no panel id')
	}
}

export function usePanelId(): string {
	const id = useContext(PanelIdContext)
	if (id === null) throw new NoPanelError()
	return id
}
