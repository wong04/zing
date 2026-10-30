export class CsvSyntaxError extends Error {
	override name = 'CsvSyntaxError'
}

// RFC 4180: quoted fields may hold commas, quotes ("") and newlines. Exports
// from Chrome, Bitwarden and 1Password all rely on that for notes fields.
export function parseCsv(text: string): string[][] {
	const source = text.charCodeAt(0) === 0xfeff ? text.slice(1) : text
	const rows: string[][] = []
	let row: string[] = []
	let field = ''
	let quoted = false
	let i = 0
	while (i < source.length) {
		const ch = source[i]!
		if (quoted) {
			if (ch === '"' && source[i + 1] === '"') {
				field += '"'
				i += 2
				continue
			}
			if (ch === '"') {
				quoted = false
				i++
				continue
			}
			field += ch
			i++
			continue
		}
		if (ch === '"') {
			if (field !== '') throw new CsvSyntaxError(`Unexpected quote inside an unquoted field at offset ${i}`)
			quoted = true
			i++
			continue
		}
		if (ch === ',') {
			row.push(field)
			field = ''
			i++
			continue
		}
		if (ch === '\r' || ch === '\n') {
			row.push(field)
			rows.push(row)
			row = []
			field = ''
			i += ch === '\r' && source[i + 1] === '\n' ? 2 : 1
			continue
		}
		field += ch
		i++
	}
	if (quoted) throw new CsvSyntaxError('Unterminated quoted field')
	if (field !== '' || row.length > 0) {
		row.push(field)
		rows.push(row)
	}
	return rows.filter((r) => !(r.length === 1 && r[0] === ''))
}

// Rows keyed by lower-cased header names.
export function parseCsvRecords(text: string): { headers: string[]; records: Record<string, string>[] } {
	const [header, ...rows] = parseCsv(text)
	if (!header) throw new CsvSyntaxError('The file is empty')
	const headers = header.map((h) => h.trim().toLowerCase())
	const records = rows.map((cells, index) => {
		if (cells.length !== headers.length) {
			throw new CsvSyntaxError(`Row ${index + 2} has ${cells.length} fields, the header has ${headers.length}`)
		}
		return Object.fromEntries(headers.map((h, j) => [h, cells[j] ?? '']))
	})
	return { headers, records }
}
