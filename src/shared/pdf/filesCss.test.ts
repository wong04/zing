import { readFileSync } from 'node:fs'
import { join } from 'node:path'
import { describe, expect, it } from 'vitest'

// files.css is shared by Convert, Engines, the viewer, the background remover and the PDF tools, and has been broken by merges
// before (a missing brace nested everything after it). These checks fail on that, and on a rule that appears twice.
const css = readFileSync(join(import.meta.dirname, '..', '..', 'renderer', 'src', 'tools', 'files', 'files.css'), 'utf8').replace(/\r/g, '')

describe('files.css', () => {
	it('has balanced braces, so no rule is nested under another', () => {
		let depth = 0
		for (const [index, line] of css.split('\n').entries()) {
			depth += line.split('{').length - line.split('}').length
			expect(depth, `line ${index + 1}`).toBeGreaterThanOrEqual(0)
			expect(depth <= 1, `line ${index + 1}`).toBe(true)
		}
		expect(depth).toBe(0)
	})
	it('has each rule once', () => {
		const selectors = [...css.matchAll(/^([^\s/@}][^{\n]*?)\s*\{$/gm)].map((m) => m[1]!)
		const repeated = selectors.filter((s, i) => selectors.indexOf(s) !== i)
		expect(repeated).toEqual([])
	})
	it('still has the rules of every tool', () => {
		for (const selector of ['.engine-list', '.convert-drop', '.convert-actions', '.viewer', '.bg-progress', '.pdf-grid', '.pdf-cell', '.pdf-sheet']) expect(css, selector).toContain(`${selector} {`)
	})
})
