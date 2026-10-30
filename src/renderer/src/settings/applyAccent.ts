import { isAccentTheme, darkTokens, lightTokens } from '@shared/theme/accent'
import { accentInk, control, raised, type PaletteTokens } from '@shared/theme/palette'

/** The CSS custom properties for one accent in one appearance; names match theme/tokens.css. */
export function accentVariables(accent: string, dark: boolean): Record<string, string> {
	if (!isAccentTheme(accent)) throw new RangeError(`Unknown accent "${accent}"`)
	const t: PaletteTokens = dark ? darkTokens(accent) : lightTokens(accent)
	return {
		'--bg': t.background,
		'--surface': t.surface,
		'--tint': t.tint,
		'--well': t.well,
		'--border': t.border,
		'--outline': t.outline,
		'--accent': t.accent,
		'--on-accent': t.onAccent,
		'--selection': t.selection,
		'--text': t.text,
		'--muted': t.textSecondary,
		'--control': control(t),
		'--raised': raised(t),
		'--accent-ink': accentInk(t),
	}
}

/** Tints this renderer with the chosen accent, and follows light/dark changes. Returns a stop function. */
export function applyAccent(accent: string): () => void {
	const query = matchMedia('(prefers-color-scheme: dark)')
	const apply = (): void => {
		for (const [name, value] of Object.entries(accentVariables(accent, query.matches))) {
			document.documentElement.style.setProperty(name, value)
		}
	}
	apply()
	query.addEventListener('change', apply)
	return () => query.removeEventListener('change', apply)
}
