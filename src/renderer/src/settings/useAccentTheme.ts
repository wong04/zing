import { useEffect } from 'react'
import { applyAccent } from './applyAccent'
import { useSettings } from './useSettings'

/** Tints this renderer with the accent and tab colour settings, so they have a reader and a change shows at once. */
export function useAccentTheme(): void {
	const settings = useSettings()?.settings
	const accent = settings?.accent
	const customColor = settings?.customColor
	useEffect(() => {
		if (accent === undefined || customColor === undefined) return
		return applyAccent(accent, customColor)
	}, [accent, customColor])
	const tabColours = settings?.tabColours
	useEffect(() => {
		if (tabColours === undefined) return
		document.documentElement.dataset.tabColours = tabColours
	}, [tabColours])
}
