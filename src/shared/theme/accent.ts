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
	type PaletteTokens,
} from './palette'

export const AccentTheme = {
	yellow: 'yellow',
	green: 'green',
	blue: 'blue',
	magenta: 'magenta',
	purple: 'purple',
	red: 'red',
	mono: 'mono',
} as const
export type AccentTheme = (typeof AccentTheme)[keyof typeof AccentTheme]

export const accentThemes: readonly AccentTheme[] = Object.values(AccentTheme)

/** Base hue in degrees, and how much of the reference palette's saturation to keep. */
export type HueProfile = { readonly hue: number; readonly saturation: number }

const labels: Readonly<Record<AccentTheme, string>> = {
	yellow: 'Yellow',
	green: 'Green',
	blue: 'Blue',
	magenta: 'Magenta',
	purple: 'Purple',
	red: 'Red',
	mono: 'Mono',
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
	red: { hue: 6, saturation: 0.84 },
	mono: { hue: 34, saturation: 0.07 },
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
