import { accentVariables } from '@shared/theme/accentVariables'

/** Tints this renderer with the chosen accent, and follows light/dark changes. Returns a stop function. */
export function applyAccent(accent: string, customColor: string): () => void {
	const query = matchMedia('(prefers-color-scheme: dark)')
	const apply = (): void => {
		for (const [name, value] of Object.entries(accentVariables(accent, customColor, query.matches))) {
			document.documentElement.style.setProperty(name, value)
		}
	}
	apply()
	query.addEventListener('change', apply)
	return () => query.removeEventListener('change', apply)
}
