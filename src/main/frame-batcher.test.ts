import { afterEach, describe, expect, it, vi } from 'vitest'
import { FrameBatcher } from './frame-batcher'

afterEach(() => vi.useRealTimers())

describe('FrameBatcher', () => {
	it('flushes a burst of changes once per frame', () => {
		vi.useFakeTimers()
		const flush = vi.fn()
		const batcher = new FrameBatcher(flush, 16)
		for (let i = 0; i < 10; i++) batcher.schedule()
		expect(flush).not.toHaveBeenCalled()
		vi.advanceTimersByTime(16)
		expect(flush).toHaveBeenCalledTimes(1)
		batcher.schedule()
		vi.advanceTimersByTime(16)
		expect(flush).toHaveBeenCalledTimes(2)
	})

	it('cancel drops a scheduled flush', () => {
		vi.useFakeTimers()
		const flush = vi.fn()
		const batcher = new FrameBatcher(flush, 16)
		batcher.schedule()
		batcher.cancel()
		vi.advanceTimersByTime(32)
		expect(flush).not.toHaveBeenCalled()
	})
})
