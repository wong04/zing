import { useEffect } from 'react'
import { mix } from '@shared/theme/color'
import { zing } from '../api'

function readToken(name: string): string {
	return getComputedStyle(document.documentElement).getPropertyValue(name).trim()
}

/**
 * The native caption buttons are drawn by Windows, not CSS, so their colours are pushed from the theme
 * tokens: CSS stays the one source of colour. They match what they sit on: the toolbar when tabs are on the
 * left, otherwise the window colour of the strip or the rail. Call after useAccentTheme, which sets the tokens.
 */
export function useTitleBarColors(position: string, accent: string | undefined, customColor: string | undefined): void {
	useEffect(() => {
		if (accent === undefined || customColor === undefined) return
		const send = (): void => {
			const surface = position === 'left' ? 'band' : 'window'
			const background = readToken(`--${surface}`)
			const ink = readToken(`--${surface}-ink`)
			void zing.setTitleBarColors({ symbolColor: ink, background, hover: mix(background, ink, 0.1), pressed: mix(background, ink, 0.2) })
		}
		send()
		const query = matchMedia('(prefers-color-scheme: dark)')
		query.addEventListener('change', send)
		return () => query.removeEventListener('change', send)
	}, [position, accent, customColor])
}
