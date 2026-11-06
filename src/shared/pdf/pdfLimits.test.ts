import { readFileSync } from 'node:fs'
import { join } from 'node:path'
import { describe, expect, it } from 'vitest'
import { PDF_LIMITS } from './pdfCore'

const host = readFileSync(join(import.meta.dirname, '..', '..', '..', 'host', 'Zing.Core', 'Viewer', 'SessionKinds.cs'), 'utf8')

/** The N of `... N * 1024 * 1024` on the line that declares the PDF row, in bytes. */
function pdfMegabytes(declaration: RegExp): number {
	const match = declaration.exec(host)
	if (match === null) throw new Error(`SessionKinds.cs no longer matches ${declaration}`)
	return Number(match[1]) * 1024 * 1024
}

describe("the size limits the panel states are the host's", () => {
	it('reads a PDF', () => expect(PDF_LIMITS.maxReadBytes).toBe(pdfMegabytes(/SessionKind Pdf = new\("pdf", (\d+)L \* 1024 \* 1024/)))
	it('saves a PDF', () => expect(PDF_LIMITS.maxSaveBytes).toBe(pdfMegabytes(/SaveFormat Pdf = new\("\.pdf", [^,]*, (\d+)L \* 1024 \* 1024/)))
})
