import { hexFromRGB, relativeLuminance } from './color'

/** About #303030: at or under it, a pixel is lost on Zing's dark tiles (contrast under 1.4). */
const DARK_LUMINANCE = 0.03
const OPAQUE = 128

/** Whether a site icon, as canvas RGBA bytes, is mostly so dark it would vanish in dark mode and needs a light backing. */
export function isDarkIcon(rgba: Uint8ClampedArray): boolean {
	let opaque = 0
	let dark = 0
	for (let i = 0; i + 3 < rgba.length; i += 4) {
		if (rgba[i + 3]! < OPAQUE) continue
		opaque++
		const hex = hexFromRGB({ r: rgba[i]! / 255, g: rgba[i + 1]! / 255, b: rgba[i + 2]! / 255 })
		if (relativeLuminance(hex) <= DARK_LUMINANCE) dark++
	}
	return opaque > 0 && dark / opaque >= 0.5
}
