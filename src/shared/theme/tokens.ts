// The non-accent design tokens from Krackn App/Theme.swift.

export type ColorPair = { readonly light: string; readonly dark: string }

/**
 * Status colours stay fixed across accents. A red "danger" that turns green
 * because the user picked the green theme would be actively dangerous in the
 * security section.
 */
export const statusColors = {
	positive: { light: '#4F7A3F', dark: '#8FBF7A' },
	caution: { light: '#A86A2E', dark: '#D9A05B' },
	danger: { light: '#A33A2E', dark: '#E08070' },
} as const satisfies Record<string, ColorPair>

/**
 * Hairline separators and control borders are the outline colour at this
 * opacity. Surfaces are separated with hairlines at a fraction of an outline's
 * weight, letting fill and soft shadow carry the depth; everything structural
 * uses this one token, so the weight of the whole app's linework can be tuned
 * in one place.
 */
export const separatorOpacity = 0.13

/** Shape and spacing constants, so radii and rhythm are the same everywhere instead of being re-picked per screen. */
export const metrics = {
	/** Cards and grouped sections. */
	cardRadius: 12,
	/** Buttons, fields, menus, segmented tracks. */
	controlRadius: 8,
	/** Space between a section's small header and its card. */
	sectionHeaderGap: 6,
} as const
