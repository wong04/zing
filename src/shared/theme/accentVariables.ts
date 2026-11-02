import { themeProfile, themeTokens } from './accent'
import { accentInk, control, raised, stackTokens, type PaletteTokens, type StackTokens } from './palette'

function stackVariables(s: StackTokens): Record<string, string> {
	return {
		'--window': s.window,
		'--window-ink': s.windowInk,
		'--band': s.band,
		'--band-ink': s.bandInk,
		'--field': s.field,
		'--field-ink': s.fieldInk,
		'--page': s.page,
		'--page-ink': s.pageInk,
		'--card-1': s.cards[0],
		'--card-2': s.cards[1],
		'--card-3': s.cards[2],
		'--card-ink-1': s.cardInks[0],
		'--card-ink-2': s.cardInks[1],
		'--card-ink-3': s.cardInks[2],
		'--ribbon-1': s.ribbons[0],
		'--ribbon-2': s.ribbons[1],
		'--ribbon-3': s.ribbons[2],
		'--ribbon-ink-1': s.ribbonInks[0],
		'--ribbon-ink-2': s.ribbonInks[1],
		'--ribbon-ink-3': s.ribbonInks[2],
		'--more': s.more,
		'--more-ink': s.moreInk,
	}
}

/**
 * The CSS custom properties for one accent in one appearance; names match renderer theme/tokens.css. Status
 * colours are deliberately absent: they stay fixed across accents. Pure, so the e2e spec can compute the same values.
 */
export function accentVariables(accent: string, customColor: string, dark: boolean): Record<string, string> {
	const t: PaletteTokens = themeTokens(accent, customColor, dark)
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
		...stackVariables(stackTokens(themeProfile(accent, customColor), dark)),
	}
}
