import { useEffect } from 'react'
import { applyAccent } from './applyAccent'
import { useSettings } from './useSettings'

/** Tints this renderer with the accent setting, so the accent has a reader and a change shows at once. */
export function useAccentTheme(): void {
	const accent = useSettings()?.settings.accent
	useEffect(() => {
		if (accent === undefined) return
		return applyAccent(accent)
	}, [accent])
}
