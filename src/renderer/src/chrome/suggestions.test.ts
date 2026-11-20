import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { AddressSuggestion } from '@shared/browser/pageUi'
import { moveHighlight, SUGGEST_DELAY_MS, suggestionRequests } from './suggestions'

const row = (url: string): AddressSuggestion => ({ kind: 'bookmark', title: url, url })
const field = { x: 0, y: 0, width: 400, height: 28 }

describe('moveHighlight', () => {
	it('goes down through the rows and back to what was typed', () => {
		expect([moveHighlight(-1, 3, 'ArrowDown'), moveHighlight(0, 3, 'ArrowDown'), moveHighlight(2, 3, 'ArrowDown')]).toEqual([0, 1, -1])
	})

	it('goes up from what was typed to the last row', () => {
		expect([moveHighlight(-1, 3, 'ArrowUp'), moveHighlight(2, 3, 'ArrowUp'), moveHighlight(0, 3, 'ArrowUp')]).toEqual([2, 1, -1])
	})

	it('has nothing to highlight without rows', () => {
		expect(moveHighlight(-1, 0, 'ArrowDown')).toBe(-1)
	})
})

describe('suggestionRequests', () => {
	beforeEach(() => vi.useFakeTimers())
	afterEach(() => vi.useRealTimers())

	it('waits 50 ms after the last key before asking', async () => {
		expect(SUGGEST_DELAY_MS).toBe(50)
		const fetch = vi.fn((text: string) => Promise.resolve([row(text)]))
		const apply = vi.fn()
		const requests = suggestionRequests(fetch, apply)
		requests.request('g', field)
		await vi.advanceTimersByTimeAsync(30)
		requests.request('gi', field)
		await vi.advanceTimersByTimeAsync(49)
		expect(fetch).not.toHaveBeenCalled()
		await vi.advanceTimersByTimeAsync(1)
		expect(fetch.mock.calls).toEqual([['gi', field]])
		expect(apply).toHaveBeenCalledWith([row('gi')])
	})

	it('drops a request cancelled before it was sent', async () => {
		const fetch = vi.fn(() => Promise.resolve([]))
		const requests = suggestionRequests(fetch, vi.fn())
		requests.request('g', field)
		requests.cancel()
		await vi.advanceTimersByTimeAsync(100)
		expect(fetch).not.toHaveBeenCalled()
	})

	it('ignores the answer to a request that was replaced or cancelled while it was out', async () => {
		const answers: ((items: readonly AddressSuggestion[]) => void)[] = []
		const fetch = vi.fn(() => new Promise<readonly AddressSuggestion[]>((resolve) => answers.push(resolve)))
		const apply = vi.fn()
		const requests = suggestionRequests(fetch, apply)
		requests.request('a', field)
		await vi.advanceTimersByTimeAsync(SUGGEST_DELAY_MS)
		requests.request('ab', field)
		await vi.advanceTimersByTimeAsync(SUGGEST_DELAY_MS)
		answers[1]!([row('ab')])
		answers[0]!([row('a')])
		await vi.runAllTimersAsync()
		expect(apply.mock.calls).toEqual([[[row('ab')]]])

		requests.request('abc', field)
		await vi.advanceTimersByTimeAsync(SUGGEST_DELAY_MS)
		requests.cancel()
		answers[2]!([row('abc')])
		await vi.runAllTimersAsync()
		expect(apply).toHaveBeenCalledTimes(1)
	})
})
