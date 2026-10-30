/** Ask only when quitting would throw away more than a single tab. */
export function needsQuitConfirmation(confirmQuit: boolean, openTabs: number): boolean {
	return confirmQuit && openTabs > 1
}
