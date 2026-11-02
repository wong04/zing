import { themeProfile, themeTokens } from './accent'
import type { TitleBarColors } from '../ipc'
import { mix } from './color'
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
		'--colour-card': s.card,
		'--colour-card-ink': s.cardInk,
		'--colour-ribbon': s.ribbon,
		'--colour-ribbon-ink': s.ribbonInk,
		'--plain': s.plain,
		'--plain-ink': s.plainInk,
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

/** Windows' caption buttons match what they sit on: the toolbar when tabs are on the left, otherwise the window colour of the strip or the rail. */
export function titleBarColors(position: string, accent: string, customColor: string, dark: boolean): TitleBarColors {
	const stack = stackTokens(themeProfile(accent, customColor), dark)
	const [background, ink] = position === 'left' ? [stack.band, stack.bandInk] : [stack.window, stack.windowInk]
	return { symbolColor: ink, background, hover: mix(background, ink, 0.1), pressed: mix(background, ink, 0.2) }
}
