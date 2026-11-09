/** A link preview's buttons view (#peek): what it shows while a preview is open. Host: ChromeApi.PeekState. */
export interface PeekState {
	/** The host of the preview's top layer, as it is now. */
	readonly host: string
	/** False while a popup layer is over the preview's page: only the page can become a tab. */
	readonly canExpand: boolean
	/** Windows' high-contrast mode, which the native scrim follows too. */
	readonly highContrast: boolean
}
