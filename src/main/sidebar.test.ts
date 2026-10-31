// The cases live in sidebar.vectors.json, which the C# port (SidebarRevealTests.cs) reads too.
import { readFileSync } from 'node:fs'
import { describe, expect, it } from 'vitest'
import { EdgeReveal } from './sidebar'

interface Step {
	readonly op: 'update' | 'setEnabled' | 'peek'
	readonly x?: number
	readonly y?: number
	readonly now?: number
	readonly inWindow?: boolean
	readonly value?: boolean
	readonly revealed?: boolean
	readonly changes?: boolean[]
}

const vectors = JSON.parse(readFileSync(new URL('./sidebar.vectors.json', import.meta.url), 'utf8')) as {
	cases: { name: string; steps: Step[] }[]
}

function need<T>(value: T | undefined, what: string): T {
	if (value === undefined) throw new TypeError(`vector step has no ${what}`)
	return value
}

describe('EdgeReveal (shared vectors)', () => {
	it.each(vectors.cases.map((c) => [c.name, c] as const))('%s', (_name, c) => {
		const changes: boolean[] = []
		const reveal = new EdgeReveal({ onChange: (r) => changes.push(r) })
		reveal.setEnabled(true)
		changes.length = 0
		for (const step of c.steps) {
			if (step.op === 'update') {
				reveal.update({ x: need(step.x, 'x'), y: step.y ?? 300, inWindow: step.inWindow ?? true, now: need(step.now, 'now') })
			} else if (step.op === 'setEnabled') {
				reveal.setEnabled(need(step.value, 'value'))
			} else {
				reveal.peek(need(step.now, 'now'))
			}
			if (step.revealed !== undefined) expect(reveal.revealed).toBe(step.revealed)
			if (step.changes !== undefined) expect(changes).toEqual(step.changes)
		}
	})
})
