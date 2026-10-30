// Port of Krackn's ScrollMetrics (App/ScrollFade.swift): which edges of a
// scroll area have more content past them, and so should show a frosted fade.

export interface ScrollMetrics {
	readonly offset: number
	readonly contentHeight: number
	readonly viewportHeight: number
}

export interface ScrollEdges {
	readonly top: boolean
	readonly bottom: boolean
}

/** A sub-pixel remainder is rounding, not content; one pixel is the threshold Krackn used. */
const EPSILON = 1

export function scrollEdges({ offset, contentHeight, viewportHeight }: ScrollMetrics): ScrollEdges {
	return {
		top: offset > EPSILON,
		bottom: contentHeight - offset - viewportHeight > EPSILON,
	}
}
