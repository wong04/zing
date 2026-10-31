// Port of the recents section of Krackn Tests/panels.
// The cases live in recentList.vectors.json, which the C# port (PanelLayoutTests.cs) reads too.
import { readFileSync } from 'node:fs'
import { describe, expect, it } from 'vitest'
import { removing, touching } from './recentList'

interface Step {
	readonly op: 'touch' | 'remove'
	readonly id: string
	readonly want: string[]
}

const vectors = JSON.parse(readFileSync(new URL('./recentList.vectors.json', import.meta.url), 'utf8')) as {
	cases: { name: string; steps: Step[] }[]
}

describe('recents (shared vectors)', () => {
	it.each(vectors.cases.map((c) => [c.name, c] as const))('%s', (_name, c) => {
		let list: string[] = []
		for (const step of c.steps) {
			list = step.op === 'touch' ? touching(step.id, list) : removing(step.id, list)
			expect(list).toEqual(step.want)
		}
	})
})
