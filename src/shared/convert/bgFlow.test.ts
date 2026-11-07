import { describe, expect, it } from 'vitest'
import { proceedAfterCheck, RunGate } from './bgFlow'

const deferred = <T>(): { promise: Promise<T>; resolve(value: T): void } => {
	let resolve!: (value: T) => void
	const promise = new Promise<T>((r) => (resolve = r))
	return { promise, resolve }
}

describe('RunGate', () => {
	it('knows only the newest run is current', () => {
		const gate = new RunGate()
		const first = gate.begin()
		expect(gate.isCurrent(first)).toBe(true)
		const second = gate.begin()
		expect(gate.isCurrent(first)).toBe(false)
		expect(gate.isCurrent(second)).toBe(true)
	})
	it('makes the current run stale on cancel', () => {
		const gate = new RunGate()
		const id = gate.begin()
		gate.cancel()
		expect(gate.isCurrent(id)).toBe(false)
	})
})

describe('proceedAfterCheck', () => {
	it('offers the model when it is not installed, and runs when it is', async () => {
		const gate = new RunGate()
		const calls: string[] = []
		await proceedAfterCheck(gate, gate.begin(), () => Promise.resolve({ installed: false }), () => calls.push('offer'), () => calls.push('run'))
		await proceedAfterCheck(gate, gate.begin(), () => Promise.resolve({ installed: true }), () => calls.push('offer'), () => calls.push('run'))
		expect(calls).toEqual(['offer', 'run'])
	})
	it('does neither when Cancel came while the model list was being fetched', async () => {
		const gate = new RunGate()
		const wait = deferred<{ installed: boolean }>()
		const calls: string[] = []
		const pending = proceedAfterCheck(gate, gate.begin(), () => wait.promise, () => calls.push('offer'), () => calls.push('run'))
		gate.cancel()
		wait.resolve({ installed: true })
		await pending
		expect(calls).toEqual([])
	})
	it('does neither when another picture was started meanwhile', async () => {
		const gate = new RunGate()
		const wait = deferred<{ installed: boolean }>()
		const calls: string[] = []
		const pending = proceedAfterCheck(gate, gate.begin(), () => wait.promise, () => calls.push('offer'), () => calls.push('run'))
		gate.begin()
		wait.resolve({ installed: false })
		await pending
		expect(calls).toEqual([])
	})
})
