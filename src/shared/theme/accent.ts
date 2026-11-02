// Port of `AccentTheme` from Krackn App/Accent.swift: the colour families the
// whole app can be tinted with.
//
// The original beige is kept byte-for-byte as `yellow` — it's the reference
// the others are derived from, and re-deriving it through the generator would
// shift a few channels by a point for no reason. Every other family is built
// by rotating that palette's hue while holding its lightness profile fixed, so
// a blue Zing has exactly the same contrast relationships and "tone and shade"
// as the beige one rather than being a separate hand-picked palette that
// drifts.
import {
	darkProfile,
	generatePalette,
	lightProfile,
	referenceDark,
	referenceLight,
	type HueProfile,
	type PaletteTokens,
} from './palette'
import { hslFromHex } from './color'

export type { HueProfile }

export const AccentTheme = {
	yellow: 'yellow',
	green: 'green',
	blue: 'blue',
	magenta: 'magenta',
	purple: 'purple',
	red: 'red',
	mono: 'mono',
	midnight: 'midnight',
} as const
export type AccentTheme = (typeof AccentTheme)[keyof typeof AccentTheme]

export const accentThemes: readonly AccentTheme[] = Object.values(AccentTheme)

/** The accent name for a user-chosen seed colour, as opposed to a preset family. */
export const CUSTOM_ACCENT = 'custom'

const labels: Readonly<Record<AccentTheme, string>> = {
	yellow: 'Amber',
	green: 'Forest',
	blue: 'Ocean',
	magenta: 'Candy',
	purple: 'Grape',
	red: 'Sunset',
	mono: 'Mono',
	midnight: 'Midnight',
}

// Warmer hues carry saturation well; the cooler and more magenta ones read as
// garish at full strength, so they're pulled back slightly. `mono` keeps a
// trace of warmth rather than going dead neutral, which would clash with the
// near-black outline the design depends on.
const hueProfiles: Readonly<Record<AccentTheme, HueProfile>> = {
	yellow: { hue: 38, saturation: 1.0 },
	green: { hue: 142, saturation: 0.82 },
	blue: { hue: 211, saturation: 0.88 },
	magenta: { hue: 322, saturation: 0.8 },
	purple: { hue: 272, saturation: 0.82 },
	red: { hue: 14, saturation: 0.84 },
	mono: { hue: 34, saturation: 0.07 },
	midnight: { hue: 232, saturation: 0.7 },
}

const accentThemeIds: readonly string[] = accentThemes

/** For a stored setting: whether it still names a family. */
export function isAccentTheme(value: string): value is AccentTheme {
	return accentThemeIds.includes(value)
}

export function accentLabel(theme: AccentTheme): string {
	return labels[theme]
}

export function hueProfile(theme: AccentTheme): HueProfile {
	return hueProfiles[theme]
}

/** A hue and saturation taken from a seed colour. Saturation has a floor so a grey seed still gets a faint tint. */
export function seedProfile(hex: string): HueProfile {
	const { hue, saturation } = hslFromHex(hex)
	return { hue, saturation: Math.min(Math.max(saturation, 0.07), 1) }
}

function requireAccentTheme(accent: string): AccentTheme {
	if (!isAccentTheme(accent)) throw new RangeError(`Unknown accent "${accent}"`)
	return accent
}

/** The hue profile for a named accent, or for the seed when the accent is `custom`. */
export function themeProfile(accent: string, customColor: string): HueProfile {
	if (accent === CUSTOM_ACCENT) return seedProfile(customColor)
	return hueProfiles[requireAccentTheme(accent)]
}

/** The palette for a named accent, or for the seed when the accent is `custom`. */
export function themeTokens(accent: string, customColor: string, dark: boolean): PaletteTokens {
	if (accent === CUSTOM_ACCENT) {
		const { hue, saturation } = seedProfile(customColor)
		return generatePalette(dark ? darkProfile : lightProfile, hue, saturation)
	}
	const theme = requireAccentTheme(accent)
	return dark ? darkTokens(theme) : lightTokens(theme)
}

export function lightTokens(theme: AccentTheme): PaletteTokens {
	if (theme === AccentTheme.yellow) return referenceLight
	const { hue, saturation } = hueProfiles[theme]
	return generatePalette(lightProfile, hue, saturation)
}

export function darkTokens(theme: AccentTheme): PaletteTokens {
	if (theme === AccentTheme.yellow) return referenceDark
	const { hue, saturation } = hueProfiles[theme]
	return generatePalette(darkProfile, hue, saturation)
}

/** A representative swatch for the settings picker — the accent itself. */
export function swatchHex(theme: AccentTheme): string {
	return lightTokens(theme).accent
}
