import { readFileSync } from 'node:fs'
import { describe, expect, it } from 'vitest'
import { DEFAULT_CUSTOM_COLOR } from '../settings'
import { accentVariables } from './accentVariables'
import { statusColors } from './tokens'

/** Fixed across accents, so the palette never themes them. */
const STATUS_VARIABLES = ['--positive', '--caution', '--danger']

// Read here rather than beside the renderer's accent tests: the renderer project has no Node types, so it cannot read files.
describe('tokens.css', () => {
	const css = readFileSync(new URL('../../renderer/src/theme/tokens.css', import.meta.url), 'utf8')
	// The light :root, then the one inside the dark media query.
	const blocks = [...css.matchAll(/:root\s*\{([^}]*)\}/g)].map((m) => m[1]!)

	/** Every declaration in a block whose value is a hex colour. */
	const hexVariables = (block: string): Record<string, string> =>
		Object.fromEntries([...block.matchAll(/(--[\w-]+):\s*(#[0-9a-fA-F]{6})\s*;/g)].map((m) => [m[1]!, m[2]!]))

	it('holds the yellow values in both appearances, so the defaults cannot drift', () => {
		expect(blocks).toHaveLength(2)
		for (const [mode, block] of [
			['light', blocks[0]!],
			['dark', blocks[1]!],
		] as const) {
			const expected = accentVariables('yellow', DEFAULT_CUSTOM_COLOR, mode === 'dark')
			const actual = hexVariables(block)
			expect(Object.keys(actual).sort(), mode).toEqual([...Object.keys(expected), ...STATUS_VARIABLES].sort())
			for (const [name, value] of Object.entries(expected)) expect(actual[name]?.toLowerCase(), `${mode} ${name}`).toBe(value.toLowerCase())
		}
	})

	it('holds the fixed status colours in both appearances', () => {
		for (const [mode, block] of [
			['light', blocks[0]!],
			['dark', blocks[1]!],
		] as const) {
			const actual = hexVariables(block)
			for (const [name, pair] of Object.entries(statusColors)) expect(actual[`--${name}`]?.toLowerCase(), `${mode} ${name}`).toBe(pair[mode].toLowerCase())
		}
	})
})
