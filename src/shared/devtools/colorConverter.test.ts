// Port of the colour checks in Krackn Tests/unit (section F).
import { describe, expect, it } from 'vitest'
import { convertColor } from './colorConverter'

describe('colour (Krackn Tests/unit F)', () => {
	it('color: hex', () => {
		expect(convertColor('#FF0000')?.rgb).toBe('rgb(255, 0, 0)')
	})
	it('color: shorthand', () => {
		expect(convertColor('#f00')?.hex).toBe('#FF0000')
	})
	it('color: rgb input', () => {
		expect(convertColor('rgb(0, 255, 0)')?.hex).toBe('#00FF00')
	})
	it('color: hsl present', () => {
		expect(convertColor('#0000FF')?.hsl).toContain('240')
	})
	it('color: cmyk present', () => {
		expect(convertColor('#000000')?.cmyk).toContain('100%')
	})
	it('color: garbage -> nil', () => {
		expect(convertColor('nope')).toBeNull()
	})
})

// Zing additions.
describe('known answers', () => {
	it('the reference beige in every notation', () => {
		expect(convertColor('d9b872')).toEqual({
			hex: '#D9B872',
			rgb: 'rgb(217, 184, 114)',
			rgbFloat: '0.851, 0.722, 0.447',
			hsl: 'hsl(40, 57%, 64%)',
			cmyk: 'cmyk(0%, 15%, 47%, 14%)',
		})
	})
	it('reads rgba() and ignores alpha', () => {
		expect(convertColor('rgba(10, 20, 30, 0.5)')?.hex).toBe('#0A141E')
	})
	it('an alpha hex keeps the first six digits', () => {
		expect(convertColor('#11223380')?.hex).toBe('#112233')
	})
	it('four-digit hex and stray signs are refused', () => {
		expect(convertColor('#abcd')).toBeNull()
		expect(convertColor('#+f0000')).toBeNull()
		expect(convertColor('rgb(1, 2)')).toBeNull()
	})
	it('white has no ink', () => {
		expect(convertColor('#fff')?.cmyk).toBe('cmyk(0%, 0%, 0%, 0%)')
	})
})
