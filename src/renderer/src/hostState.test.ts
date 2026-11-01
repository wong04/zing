import { describe, expect, it } from 'vitest'
import { followHostState } from './hostState'

function deferred<T>() {
	let resolve!: (value: T) => void
	const promise = new Promise<T>((r) => (resolve = r))
	return { promise, resolve }
}

describe('followHostState', () => {
	it('applies the first fetch when nothing has been pushed yet', async () => {
		const reply = deferred<string>()
		const seen: string[] = []
		followHostState<string>(() => () => undefined, () => reply.promise, (s) => seen.push(s))
		reply.resolve('initial')
		await reply.promise
		expect(seen).toEqual(['initial'])
	})

	it('drops a fetch reply that arrives after a push, which it cannot be newer than', async () => {
		const reply = deferred<string>()
		let push: (s: string) => void = () => undefined
		const seen: string[] = []
		followHostState<string>(
			(listener) => {
				push = listener
				return () => undefined
			},
			() => reply.promise,
			(s) => seen.push(s),
		)
		push('active tab set')
		reply.resolve('before the tab was active')
		await reply.promise
		expect(seen).toEqual(['active tab set'])
	})

	it('stops listening when unsubscribed', () => {
		let off = false
		const unsubscribe = followHostState<string>(
			() => () => {
				off = true
			},
			() => new Promise(() => undefined),
			() => undefined,
		)
		unsubscribe()
		expect(off).toBe(true)
	})
})
