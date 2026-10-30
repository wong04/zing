// Port of `PaletteTokens` from Krackn App/Accent.swift: the palette as
// CSS-ready hex, so web content the app renders (the start page) is coloured
// from exactly the same numbers as the UI — no hand-copied values to drift.
import { contrastRatio, gammaLuma, hexFromHSL, mix } from './color'

export type PaletteTokens = {
	readonly background: string
	readonly surface: string
	readonly tint: string
	readonly well: string
	readonly border: string
	readonly outline: string
	readonly accent: string
	readonly onAccent: string
	readonly selection: string
	readonly text: string
	readonly textSecondary: string
}

/** Saturation and lightness for one role, 0…1. */
export type Role = { readonly saturation: number; readonly lightness: number }

export type Profile = { readonly [K in keyof PaletteTokens]: Role } & {
	/**
	 * Roles that must stay near-neutral ink rather than take the hue at full
	 * strength, or dark mode's "outline" turns into a coloured haze.
	 */
	readonly inkDamping: number
}

/**
 * What a generated pair is lifted to. A little headroom over AA (4.5) so
 * rounding to whole channels can't tip it back under.
 */
export const legibleContrast = 4.6

const liftStep = 0.01
const liftCeiling = 0.92
const liftMaxSteps = 40
const inkStep = 0.05

// MARK: - The reference palette

/** The original beige, unchanged. Everything else is measured against this. */
export const referenceLight: PaletteTokens = {
	background: '#F7F2E9',
	surface: '#FFFDF8',
	tint: '#EFE6D6',
	well: '#EBE1CF',
	border: '#DECFB6',
	outline: '#2A2420',
	accent: '#D9B872',
	onAccent: '#2A2118',
	selection: '#E6D3AC',
	text: '#241F19',
	textSecondary: '#6E6355',
}

export const referenceDark: PaletteTokens = {
	background: '#1A1714',
	surface: '#241F1A',
	tint: '#201B16',
	well: '#2C251E',
	border: '#3A322A',
	outline: '#CDBFA6',
	accent: '#C9A45E',
	onAccent: '#1A1410',
	selection: '#3D3325',
	text: '#F0E7D8',
	textSecondary: '#A69884',
}

// MARK: - Profiles

// Measured off the reference palette above. Rotating the hue through these
// keeps every contrast pair — text on background, onAccent on accent — at the
// ratio the beige theme was designed around.

export const lightProfile: Profile = {
	background: { saturation: 0.47, lightness: 0.941 },
	surface: { saturation: 1.0, lightness: 0.986 },
	tint: { saturation: 0.44, lightness: 0.888 },
	well: { saturation: 0.41, lightness: 0.867 },
	border: { saturation: 0.38, lightness: 0.792 },
	outline: { saturation: 0.135, lightness: 0.145 },
	accent: { saturation: 0.575, lightness: 0.649 },
	onAccent: { saturation: 0.27, lightness: 0.13 },
	selection: { saturation: 0.54, lightness: 0.79 },
	text: { saturation: 0.18, lightness: 0.12 },
	textSecondary: { saturation: 0.128, lightness: 0.382 },
	inkDamping: 0.55,
}

export const darkProfile: Profile = {
	background: { saturation: 0.13, lightness: 0.09 },
	surface: { saturation: 0.16, lightness: 0.122 },
	tint: { saturation: 0.185, lightness: 0.106 },
	well: { saturation: 0.19, lightness: 0.145 },
	border: { saturation: 0.16, lightness: 0.196 },
	outline: { saturation: 0.28, lightness: 0.727 },
	accent: { saturation: 0.498, lightness: 0.578 },
	onAccent: { saturation: 0.236, lightness: 0.082 },
	selection: { saturation: 0.245, lightness: 0.192 },
	text: { saturation: 0.44, lightness: 0.894 },
	textSecondary: { saturation: 0.16, lightness: 0.584 },
	inkDamping: 0.55,
}

// MARK: - Generation

/**
 * A full palette for one hue, holding `profile`'s lightness for every role.
 * `saturationScale` is how much of the profile's saturation to keep.
 */
export function generatePalette(profile: Profile, hue: number, saturationScale: number): PaletteTokens {
	const role = (r: Role): string => hexFromHSL(hue, r.saturation * saturationScale, r.lightness)
	// Ink stays ink: the outline and body text carry only a hint of the family
	// so they read as near-black / near-white, not as coloured.
	const ink = (r: Role): string => hexFromHSL(hue, r.saturation * saturationScale * profile.inkDamping, r.lightness)

	const onAccent = ink(profile.onAccent)
	// HSL lightness is not perceived lightness. Blues and purples come out
	// markedly darker than yellows and greens at the same L, so holding L
	// fixed across hues would let some families' accents fall below the
	// contrast the reference palette was designed around — purple's dark
	// accent landed at 4.39:1, just under AA, entirely invisibly. Lifting
	// until the pair clears keeps "the accent always carries its own text" a
	// property of the system rather than something each new hue has to be
	// checked for by hand.
	const accent = liftUntilLegible(
		{ saturation: profile.accent.saturation * saturationScale, lightness: profile.accent.lightness },
		hue,
		onAccent,
	)

	return {
		background: role(profile.background),
		surface: role(profile.surface),
		tint: role(profile.tint),
		well: role(profile.well),
		border: role(profile.border),
		outline: ink(profile.outline),
		accent,
		onAccent,
		selection: role(profile.selection),
		text: ink(profile.text),
		textSecondary: ink(profile.textSecondary),
	}
}

/**
 * The colour at `hue` with `base`'s saturation, its lightness raised in small
 * steps from `base.lightness` until it reaches `legibleContrast` against
 * `foreground`. Stops at L 0.92 or after 40 steps, so it can come back short
 * of the target for a foreground that no lighter shade can carry.
 */
export function liftUntilLegible(base: Role, hue: number, foreground: string): string {
	let lightness = base.lightness
	let candidate = hexFromHSL(hue, base.saturation, lightness)
	for (let steps = 0; contrastRatio(candidate, foreground) < legibleContrast && lightness < liftCeiling && steps < liftMaxSteps; steps++) {
		lightness += liftStep
		candidate = hexFromHSL(hue, base.saturation, lightness)
	}
	return candidate
}

// MARK: - Derived roles

/**
 * The accent as a *foreground* — a selected sidebar glyph, a link, an active
 * tab's icon.
 *
 * `accent` itself is tuned to carry dark text, which makes it far too light to
 * be text: the original yellow is under 2:1 on its own surface. This walks the
 * accent toward the text colour until it clears AA against `surface` and
 * `selection`, so it keeps the family's hue while staying readable. In dark
 * appearance the accent usually passes untouched.
 */
export function accentInk(tokens: PaletteTokens): string {
	// Checked against `selection` too: a selected sidebar row draws this glyph
	// on the selection plate, which is darker than the surface.
	const legible = (c: string): boolean =>
		contrastRatio(c, tokens.surface) >= legibleContrast && contrastRatio(c, tokens.selection) >= legibleContrast
	let candidate = tokens.accent
	for (let step = 0; !legible(candidate) && step < 1; ) {
		step += inkStep
		candidate = mix(tokens.accent, tokens.text, step)
	}
	return candidate
}

/** Whether this is the dark half of a family. */
export function isDark(tokens: PaletteTokens): boolean {
	return gammaLuma(tokens.background) < 0.5
}

/**
 * Resting fill of a neutral control — field, segmented track, secondary
 * button. In light appearance the raw `well` is a heavy beige that made every
 * control look like a printed label; pulling it toward the surface gives the
 * faint, recessed tone a native control has.
 */
export function control(tokens: PaletteTokens): string {
	return isDark(tokens) ? tokens.well : mix(tokens.well, tokens.surface, 0.45)
}

/**
 * The plate a selected segment sits on. It has to read as *raised* above
 * `control`: in light that's the bright surface, but in dark the surface is
 * darker than the track, which made the selection look sunken — so dark lifts
 * the well toward the text colour instead.
 */
export function raised(tokens: PaletteTokens): string {
	return isDark(tokens) ? mix(tokens.well, tokens.text, 0.13) : tokens.surface
}
