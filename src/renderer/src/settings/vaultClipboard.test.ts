import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { CLIPBOARD_CLEAR_MS, copySecret } from './vaultClipboard'

describe('copySecret', () => {
	const writes: string[] = []

	beforeEach(() => {
		writes.length = 0
		vi.useFakeTimers()
		vi.stubGlobal('navigator', { clipboard: { writeText: (text: string) => Promise.resolve(void writes.push(text)) } })
	})

	afterEach(() => {
		vi.useRealTimers()
		vi.unstubAllGlobals()
	})

	it('puts the password on the clipboard and clears it after the timeout', async () => {
		await copySecret('hunter22')
		expect(writes).toEqual(['hunter22'])

		await vi.advanceTimersByTimeAsync(CLIPBOARD_CLEAR_MS - 1)
		expect(writes).toEqual(['hunter22'])
		await vi.advanceTimersByTimeAsync(1)
		expect(writes).toEqual(['hunter22', ''])
	})

	it('schedules no clear when the clipboard refuses the write', async () => {
		vi.stubGlobal('navigator', { clipboard: { writeText: () => Promise.reject(new DOMException('no', 'NotAllowedError')) } })

		await expect(copySecret('hunter22')).rejects.toThrow('no')
		expect(vi.getTimerCount()).toBe(0)
	})
})
