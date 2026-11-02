import { useEffect } from 'react'
import { applyAccent } from './applyAccent'
import { useSettings } from './useSettings'

/** Tints this renderer with the accent setting, so the accent has a reader and a change shows at once. */
export function useAccentTheme(): void {
	const settings = useSettings()?.settings
	const accent = settings?.accent
	const customColor = settings?.customColor
	useEffect(() => {
		if (accent === undefined || customColor === undefined) return
		return applyAccent(accent, customColor)
	}, [accent, customColor])
}
