// The cases live in downloadName.vectors.json, which the C# port's tests read too.
import { describe, expect, it } from 'vitest'
import { downloadFileName } from './downloadName'
import vectors from './downloadName.vectors.json'

describe('downloadFileName', () => {
	it.each(vectors.fileName)('$name', ({ filename, taken, expected }) => {
		const used = new Set(taken)
		expect(downloadFileName(filename, (n) => used.has(n))).toBe(expected)
	})
})
