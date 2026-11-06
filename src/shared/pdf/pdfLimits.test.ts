import { readFileSync } from 'node:fs'
import { join } from 'node:path'
import { describe, expect, it } from 'vitest'
import { PDF_LIMITS } from './pdfCore'

const host = readFileSync(join(import.meta.dirname, '..', '..', '..', 'host', 'Zing.Core', 'PdfFiles.cs'), 'utf8')

/** `public const long Name = 50L * 1024 * 1024;` as a number of bytes. */
function megabytes(name: string): number {
	const match = new RegExp(`${name} = (\\d+)L \\* 1024 \\* 1024;`).exec(host)
	if (match === null) throw new Error(`PdfFiles.cs has no ${name} written as N megabytes`)
	return Number(match[1]) * 1024 * 1024
}

describe('the size limits the panel states are the host\'s', () => {
	it('reads', () => expect(PDF_LIMITS.maxReadBytes).toBe(megabytes('MaxReadBytes')))
	it('saves', () => expect(PDF_LIMITS.maxSaveBytes).toBe(megabytes('MaxSaveBytes')))
})
