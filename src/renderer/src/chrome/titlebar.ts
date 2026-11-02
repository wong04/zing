import { useEffect } from 'react'
import { titleBarColors } from '@shared/theme/accentVariables'
import { zing } from '../api'

/** The native caption buttons are drawn by Windows, not CSS, so their colours are pushed from the theme. */
export function useTitleBarColors(position: string, accent: string | undefined, customColor: string | undefined): void {
	useEffect(() => {
		if (accent === undefined || customColor === undefined) return
		const query = matchMedia('(prefers-color-scheme: dark)')
		const send = (): void => void zing.setTitleBarColors(titleBarColors(position, accent, customColor, query.matches))
		send()
		query.addEventListener('change', send)
		return () => query.removeEventListener('change', send)
	}, [position, accent, customColor])
}
