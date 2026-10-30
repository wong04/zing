import { describe, expect, it } from 'vitest'
import { downloadFileName } from './downloadName'

describe('downloadFileName', () => {
	it('keeps a free name', () => {
		expect(downloadFileName('report.pdf', () => false)).toBe('report.pdf')
	})

	it('numbers on collision', () => {
		const taken = new Set(['report.pdf', 'report 2.pdf'])
		expect(downloadFileName('report.pdf', (n) => taken.has(n))).toBe('report 3.pdf')
	})

	it('numbers a file with no extension', () => {
		expect(downloadFileName('LICENSE', (n) => n === 'LICENSE')).toBe('LICENSE 2')
	})

	it('strips characters Windows forbids', () => {
		expect(downloadFileName('a<b>.txt', () => false)).toBe('ab.txt')
	})

	it('keeps a leading-dot name whole', () => {
		expect(downloadFileName('.env', () => false)).toBe('.env')
	})
})
