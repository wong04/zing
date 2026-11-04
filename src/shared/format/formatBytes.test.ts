import { describe, expect, it } from 'vitest'
import { formatBytes } from './formatBytes'

describe('formatBytes', () => {
	it('shows whole bytes without a decimal', () => {
		expect(formatBytes(0)).toBe('0 B')
		expect(formatBytes(1023)).toBe('1023 B')
	})
	it('steps up at 1024 and shows one decimal', () => {
		expect(formatBytes(1024)).toBe('1.0 KB')
		expect(formatBytes(114_768_076)).toBe('109.5 MB')
		expect(formatBytes(5 * 1024 ** 3)).toBe('5.0 GB')
	})
	it('stops at GB', () => {
		expect(formatBytes(2048 * 1024 ** 3)).toBe('2048.0 GB')
	})
})
